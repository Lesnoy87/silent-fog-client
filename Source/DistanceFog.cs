using System;
using System.IO;
using System.Reflection;
using UnityEngine;

// Полноэкранный пост-эффект: свой туман по расстоянию.
// Шейдер лежит в AssetBundle `AssetBundles/silentfogfog` рядом с DLL
// и собирается в Unity 2022.3.62f2 (версия движка игры).
public class SilentFogImageEffect : MonoBehaviour
{
    public Material material;
    public float fogStart = 18f;
    public float fogEnd = 90f;
    public float strength = 1f;
    public Color color = Color.white;

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
    }
}

internal static class DistanceFog
{
    private const string BundleName = "silentfogfog";
    private const string ShaderName = "Hidden/SilentFog/DistanceFog";

    private static Shader shader;
    private static Material material;
    private static bool loadTried;
    private static string status = "не загружен";
    private static Camera hooked;
    private static SilentFogImageEffect effect;
    private static float smoothStart = -1f;

    public static bool Available
    {
        get { return material != null; }
    }

    public static string Status
    {
        get { return status; }
    }

    // Вызывается каждый кадр из логики плотности: вешает эффект на камеру
    // игрока и передаёт ему актуальные параметры.
    public static void Tick(EntityPlayerLocal player)
    {
        if (!loadTried) TryLoad();
        if (material == null || player == null) return;

        Camera cam = player.playerCamera;
        if (cam == null) return;

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
    }

    // Параметры на текущий кадр. Сила приходит из общей логики плотности
    // (улица, буря, ночь, транспорт), поэтому все ползунки работают.
    // Под крышей начало тумана уезжает далеко вперёд — в комнате чисто,
    // а в окно всё равно видно туман на дальних дистанциях.
    public static void Apply(float strength, float shelter, float indoorPush)
    {
        if (effect == null) return;

        float span = Mathf.Max(FogSettings.DistanceEnd - FogSettings.DistanceStart, 10f);
        float target = FogSettings.DistanceStart + indoorPush * Mathf.Clamp01(shelter);

        // Резкий скачок (телепорт, вход в подвал) переносим сразу,
        // а обычные переходы сглаживаем.
        if (smoothStart < 0f || Mathf.Abs(target - smoothStart) > 120f)
        {
            smoothStart = target;
        }
        else
        {
            smoothStart = Mathf.Lerp(smoothStart, target, Time.deltaTime * 2.5f);
        }

        effect.fogStart = smoothStart;
        effect.fogEnd = smoothStart + span;
        effect.strength = Mathf.Clamp01(strength);
        effect.color = SkyManager.GetFogColor();
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

            shader = bundle.LoadAsset<Shader>(ShaderName);
            if (shader == null)
            {
                Shader[] all = bundle.LoadAllAssets<Shader>();
                if (all != null && all.Length > 0) shader = all[0];
            }

            if (shader == null || !shader.isSupported)
            {
                status = "шейдер не загрузился";
                return;
            }

            material = new Material(shader);
            material.hideFlags = HideFlags.DontSave;
            status = "готов";
        }
        catch (Exception e)
        {
            status = "ошибка: " + e.Message;
        }
    }
}
