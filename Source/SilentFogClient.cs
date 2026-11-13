using System.Reflection;
using HarmonyLib;
using UnityEngine;

// Точка входа DLL-мода 7 Days to Die (V1+/V2+/3.x).
public class SilentFogClientMod : IModApi
{
    public void InitMod(Mod _modInstance)
    {
        new Harmony("silentfog.client").PatchAll(Assembly.GetExecutingAssembly());
    }
}

// Общая логика плотности тумана для обоих патчей.
internal static class FogDensityLogic
{
    public const float SafeRadius = 12f;       // безопасная зона вокруг POI, метров
    public const float FadeEndRadius = 25f;    // конец плавного перехода
    // ВАЖНО: игра возводит это значение в куб перед RenderSettings.fogDensity.
    // 0.30^3 ≈ 0.027 — слабый видимый туман в помещении.
    // 0.75^3 ≈ 0.422 — плотный уличный туман без полного «молока».
    public const float IndoorFogDensity = 0.30f;
    public const float OutdoorFogDensity = 0.75f;
    private const float CacheInterval = 0.15f;

    private static float nextDistanceCheck;
    private static float cachedPoiDistance = float.MaxValue;
    private static Vector3 lastCheckPosition;

    // Возвращает желаемую плотность или -1, если мир/игрок ещё не готовы.
    // baseDensity — то, что насчитала vanilla/погода/сервер. Снаружи мы НЕ
    // занижаем её: 0.18–0.20 — это только минимальный порог.
    public static float GetDesiredDensity(float baseDensity)
    {
        World world = GameManager.Instance != null ? GameManager.Instance.World : null;
        EntityPlayerLocal player = world != null ? world.GetPrimaryPlayer() : null;
        if (player == null) return -1f;

        // В доме/подвале туман остаётся, но слабый.
        if (player.isIndoorsCurrent) return IndoorFogDensity;

        float now = Time.time;
        Vector3 pos = player.position;

        bool moved = (pos - lastCheckPosition).sqrMagnitude > 1f;
        if (now >= nextDistanceCheck || moved)
        {
            cachedPoiDistance = CalculateNearestPoiDistance(player);
            lastCheckPosition = pos;
            nextDistanceCheck = now + CacheInterval;
        }

        if (cachedPoiDistance <= SafeRadius) return IndoorFogDensity;

        float outdoor = Mathf.Max(baseDensity, OutdoorFogDensity);

        if (cachedPoiDistance < FadeEndRadius)
        {
            float t = Mathf.InverseLerp(SafeRadius, FadeEndRadius, cachedPoiDistance);
            return Mathf.Lerp(IndoorFogDensity, outdoor, t);
        }

        return outdoor;
    }

    private static float CalculateNearestPoiDistance(EntityPlayerLocal player)
    {
        World world = player.world;
        if (world == null) return float.MaxValue;

        DynamicPrefabDecorator decorator = world.ChunkCache != null
            ? world.ChunkCache.ChunkProvider.GetDynamicPrefabDecorator()
            : null;
        if (decorator == null || decorator.poiPrefabs == null) return float.MaxValue;

        float px = player.position.x;
        float pz = player.position.z;
        float bestSq = float.MaxValue;

        // poiPrefabs — только POI, без обычных декораций и деревьев.
        for (int i = 0; i < decorator.poiPrefabs.Count; i++)
        {
            PrefabInstance pi = decorator.poiPrefabs[i];
            if (pi == null) continue;

            float minX = pi.boundingBoxPosition.x;
            float minZ = pi.boundingBoxPosition.z;
            float maxX = minX + pi.boundingBoxSize.x;
            float maxZ = minZ + pi.boundingBoxSize.z;

            // Расстояние в плоскости XZ до прямоугольника POI.
            float dx = 0f;
            if (px < minX) dx = minX - px;
            else if (px > maxX) dx = px - maxX;

            float dz = 0f;
            if (pz < minZ) dz = minZ - pz;
            else if (pz > maxZ) dz = pz - maxZ;

            float distSq = dx * dx + dz * dz;
            if (distSq < bestSq) bestSq = distSq;
        }

        return bestSq == float.MaxValue ? float.MaxValue : Mathf.Sqrt(bestSq);
    }
}

// Основной патч плотности.
[HarmonyPatch(typeof(SkyManager), "GetFogDensity")]
internal static class PoiFogPatch
{
    private static void Postfix(ref float __result)
    {
        float desired = FogDensityLogic.GetDesiredDensity(__result);
        if (desired >= 0f) __result = desired;
    }
}

// Страховка от Safehouse / других систем, которые выставляют туман после
// SpectrumsFrameUpdate. Патч применяется после ванильного расчёта и
// принудительно возвращает нашу плотность.
[HarmonyPatch(typeof(WorldEnvironment), "SpectrumsFrameUpdate")]
internal static class SafehouseFogPatch
{
    private static void Postfix()
    {
        float desired = FogDensityLogic.GetDesiredDensity(SkyManager.GetFogDensity());
        if (desired >= 0f)
        {
            SkyManager.SetFogDensity(desired);
        }
    }
}

// Финальная страховка: некоторые системы/моды могут выставить RenderSettings
// уже после UpdateShaderGlobals. Возвращаем плотность в самом конце.
[HarmonyPatch(typeof(SkyManager), "UpdateShaderGlobals")]
internal static class FinalFogRenderPatch
{
    private static void Postfix()
    {
        float desired = FogDensityLogic.GetDesiredDensity(SkyManager.GetFogDensity());
        if (desired < 0f) return;

        SkyManager.SetFogDensity(desired);

        // В vanilla плотность возводится в куб перед передачей в RenderSettings.
        float renderDensity = desired * desired * desired;
        RenderSettings.fogDensity = renderDensity;
        Shader.SetGlobalVector("_FogParams", new Vector4(
            renderDensity,
            SkyManager.GetFogStart(),
            SkyManager.GetFogEnd(),
            0f
        ));
    }
}

// Делаем ночь темнее. Чем ближе dayPercent к 0, тем сильнее затемнение.
// Днём коэффициент возвращается к 1.0 и ничего не ломает.
