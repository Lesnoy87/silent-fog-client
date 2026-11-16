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

    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        if (material == null)
        {
            Graphics.Blit(source, destination);
            return;
        }

        material.SetFloat("_FogStart", fogStart);
        material.SetFloat("_FogEnd", Mathf.Max(fogEnd, fogStart + 1f));
        material.SetFloat("_FogStrength", Mathf.Clamp01(strength));
        material.SetFloat("_SkyExclude", 1f);
        material.SetColor("_FogColor", color);

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

    private const int MaxParticles = 900;

    // Частицы пока отложены: код оставлен, но выключен, чтобы в релизе
    // не было отладочной отрисовки и лишней нагрузки.
    private const bool ParticlesEnabled = false;

    private static Shader shader;
    private static Material material;
    private static Shader particleShader;
    private static Material particleMaterial;
    private static bool loadTried;
    private static string status = "не загружен";
    private static Camera hooked;
    private static SilentFogImageEffect effect;
    private static float smoothStart = -1f;
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

    public static void Tick(EntityPlayerLocal player)
    {
        if (!loadTried) TryLoad();
        if (material == null || player == null) return;

        Camera cam = player.playerCamera;
        if (cam == null) return;

        localPlayer = player;

        if (hooked != cam || effect == null)
        {
            hooked = cam;
            cam.depthTextureMode |= DepthTextureMode.Depth;

            effect = cam.GetComponent<SilentFogImageEffect>();
            if (effect == null)
            {
                effect = cam.gameObject.AddComponent<SilentFogImageEffect>();
            }
            effect.material = material;
        }

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

        float span = Mathf.Max(FogSettings.DistanceEnd - FogSettings.DistanceStart, 10f);

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

        float target = FogSettings.DistanceStart + indoorPush * Mathf.Clamp01(shelter) + breathe
            - horde * 6f;

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

            float headroom = strength > 0.01f ? 1f / strength : 1.35f;
            densityScale = Mathf.Clamp(densityScale, 0.4f, Mathf.Min(headroom, 1.35f));
        }

        LiveFactor = densityScale;
        HordeAmount = horde;

        effect.fogStart = smoothStart;
        effect.fogEnd = smoothStart + span;
        effect.strength = Mathf.Clamp01(strength * densityScale * (1f + horde * 0.35f));
        Color fogColor = BiomeFog.Apply(SkyManager.GetFogColor(), localPlayer);
        if (horde > 0.001f)
        {
            // Бордовый, а не алый: алый в 7DTD уже занят вспышками взрывов.
            fogColor = Color.Lerp(fogColor, new Color(0.52f, 0.06f, 0.08f), horde * 0.8f);
        }
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

            if (particleShader != null && particleShader.isSupported)
            {
                particleMaterial = new Material(particleShader);
                particleMaterial.hideFlags = HideFlags.DontSave;
            }

            status = "готов";
        }
        catch (Exception e)
        {
            status = "ошибка: " + e.Message;
        }
    }
}
