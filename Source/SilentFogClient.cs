using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

// Точка входа DLL-мода 7 Days to Die (V1+/V2+/3.x).
public class SilentFogClientMod : IModApi
{
    public void InitMod(Mod _modInstance)
    {
        new Harmony("silentfog.client").PatchAll(Assembly.GetExecutingAssembly());
        FogSettings.Load();
        FogMenu.Create();
    }
}

// Публичное API для других модов (например, DifRUST-интеграции).
public static class SilentFogApi
{
    // Установлен ли DifRUST. Без жёсткой зависимости: проверяем список модов,
    // затем имена загруженных сборок.
    public static bool DifRUSTInstalled
    {
        get { return ModCompat.DifRUSTInstalled; }
    }
}

internal static class ModCompat
{
    private static bool? difrustCached;

    public static bool DifRUSTInstalled
    {
        get
        {
            if (difrustCached.HasValue) return difrustCached.Value;

            bool found = false;

            try
            {
                found = ModManager.ModLoaded("DifRUST");
            }
            catch
            {
            }

            if (!found)
            {
                try
                {
                    Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                    for (int i = 0; i < assemblies.Length; i++)
                    {
                        string name = assemblies[i].GetName().Name;
                        if (name != null &&
                            name.IndexOf("DifRUST", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            found = true;
                            break;
                        }
                    }
                }
                catch
                {
                }
            }

            difrustCached = found;
            return found;
        }
    }
}

// Пепельная буря в горелом лесу. Привязана к ванильной буре:
// StormWarning -> туман начинает густеть, isPlayerInStorm -> полная буря,
// после окончания плавно спадает. Погода серверная, поэтому у всех одинаково.
internal static class AshStorm
{
    private static float strength;
    private static int lastFrame = -1;
    private static float demoUntil;
    private static float gustPhase;

    public static string BiomeName { get; private set; }
    public static bool InBurntForest { get; private set; }
    public static bool StormNow { get; private set; }
    public static bool WarningNow { get; private set; }

    public static float Strength
    {
        get { return strength; }
    }

    public static float EffectiveStrength
    {
        get
        {
            if (!FogSettings.StormEnabled) return 0f;
            return strength * Gust;
        }
    }

    // Порывы ветра: туман слегка «дышит», как при буре.
    private static float Gust
    {
        get
        {
            float g = Mathf.Sin(gustPhase) * Mathf.Sin(gustPhase * 0.37f + 1.7f);
            return 0.86f + 0.14f * g;
        }
    }

    // Демо: показать бурю здесь и сейчас, не дожидаясь погоды.
    public static void ForceDemo(float seconds)
    {
        demoUntil = Time.time + seconds;
    }

    public static bool DemoActive
    {
        get { return Time.time < demoUntil; }
    }

    public static float DemoRemaining
    {
        get { return Mathf.Max(0f, demoUntil - Time.time); }
    }

    // Короткое состояние для меню.
    public static string StateText
    {
        get
        {
            if (StormNow) return "идёт";
            if (WarningNow) return "собирается";
            if (DemoActive) return "демо";
            return "нет";
        }
    }

    public static void Tick(EntityPlayerLocal player)
    {
        if (player == null) return;
        if (Time.frameCount == lastFrame) return;
        lastFrame = Time.frameCount;

        BiomeDefinition biome = player.biomeStandingOn;
        string name = biome != null ? biome.m_sBiomeName : null;
        BiomeName = name;
        InBurntForest = name != null &&
                        name.IndexOf("burnt", StringComparison.OrdinalIgnoreCase) >= 0;

        StormNow = false;
        WarningNow = false;

        if (InBurntForest)
        {
            try
            {
                int stormLevel = 0;
                BiomeDefinition.WeatherGroup weather = biome.currentWeatherGroup;
                if (weather != null) stormLevel = weather.stormLevel;

                // isPlayerInStorm игра выставляет сама, когда в текущей
                // погоде биома stormLevel >= 2. Бафф — страховка.
                StormNow = player.isPlayerInStorm ||
                           player.Buffs.HasBuff("buffBurnt_Storm");

                WarningNow = !StormNow && stormLevel > 0;
            }
            catch
            {
            }
        }

        float target = 0f;
        if (StormNow)
        {
            target = 1f;
        }
        else if (WarningNow)
        {
            // Буря только собирается — туман уже начинает густеть.
            target = 0.35f;
        }

        if (Time.time < demoUntil)
        {
            target = 1f;
        }

        strength = Mathf.MoveTowards(strength, Mathf.Clamp01(target), Time.deltaTime / 3f);
        gustPhase += Time.deltaTime * 0.9f;
        if (gustPhase > 1000f) gustPhase -= 1000f;
    }
}

// Настройки тумана. Значения одинаковые у всех: хост меняет — уходит всем.
internal static class FogSettings
{
    public const float DefaultOutdoor = 0.75f;
    // В помещении по умолчанию почти чисто: туман не должен «протекать» в дом.
    public const float DefaultIndoor = 0.10f;
    public const float DefaultClosedVehicle = 0.30f;
    public const float DefaultOpenVehicle = 0.75f;

    // Когда туман выключен в меню, клиент гасит его сам: серверный
    // fogPower иначе продолжает давать плотный туман.
    public const float OffDensity = 0.05f;

    public static bool Enabled = true;
    public static float Outdoor = DefaultOutdoor;
    public static float Indoor = DefaultIndoor;
    public static float ClosedVehicle = DefaultClosedVehicle;
    public static float OpenVehicle = DefaultOpenVehicle;

    // Пепельная буря в горелом лесу (демо).
    public const float DefaultStormDensity = 0.90f;
    public static bool StormEnabled = true;
    public static float StormDensity = DefaultStormDensity;

    // Ночной туман при установленном DifRUST: 1.0 — не менять,
    // 0.6 — оставить 60% от дневной плотности. Ночь и так тёмная.
    public const float DefaultNightSoftening = 0.6f;
    public static float NightSoftening = DefaultNightSoftening;

    // Считать «помещением» любое место, где над головой есть блок.
    // Помогает в домах с проёмом/балконом, где isIndoorsCurrent врёт.
    public static bool UseShelter = true;

    // Свой туман по расстоянию (пост-эффект на камере).
    // Шейдер в AssetBundle, грузится в DistanceFog.
    public static bool DistanceOn = true;
    public static float DistanceStart = 12f;   // метров
    public static float DistanceEnd = 80f;     // метров

    // Насколько сильно подкрашивать туман под биом (0 — ванильный цвет).
    public static float BiomeTint = 0.55f;

    // «Живость»: кромка тумана медленно дышит и иногда накатывает порывами.
    // Считается по шуму Перлина в C#, шейдер не меняется вообще.
    public static bool LiveOn = true;
    public static float LiveAmount = 0.35f;   // 0..1: кромка ±8 м, плотность до −35%

    // CVar-имена синхронизации. Вандальные NetPackageModifyCVar, свой пакет
    // регистрировать не нужно — клиенты без мода просто ничего не заметят.
    public const string CvarEnabled = "sf_fog_on";
    public const string CvarOutdoor = "sf_fog_out";
    public const string CvarIndoor = "sf_fog_in";
    public const string CvarClosed = "sf_fog_veh_closed";
    public const string CvarOpen = "sf_fog_veh_open";
    public const string CvarStormOn = "sf_storm_on";
    public const string CvarStormDensity = "sf_storm_density";
    public const string CvarNightFog = "sf_night_fog";
    public const string CvarShelter = "sf_shelter_on";

    // Настройки тумана по расстоянию и оттенок биома — тоже от хоста.
    public const string CvarDistanceOn = "sf_distance_on";
    public const string CvarDistanceStart = "sf_distance_start";
    public const string CvarDistanceEnd = "sf_distance_end";
    public const string CvarBiomeTint = "sf_biome_tint";
    public const string CvarLiveOn = "sf_live_on";
    public const string CvarLiveAmount = "sf_live_amount";

    private static bool dirty;
    private static float nextSend;
    private static float nextHeartbeat;
    private static float nextRead;

    // Пришли ли настройки от хоста (для диагностики в меню).
    public static bool Synced { get; private set; }

    public static bool IsHost
    {
        get
        {
            var cm = SingletonMonoBehaviour<ConnectionManager>.Instance;
            return cm == null || cm.IsServer;
        }
    }

    public static World CurrentWorld
    {
        get
        {
            GameManager gm = GameManager.Instance;
            return gm != null ? gm.World : null;
        }
    }

    // Клиент читает то, что ему прислал хост. Если сервер без мода —
    // остаются дефолты, и мод работает сам по себе.
    public static void ReadFromLocalPlayer(EntityPlayerLocal player)
    {
        if (player == null || player.Buffs == null) return;

        // Хост — источник настроек, ему читать cvar-ы незачем.
        if (IsHost) return;

        float now = Time.time;
        if (now < nextRead) return;
        nextRead = now + 0.2f;

        EntityBuffs buffs = player.Buffs;
        if (!buffs.HasCustomVar(CvarEnabled))
        {
            Synced = false;
            return;
        }

        Synced = true;
        Enabled = buffs.GetCustomVar(CvarEnabled) >= 0.5f;
        Outdoor = Clamp01(buffs.GetCustomVar(CvarOutdoor));
        Indoor = Clamp01(buffs.GetCustomVar(CvarIndoor));
        ClosedVehicle = Clamp01(buffs.GetCustomVar(CvarClosed));
        OpenVehicle = Clamp01(buffs.GetCustomVar(CvarOpen));
        StormEnabled = buffs.GetCustomVar(CvarStormOn) >= 0.5f;
        StormDensity = Clamp01(buffs.GetCustomVar(CvarStormDensity));
        NightSoftening = Clamp01(buffs.GetCustomVar(CvarNightFog));
        UseShelter = buffs.GetCustomVar(CvarShelter) >= 0.5f;

        // Новые настройки: если хост их ещё не присылал (старый клиент),
        // остаются текущие локальные значения.
        if (buffs.HasCustomVar(CvarDistanceOn))
            DistanceOn = buffs.GetCustomVar(CvarDistanceOn) >= 0.5f;
        if (buffs.HasCustomVar(CvarDistanceStart))
            DistanceStart = Mathf.Clamp(buffs.GetCustomVar(CvarDistanceStart), 2f, 60f);
        if (buffs.HasCustomVar(CvarDistanceEnd))
            DistanceEnd = Mathf.Clamp(buffs.GetCustomVar(CvarDistanceEnd), 20f, 200f);
        if (buffs.HasCustomVar(CvarBiomeTint))
            BiomeTint = Clamp01(buffs.GetCustomVar(CvarBiomeTint));
        if (buffs.HasCustomVar(CvarLiveOn))
            LiveOn = buffs.GetCustomVar(CvarLiveOn) >= 0.5f;
        if (buffs.HasCustomVar(CvarLiveAmount))
            LiveAmount = Clamp01(buffs.GetCustomVar(CvarLiveAmount));

        // Внутри не должно быть плотнее, чем на улице.
        if (Indoor > Outdoor) Indoor = Outdoor;
    }

    public static void MarkDirty()
    {
        dirty = true;
    }

    public static void Tick()
    {
        if (!IsHost) return;
        World world = CurrentWorld;
        if (world == null) return;

        // Правки уходят почти сразу, но не чаще 4 раз в секунду,
        // чтобы перетаскивание ползунка не забивало сеть.
        if (dirty && Time.time >= nextSend)
        {
            dirty = false;
            nextSend = Time.time + 0.25f;
            Broadcast(world);
            Save();
            return;
        }

        // Новые игроки должны получить настройки и без правок в меню.
        if (Time.time >= nextHeartbeat)
        {
            nextHeartbeat = Time.time + 5f;
            Broadcast(world);
        }
    }

    public static void Broadcast(World world)
    {
        if (world == null) return;
        var players = world.Players.list;
        for (int i = 0; i < players.Count; i++)
        {
            EntityPlayer player = players[i];
            if (player == null || player.Buffs == null) continue;

            Send(player, CvarEnabled, Enabled ? 1f : 0f);
            Send(player, CvarOutdoor, Outdoor);
            Send(player, CvarIndoor, Indoor);
            Send(player, CvarClosed, ClosedVehicle);
            Send(player, CvarOpen, OpenVehicle);
            Send(player, CvarStormOn, StormEnabled ? 1f : 0f);
            Send(player, CvarStormDensity, StormDensity);
            Send(player, CvarNightFog, NightSoftening);
            Send(player, CvarShelter, UseShelter ? 1f : 0f);
            Send(player, CvarDistanceOn, DistanceOn ? 1f : 0f);
            Send(player, CvarDistanceStart, DistanceStart);
            Send(player, CvarDistanceEnd, DistanceEnd);
            Send(player, CvarBiomeTint, BiomeTint);
            Send(player, CvarLiveOn, LiveOn ? 1f : 0f);
            Send(player, CvarLiveAmount, LiveAmount);
        }
    }

    private static void Send(EntityPlayer player, string name, float value)
    {
        // Локально (хост) + всем клиентам ванильным пакетом CVar.
        player.Buffs.SetCustomVar(name, value, false, CVarOperation.set, false);
        player.Buffs.SetCustomVarNetwork(name, value, CVarOperation.set);
    }

    public static void ResetToDefaults()
    {
        Enabled = true;
        Outdoor = DefaultOutdoor;
        Indoor = DefaultIndoor;
        ClosedVehicle = DefaultClosedVehicle;
        OpenVehicle = DefaultOpenVehicle;
        StormEnabled = true;
        StormDensity = DefaultStormDensity;
        NightSoftening = DefaultNightSoftening;
        UseShelter = true;
        DistanceOn = true;
        DistanceStart = 12f;
        DistanceEnd = 80f;
        BiomeTint = 0.55f;
        LiveOn = true;
        LiveAmount = 0.35f;
        MarkDirty();
    }

    // Готовые наборы настроек: меняют сразу группу ползунков.
    public static void ApplyPreset(int index)
    {
        switch (index)
        {
            case 0: // лёгкий
                Outdoor = 0.45f;
                DistanceStart = 20f;
                DistanceEnd = 60f;
                BiomeTint = 0.35f;
                break;

            case 1: // средний (дефолт)
                Outdoor = DefaultOutdoor;
                DistanceStart = 12f;
                DistanceEnd = 80f;
                BiomeTint = 0.55f;
                break;

            case 2: // плотный
                Outdoor = 0.90f;
                DistanceStart = 8f;
                DistanceEnd = 50f;
                BiomeTint = 0.70f;
                break;

            case 3: // шторм
                Outdoor = 1.00f;
                DistanceStart = 5f;
                DistanceEnd = 40f;
                BiomeTint = 0.80f;
                StormDensity = 1.00f;
                break;
        }

        MarkDirty();
    }

    private static float Clamp01(float v)
    {
        if (float.IsNaN(v)) return 0f;
        return Mathf.Clamp01(v);
    }

    // 0 днём, 1 с 22:00 до 04:00, плавные переходы 20–22 и 04–06.
    // Совпадает с расписанием DifRUST, чтобы туман слабел вместе с тьмой.
    public static float NightAmount()
    {
        World world = CurrentWorld;
        if (world == null) return 0f;

        ulong time = world.GetWorldTime();
        int hours = GameUtils.WorldTimeToHours(time);
        int minutes = GameUtils.WorldTimeToMinutes(time);
        float clock = hours + minutes / 60f;

        if (clock >= 20f && clock < 22f) return (clock - 20f) / 2f;
        if (clock >= 22f || clock < 4f) return 1f;
        if (clock >= 4f && clock < 6f) return 1f - (clock - 4f) / 2f;
        return 0f;
    }

    // --- сохранение настроек хоста между запусками ---

    private static string SettingsPath
    {
        get
        {
            return Path.Combine(Application.persistentDataPath, "SilentFog.cfg");
        }
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            string[] lines = File.ReadAllLines(SettingsPath);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();
                float num;
                if (!float.TryParse(val, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out num)) continue;

                switch (key)
                {
                    case "enabled": Enabled = num >= 0.5f; break;
                    case "outdoor": Outdoor = Clamp01(num); break;
                    case "indoor": Indoor = Clamp01(num); break;
                    case "closed": ClosedVehicle = Clamp01(num); break;
                    case "open": OpenVehicle = Clamp01(num); break;
                    case "storm": StormEnabled = num >= 0.5f; break;
                    case "storm_density": StormDensity = Clamp01(num); break;
                    case "night_fog": NightSoftening = Clamp01(num); break;
                    case "shelter": UseShelter = num >= 0.5f; break;
                    case "distance": DistanceOn = num >= 0.5f; break;
                    case "distance_start": DistanceStart = Mathf.Clamp(num, 2f, 60f); break;
                    case "distance_end": DistanceEnd = Mathf.Clamp(num, 20f, 200f); break;
                    case "biome_tint": BiomeTint = Mathf.Clamp01(num); break;
                    case "live": LiveOn = num >= 0.5f; break;
                    case "live_amount": LiveAmount = Clamp01(num); break;
                }
            }
        }
        catch
        {
            // Битый или недоступный файл — просто дефолты.
        }
    }

    public static void Save()
    {
        try
        {
            string text = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "enabled={0}\noutdoor={1}\nindoor={2}\nclosed={3}\nopen={4}\nstorm={5}\nstorm_density={6}\nnight_fog={7}\nshelter={8}\ndistance={9}\ndistance_start={10}\ndistance_end={11}\nbiome_tint={12}\nlive={13}\nlive_amount={14}\n",
                Enabled ? "1" : "0", Outdoor, Indoor, ClosedVehicle, OpenVehicle,
                StormEnabled ? "1" : "0", StormDensity, NightSoftening,
                UseShelter ? "1" : "0",
                DistanceOn ? "1" : "0", DistanceStart, DistanceEnd, BiomeTint,
                LiveOn ? "1" : "0", LiveAmount);
            File.WriteAllText(SettingsPath, text);
        }
        catch
        {
        }
    }
}

// Общая логика плотности тумана для патчей.
internal static class FogDensityLogic
{
    public const float SafeRadius = 12f;       // безопасная зона вокруг POI, метров
    public const float FadeEndRadius = 32f;    // конец плавного перехода
    private const float CacheInterval = 0.15f;

    private static float nextDistanceCheck;
    private static float cachedPoiDistance = float.MaxValue;
    private static Vector3 lastCheckPosition;

    private static bool arkReflectionChecked;
    private static MethodInfo arkInCabinMethod;

    // Что сейчас применяется: улица / помещение / сейф-зона / транспорт.
    // Показывается в меню, чтобы было понятно, какой ползунок действует.
    public static string CurrentMode { get; private set; }

    // Диагностика для меню: сколько процентов лучей укрытия попало в перекрытие.
    public static float ShelterPercent
    {
        get { return Mathf.Clamp01(shelterCached) * 100f; }
    }

    // Фактическая плотность, которую мод отдаёт в игру (для диагностики).
    public static float AppliedDensity { get; private set; }

    // Возвращает желаемую плотность или -1, если мир/игрок ещё не готовы.
    public static float GetDesiredDensity()
    {
        World world = FogSettings.CurrentWorld;
        EntityPlayerLocal player = world != null ? world.GetPrimaryPlayer() : null;
        if (player == null) return -1f;

        // ВАЖНО: сначала читаем настройки хоста, даже если туман выключен.
        // Иначе выключенный туман никогда не включится обратно: чтение CVar-ов
        // стояло после проверки Enabled.
        AshStorm.Tick(player);
        FogSettings.ReadFromLocalPlayer(player);

        if (!FogSettings.Enabled)
        {
            if (DistanceFog.Available) DistanceFog.Apply(0f, 0f, 0f);
            AppliedDensity = FogSettings.OffDensity;
            return FogSettings.OffDensity;
        }

        // Свой туман по расстоянию: ванильную экспоненту гасим в ноль,
        // иначе она складывается с пост-эффектом и получается двойная пелена.
        DistanceFog.Tick(player);

        float raw;

        switch (GetVehicleShelter(player))
        {
            case VehicleShelter.Closed:
                CurrentMode = "закрытый транспорт";
                raw = FogSettings.ClosedVehicle;
                break;
            case VehicleShelter.Open:
                CurrentMode = "велик / мотоцикл";
                raw = FogSettings.OpenVehicle;
                break;
            default:
                raw = GetGroundDensity(player);
                break;
        }

        AppliedDensity = Smooth(raw);

        // Свой туман по расстоянию: игра получает нулевую ванильную плотность,
        // а всю работу делает пост-эффект. Сила берётся из того же расчёта,
        // поэтому буря, ночь и транспорт продолжают работать.
        if (FogSettings.DistanceOn && DistanceFog.Available)
        {
            float street = Mathf.Max(FogSettings.Outdoor, 0.05f);
            float protection = Mathf.Clamp01(1f - FogSettings.Indoor / street);

            // Чем выше «Защита помещения», тем дальше уезжает начало тумана
            // под крышей: 100% — туман внутрь не достаёт вообще.
            DistanceFog.Apply(AppliedDensity, ShelterPercent / 100f, protection * 60f);
            CurrentMode = "туман по расстоянию";
            return 0f;
        }

        return AppliedDensity;
    }

    private static float GetGroundDensity(EntityPlayerLocal player)
    {
        // В доме/подвале туман остаётся, но слабый.
        // В домах с проёмом/балконом isIndoorsCurrent врёт, поэтому
        // дополнительно смотрим, есть ли блок над головой.
        float shelter = ShelterFactor(player);

        if (shelter >= 0.999f)
        {
            CurrentMode = player.isIndoorsCurrent ? "помещение" : "под крышей";
            return FogSettings.Indoor;
        }

        // Территория торговца — сейф-зона, даже если здание «дырявое»
        // и игра не считает его помещением.
        if (IsInTraderSafeZone(player))
        {
            CurrentMode = "сейф-зона";
            return FogSettings.Indoor;
        }

        float now = Time.time;
        Vector3 pos = player.position;

        bool moved = (pos - lastCheckPosition).sqrMagnitude > 1f;
        if (now >= nextDistanceCheck || moved)
        {
            cachedPoiDistance = CalculateNearestPoiDistance(player);
            lastCheckPosition = pos;
            nextDistanceCheck = now + CacheInterval;
        }

        if (cachedPoiDistance <= SafeRadius)
        {
            CurrentMode = "сейф-зона";
            return FogSettings.Indoor;
        }

        CurrentMode = "улица";

        float outdoor = FogSettings.Outdoor;

        // Пепельная буря усиливает уличный туман.
        float storm = AshStorm.EffectiveStrength;
        if (storm > 0.001f)
        {
            outdoor = Mathf.Lerp(outdoor, FogSettings.StormDensity, storm);
        }

        // DifRUST делает ночь почти чёрной. Если он установлен,
        // ночью ослабляем уличный туман, чтобы не было «чёрной стены».
        if (ModCompat.DifRUSTInstalled)
        {
            float night = FogSettings.NightAmount();
            if (night > 0.001f)
            {
                outdoor *= Mathf.Lerp(1f, FogSettings.NightSoftening, night);
            }
        }

        float result;
        if (cachedPoiDistance < FadeEndRadius)
        {
            // SmoothStep вместо линейного: у самой сейф-зоны и на подходе
            // к улице туман меняется мягче, без ощущения «включилось».
            float t = Mathf.InverseLerp(SafeRadius, FadeEndRadius, cachedPoiDistance);
            result = Mathf.Lerp(FogSettings.Indoor, outdoor, Mathf.SmoothStep(0f, 1f, t));
        }
        else
        {
            result = outdoor;
        }

        // Мягкое укрытие: доля перекрытых лучей плавно подмешивает «домашнюю»
        // плотность. В дверях и под свесом получается промежуточное значение,
        // а не резкое «улица / дом».
        if (shelter > 0.01f)
        {
            result = Mathf.Lerp(result, FogSettings.Indoor, Mathf.SmoothStep(0f, 1f, shelter));
            if (shelter >= 0.5f)
            {
                CurrentMode = player.isIndoorsCurrent ? "помещение" : "под крышей";
            }
        }

        return result;
    }

    private static float smoothed = -1f;
    private static float smoothVelocity;
    private static int smoothFrame = -1;

    // Плавный переход между состояниями (дом <-> улица). SmoothDamp даёт
    // мягкий разгон и торможение: старт от сейф-зоны и подход к улице
    // без рывков, даже если значение меняется далеко не на один шаг.
    private static float Smooth(float target)
    {
        if (Time.frameCount == smoothFrame) return smoothed;
        smoothFrame = Time.frameCount;

        if (smoothed < 0f)
        {
            smoothed = target;
            smoothVelocity = 0f;
            return smoothed;
        }

        smoothed = Mathf.SmoothDamp(smoothed, target, ref smoothVelocity, 0.55f);
        return smoothed;
    }

    private enum VehicleShelter
    {
        None,
        Open,
        Closed
    }

    // Закрытый транспорт (4x4, SUV, гирокоптер, Ark и т.п.) считаем домом.
    // Велик, минибайк и мотоцикл — открытые, туман остаётся уличным.
    // Ark дополнительно проверяется через reflection, если игрок ещё не прикреплён
    // к EntityVehicle, но находится внутри развёрнутого лагеря.
    private static VehicleShelter GetVehicleShelter(EntityPlayerLocal player)
    {
        Entity attached = player.AttachedToEntity;
        EntityVehicle vehicleEntity = attached as EntityVehicle;

        if (vehicleEntity != null)
        {
            string name = vehicleEntity.vehicle != null
                ? (vehicleEntity.vehicle.vehicleName ?? "")
                : "";
            string lower = name.ToLowerInvariant();

            if (lower.Contains("bicycle") ||
                lower.Contains("minibike") ||
                lower.Contains("motorcycle"))
            {
                return VehicleShelter.Open;
            }

            return VehicleShelter.Closed;
        }

        if (!arkReflectionChecked)
        {
            arkReflectionChecked = true;
            Type arkShellView = Type.GetType("ArkMod.ArkShellView, Ark");
            if (arkShellView != null)
            {
                arkInCabinMethod = arkShellView.GetMethod(
                    "InCabin",
                    BindingFlags.Public | BindingFlags.Static
                );
            }
        }

        if (arkInCabinMethod != null)
        {
            try
            {
                if ((bool)arkInCabinMethod.Invoke(null, new object[] { player.position }))
                {
                    return VehicleShelter.Closed;
                }
            }
            catch
            {
                // Если Ark обновится и сигнатура изменится — просто без fallback.
            }
        }

        return VehicleShelter.None;
    }

    // Территория торговца (сейф-зона) по данным игры.
    private static bool IsInTraderSafeZone(EntityPlayerLocal player)
    {
        try
        {
            World world = player.world;
            if (world == null) return false;

            Vector3i pos = World.worldToBlockPos(player.position);
            return world.IsWithinTraderArea(pos);
        }
        catch
        {
            return false;
        }
    }

    // Строгая проверка укрытия.
    // Раньше использовался player.shelterAbovePercent, который считал укрытием
    // даже стену, к которой игрок прислонился (луч упирался в блок стены).
    // Теперь стреляем кольцом лучей вверх и засчитываем только горизонтальные
    // поверхности: у потолка нормаль смотрит вниз, у стены — вбок.
    private const bool StrictShelter = true;
    private const int ShelterRayCount = 8;
    private const float ShelterRingRadius = 1.2f;
    private const float ShelterRayLength = 10f;
    private const float ShelterMinHeight = 1f;
    private const float ShelterCoveredThreshold = 0.7f;

    private static int shelterFrame = -1;
    private static float shelterCached;

    // Крыша над головой: в домах с проёмом/балконом isIndoorsCurrent
    // может быть false, хотя игрок стоит под крышей.
    private static float ShelterFactor(EntityPlayerLocal player)
    {
        if (player.isIndoorsCurrent) return 1f;
        if (!FogSettings.UseShelter) return 0f;

        if (!StrictShelter)
        {
            return player.shelterAbovePercent >= 0.5f ? 1f : 0f;
        }

        return ShelterCoverage(player);
    }

    private static float ShelterCoverage(EntityPlayerLocal player)
    {
        if (Time.frameCount == shelterFrame) return shelterCached;
        shelterFrame = Time.frameCount;

        World world = player.world;
        if (world == null) return 0f;

        Vector3 pos = player.position;
        bool[] covered = new bool[5];
        int hits = 0;

        // 5 точек в пределах «пятна» игрока: центр и четыре смещения по 0.35 м.
        // Physics.Raycast в воксельном мире 7DTD не работает (свои коллайдеры),
        // поэтому смотрим блоки напрямую через World.GetBlock.
        for (int i = 0; i < 5; i++)
        {
            float ox = 0f;
            float oz = 0f;
            if (i == 1) ox = 0.35f;
            else if (i == 2) ox = -0.35f;
            else if (i == 3) oz = 0.35f;
            else if (i == 4) oz = -0.35f;

            Vector3i p = World.worldToBlockPos(new Vector3(pos.x + ox, pos.y + 0.2f, pos.z + oz));

            for (int dy = 2; dy <= 8; dy++)
            {
                BlockValue bv = world.GetBlock(p.x, p.y + dy, p.z);
                if (bv.isair) continue;

                covered[i] = true;
                hits++;
                break;
            }
        }

        // Главное правило: если над самим игроком (центральная точка) неба нет,
        // это укрытие. Если центр открыт — мы на улице, даже если края пятна
        // задевают свес крыши или карниз у стены.
        if (!covered[0])
        {
            shelterCached = 0f;
            return shelterCached;
        }

        shelterCached = hits / 5f;
        return shelterCached;
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
        float desired = FogDensityLogic.GetDesiredDensity();
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
        float desired = FogDensityLogic.GetDesiredDensity();
        if (desired >= 0f)
        {
            SkyManager.SetFogDensity(desired);
        }
    }
}

// Пепельный оттенок тумана во время бури. Патчим цвет до того, как
// UpdateShaderGlobals разложит его по RenderSettings и шейдерам.
[HarmonyPatch(typeof(SkyManager), "GetFogColor")]
internal static class StormFogColorPatch
{
    private static readonly Color AshLight = new Color(0.50f, 0.47f, 0.42f, 1f);
    private static readonly Color AshDark = new Color(0.33f, 0.31f, 0.28f, 1f);

    private static void Postfix(ref Color __result)
    {
        float storm = AshStorm.EffectiveStrength;
        if (storm <= 0.001f) return;

        // Чем сильнее буря, тем темнее пепел — иначе получается серая стена.
        Color ash = Color.Lerp(AshLight, AshDark, Mathf.Clamp01(storm));
        __result = Color.Lerp(__result, ash, storm * 0.9f);
    }
}

// Финальная страховка: некоторые системы/моды могут выставить RenderSettings
// уже после UpdateShaderGlobals. Возвращаем плотность в самом конце.
[HarmonyPatch(typeof(SkyManager), "UpdateShaderGlobals")]
internal static class FinalFogRenderPatch
{
    private static void Postfix()
    {
        float desired = FogDensityLogic.GetDesiredDensity();
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

// Пока открыто меню, не пускаем ввод в игрока: иначе камера едет за курсором,
// а персонаж продолжает идти по WASD.
[HarmonyPatch(typeof(PlayerMoveController), "Update")]
internal static class MenuInputBlockPatch
{
    private static bool Prefix()
    {
        if (!FogMenuBehaviour.IsOpen) return true;

        // Страховка: если мир выгрузился, пока меню было открыто,
        // не блокируем управление навсегда.
        GameManager gm = GameManager.Instance;
        if (gm == null || gm.World == null)
        {
            FogMenuBehaviour.ResetState();
            return true;
        }

        return false;
    }
}

// Меню настроек по NumPad5 (или F7). Работает у хоста: правки уходят всем.
internal static class FogMenu
{
    public static void Create()
    {
        try
        {
            GameObject go = new GameObject("SilentFogMenu");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<FogMenuBehaviour>();
        }
        catch (Exception e)
        {
            Debug.Log("[SilentFog] menu create failed: " + e.Message);
        }
    }
}

public class FogMenuBehaviour : MonoBehaviour
{
    public static bool IsOpen { get; private set; }

    public static void ResetState()
    {
        IsOpen = false;
    }

    private bool open;
    private Rect windowRect = new Rect(60f, 36f, 480f, 878f);

    // Прокрутка тела панели: настроек больше, чем помещается в окно.
    private Vector2 scrollPos;
    private const float ScrollBodyTop = 56f;
    private const float ScrollContentHeight = 1250f;

    private Texture2D panelTex;
    private Texture2D accentTex;
    private Texture2D dimTex;
    private Texture2D rowTex;
    private Texture2D checkOffTex;
    private Texture2D checkOnTex;

    private GUIStyle titleStyle;
    private GUIStyle labelStyle;
    private GUIStyle hintStyle;
    private GUIStyle valueStyle;
    private GUIStyle buttonStyle;
    private GUIStyle toggleStyle;
    private GUIStyle okStyle;
    private GUIStyle sectionStyle;
    private GUIStyle hudTitleStyle;
    private GUIStyle hudSmallStyle;

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Keypad5) || Input.GetKeyDown(KeyCode.F7))
        {
            Toggle();
        }

        GameManager gm = GameManager.Instance;
        if (!open)
        {
            FogSettings.Tick();
            return;
        }

        if (gm == null || gm.World == null)
        {
            Close();
            return;
        }

        // Держим курсор разблокированным, иначе игра его забирает каждый кадр.
        if (!gm.GetCursorEnabledOverride())
        {
            gm.SetCursorEnabledOverride(true, true);
        }

        EntityPlayerLocal player = gm.World.GetPrimaryPlayer();
        if (player != null)
        {
            player.moveDirection = Vector3.zero;
        }

        FogSettings.Tick();
    }

    private void Toggle()
    {
        if (open)
        {
            Close();
            return;
        }

        GameManager gm = GameManager.Instance;
        if (gm == null || gm.World == null)
        {
            return;
        }

        open = true;
        IsOpen = true;
        gm.SetCursorEnabledOverride(true, true);
    }

    private void Close()
    {
        open = false;
        IsOpen = false;
        GameManager gm = GameManager.Instance;
        if (gm != null)
        {
            gm.SetCursorEnabledOverride(false, false);
        }
    }

    private void OnGUI()
    {
        EnsureStyles();

        if (open)
        {
            // Затемняем игру, чтобы панель читалась поверх любого меню.
            GUI.DrawTexture(
                new Rect(0f, 0f, Screen.width, Screen.height),
                dimTex,
                ScaleMode.StretchToFill
            );

            windowRect = GUI.Window(0x5F06, windowRect, DrawWindow, GUIContent.none, GUIStyle.none);
        }

        DrawStormHud();
    }

    private void DrawStormHud()
    {
        if (!FogSettings.Enabled) return;

        float s = AshStorm.EffectiveStrength;
        if (s <= 0.02f) return;

        GameManager gm = GameManager.Instance;
        if (gm == null || gm.World == null) return;

        float w = 340f;
        float x = (Screen.width - w) / 2f;
        float y = 74f;
        float alpha = Mathf.Clamp01(s * 1.3f);

        Color oldColor = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, alpha);

        GUI.DrawTexture(new Rect(x, y, w, 46f), panelTex, ScaleMode.StretchToFill);
        GUI.Label(new Rect(x, y + 4f, w, 22f), "ПЕПЕЛЬНАЯ БУРЯ", hudTitleStyle);
        GUI.DrawTexture(
            new Rect(x + 50f, y + 30f, (w - 100f) * s, 4f),
            accentTex,
            ScaleMode.StretchToFill
        );

        if (AshStorm.DemoActive)
        {
            GUI.Label(
                new Rect(x, y + 30f, w, 18f),
                Mathf.CeilToInt(AshStorm.DemoRemaining) + " с",
                hudSmallStyle
            );
        }

        GUI.color = oldColor;
    }

    private void DrawWindow(int id)
    {
        float w = windowRect.width;
        float h = windowRect.height;

        // Фон панели рисуем сами — GUI.Window со стилем на 1x1 текстуре
        // в этой версии Unity фон не отображает.
        GUI.DrawTexture(new Rect(0f, 0f, w, h), panelTex, ScaleMode.StretchToFill);
        GUI.DrawTexture(new Rect(0f, 0f, w, 3f), accentTex, ScaleMode.StretchToFill);
        GUI.DrawTexture(new Rect(0f, h - 1f, w, 1f), accentTex, ScaleMode.StretchToFill);

        float y = 12f;
        GUI.Label(new Rect(18f, y, w - 70f, 26f), "SILENT FOG", titleStyle);

        if (GUI.Button(new Rect(w - 38f, 10f, 26f, 24f), "x", buttonStyle))
        {
            Close();
            return;
        }

        y += 32f;
        GUI.DrawTexture(new Rect(18f, y, w - 36f, 1f), rowTex, ScaleMode.StretchToFill);

        // Ниже шапки — прокручиваемая область: настроек стало больше, чем влезает.
        scrollPos = GUI.BeginScrollView(
            new Rect(0f, ScrollBodyTop, w, h - ScrollBodyTop),
            scrollPos,
            new Rect(0f, 0f, w - 18f, ScrollContentHeight));

        y = 0f;

        bool host = FogSettings.IsHost;

        if (host)
        {
            bool was = FogSettings.Enabled;
            FogSettings.Enabled = GUI.Toggle(
                new Rect(18f, y, w - 36f, 24f),
                FogSettings.Enabled,
                "  Туман включён",
                toggleStyle
            );
            if (was != FogSettings.Enabled) FogSettings.MarkDirty();
        }
        else
        {
            GUI.Label(new Rect(18f, y, w - 36f, 20f),
                "Настройки задаёт хост сервера", hintStyle);
            GUI.Label(new Rect(18f, y + 16f, w - 36f, 20f),
                FogSettings.Synced ? "синхронизация: получена" : "синхронизация: нет",
                FogSettings.Synced ? okStyle : hintStyle);
        }

        y += 40f;
        GUI.enabled = host && FogSettings.Enabled;

        // --- пресеты ---
        GUI.Label(new Rect(18f, y, w - 36f, 20f), "ПРЕСЕТЫ", sectionStyle);
        y += 22f;

        {
            float bw = (w - 36f - 12f) / 4f;
            if (GUI.Button(new Rect(18f, y, bw, 26f), "Лёгкий", buttonStyle))
                FogSettings.ApplyPreset(0);
            if (GUI.Button(new Rect(18f + bw + 4f, y, bw, 26f), "Средний", buttonStyle))
                FogSettings.ApplyPreset(1);
            if (GUI.Button(new Rect(18f + (bw + 4f) * 2f, y, bw, 26f), "Плотный", buttonStyle))
                FogSettings.ApplyPreset(2);
            if (GUI.Button(new Rect(18f + (bw + 4f) * 3f, y, bw, 26f), "Шторм", buttonStyle))
                FogSettings.ApplyPreset(3);
        }
        y += 34f;

        y = DrawRow(y, w, "Сила тумана", ref FogSettings.Outdoor);
        y = DrawRangeRow(y, w, "Начало тумана, м", ref FogSettings.DistanceStart, 2f, 60f);
        y = DrawRangeRow(y, w, "Конец тумана, м", ref FogSettings.DistanceEnd, 20f, 200f);

        // Помечено как эксперимент: эффект есть, но вкус у всех разный.
        GUI.Label(new Rect(18f, y, w - 36f, 18f), "ЭКСПЕРИМЕНТ · ДЫХАНИЕ ТУМАНА", sectionStyle);
        y += 20f;

        {
            bool was = FogSettings.LiveOn;
            FogSettings.LiveOn = DrawToggle(
                new Rect(18f, y, w - 36f, 22f),
                FogSettings.LiveOn,
                "Живость тумана (дыхание)");
            if (was != FogSettings.LiveOn) FogSettings.MarkDirty();
        }
        y += 28f;

        {
            bool wasEnabled = GUI.enabled;
            GUI.enabled = host && FogSettings.Enabled && FogSettings.LiveOn;
            y = DrawRow(y, w, "Амплитуда дыхания (в эксперименте)", ref FogSettings.LiveAmount);
            GUI.enabled = wasEnabled;

            GUI.Label(new Rect(18f, y - 6f, w - 36f, 18f),
                "Плотность гуляет до −35% на 100%. Синхронизируется от хоста.", hintStyle);
            y += 16f;
        }

        y = DrawRow(y, w, "Оттенок биома", ref FogSettings.BiomeTint);
        y = DrawProtectionRow(y, w);

        y = DrawRow(y, w, "Закрытый транспорт / Ark", ref FogSettings.ClosedVehicle);
        y = DrawRow(y, w, "Велик / мотоцикл", ref FogSettings.OpenVehicle);

        y += 2f;
        GUI.Label(
            new Rect(18f, y, w - 36f, 20f),
            string.Format("Сейчас: {0} · сила {1}% · укрытие {2}% · дыхание {3}%",
                FogDensityLogic.CurrentMode ?? "—",
                Mathf.RoundToInt(Mathf.Clamp01(FogDensityLogic.AppliedDensity) * 100f),
                Mathf.RoundToInt(FogDensityLogic.ShelterPercent),
                Mathf.RoundToInt(Mathf.Clamp01(DistanceFog.LiveFactor) * 100f)),
            hintStyle
        );
        y += 20f;

        GUI.Label(new Rect(18f, y, w - 36f, 20f),
            "Шейдер: " + DistanceFog.Status + " · частицы: " + DistanceFog.ParticleStatus, hintStyle);
        y += 22f;

        {
            bool was = FogSettings.DistanceOn;
            GUI.enabled = host && FogSettings.Enabled;
            FogSettings.DistanceOn = DrawToggle(
                new Rect(18f, y, w - 36f, 22f),
                FogSettings.DistanceOn,
                "Туман по расстоянию (свой шейдер)");
            GUI.enabled = true;
            if (was != FogSettings.DistanceOn) FogSettings.MarkDirty();
        }
        y += 28f;

        {
            bool was = FogSettings.UseShelter;
            GUI.enabled = host;
            FogSettings.UseShelter = DrawToggle(
                new Rect(18f, y, w - 36f, 22f),
                FogSettings.UseShelter,
                "Считать укрытием место под крышей");
            GUI.enabled = true;
            if (was != FogSettings.UseShelter) FogSettings.MarkDirty();
        }
        y += 28f;

        GUI.enabled = true;
        y += 2f;

        // --- пепельная буря ---
        GUI.DrawTexture(new Rect(18f, y, w - 36f, 1f), rowTex, ScaleMode.StretchToFill);
        y += 10f;
        GUI.Label(new Rect(18f, y, w - 36f, 20f), "ПЕПЕЛЬНАЯ БУРЯ · ГОРЕЛЫЙ ЛЕС", sectionStyle);
        y += 24f;

        GUI.enabled = host;
        {
            bool was = FogSettings.StormEnabled;
            FogSettings.StormEnabled = GUI.Toggle(
                new Rect(18f, y, w - 36f, 24f),
                FogSettings.StormEnabled,
                "  Буря включена",
                toggleStyle
            );
            if (was != FogSettings.StormEnabled) FogSettings.MarkDirty();
        }
        y += 28f;

        GUI.enabled = host && FogSettings.StormEnabled;
        y = DrawRow(y, w, "Плотность бури", ref FogSettings.StormDensity);
        GUI.enabled = true;

        string biome = AshStorm.BiomeName ?? "—";
        GUI.Label(
            new Rect(18f, y - 6f, w - 220f, 20f),
            string.Format("{0} · {1}% · буря: {2}",
                biome,
                Mathf.RoundToInt(AshStorm.EffectiveStrength * 100f),
                AshStorm.StateText),
            hintStyle
        );

        if (GUI.Button(new Rect(w - 196f, y - 10f, 178f, 26f), "Показать бурю", buttonStyle))
        {
            AshStorm.ForceDemo(45f);
        }

        y += 22f;

        // --- ночь и DifRUST ---
        GUI.DrawTexture(new Rect(18f, y, w - 36f, 1f), rowTex, ScaleMode.StretchToFill);
        y += 10f;

        bool difrust = ModCompat.DifRUSTInstalled;

        if (difrust)
        {
            GUI.Label(new Rect(18f, y, w - 36f, 20f), "НОЧЬ · DIFRUST ОБНАРУЖЕН", sectionStyle);
            y += 24f;

            GUI.enabled = host;
            y = DrawRow(y, w, "Ночной туман", ref FogSettings.NightSoftening);
            GUI.enabled = true;

            GUI.Label(new Rect(18f, y - 8f, w - 36f, 20f),
                "100% — не ослаблять, 60% — оставить 60% от дневного.", hintStyle);
            y += 22f;
        }
        else
        {
            GUI.Label(new Rect(18f, y, w - 36f, 20f), "НОЧЬ", sectionStyle);
            y += 22f;
            GUI.Label(new Rect(18f, y, w - 36f, 36f),
                "DifRUST не найден: ночное ослабление выключено.", hintStyle);
            y += 30f;
        }

        if (GUI.Button(new Rect(18f, y, 150f, 30f), "Сбросить", buttonStyle) && host)
        {
            FogSettings.ResetToDefaults();
        }
        bool closeRequested = false;
        if (GUI.Button(new Rect(178f, y, w - 196f, 30f), "Закрыть", buttonStyle))
        {
            Close();
            closeRequested = true;
        }

        y += 38f;
        GUI.Label(
            new Rect(18f, y, w - 36f, 40f),
            host
                ? "NumPad5 / F7 — открыть меню. Изменения сразу применяются всем игрокам."
                : "NumPad5 / F7 — открыть меню. Менять силу тумана может только хост.",
            hintStyle
        );

        GUI.EndScrollView();
        if (closeRequested) return;

        GUI.DragWindow(new Rect(0f, 0f, w, ScrollBodyTop - 12f));
    }

    // Защита помещения: 100% — тумана в доме нет, 0% — как на улице.
    private float DrawProtectionRow(float y, float w)
    {
        float street = Mathf.Max(FogSettings.Outdoor, 0.05f);
        float protection = Mathf.Clamp01(1f - FogSettings.Indoor / street);

        GUI.Label(new Rect(18f, y, w - 120f, 20f),
            "Защита помещения / сейф-зоны", labelStyle);
        GUI.Label(new Rect(w - 110f, y, 92f, 20f),
            Mathf.RoundToInt(protection * 100f) + "%", valueStyle);

        float newProtection = GUI.HorizontalSlider(
            new Rect(18f, y + 22f, w - 36f, 18f), protection, 0f, 1f);

        if (Mathf.Abs(newProtection - protection) > 0.001f)
        {
            FogSettings.Indoor = Mathf.Clamp01((1f - Mathf.Clamp01(newProtection)) * street);
            FogSettings.MarkDirty();
        }

        return y + 50f;
    }

    private float DrawRow(float y, float w, string title, ref float value)
    {
        GUI.Label(new Rect(18f, y, w - 120f, 20f), title, labelStyle);
        GUI.Label(new Rect(w - 110f, y, 92f, 20f),
            Mathf.RoundToInt(value * 100f) + "%", valueStyle);

        float newValue = GUI.HorizontalSlider(
            new Rect(18f, y + 22f, w - 36f, 18f), value, 0f, 1f);

        if (Mathf.Abs(newValue - value) > 0.001f)
        {
            value = Mathf.Clamp01(newValue);
            FogSettings.MarkDirty();
        }

        return y + 50f;
    }

    // Слайдер в метрах (для эксперимента с линейным туманом).
    private float DrawRangeRow(float y, float w, string title, ref float value,
        float min, float max)
    {
        GUI.Label(new Rect(18f, y, w - 120f, 20f), title, labelStyle);
        GUI.Label(new Rect(w - 110f, y, 92f, 20f),
            Mathf.RoundToInt(value) + " м", valueStyle);

        float newValue = GUI.HorizontalSlider(
            new Rect(18f, y + 22f, w - 36f, 18f), value, min, max);

        if (Mathf.Abs(newValue - value) > 0.05f)
        {
            value = Mathf.Clamp(newValue, min, max);
            FogSettings.MarkDirty();
        }

        return y + 50f;
    }

    private void EnsureStyles()
    {
        if (panelTex != null) return;

        panelTex = SolidTexture(new Color(0.055f, 0.07f, 0.08f, 0.97f));
        accentTex = SolidTexture(new Color(0.45f, 0.68f, 0.8f, 1f));
        rowTex = SolidTexture(new Color(0.25f, 0.32f, 0.36f, 0.9f));
        dimTex = SolidTexture(new Color(0f, 0f, 0f, 0.5f));
        checkOffTex = CheckboxTexture(false);
        checkOnTex = CheckboxTexture(true);

        titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 18,
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(0.8f, 0.92f, 0.97f) }
        };

        labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            normal = { textColor = new Color(0.87f, 0.9f, 0.92f) }
        };

        hintStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            wordWrap = true,
            normal = { textColor = new Color(0.62f, 0.68f, 0.72f) }
        };

        okStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            normal = { textColor = new Color(0.6f, 0.85f, 0.65f) }
        };

        sectionStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(0.6f, 0.7f, 0.76f) }
        };

        hudTitleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 15,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = new Color(0.95f, 0.9f, 0.8f) }
        };

        hudSmallStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            alignment = TextAnchor.MiddleCenter,
            normal = { textColor = new Color(0.85f, 0.82f, 0.75f) }
        };

        valueStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13,
            alignment = TextAnchor.MiddleRight,
            fontStyle = FontStyle.Bold,
            normal = { textColor = new Color(0.8f, 0.92f, 0.97f) }
        };

        buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 13,
            normal = { textColor = new Color(0.9f, 0.93f, 0.95f) },
            hover = { textColor = new Color(1f, 1f, 1f) },
            active = { textColor = new Color(1f, 1f, 1f) }
        };

        toggleStyle = new GUIStyle(GUI.skin.toggle)
        {
            fontSize = 13,
            normal = { textColor = new Color(0.87f, 0.9f, 0.92f) },
            onNormal = { textColor = new Color(0.87f, 0.9f, 0.92f) },
            hover = { textColor = new Color(1f, 1f, 1f) },
            onHover = { textColor = new Color(1f, 1f, 1f) }
        };
    }

    // Свой чекбокс: GUI.skin.toggle в этой игре для части состояний не рисует
    // птичку, из-за чего включённая настройка выглядела выключенной.
    private bool DrawToggle(Rect rect, bool value, string text)
    {
        if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
        {
            value = !value;
        }

        Color old = GUI.color;
        if (!GUI.enabled) GUI.color = new Color(1f, 1f, 1f, 0.45f);

        const float box = 18f;
        GUI.DrawTexture(
            new Rect(rect.x, rect.y + (rect.height - box) * 0.5f, box, box),
            value ? checkOnTex : checkOffTex,
            ScaleMode.StretchToFill);

        GUI.Label(new Rect(rect.x + 26f, rect.y, rect.width - 26f, rect.height), text, toggleStyle);

        GUI.color = old;
        return value;
    }

    private static Texture2D CheckboxTexture(bool on)
    {
        const int size = 18;
        Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);

        Color bg = new Color(0.07f, 0.09f, 0.10f, 1f);
        Color border = new Color(0.30f, 0.38f, 0.42f, 1f);
        Color mark = new Color(0.55f, 0.80f, 0.95f, 1f);

        for (int x = 0; x < size; x++)
        {
            for (int y = 0; y < size; y++)
            {
                bool edge = x == 0 || y == 0 || x == size - 1 || y == size - 1;
                tex.SetPixel(x, y, edge ? border : bg);
            }
        }

        if (on)
        {
            // Галочка: короткий штрих вниз, длинный — вверх-вправо.
            for (int t = 0; t < 2; t++)
            {
                for (int i = 0; i <= 3; i++)
                {
                    int x = 4 + i;
                    int y = 7 - i + t;
                    if (x >= 0 && x < size && y >= 0 && y < size) tex.SetPixel(x, y, mark);
                }
                for (int i = 0; i <= 6; i++)
                {
                    int x = 7 + i;
                    int y = 4 + i + t;
                    if (x >= 0 && x < size && y >= 0 && y < size) tex.SetPixel(x, y, mark);
                }
            }
        }

        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Point;
        tex.Apply();
        return tex;
    }

    private static Texture2D SolidTexture(Color color)
    {
        Texture2D tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        tex.SetPixel(0, 0, color);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Point;
        tex.Apply();
        return tex;
    }
}
