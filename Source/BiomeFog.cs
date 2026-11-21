using UnityEngine;

// Цвет тумана под биом. Ванильный цвет уже учитывает время суток и погоду,
// поэтому мы не заменяем его, а сдвигаем оттенок, сохраняя яркость:
// ночью туман остаётся тёмным, а не светится дневным песком.
internal static class BiomeFog
{
    private static readonly Color Snow      = new Color(0.62f, 0.78f, 1.00f, 1f);
    private static readonly Color Desert    = new Color(1.00f, 0.62f, 0.34f, 1f);
    private static readonly Color Radiated  = new Color(0.62f, 0.90f, 0.26f, 1f);
    private static readonly Color Wasteland = new Color(0.86f, 0.58f, 0.28f, 1f);
    private static readonly Color Burnt     = new Color(0.52f, 0.46f, 0.38f, 1f);

    // Насколько выкрутить насыщенность палитры поверх выравнивания по яркости.
    // 1.0 — как есть, больше — цвет заметнее днём.
    private const float Vividness = 1.45f;

    private static Color current = Color.white;
    private static bool started;

    public static Color Apply(Color vanilla, EntityPlayerLocal player)
    {
        float strength = Mathf.Clamp01(FogSettings.BiomeTint);
        if (strength <= 0.001f) return vanilla;

        Color target;
        bool palette = TryPalette(player, out target);
        if (!palette) target = vanilla;

        if (!started)
        {
            current = target;
            started = true;
        }

        // Плавный переход при входе в биом: ~1.5 секунды.
        current = Color.Lerp(current, target, Mathf.Clamp01(Time.deltaTime * 0.7f));

        // Для нейтральных биомов (лес/равнины/город) не сатурируем ванильный
        // цвет: иначе синева неба становится только сильнее.
        Color scaled = palette
            ? Saturate(KeepLuma(current, vanilla), Vividness)
            : current;

        Color result = Color.Lerp(vanilla, scaled, strength);
        result.a = 1f;
        return result;
    }

    // Молочный нейтральный оттенок: убираем лишнюю синеву, сохраняя яркость.
    // Ночью туман остаётся тёмным, днём — белёсым.
    public static Color Milkify(Color c)
    {
        float l = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
        Color milk = new Color(
            Mathf.Clamp01(l * 1.12f + 0.05f),
            Mathf.Clamp01(l * 1.07f + 0.05f),
            Mathf.Clamp01(l * 1.02f + 0.05f),
            1f);

        Color result = Color.Lerp(c, milk, 0.80f);
        result.a = 1f;
        return result;
    }

    // Выкручиваем насыщенность вокруг собственной яркости цвета.
    // При vividness = 1.0 цвет не меняется.
    private static Color Saturate(Color c, float vividness)
    {
        if (vividness <= 1.001f) return c;
        float luma = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
        Color boosted = new Color(
            luma + (c.r - luma) * vividness,
            luma + (c.g - luma) * vividness,
            luma + (c.b - luma) * vividness,
            1f);
        boosted.r = Mathf.Clamp01(boosted.r);
        boosted.g = Mathf.Clamp01(boosted.g);
        boosted.b = Mathf.Clamp01(boosted.b);
        return boosted;
    }

    // Сохраняем яркость ванильного цвета: меняется только оттенок.
    private static Color KeepLuma(Color palette, Color vanilla)
    {
        float src = 0.299f * vanilla.r + 0.587f * vanilla.g + 0.114f * vanilla.b;
        float dst = 0.299f * palette.r + 0.587f * palette.g + 0.114f * palette.b;
        if (dst < 0.001f) return palette;

        Color scaled = palette * (src / dst);
        scaled.a = 1f;
        return scaled;
    }

    // false — биом нейтральный, цвет берём ванильный.
    private static bool TryPalette(EntityPlayerLocal player, out Color color)
    {        color = Color.white;        if (player == null) return false;

        string name = null;
        try
        {
            if (player.biomeStandingOn != null)
            {
                name = player.biomeStandingOn.m_sBiomeName;
            }
        }
        catch
        {
        }

        if (string.IsNullOrEmpty(name)) return false;

        switch (name.ToLowerInvariant())
        {
            case "snow": color = Snow; return true;
            case "desert": color = Desert; return true;
            case "radiated": color = Radiated; return true;
            case "wasteland":
            case "wasteland_hub":
            case "city_wasteland": color = Wasteland; return true;
            case "burnt_forest": color = Burnt; return true;
            default: return false;   // pine_forest, forest, plains, city — не трогаем
        }
    }

    // Частицы под биом. Вызывается каждый кадр, поэтому без аллокаций.
    // speed: + вниз (снег, песок), - вверх (пепел, радиация).
    // size: размер частицы в метрах. lifetime: сколько живёт, сек.
    public static void GetParticles(
        EntityPlayerLocal player,
        out Color color,
        out float density,
        out float size,
        out float speed,
        out float lifetime,
        out Vector3 wind)
    {
        color = Color.white;
        density = 0f;
        size = 0.05f;
        speed = 1f;
        lifetime = 4f;
        wind = Vector3.zero;

        string name = GetBiomeName(player);
        if (string.IsNullOrEmpty(name)) return;

        switch (name.ToLowerInvariant())
        {
            case "snow":
                color = new Color(0.95f, 0.97f, 1.00f, 1f);
                density = 0.22f;
                size = 0.12f;
                speed = 1.4f;
                lifetime = 4f;
                wind = new Vector3(0.35f, 0f, 0.12f);
                return;

            case "desert":
                color = new Color(0.95f, 0.80f, 0.55f, 1f);
                density = 0.14f;
                size = 0.10f;
                speed = 0.15f;
                lifetime = 3f;
                wind = new Vector3(1.6f, 0f, 0.6f);
                return;

            case "radiated":
                color = new Color(0.75f, 0.95f, 0.45f, 1f);
                density = 0.22f;
                size = 0.09f;
                speed = -0.5f;
                lifetime = 5f;
                wind = new Vector3(0.2f, 0f, 0.2f);
                return;

            case "wasteland":
            case "wasteland_hub":
            case "city_wasteland":
                color = new Color(0.88f, 0.70f, 0.45f, 1f);
                density = 0.18f;
                size = 0.08f;
                speed = 0.10f;
                lifetime = 3.5f;
                wind = new Vector3(0.9f, 0f, 0.4f);
                return;

            case "burnt_forest":
                color = new Color(0.62f, 0.58f, 0.52f, 1f);
                density = 0.22f;
                size = 0.09f;
                speed = -0.7f;
                lifetime = 5f;
                wind = new Vector3(0.3f, 0f, 0.2f);
                return;

            default:
                return;   // лес, равнины, город — без частиц
        }
    }

    // Имя биома под игроком, в нижнем регистре. null — неизвестно.
    public static string GetBiomeName(EntityPlayerLocal player)
    {
        try
        {
            if (player != null && player.biomeStandingOn != null)
            {
                string name = player.biomeStandingOn.m_sBiomeName;
                return string.IsNullOrEmpty(name) ? null : name.ToLowerInvariant();
            }
        }
        catch
        {
        }
        return null;
    }
}
