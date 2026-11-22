using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

// Полноэкранный пост-эффект: свой туман по расстоянию.
// После блита рисуем частицы биома — они уже не попадают под туман.
public class SilentFogImageEffect : MonoBehaviour
{
    public Material material;
    public float fogStart = 18f;
    public float fogEnd = 90f;
    public float strength = 1f;
    public Color color = Color.white;
    public float shelter = 0f;

    // Динамика: амплитуда уже в долях плотности (0..0.30, с мягкими краями ~±22%)
    // и мировой сдвиг ветра.
    public float dynamicAmount = 0f;
    public Vector2 wind;

    // Мировой центр тумана (XZ), высота земли и вертикальное «стекание» шума.
    public Vector2 center;
    public float baseY;
    public float noiseSink;

    // До 6 боксов-препятствий (торговец, дом/POI, база).
    public Vector4[] boxMin = new Vector4[6];
    public Vector4[] boxMax = new Vector4[6];
    public int boxCount;
    public Vector4 edgeParams = new Vector4(1.5f, 0.5f, 0.27f, 0.12f);
    public bool debug;
    public int debugMode;

    // Смещение render-space -> game-space для камеры (floating origin).
    // Считается как player.position - player.transform.position.
    public Vector3 worldOffset;

    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        if (material == null || (strength <= 0.001f && boxCount == 0))
        {
            Graphics.Blit(source, destination);
            return;
        }

        material.SetFloat("_FogStart", fogStart);
        material.SetFloat("_FogEnd", Mathf.Max(fogEnd, fogStart + 1f));
        material.SetFloat("_FogStrength", Mathf.Clamp01(strength));
        material.SetFloat("_SkyExclude", 1f);
        material.SetColor("_FogColor", color);

        Camera cam = GetComponent<Camera>();
        if (cam != null)
        {
            Transform t = cam.transform;
            Vector3 pos = t.position + worldOffset;
            Vector3 right = t.right;
            Vector3 up = t.up;
            Vector3 forward = t.forward;

            material.SetVector("_CamPos", new Vector4(pos.x, pos.y, pos.z, 0f));
            material.SetVector("_CamRight", new Vector4(right.x, right.y, right.z, 0f));
            material.SetVector("_CamUp", new Vector4(up.x, up.y, up.z, 0f));
            material.SetVector("_CamForward", new Vector4(forward.x, forward.y, forward.z, 0f));

            // Тангенсы берём из матрицы проекции: так учитывается реальный
            // FOV и aspect, даже если игра их меняет (прицел, оптика).
            Matrix4x4 p = cam.projectionMatrix;
            float tanY = p.m11 > 0.0001f ? 1f / p.m11 : 0.5f;
            float tanX = p.m00 > 0.0001f ? 1f / p.m00 : 0.5f;
            material.SetFloat("_TanHalfFovY", tanY);
            material.SetFloat("_TanHalfFovX", tanX);
        }

        material.SetFloat("_DynamicAmount", Mathf.Clamp01(dynamicAmount));
        material.SetVector("_Wind", new Vector4(wind.x, wind.y, 0f, 0f));

        material.SetVector("_FogCenter", new Vector4(center.x, center.y, 0f, 0f));
        material.SetFloat("_BaseY", baseY);
        material.SetFloat("_NoiseSink", noiseSink);
        material.SetVectorArray("_FogBoxMin", boxMin);
        material.SetVectorArray("_FogBoxMax", boxMax);
        material.SetFloat("_FogBoxCount", boxCount);
        material.SetVector("_FogEdge", edgeParams);
        material.SetFloat("_FogDebug", debugMode);

        Graphics.Blit(source, destination, material);

        // Частицы рисуем в тот же буфер, но уже поверх тумана.
        // ВРЕМЕННО отключено: проверяем обычный DrawMesh.
        // DistanceFog.DrawParticles(destination, 1f - Mathf.Clamp01(shelter));
    }
}

internal struct FogParticle
{
    public Vector3 pos;
    public Vector3 vel;
    public float life;
    public float maxLife;
    public float size;
}

internal static class DistanceFog
{
    private const string BundleName = "silentfogfog";
    private const string ShaderName = "Hidden/SilentFog/DistanceFog";
    private const string ParticleShaderName = "Hidden/SilentFog/Particle";

    // Версия бандла — для меню: видно, какой шейдер реально загружен.
    public const string BundleTag = "1.20.0";

    private const int MaxParticles = 900;

    // Частицы пока отложены: код оставлен, но выключен, чтобы в релизе
    // не было отладочной отрисовки и лишней нагрузки.
    private const bool ParticlesEnabled = false;

    private static Shader shader;
    private static Material material;
    private static Shader particleShader;
    private static Material particleMaterial;
    private static Texture3D noiseTexture;
    private static Vector2 wind;
    private static bool loadTried;
    private static string status = "не загружен";
    private static Camera hooked;
    private static SilentFogImageEffect effect;

    // Мировое слежение: центр отстаёт от игрока на триггер.
    public static bool FollowLag = true;
    // Камера в game-space (render + floating origin) — для боксов и пузыря.
    public static Vector3 CamGame;
    private static World lastWorld;

    private static Vector2 center;
    private static bool centerInit;
    private static Vector2 lastPlayerPos;
    private static bool playerPosInit;
    private static float playerSpeed;
    private static float noiseSink;
    private static float baseY;
    private static bool baseInit;
    private static int tickFrame = -1;
    private static int applyFrame = -1;
    private static float smoothStart = -1f;
    private static float smoothSpan = -1f;
    private static float smoothStrength = -1f;
    private static EntityPlayerLocal localPlayer;

    // --- Частицы ---
    private static readonly FogParticle[] parts = new FogParticle[MaxParticles];
    private static int partCount;
    private static float spawnAcc;
    private static Color particleColor = Color.white;
    private static Mesh mesh;
    private static readonly List<Vector3> mVerts = new List<Vector3>(MaxParticles * 4);
    private static readonly List<Color> mCols = new List<Color>(MaxParticles * 4);
    private static readonly List<Vector2> mUvs = new List<Vector2>(MaxParticles * 4);
    private static readonly List<int> mTris = new List<int>(MaxParticles * 6);
    private static readonly float[] sortKeys = new float[MaxParticles];
    private static readonly int[] sortIdx = new int[MaxParticles];

    private static CommandBuffer particleBuffer;
    private static Camera bufferCamera;

    // Частицы рисуем прямо в кадр после блита тумана (см. DrawParticles).

    public static bool Available
    {
        get { return material != null; }
    }

    public static string Status
    {
        get { return status; }
    }

    public static string ParticleStatus { get; private set; } = "—";

    // Текущий коэффициент «дыхания» (1.0 — без изменений). Для меню.
    public static float LiveFactor { get; private set; } = 1f;

    // Насколько сейчас «включена» ночь орды (0..1) — для меню.
    public static float HordeAmount { get; private set; }

    // Текущая амплитуда клочьев (0..1, как в меню) и скорость ветра — для меню.
    public static float DynamicLive { get; private set; }
    public static float WindSpeed { get; private set; }

    // Диагностика для меню: фактическое начало/конец и отставание центра.
    public static float ActiveStart { get; private set; }
    public static float ActiveEnd { get; private set; }
    public static float CenterOffset { get; private set; }
    public static float ActiveStrength { get; private set; }

    // Цвет тумана до и после наших поправок — чтобы видеть источник оттенка.
    public static Color RawFogColor { get; private set; } = Color.white;
    public static Color FinalFogColor { get; private set; } = Color.white;

    public static void Tick(EntityPlayerLocal player)
    {
        if (!loadTried) TryLoad();
        if (material == null || player == null) return;

        Camera cam = player.playerCamera;
        if (cam == null) return;

        localPlayer = player;

        // Новый мир — сбрасываем боксы и пересобираем статику.
        if (lastWorld != player.world)
        {
            lastWorld = player.world;
            FogObstacles.Reset();
        }

        // GetDesiredDensity вызывается несколько раз за кадр (несколько
        // Harmony-патчей). Ветер/центр/«стекание» должны шагать один раз.
        if (Time.frameCount == tickFrame) return;
        tickFrame = Time.frameCount;

        // «Стекание»: шум медленно уезжает вниз, как будто туман оседает.
        // Высота тумана — от рельефа, а не от ног игрока: иначе на вышке
        // вертикальный градиент едет за камерой.
        float groundY = player.position.y;
        World world = player.world;
        if (world != null)
        {
            float terrain = world.GetHeightAt(player.position.x, player.position.z);
            if (terrain > 0.5f && terrain <= player.position.y + 2f)
                groundY = terrain;
        }

        if (!baseInit || Mathf.Abs(groundY - baseY) > 40f)
        {
            baseY = groundY;
            baseInit = true;
        }
        else
        {
            baseY = Mathf.Lerp(baseY, groundY, Time.deltaTime * 2f);
        }

        noiseSink += 0.35f * Time.deltaTime;
        if (noiseSink > 2000f) noiseSink -= 2000f;

        if (hooked != cam || effect == null)
        {
            centerInit = false;
            playerPosInit = false;
            playerSpeed = 0f;
            smoothStart = -1f;
            smoothSpan = -1f;
            smoothStrength = -1f;

            hooked = cam;
            cam.depthTextureMode |= DepthTextureMode.Depth;

            effect = cam.GetComponent<SilentFogImageEffect>();
            if (effect == null)
            {
                effect = cam.gameObject.AddComponent<SilentFogImageEffect>();
            }
            effect.material = material;
        }

        // Render-space -> game-space: камера в 7DTD может быть смещена
        // floating origin, а блоки/центр/бокс — в мировых координатах.
        effect.worldOffset = player.position - player.transform.position;
        CamGame = cam.transform.position + effect.worldOffset;

        if (ParticlesEnabled) EnsureBuffer(cam);
    }

    private static void EnsureBuffer(Camera cam)
    {
        // Ставим буфер РОВНО один раз на камеру. Повторная проверка
        // и добавление каждый кадр приводили к накоплению буферов
        // и падению FPS до 3.
        if (bufferCamera == cam && particleBuffer != null) return;

        if (bufferCamera != null && particleBuffer != null)
        {
            bufferCamera.RemoveCommandBuffer(CameraEvent.AfterImageEffects, particleBuffer);
        }

        bufferCamera = cam;
        particleBuffer = new CommandBuffer();
        particleBuffer.name = "SilentFogParticles";
        cam.AddCommandBuffer(CameraEvent.AfterImageEffects, particleBuffer);
    }

    private static void FillBuffer(float shelterFade)
    {
        if (particleBuffer == null) return;

        particleBuffer.Clear();

        if (particleMaterial == null || mesh == null || mVerts.Count == 0) return;

        particleMaterial.SetFloat("_ShelterFade", Mathf.Clamp01(shelterFade));
        particleBuffer.DrawMesh(mesh, Matrix4x4.identity, particleMaterial, 0, 0);
    }

    public static void Apply(float strength, float shelter, float indoorPush)
    {
        if (effect == null) return;

        // Несколько Harmony-патчей зовут GetDesiredDensity за кадр.
        // Apply должен обновлять мир/ветер/центр ровно один раз.
        if (Time.frameCount == applyFrame) return;
        applyFrame = Time.frameCount;

        // --- мировой центр: отстаёт от игрока, пока тот не выйдет из триггера ---
        if (localPlayer != null)
        {
            Vector2 p = new Vector2(localPlayer.position.x, localPlayer.position.z);
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);

            // Скорость игрока: на мотоцикле центр должен догонять быстрее,
            // иначе цилиндр отстаёт и «не поспевает».
            if (playerPosInit)
            {
                float inst = Vector2.Distance(p, lastPlayerPos) / dt;
                if (inst < 80f)
                {
                    playerSpeed = Mathf.Lerp(playerSpeed, inst,
                        Mathf.Clamp01(dt * 6f));
                }
            }
            else
            {
                playerSpeed = 0f;
                playerPosInit = true;
            }
            lastPlayerPos = p;

            if (!centerInit || (p - center).sqrMagnitude > 80f * 80f)
            {
                center = p;
                centerInit = true;
                playerSpeed = 0f;
            }

            if (FollowLag)
            {
                float trigger = 4f;
                float dist = Vector2.Distance(p, center);
                if (dist > trigger)
                {
                    Vector2 targetCenter = p + (center - p) / dist * trigger;
                    float followSpeed = Mathf.Max(24f, playerSpeed * 1.25f + 4f);
                    center = Vector2.MoveTowards(center, targetCenter,
                        followSpeed * dt);
                }
            }
            else
            {
                // В доме/у торговца центр не отстаёт — иначе туман окажется
                // внутри игрока при коротком радиусе.
                center = p;
            }
        }

        // Насколько камера внутри чистого объёма: 0 — улица, 1 — дом/торговец.
        // Тот же вес сжимает пузырь игрока: внутри R=0, снаружи R=start.
        float wIn = Mathf.Clamp01(FogObstacles.InsideWeight);

        // Внутри здания сила тумана снаружи должна быть уличной, а не
        // «домашней»: иначе окно почти чистое.
        float baseStrength = Mathf.Lerp(strength,
            FogDensityLogic.OutdoorDensity, wIn);

        float span = Mathf.Max(
            FogSettings.DistanceEnd - FogSettings.DistanceStart, 10f);

        // Внутри здания центр сводим к камере: тогда worldDist == Dh и
        // остатка лага внутри дома нет. На улице остаётся обычный FollowLag.
        Vector2 centerEff = center;
        if (localPlayer != null)
        {
            Vector2 camXZ = new Vector2(CamGame.x, CamGame.z);
            centerEff = Vector2.Lerp(center, camXZ, wIn);
        }

        // Сглаживаем span при смене режима (улица 33 м -> дом 2.5 м),
        // иначе fogEnd прыгает и на секунду появляется плотная оболочка.
        if (smoothSpan < 0f || Mathf.Abs(span - smoothSpan) > 120f)
        {
            smoothSpan = span;
        }
        else
        {
            smoothSpan = Mathf.Lerp(smoothSpan, span, Time.deltaTime * 2.5f);
        }

        // «Живость»: два независимых слоя по шуму Перлина.
        // 1) кромка гуляет ±3 м; 2) плотность дышит ±18%.
        float t = Time.time;
        float liveAmount = FogSettings.LiveOn ? Mathf.Clamp01(FogSettings.LiveAmount) : 0f;

        float breathe = 0f;
        if (liveAmount > 0f)
        {
            float breath = Mathf.PerlinNoise(t * 0.09f, 0.37f) * 2f - 1f;
            float gust = Mathf.PerlinNoise(t * 0.27f, 3.11f) * 2f - 1f;
            breathe = (breath * 0.75f + gust * 0.25f) * liveAmount * 8f;
        }

        // Ночь орды: туман краснеет, подходит ближе и густеет.
        // Считается один раз в кадр в HordeNight, здесь только применяем.
        float horde = FogSettings.HordeFogOn
            ? HordeNight.Amount() * Mathf.Clamp01(FogSettings.HordeFog)
            : 0f;

        // Кромка: настройка + защита помещения + дыхание. Внутри здания
        // indoorPush гаснет, потому что пузырь всё равно сжат до 0.
        float target = FogSettings.DistanceStart
            + indoorPush * Mathf.Clamp01(shelter) * (1f - wIn)
            + breathe;

        // Начало не уходит за спину игрока при любой настройке.
        // 4 м — триггер follow-центра; 6 м гарантируют, что туман впереди.
        target = Mathf.Max(target, 6f);

        if (smoothStart < 0f || Mathf.Abs(target - smoothStart) > 120f)
        {
            smoothStart = target;
        }
        else
        {
            smoothStart = Mathf.Lerp(smoothStart, target, Time.deltaTime * 2.5f);
        }

        // Плотность дышит в обе стороны: и гуще, и реже. Видно на любой
        // дистанции, даже когда стена далеко и сдвиг кромки не читается.
        float densityScale = 1f;
        if (liveAmount > 0f)
        {
            float wave = Mathf.PerlinNoise(t * 0.07f, 7.71f);
            float puff = Mathf.PerlinNoise(t * 0.25f, 1.93f);
            float mix = Mathf.Clamp01(wave * 0.7f + puff * 0.3f);

            // Качаем симметрично вокруг заданной силы: и гуще, и реже.
            // Вверх упираемся в потолок шейдера (1.0), поэтому запас ограничен
            // фактической силой: при 100% туман может только редеть.
            // При полной амплитуде: 0.75..1.25 (или до потолка), на 35%: ~0.91..1.09.
            float swing = (mix - 0.5f) * 2f;
            densityScale = 1f + swing * liveAmount * 0.25f;

            float headroom = baseStrength > 0.01f ? 1f / baseStrength : 1.35f;
            densityScale = Mathf.Clamp(densityScale, 0.4f, Mathf.Min(headroom, 1.35f));
        }

        LiveFactor = densityScale;
        HordeAmount = horde;

        // Динамика: клочья живут в мире и плывут по ветру. Ветер копим в
        // мировых координатах и не даём числу расти бесконечно.
        float dynamicOn = FogSettings.DynamicOn
            ? Mathf.Clamp01(FogSettings.DynamicAmount)
            : 0f;
        float windSpeed = Mathf.Lerp(0f, 4.5f, Mathf.Clamp01(FogSettings.DynamicSpeed));

        if (dynamicOn > 0.0001f)
        {
            wind.x += 0.82f * windSpeed * Time.deltaTime;
            wind.y += 0.57f * windSpeed * Time.deltaTime;
        }

        DynamicLive = dynamicOn;
        WindSpeed = windSpeed;

        effect.fogStart = smoothStart * (1f - wIn);
        effect.fogEnd = effect.fogStart + smoothSpan;

        ActiveStart = effect.fogStart;
        ActiveEnd = effect.fogEnd;
        CenterOffset = localPlayer != null
            ? Vector2.Distance(centerEff,
                new Vector2(localPlayer.position.x, localPlayer.position.z))
            : 0f;

        float targetStrength = Mathf.Clamp01(baseStrength * densityScale * (1f + horde * 0.35f));
        if (smoothStrength < 0f || targetStrength <= 0.001f)
        {
            smoothStrength = targetStrength;
        }
        else
        {
            smoothStrength = Mathf.Lerp(smoothStrength, targetStrength, Time.deltaTime * 4f);
        }

        effect.strength = smoothStrength;
        ActiveStrength = smoothStrength;
        // Клочья считаются в шейдере на каждый пиксель (4 tex3D). При нулевой
        // плотности результат всё равно умножается на 0 — не тратим GPU.
        effect.dynamicAmount = smoothStrength > 0.001f ? dynamicOn * 0.30f : 0f;
        effect.wind = wind;
        effect.center = centerEff;
        effect.worldOffset = localPlayer != null
            ? localPlayer.position - localPlayer.transform.position
            : Vector3.zero;
        effect.baseY = baseY;
        effect.noiseSink = noiseSink;
        effect.boxMin = FogObstacles.BoxMin;
        effect.boxMax = FogObstacles.BoxMax;
        effect.boxCount = FogObstacles.Count;
        effect.debugMode = FogSettings.DebugMode;
        RawFogColor = SkyManager.GetFogColor();
        Color fogColor = BiomeFog.Milkify(
            BiomeFog.Apply(RawFogColor, localPlayer));
        if (horde > 0.001f)
        {
            // Бордовый, а не алый: алый в 7DTD уже занят вспышками взрывов.
            fogColor = Color.Lerp(fogColor, new Color(0.52f, 0.06f, 0.08f), horde * 0.8f);
        }
        FinalFogColor = fogColor;
        effect.color = fogColor;
        effect.shelter = Mathf.Clamp01(shelter);

        if (ParticlesEnabled)
        {
            UpdateParticles(localPlayer, Mathf.Clamp01(strength), Mathf.Clamp01(shelter));
        }
        else
        {
            ParticleStatus = "отложены";
        }
    }

    // --- Частицы: симуляция на CPU, отрисовка квадами после тумана ---

    private static void UpdateParticles(EntityPlayerLocal player, float strength, float shelter)
    {
        if (particleMaterial == null)
        {
            ParticleStatus = particleShader == null ? "нет шейдера" : "шейдер отклонён";
            return;
        }
        if (player == null) return;

        Color color;
        float density, size, speed, lifetime;
        Vector3 wind;
        BiomeFog.GetParticles(player, out color, out density, out size, out speed, out lifetime, out wind);

        particleColor = color;

        float rate = density * 380f * strength * (1f - shelter);
        ParticleStatus = rate > 0.5f ? "работают" : "биом без частиц";

        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        Vector3 center = player.position;

        // Спавн
        if (rate > 0.5f)
        {
            spawnAcc += rate * dt;
            int guard = 0;
            while (spawnAcc >= 1f && partCount < MaxParticles && guard++ < 60)
            {
                spawnAcc -= 1f;
                SpawnParticle(center, size, speed, lifetime, wind);
            }
            if (spawnAcc > 4f) spawnAcc = 4f;
        }
        else
        {
            spawnAcc = 0f;
        }

        // Симуляция и переиспользование слотов
        const float maxDist = 34f;
        float maxDistSq = maxDist * maxDist;

        for (int i = 0; i < partCount; i++)
        {
            FogParticle p = parts[i];
            p.pos += p.vel * dt;
            p.life -= dt;

            Vector3 delta = p.pos - center;
            if (p.life <= 0f || delta.sqrMagnitude > maxDistSq)
            {
                parts[i] = parts[partCount - 1];
                partCount--;
                i--;
            }
            else
            {
                parts[i] = p;
            }
        }

        if (hooked != null) BuildMesh(hooked);
        FillBuffer(1f - shelter);

        if (rate > 0.5f) ParticleStatus = "работают (" + partCount + "ч, " + (mVerts.Count / 4) + "кв)";
    }

    private static void SpawnParticle(Vector3 center, float size, float speed, float lifetime, Vector3 wind)
    {
        float near = 2.5f;
        float far = 13f;
        float yMin = -2f;
        float yMax = 7f;

        // Снег сыпется сверху.
        if (speed > 0.5f)
        {
            yMin = 4f;
            yMax = 11f;
        }

        // Кольцо вокруг игрока: не спавним в лицо, но и не слишком далеко.
        float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
        float rad = Mathf.Lerp(near, far, Mathf.Sqrt(UnityEngine.Random.Range(0f, 1f)));

        FogParticle p;
        p.pos = center + new Vector3(
            Mathf.Cos(ang) * rad,
            UnityEngine.Random.Range(yMin, yMax),
            Mathf.Sin(ang) * rad);

        p.vel = new Vector3(wind.x, -speed, wind.z);
        p.vel += new Vector3(
            UnityEngine.Random.Range(-0.25f, 0.25f),
            UnityEngine.Random.Range(-0.12f, 0.12f),
            UnityEngine.Random.Range(-0.25f, 0.25f));

        p.maxLife = lifetime * UnityEngine.Random.Range(0.75f, 1.3f);
        p.life = p.maxLife;
        p.size = size * 2f * UnityEngine.Random.Range(0.6f, 1.4f);

        parts[partCount] = p;
        partCount++;
    }

    private static void BuildMesh(Camera cam)
    {
        if (mesh == null)
        {
            mesh = new Mesh();
            mesh.hideFlags = HideFlags.DontSave;
            mesh.MarkDynamic();
        }

        mVerts.Clear();
        mCols.Clear();
        mUvs.Clear();
        mTris.Clear();

        int n = partCount;
        if (n == 0)
        {
            mesh.Clear();
            return;
        }

        Vector3 camPos = cam.transform.position;
        for (int i = 0; i < n; i++)
        {
            sortKeys[i] = (parts[i].pos - camPos).sqrMagnitude;
            sortIdx[i] = i;
        }
        Array.Sort(sortKeys, sortIdx, 0, n);

        Vector3 right = cam.transform.right;
        Vector3 up = cam.transform.up;

        // Дальние первыми — так альфа-смешивание корректно.
        for (int k = n - 1; k >= 0; k--)
        {
            FogParticle p = parts[sortIdx[k]];

            float fadeIn = Mathf.Clamp01((p.maxLife - p.life) / 0.4f);
            float fadeOut = Mathf.Clamp01(p.life / 0.6f);
            float alpha = fadeIn * fadeOut;
            if (alpha <= 0.01f) continue;

            Vector3 r = right * (p.size * 0.5f);
            Vector3 u = up * (p.size * 0.5f);
            int b = mVerts.Count;

            mVerts.Add(p.pos - r - u);
            mVerts.Add(p.pos - r + u);
            mVerts.Add(p.pos + r + u);
            mVerts.Add(p.pos + r - u);

            Color c = particleColor;
            c.a = alpha;
            mCols.Add(c);
            mCols.Add(c);
            mCols.Add(c);
            mCols.Add(c);

            mUvs.Add(new Vector2(0f, 0f));
            mUvs.Add(new Vector2(0f, 1f));
            mUvs.Add(new Vector2(1f, 1f));
            mUvs.Add(new Vector2(1f, 0f));

            mTris.Add(b);
            mTris.Add(b + 1);
            mTris.Add(b + 2);
            mTris.Add(b + 2);
            mTris.Add(b + 3);
            mTris.Add(b);
        }

        mesh.Clear();
        mesh.SetVertices(mVerts);
        mesh.SetColors(mCols);
        mesh.SetUVs(0, mUvs);
        mesh.SetTriangles(mTris, 0);
        mesh.RecalculateBounds();
    }

    private static void TryLoad()
    {
        loadTried = true;

        try
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string path = Path.Combine(Path.Combine(dir, "AssetBundles"), BundleName);

            if (!File.Exists(path))
            {
                status = "нет бандла";
                return;
            }

            AssetBundle bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null)
            {
                status = "бандл не читается";
                return;
            }

            Shader[] all = bundle.LoadAllAssets<Shader>();
            shader = null;
            particleShader = null;

            if (all != null)
            {
                for (int i = 0; i < all.Length; i++)
                {
                    Shader s = all[i];
                    if (s == null) continue;

                    if (s.name == ShaderName) shader = s;
                    else if (s.name == ParticleShaderName) particleShader = s;
                }

                if (shader == null) shader = all[0];

                if (particleShader == null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i] != null && all[i] != shader)
                        {
                            particleShader = all[i];
                            break;
                        }
                    }
                }
            }

            if (shader == null || !shader.isSupported)
            {
                status = "шейдер не загрузился";
                return;
            }

            material = new Material(shader);
            material.hideFlags = HideFlags.DontSave;

            // Текстуру шума делаем сами при старте: 64x64x64, бесшовная,
            // с мипами — на дальних дистанциях она сама размывается и не мерцает.
            try
            {
                noiseTexture = CreateNoiseTexture3D(64);
                material.SetTexture("_NoiseTex", noiseTexture);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SilentFog] noise texture failed: " + e.Message);
            }

            if (particleShader != null && particleShader.isSupported)
            {
                particleMaterial = new Material(particleShader);
                particleMaterial.hideFlags = HideFlags.DontSave;
            }

            status = noiseTexture != null ? "готов" : "готов (без шума)";
        }
        catch (Exception e)
        {
            status = "ошибка: " + e.Message;
        }
    }

    // --- бесшовная 3D-текстура шума ---

    private static Texture3D CreateNoiseTexture3D(int size)
    {
        Texture3D tex = new Texture3D(size, size, size, TextureFormat.RGBA32, true);
        tex.name = "SilentFogNoise3D";
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Bilinear;
        tex.anisoLevel = 2;
        tex.hideFlags = HideFlags.DontSave;

        // Периоды кратны размеру тайла, поэтому текстура стыкуется без шва
        // по всем трём осям.
        int[] periods = { 4, 8, 16, 32 };
        float[] weights = { 0.55f, 0.27f, 0.13f, 0.05f };

        Color[] pixels = new Color[size * size * size];
        int index = 0;

        for (int z = 0; z < size; z++)
        {
            float w = z / (float)size;
            for (int y = 0; y < size; y++)
            {
                float v = y / (float)size;
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)size;
                    float n = 0f;

                    for (int o = 0; o < periods.Length; o++)
                    {
                        int period = periods[o];
                        n += ValueNoise3D(
                            u * period, v * period, w * period,
                            period, 1337 + o * 101) * weights[o];
                    }

                    // Чуть повышаем контраст: клочья должны читаться,
                    // а не быть серым шумом. 1.15 — мягче, чем в 1.14.0.
                    n = Mathf.Clamp01((n - 0.5f) * 1.15f + 0.5f);
                    pixels[index++] = new Color(n, n, n, 1f);
                }
            }
        }

        tex.SetPixels(pixels);
        tex.Apply(true, true);
        return tex;
    }

    // Гладкий 3D value-noise с заворотом координат по периоду: соседние
    // тайлы продолжают друг друга, поэтому на стыке нет видимого шва.
    private static float ValueNoise3D(float x, float y, float z, int period, int seed)
    {
        int x0 = Mathf.FloorToInt(x);
        int y0 = Mathf.FloorToInt(y);
        int z0 = Mathf.FloorToInt(z);
        float fx = x - x0;
        float fy = y - y0;
        float fz = z - z0;

        // Квинтическая сглаживающая кривая: мягче кубической, меньше
        // «квадратности» у крупных клочьев.
        fx = fx * fx * fx * (fx * (fx * 6f - 15f) + 10f);
        fy = fy * fy * fy * (fy * (fy * 6f - 15f) + 10f);
        fz = fz * fz * fz * (fz * (fz * 6f - 15f) + 10f);

        int xa = Wrap(x0, period);
        int xb = Wrap(x0 + 1, period);
        int ya = Wrap(y0, period);
        int yb = Wrap(y0 + 1, period);
        int za = Wrap(z0, period);
        int zb = Wrap(z0 + 1, period);

        float v000 = Hash01(xa, ya, za, seed);
        float v100 = Hash01(xb, ya, za, seed);
        float v010 = Hash01(xa, yb, za, seed);
        float v110 = Hash01(xb, yb, za, seed);
        float v001 = Hash01(xa, ya, zb, seed);
        float v101 = Hash01(xb, ya, zb, seed);
        float v011 = Hash01(xa, yb, zb, seed);
        float v111 = Hash01(xb, yb, zb, seed);

        float a = Mathf.Lerp(v000, v100, fx);
        float b = Mathf.Lerp(v010, v110, fx);
        float c = Mathf.Lerp(v001, v101, fx);
        float d = Mathf.Lerp(v011, v111, fx);

        float ab = Mathf.Lerp(a, b, fy);
        float cd = Mathf.Lerp(c, d, fy);
        return Mathf.Lerp(ab, cd, fz);
    }

    private static int Wrap(int v, int period)
    {
        int m = v % period;
        return m < 0 ? m + period : m;
    }

    private static float Hash01(int x, int y, int z, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + z * 1442695041 + seed * 69069);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFFu) / 16777215f;
        }
    }
}
