using System;
using System.Collections.Generic;
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
    public const float DefaultOutdoor = 0.94f;
    // В помещении по умолчанию почти чисто: туман не должен «протекать» в дом.
    public const float DefaultIndoor = 0.10f;
    public const float DefaultClosedVehicle = 0.30f;
    public const float DefaultOpenVehicle = 0.75f;

    // Когда туман выключен в меню, клиент гасит его сам: серверный
    // fogPower иначе продолжает давать плотный туман.
    public const float OffDensity = 0.05f;

    public static bool Enabled = true;
    // Локальная диагностика: 0 — выкл, 1 — чистая доля пути, 2 — зенит.
    public static int DebugMode = 0;
    // Хост-замок: клиенты не могут выключить туман/дистанцию у себя.
    public static bool ForceForClients = true;
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
    public static float DistanceStart = 8f;    // метров
    public static float DistanceEnd = 35f;     // метров

    // Насколько сильно подкрашивать туман под биом (0 — ванильный цвет).
    public static float BiomeTint = 0.55f;

    // «Живость»: кромка тумана медленно дышит и иногда накатывает порывами.
    // Считается по шуму Перлина в C#, шейдер не меняется вообще.
    public static bool LiveOn = true;
    public static float LiveAmount = 0.35f;   // 0..1: кромка ±8 м, плотность ±25% (база −15%)

    // Динамика: мировые клочья тумана. FBM-шум в шейдере, координаты — XZ мира.
    // Amount 1.0 — плотность гуляет примерно на ±22% (с мягкими краями).
    public static bool DynamicOn = true;
    public static float DynamicAmount = 0.60f;
    public static float DynamicSpeed = 0.45f;

    // Ночь орды: туман краснеет и плотнеет. HordeFog — сила эффекта 0..1.
    public static bool HordeFogOn = true;
    public static float HordeFog = 0.7f;

    // CVar-имена синхронизации. Вандальные NetPackageModifyCVar, свой пакет
    // регистрировать не нужно — клиенты без мода просто ничего не заметят.
    public const string CvarEnabled = "sf_fog_on";
    public const string CvarForce = "sf_force";
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
    public const string CvarHordeOn = "sf_horde_on";
    public const string CvarHordeFog = "sf_horde_fog";
    public const string CvarDynamicOn = "sf_dynamic_on";
    public const string CvarDynamicAmount = "sf_dynamic_amount";
    public const string CvarDynamicSpeed = "sf_dynamic_speed";

    private static bool dirty;
    private static float nextSend;
    private static float nextHeartbeat;
    private static float nextForce;
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

        // Готовность: ждём не только первый cvar, но и базовый набор.
        // Иначе при частично пришедшем пакете Outdoor=0 и туман моргает.
        if (!buffs.HasCustomVar(CvarEnabled)
            || !buffs.HasCustomVar(CvarOutdoor)
            || !buffs.HasCustomVar(CvarIndoor))
        {
            Synced = false;
            return;
        }

        Synced = true;
        Enabled = buffs.GetCustomVar(CvarEnabled) >= 0.5f;
        Outdoor = Clamp01(buffs.GetCustomVar(CvarOutdoor));
        Indoor = Clamp01(buffs.GetCustomVar(CvarIndoor));

        // Остальные — только если хост их реально прислал (совместимость
        // со старыми клиентами): иначе остаются текущие значения.
        if (buffs.HasCustomVar(CvarClosed))
            ClosedVehicle = Clamp01(buffs.GetCustomVar(CvarClosed));
        if (buffs.HasCustomVar(CvarOpen))
            OpenVehicle = Clamp01(buffs.GetCustomVar(CvarOpen));
        if (buffs.HasCustomVar(CvarStormOn))
            StormEnabled = buffs.GetCustomVar(CvarStormOn) >= 0.5f;
        if (buffs.HasCustomVar(CvarStormDensity))
            StormDensity = Clamp01(buffs.GetCustomVar(CvarStormDensity));
        if (buffs.HasCustomVar(CvarNightFog))
            NightSoftening = Clamp01(buffs.GetCustomVar(CvarNightFog));
        if (buffs.HasCustomVar(CvarShelter))
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
        if (buffs.HasCustomVar(CvarHordeOn))
            HordeFogOn = buffs.GetCustomVar(CvarHordeOn) >= 0.5f;
        if (buffs.HasCustomVar(CvarHordeFog))
            HordeFog = Clamp01(buffs.GetCustomVar(CvarHordeFog));
        if (buffs.HasCustomVar(CvarDynamicOn))
            DynamicOn = buffs.GetCustomVar(CvarDynamicOn) >= 0.5f;
        if (buffs.HasCustomVar(CvarDynamicAmount))
            DynamicAmount = Clamp01(buffs.GetCustomVar(CvarDynamicAmount));
        if (buffs.HasCustomVar(CvarDynamicSpeed))
            DynamicSpeed = Clamp01(buffs.GetCustomVar(CvarDynamicSpeed));

        // Внутри не должно быть плотнее, чем на улице.
        if (Indoor > Outdoor) Indoor = Outdoor;

        // Хост-замок: даже если клиент выставил sf_fog_on/sf_distance_on
        // в 0, туман остаётся включённым. Силу не даём уронить в ноль.
        bool force = buffs.HasCustomVar(CvarForce)
            && buffs.GetCustomVar(CvarForce) >= 0.5f;
        if (force)
        {
            Enabled = true;
            DistanceOn = true;
            if (Outdoor < 0.5f) Outdoor = 0.5f;
        }
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

        // Хост-замок: короткий heartbeat только для force/enabled/distance,
        // чтобы клиент не мог отключить туман дольше ~1.5 с.
        if (ForceForClients && Enabled && DistanceOn
            && Time.time >= nextForce)
        {
            nextForce = Time.time + 1.5f;
            BroadcastForce(world);
        }
    }

    private static void BroadcastForce(World world)
    {
        if (world == null) return;
        var players = world.Players.list;
        for (int i = 0; i < players.Count; i++)
        {
            EntityPlayer player = players[i];
            if (player == null || player.Buffs == null) continue;

            Send(player, CvarForce, 1f);
            Send(player, CvarEnabled, 1f);
            Send(player, CvarDistanceOn, 1f);
            Send(player, CvarOutdoor, Outdoor);
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
            Send(player, CvarForce,
                (ForceForClients && Enabled && DistanceOn) ? 1f : 0f);
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
            Send(player, CvarHordeOn, HordeFogOn ? 1f : 0f);
            Send(player, CvarHordeFog, HordeFog);
            Send(player, CvarDynamicOn, DynamicOn ? 1f : 0f);
            Send(player, CvarDynamicAmount, DynamicAmount);
            Send(player, CvarDynamicSpeed, DynamicSpeed);
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
        ForceForClients = true;
        Outdoor = DefaultOutdoor;
        Indoor = DefaultIndoor;
        ClosedVehicle = DefaultClosedVehicle;
        OpenVehicle = DefaultOpenVehicle;
        StormEnabled = true;
        StormDensity = DefaultStormDensity;
        NightSoftening = DefaultNightSoftening;
        UseShelter = true;
        DistanceOn = true;
        DistanceStart = 8f;
        DistanceEnd = 35f;
        BiomeTint = 0.55f;
        LiveOn = true;
        LiveAmount = 0.35f;
        HordeFogOn = true;
        HordeFog = 0.7f;
        DynamicOn = true;
        DynamicAmount = 0.60f;
        DynamicSpeed = 0.45f;
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
                DistanceStart = 8f;
                DistanceEnd = 35f;
                BiomeTint = 0.55f;
                break;

            case 2: // плотный
                Outdoor = 0.96f;
                DistanceStart = 8f;
                DistanceEnd = 35f;
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
                    case "force": ForceForClients = num >= 0.5f; break;
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
                    case "horde_on": HordeFogOn = num >= 0.5f; break;
                    case "horde_fog": HordeFog = Clamp01(num); break;
                    case "dynamic_on": DynamicOn = num >= 0.5f; break;
                    case "dynamic_amount": DynamicAmount = Clamp01(num); break;
                    case "dynamic_speed": DynamicSpeed = Clamp01(num); break;
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
                "enabled={0}\noutdoor={1}\nindoor={2}\nclosed={3}\nopen={4}\nstorm={5}\nstorm_density={6}\nnight_fog={7}\nshelter={8}\ndistance={9}\ndistance_start={10}\ndistance_end={11}\nbiome_tint={12}\nlive={13}\nlive_amount={14}\nhorde_on={15}\nhorde_fog={16}\ndynamic_on={17}\ndynamic_amount={18}\ndynamic_speed={19}\nforce={20}\n",
                Enabled ? "1" : "0", Outdoor, Indoor, ClosedVehicle, OpenVehicle,
                StormEnabled ? "1" : "0", StormDensity, NightSoftening,
                UseShelter ? "1" : "0",
                DistanceOn ? "1" : "0", DistanceStart, DistanceEnd, BiomeTint,
                LiveOn ? "1" : "0", LiveAmount,
                HordeFogOn ? "1" : "0", HordeFog,
                DynamicOn ? "1" : "0", DynamicAmount, DynamicSpeed,
                ForceForClients ? "1" : "0");
            File.WriteAllText(SettingsPath, text);
        }
        catch
        {
        }
    }
}

// Ночь орды. Сначала пробуем спросить саму игру (BloodMoonComponent — активна ли
// орда), а если поле не нашлось — считаем по номеру дня: по умолчанию орда
// каждые 7 дней. Так мод не падает на другой версии игры и не тянет лишних ссылок.
internal static class HordeNight
{
    private static int cachedFrame = -1;
    private static float cached;
    private static float lastActive = -999f;

    // Кэш дня орды: пересчитываем раз за игровой день и при смене мира.
    private static int cachedBmDays = -1;
    private static int cachedBmDay = -1;
    private static World lastBmWorld;

    // Сколько секунд туман гаснет после последней волны.
    private const float FadeSeconds = 120f;

    // Насколько сильно краснеет до боя (предчувствие).
    private const float Premonition = 0.55f;

    public static float Amount()
    {
        if (Time.frameCount != cachedFrame)
        {
            cachedFrame = Time.frameCount;
            cached = Compute();
        }
        return cached;
    }

    private static float Compute()
    {
        World world = GameManager.Instance != null ? GameManager.Instance.World : null;
        if (world == null) return 0f;

        // Хорда возможна только вечером и ночью. Днём рефлексию не дёргаем:
        // раньше TryBloodMoon вызывался каждый кадр даже в полдень.
        float evening = Evening(world);
        bool fading = lastActive > 0f && Time.time - lastActive < FadeSeconds;
        if (evening <= 0f && !fading) return 0f;

        bool active;
        bool known = TryBloodMoon(world, out active);

        // 1. Орда идёт — полный красный. Флаг игры честно гаснет, когда орда
        //    заканчивается, поэтому ждать рассвета не нужно.
        if (known && active)
        {
            lastActive = Time.time;
            return 1f;
        }

        // 2. Только что кончилась — гаснем за две минуты, плавно.
        if (lastActive > 0f)
        {
            float since = Time.time - lastActive;
            if (since < FadeSeconds)
                return 1f - Mathf.Clamp01(since / FadeSeconds);
        }

        if (evening <= 0f) return 0f;

        // 3. Предчувствие: вечером того дня, когда будет орда — не полный цвет.
        //    Если игра умеет сказать «сегодня орда» (BloodMoonWarning) — верим ей,
        //    иначе считаем по номеру дня (орда каждые 7 дней).
        bool today;
        if (known)
        {
            if (!TryBoolMember(world, "BloodMoonWarning", out today))
                today = IsHordeDay(world);
        }
        else
        {
            today = IsHordeDay(world);
        }

        return today ? evening * Premonition : 0f;
    }

    // Часы: 0 — не вечер орды, 1 — глубокая ночь. Краснеем с 16:00,
    // полностью к 21:30, утром гаснем 04:00 → 06:30.
    private static float Evening(World world)
    {
        float clock = Clock(world);
        if (clock < 0f) return 0f;

        if (clock >= 16f && clock < 21.5f)
        {
            float t = Mathf.InverseLerp(16f, 21.5f, clock);
            return t * t * (3f - 2f * t); // smoothstep
        }

        if (clock >= 21.5f || clock < 4f) return 1f;
        if (clock >= 4f && clock < 6.5f) return 1f - Mathf.InverseLerp(4f, 6.5f, clock);
        return 0f;
    }

    private static bool IsHordeDay(World world)
    {
        int days = GameUtils.WorldTimeToDays(world.GetWorldTime());
        if (days <= 0) return false;

        // До 04:00 формально уже новый день, но ночь орды ещё идёт.
        // Берём предыдущий день, иначе предчувствие обрывается в полночь.
        float clock = Clock(world);
        if (clock >= 0f && clock < 4f) days -= 1;

        // 1) Штатный день орды из статов игры: учитывает частоту, разброс
        //    и ручной перенос (bmDayNextOverride).
        int hordeDay = HordeDay(world);
        if (hordeDay > 0) return days == hordeDay;

        // 2) Флаг «сегодня будет орда» — по нему красится счётчик дня.
        bool flag;
        if (TryBoolMember(world, "BloodMoonWarning", out flag) && flag)
            return true;

        // 3) Запасной путь — реальная частота из настроек мира, не константа 7.
        int freq = BloodMoonFrequency();
        return freq > 0 && (days % freq) == 0;
    }

    // День следующей/текущей орды. -1 — игра не сказала.
    // Порядок: GameStat.BloodMoonDay → BloodMoonComponent (PropBloodMoonDay /
    // _bmDay) → -1. Раньше здесь был days % 7, из-за чего в мире
    // с BloodMoonFrequency = 5 туман краснел на 21-й день, а орда была на 24-й.
    public static int HordeDay(World world)
    {
        if (!ReferenceEquals(world, lastBmWorld))
        {
            lastBmWorld = world;
            cachedBmDays = -1;
            cachedBmDay = -1;
        }

        int days = world != null
            ? GameUtils.WorldTimeToDays(world.GetWorldTime())
            : -1;
        if (days == cachedBmDays) return cachedBmDay;

        cachedBmDays = days;
        int stat = GameStatHordeDay();
        cachedBmDay = stat > 0 ? stat : ReadHordeDay(world);
        return cachedBmDay;
    }

    private static int GameStatHordeDay()
    {
        try
        {
            int d = GameStats.GetInt(EnumGameStats.BloodMoonDay);
            return d > 0 ? d : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static int ReadHordeDay(World world)
    {
        try
        {
            object ai = Member(world, "aiDirector");
            object bm = ai != null ? Member(ai, "BloodMoonComponent") : null;
            if (bm == null) return -1;

            Type t = bm.GetType();
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance;

            // PropBloodMoonDay (текущая цель), _bmDay. bmDayLast — прошлая
            // орда, его не берём: иначе «сегодня орда» срабатывает вчера.
            string[] names = { "PropBloodMoonDay", "_bmDay", "bloodMoonDay", "BloodMoonDay" };
            for (int i = 0; i < names.Length; i++)
            {
                PropertyInfo pi = t.GetProperty(names[i], F);
                if (pi != null)
                {
                    int d = ToInt(pi.GetValue(bm, null));
                    if (d > 0) return d;
                }

                FieldInfo fi = t.GetField(names[i], F);
                if (fi != null)
                {
                    int d = ToInt(fi.GetValue(bm));
                    if (d > 0) return d;
                }
            }
        }
        catch
        {
        }
        return -1;
    }

    private static int BloodMoonFrequency()
    {
        try
        {
            return GamePrefs.GetInt(EnumGamePrefs.BloodMoonFrequency);
        }
        catch
        {
            return 7;
        }
    }

    private static int ToInt(object v)
    {
        if (v is int) return (int)v;
        if (v is float) return Mathf.RoundToInt((float)v);
        if (v is double) return Mathf.RoundToInt((float)(double)v);
        if (v is uint) return (int)(uint)v;
        if (v is long) return (int)(long)v;
        return -1;
    }

    private static bool TryBoolMember(World world, string name, out bool value)
    {
        value = false;
        try
        {
            object ai = Member(world, "aiDirector");
            if (ai == null) return false;

            object bm = Member(ai, "BloodMoonComponent");
            if (bm == null) return false;

            object flag = Member(bm, name);
            if (flag is bool)
            {
                value = (bool)flag;
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    private static bool TryBloodMoon(World world, out bool active)
    {
        active = false;
        try
        {
            object ai = Member(world, "aiDirector");
            if (ai == null) return false;

            object bm = Member(ai, "BloodMoonComponent");
            if (bm == null) return false;

            object flag = Member(bm, "BloodMoonActive");
            if (flag is bool)
            {
                active = (bool)flag;
                return true;
            }
        }
        catch
        {
        }
        return false;
    }

    // Кэш метаданных: type → (имя → свойство/поле). Без него каждый кадр
    // шли GetProperty/GetField, хотя набор имён фиксированный.
    private static readonly Dictionary<Type, Dictionary<string, MemberInfo>> memberCache =
        new Dictionary<Type, Dictionary<string, MemberInfo>>(4);

    private static object Member(object target, string name)
    {
        if (target == null) return null;
        Type t = target.GetType();

        Dictionary<string, MemberInfo> map;
        if (!memberCache.TryGetValue(t, out map))
        {
            map = new Dictionary<string, MemberInfo>(8);
            memberCache[t] = map;
        }

        MemberInfo mi;
        if (!map.TryGetValue(name, out mi))
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance;
            mi = t.GetProperty(name, F);
            if (mi == null) mi = t.GetField(name, F);
            map[name] = mi;
        }

        PropertyInfo p = mi as PropertyInfo;
        if (p != null) return p.GetValue(target, null);

        FieldInfo f = mi as FieldInfo;
        return f != null ? f.GetValue(target) : null;
    }

    private static float Clock(World world)
    {
        ulong time = world.GetWorldTime();
        int hours = GameUtils.WorldTimeToHours(time);
        int minutes = GameUtils.WorldTimeToMinutes(time);
        return hours + minutes / 60f;
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

    // Уличная плотность текущего кадра (с бурей/ночью) — для интерьера.
    private static float outdoorDensity = FogSettings.DefaultOutdoor;
    // Гистерезис «внутри»: не дёргается на пороге двери/окна.
    private static bool insideLatched;
    private static float insideClearTimer;
    // Латч трейдера: сейф-зона не должна мигать на границе.
    private static float traderLatchUntil;

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
    // Несколько Harmony-патчей зовут метод по несколько раз за кадр —
    // считаем один раз, иначе таймеры (гистерезис укрытия) идут втрое
    // быстрее, а строки статуса собираются зря.
    private static int densityFrame = -1;
    private static float densityCached = -1f;
    private static bool densityErrorLogged;

    public static float GetDesiredDensity()
    {
        if (Time.frameCount == densityFrame) return densityCached;
        densityFrame = Time.frameCount;

        try
        {
            densityCached = ComputeDesiredDensity();
        }
        catch (Exception e)
        {
            densityCached = -1f;
            if (!densityErrorLogged)
            {
                densityErrorLogged = true;
                Debug.LogWarning("[SilentFog] density: " + e.Message);
            }
        }

        return densityCached;
    }

    private static float ComputeDesiredDensity()
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
            FogObstacles.Reset();
            DistanceFog.FollowLag = true;

            if (DistanceFog.Available) DistanceFog.Apply(0f, 0f, 0f);
            AppliedDensity = FogSettings.OffDensity;
            return FogSettings.OffDensity;
        }

        // «Сила тумана» = 0% должна выключать туман полностью, а не оставлять
        // слабую «домашнюю» дымку через Indoor/interior fallback.
        if (FogSettings.Outdoor <= 0.001f)
        {
            FogObstacles.Reset();
            DistanceFog.FollowLag = true;

            if (DistanceFog.Available) DistanceFog.Apply(0f, 0f, 0f);
            AppliedDensity = 0f;
            return 0f;
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
            // Один раз в кадр: обновляем боксы и укрытие.
            UpdateShelterLatch(player);
            FogObstacles.Update(player, DistanceFog.CamGame,
                FogSettings.DistanceEnd, insideLatched);

            float street = Mathf.Max(FogSettings.Outdoor, 0.05f);
            float protection = Mathf.Clamp01(1f - FogSettings.Indoor / street);

            // Чем выше «Защита помещения», тем дальше уезжает начало тумана
            // под крышей: 100% — туман внутрь не достаёт вообще.
            DistanceFog.Apply(AppliedDensity, ShelterPercent / 100f, protection * 60f);

            // Строку статуса собираем только при открытом меню: 3 вызова
            // за кадр по 4 склейки — лишний мусор для GC.
            if (FogMenuBehaviour.IsOpen)
            {
                CurrentMode = FogObstacles.InsideKind >= 0
                    ? "одеяло: внутри ("
                        + FogObstacles.KindName(FogObstacles.InsideKind) + ") "
                        + Mathf.RoundToInt(FogObstacles.InsideWeight * 100f) + "%"
                    : (FogObstacles.Count > 0
                        ? "одеяло: объёмов " + FogObstacles.Count
                        : "туман по расстоянию");
            }
            return 0f;
        }

        FogObstacles.Reset();
        DistanceFog.FollowLag = true;

        // Режим своего шейдера выключен: гасим пост-эффект, иначе он
        // останется с прошлой силой и сложится с ванильным туманом.
        if (DistanceFog.Available) DistanceFog.Apply(0f, 0f, 0f);

        return AppliedDensity;
    }

    // Гистерезис укрытия: на пороге/у окна укрытие может мигать 0/0.4/1.
    private static void UpdateShelterLatch(EntityPlayerLocal player)
    {
        float shelter = ShelterFactor(player);

        if (shelter > 0.35f)
        {
            insideLatched = true;
            insideClearTimer = 0f;
        }
        else if (shelter < 0.15f)
        {
            insideClearTimer += Time.deltaTime;
            if (insideClearTimer > 0.5f) insideLatched = false;
        }
    }

    // Уличная плотность текущего кадра — для тумана снаружи окон/боксов.
    public static float OutdoorDensity
    {
        get { return outdoorDensity; }
    }

    private static float GetGroundDensity(EntityPlayerLocal player)
    {
        // В доме/подвале туман остаётся, но слабый.
        // В домах с проёмом/балконом isIndoorsCurrent врёт, поэтому
        // дополнительно смотрим, есть ли блок над головой.
        float shelter = ShelterFactor(player);

        // Уличная плотность нужна и для интерьера (маска/стены/трейдер),
        // поэтому считаем её до ранних возвратов.
        float outdoor = FogSettings.Outdoor;
        float storm = AshStorm.EffectiveStrength;
        if (storm > 0.001f)
        {
            outdoor = Mathf.Lerp(outdoor, FogSettings.StormDensity, storm);
        }

        if (ModCompat.DifRUSTInstalled)
        {
            float night = FogSettings.NightAmount();
            if (night > 0.001f)
            {
                outdoor *= Mathf.Lerp(1f, FogSettings.NightSoftening, night);
            }
        }

        outdoorDensity = outdoor;

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
        if (Time.time < traderLatchUntil) return true;

        try
        {
            World world = player.world;
            if (world == null) return false;

            Vector3i pos = World.worldToBlockPos(player.position);
            if (world.IsWithinTraderArea(pos))
            {
                traderLatchUntil = Time.time + 0.5f;
                return true;
            }

            // Protect-прямоугольник уже, чем двор/дорога трейдера. Проверяем
            // весь trader-префаб с запасом 12 м, чтобы сейф-зона ловилась
            // не только внутри забора.
            var areas = world.TraderAreas;
            if (areas != null)
            {
                for (int i = 0; i < areas.Count; i++)
                {
                    TraderArea ta = areas[i];
                    if (ta == null) continue;

                    Vector3i p = ta.Position;
                    Vector3i s = ta.PrefabSize;
                    const float margin = 2f;

                    if (player.position.x < p.x - margin ||
                        player.position.x > p.x + s.x + margin ||
                        player.position.z < p.z - margin ||
                        player.position.z > p.z + s.z + margin)
                        continue;

                    if (player.position.y < p.y - 12f ||
                        player.position.y > p.y + s.y + 20f)
                        continue;

                    traderLatchUntil = Time.time + 0.5f;
                    return true;
                }
            }
        }
        catch
        {
            // Если API трейдеров изменится — остаёмся на IsWithinTraderArea.
        }

        return false;
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
        // НЕ доверяем player.isIndoorsCurrent: под кроной деревьев игра
        // иногда ставит true, и туман начинал вести себя как в доме.
        // Наши лучи по блокам уже игнорируют листву и декорации.
        if (!FogSettings.UseShelter)
        {
            // Кэш обязательно обнуляем: иначе после выключения настройки
            // в Apply продолжает уходить старое «укрытие» и туман уезжает.
            shelterCached = 0f;
            return 0f;
        }

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

            for (int dy = 2; dy <= 16; dy++)
            {
                BlockValue bv = world.GetBlock(p.x, p.y + dy, p.z);
                if (bv.isair) continue;

                // Листва, трава и декорации — не крыша. Иначе в лесу игрок
                // «под крышей», и туман уезжает/прячется за деревьями.
                Block block = bv.Block;
                if (block == null || block.bIsPlant) continue;
                if (block.blockMaterial != null && !block.blockMaterial.IsCollidable)
                    continue;

                covered[i] = true;
                hits++;
                break;
            }
        }

        // Главное правило: если над самим игроком (центральная точка) неба нет,
        // это укрытие. Если центр открыт — мы на улице, даже если края пятна
        // задевают свес крыши или карниз у стены. Исключение — высокий атриум/
        // световой люк: 3+ боковых луча перекрыты, считаем помещением.
        if (!covered[0] && hits < 3)
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
            && world.ChunkCache.ChunkProvider != null
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
    private Rect windowRect = new Rect(60f, 28f, 480f, 840f);

    // Вкладки: вместо одного длинного скролла — три группы настроек.
    private int tab;
    private static readonly string[] TabNames = { "Основное", "Атмосфера", "Диагностика" };

    // Прокрутка тела панели.
    private Vector2 scrollPos;

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

        GUI.Label(new Rect(18f, 12f, w - 70f, 26f), "SILENT FOG", titleStyle);

        if (GUI.Button(new Rect(w - 38f, 10f, 26f, 24f), "x", buttonStyle))
        {
            Close();
            return;
        }

        GUI.DrawTexture(new Rect(18f, 44f, w - 36f, 1f), rowTex, ScaleMode.StretchToFill);

        // --- вкладки: три группы вместо одного длинного скролла ---
        float tabW = (w - 36f - 8f) / TabNames.Length;
        for (int i = 0; i < TabNames.Length; i++)
        {
            float tx = 18f + (tabW + 4f) * i;
            if (GUI.Button(new Rect(tx, 52f, tabW, 26f), TabNames[i], buttonStyle) && tab != i)
            {
                tab = i;
                scrollPos = Vector2.zero;
            }

            if (tab == i)
            {
                GUI.DrawTexture(new Rect(tx, 80f, tabW, 2f), accentTex, ScaleMode.StretchToFill);
            }
        }

        const float bodyTop = 88f;
        const float footer = 54f;

        scrollPos = GUI.BeginScrollView(
            new Rect(0f, bodyTop, w, h - bodyTop - footer),
            scrollPos,
            new Rect(0f, 0f, w - 18f, TabHeight(tab)));

        bool host = FogSettings.IsHost;
        float y = 4f;

        if (tab == 0) y = DrawTabMain(y, w, host);
        else if (tab == 1) y = DrawTabAtmo(y, w, host);
        else y = DrawTabDiag(y, w, host);

        GUI.EndScrollView();

        // Нижняя панель: кнопки не уезжают при скролле и смене вкладки.
        float by = h - 44f;
        GUI.DrawTexture(new Rect(18f, by - 10f, w - 36f, 1f), rowTex, ScaleMode.StretchToFill);

        GUI.enabled = host;
        if (GUI.Button(new Rect(18f, by, 150f, 30f), "Сбросить", buttonStyle))
        {
            FogSettings.ResetToDefaults();
        }
        GUI.enabled = true;

        if (GUI.Button(new Rect(178f, by, w - 196f, 30f), "Закрыть", buttonStyle))
        {
            Close();
            return;
        }

        GUI.DragWindow(new Rect(0f, 0f, w, 46f));
    }

    private static float TabHeight(int t)
    {
        // Запас под содержимое вкладки: скролл не должен обрезать хвост.
        if (t == 0) return 560f;
        if (t == 1) return 700f;
        return 300f;
    }

    // --- вкладка «Основное» ---
    private float DrawTabMain(float y, float w, bool host)
    {
        if (host)
        {
            bool was = FogSettings.Enabled;
            FogSettings.Enabled = GUI.Toggle(
                new Rect(18f, y, w - 36f, 24f), FogSettings.Enabled,
                "  Туман включён", toggleStyle);
            if (was != FogSettings.Enabled) FogSettings.MarkDirty();

            bool wasForce = FogSettings.ForceForClients;
            FogSettings.ForceForClients = GUI.Toggle(
                new Rect(18f, y + 26f, w - 36f, 24f), FogSettings.ForceForClients,
                "  Клиенты не могут отключить туман", toggleStyle);
            if (wasForce != FogSettings.ForceForClients) FogSettings.MarkDirty();
            y += 58f;
        }
        else
        {
            GUI.Label(new Rect(18f, y, w - 36f, 20f),
                "Настройки задаёт хост сервера", hintStyle);
            GUI.Label(new Rect(18f, y + 16f, w - 36f, 20f),
                FogSettings.Synced ? "синхронизация: получена" : "синхронизация: нет",
                FogSettings.Synced ? okStyle : hintStyle);
            y += 44f;
        }

        GUI.enabled = host && FogSettings.Enabled;

        GUI.Label(new Rect(18f, y, w - 36f, 20f), "ПРЕСЕТЫ", sectionStyle);
        y += 22f;

        float bw = (w - 36f - 12f) / 4f;
        if (GUI.Button(new Rect(18f, y, bw, 26f), "Лёгкий", buttonStyle))
            FogSettings.ApplyPreset(0);
        if (GUI.Button(new Rect(18f + bw + 4f, y, bw, 26f), "Средний", buttonStyle))
            FogSettings.ApplyPreset(1);
        if (GUI.Button(new Rect(18f + (bw + 4f) * 2f, y, bw, 26f), "Плотный", buttonStyle))
            FogSettings.ApplyPreset(2);
        if (GUI.Button(new Rect(18f + (bw + 4f) * 3f, y, bw, 26f), "Шторм", buttonStyle))
            FogSettings.ApplyPreset(3);
        y += 34f;

        y = DrawRow(y, w, "Сила тумана", ref FogSettings.Outdoor);
        y = DrawRangeRow(y, w, "Начало тумана, м", ref FogSettings.DistanceStart, 2f, 60f);
        y = DrawRangeRow(y, w, "Конец тумана, м", ref FogSettings.DistanceEnd, 20f, 200f);

        y = DrawProtectionRow(y, w);

        y = DrawRow(y, w, "Закрытый транспорт / Ark", ref FogSettings.ClosedVehicle);
        y = DrawRow(y, w, "Велик / мотоцикл", ref FogSettings.OpenVehicle);

        {
            bool was = FogSettings.DistanceOn;
            GUI.enabled = host && FogSettings.Enabled;
            FogSettings.DistanceOn = DrawToggle(
                new Rect(18f, y, w - 36f, 22f), FogSettings.DistanceOn,
                "Туман по расстоянию (свой шейдер)");
            if (was != FogSettings.DistanceOn) FogSettings.MarkDirty();
        }
        y += 28f;

        {
            bool was = FogSettings.UseShelter;
            GUI.enabled = host && FogSettings.Enabled;
            FogSettings.UseShelter = DrawToggle(
                new Rect(18f, y, w - 36f, 22f), FogSettings.UseShelter,
                "Считать укрытием место под крышей");
            if (was != FogSettings.UseShelter) FogSettings.MarkDirty();
        }
        y += 30f;

        GUI.enabled = true;
        return y;
    }

    // --- вкладка «Атмосфера» ---
    private float DrawTabAtmo(float y, float w, bool host)
    {
        GUI.enabled = host && FogSettings.Enabled;

        GUI.Label(new Rect(18f, y, w - 36f, 18f), "ДЫХАНИЕ ТУМАНА", sectionStyle);
        y += 20f;

        {
            bool was = FogSettings.LiveOn;
            FogSettings.LiveOn = DrawToggle(
                new Rect(18f, y, w - 36f, 22f), FogSettings.LiveOn,
                "Живость тумана (дыхание)");
            if (was != FogSettings.LiveOn) FogSettings.MarkDirty();
        }
        y += 28f;

        {
            bool wasEnabled = GUI.enabled;
            GUI.enabled = host && FogSettings.Enabled && FogSettings.LiveOn;
            y = DrawRow(y, w, "Амплитуда дыхания", ref FogSettings.LiveAmount);
            GUI.enabled = wasEnabled;

            GUI.Label(new Rect(18f, y - 6f, w - 36f, 18f),
                "Туман то гуще, то реже: на 100% ±25% от силы. Идёт от хоста.", hintStyle);
            y += 16f;
        }

        GUI.Label(new Rect(18f, y, w - 36f, 18f), "ДИНАМИКА ТУМАНА", sectionStyle);
        y += 20f;

        {
            bool was = FogSettings.DynamicOn;
            GUI.enabled = host && FogSettings.Enabled;
            FogSettings.DynamicOn = DrawToggle(
                new Rect(18f, y, w - 36f, 22f), FogSettings.DynamicOn,
                "Клочья тумана в мире (ветер)");
            if (was != FogSettings.DynamicOn) FogSettings.MarkDirty();
        }
        y += 28f;

        {
            bool wasEnabled = GUI.enabled;
            GUI.enabled = host && FogSettings.Enabled && FogSettings.DynamicOn;
            y = DrawRow(y, w, "Амплитуда клочьев", ref FogSettings.DynamicAmount);
            y = DrawRow(y, w, "Скорость ветра", ref FogSettings.DynamicSpeed);
            GUI.enabled = wasEnabled;

            GUI.Label(new Rect(18f, y - 6f, w - 36f, 32f),
                "Клочья живут в мире и плывут по ветру: на 100% плотность гуляет примерно на ±22%. Идёт от хоста.",
                hintStyle);
            y += 30f;
        }

        GUI.enabled = host && FogSettings.Enabled;
        y = DrawRow(y, w, "Оттенок биома", ref FogSettings.BiomeTint);

        {
            bool was = FogSettings.HordeFogOn;
            FogSettings.HordeFogOn = DrawToggle(
                new Rect(18f, y, w - 36f, 22f), FogSettings.HordeFogOn,
                "Туман в ночь орды (красный, плотнее)");
            if (was != FogSettings.HordeFogOn) FogSettings.MarkDirty();
        }
        y += 28f;

        {
            bool wasEnabled = GUI.enabled;
            GUI.enabled = host && FogSettings.Enabled && FogSettings.HordeFogOn;
            y = DrawRow(y, w, "Сила тумана орды", ref FogSettings.HordeFog);
            GUI.enabled = wasEnabled;
        }

        // --- пепельная буря ---
        GUI.DrawTexture(new Rect(18f, y, w - 36f, 1f), rowTex, ScaleMode.StretchToFill);
        y += 10f;
        GUI.Label(new Rect(18f, y, w - 36f, 20f), "ПЕПЕЛЬНАЯ БУРЯ · ГОРЕЛЫЙ ЛЕС", sectionStyle);
        y += 24f;

        GUI.enabled = host;
        {
            bool was = FogSettings.StormEnabled;
            FogSettings.StormEnabled = GUI.Toggle(
                new Rect(18f, y, w - 36f, 24f), FogSettings.StormEnabled,
                "  Буря включена", toggleStyle);
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
            hintStyle);

        if (GUI.Button(new Rect(w - 196f, y - 10f, 178f, 26f), "Показать бурю", buttonStyle))
        {
            AshStorm.ForceDemo(45f);
        }
        y += 24f;

        // --- ночь и DifRUST ---
        GUI.DrawTexture(new Rect(18f, y, w - 36f, 1f), rowTex, ScaleMode.StretchToFill);
        y += 10f;

        if (ModCompat.DifRUSTInstalled)
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

        GUI.enabled = true;
        return y;
    }

    // --- вкладка «Диагностика» ---
    private float DrawTabDiag(float y, float w, bool host)
    {
        GUI.Label(new Rect(18f, y, w - 36f, 20f), "СЕЙЧАС", sectionStyle);
        y += 22f;

        GUI.Label(
            new Rect(18f, y, w - 36f, 20f),
            string.Format("{0} · сила {1}% · укрытие {2}%",
                FogDensityLogic.CurrentMode ?? "—",
                Mathf.RoundToInt(Mathf.Clamp01(DistanceFog.ActiveStrength) * 100f),
                Mathf.RoundToInt(FogDensityLogic.ShelterPercent)),
            hintStyle);
        y += 18f;

        int hordeDay = HordeNight.HordeDay(
            GameManager.Instance != null ? GameManager.Instance.World : null);
        string hordeDayText = hordeDay > 0 ? hordeDay.ToString() : "?";

        GUI.Label(
            new Rect(18f, y, w - 36f, 54f),
            string.Format("дыхание {0}% · орда {1}% · день орды {7}\nклочья {2}% · ветер {3:0.0} м/с\nтуман {4:0}–{5:0} м · центр {6:0.0} м",
                Mathf.RoundToInt(Mathf.Clamp01(DistanceFog.LiveFactor) * 100f),
                Mathf.RoundToInt(Mathf.Clamp01(DistanceFog.HordeAmount) * 100f),
                Mathf.RoundToInt(DistanceFog.DynamicLive * 100f),
                DistanceFog.WindSpeed,
                DistanceFog.ActiveStart,
                DistanceFog.ActiveEnd,
                DistanceFog.CenterOffset,
                hordeDayText),
            hintStyle);
        y += 56f;

        GUI.Label(new Rect(18f, y, w - 36f, 20f),
            "Шейдер: " + DistanceFog.Status + " · бандл " + DistanceFog.BundleTag
            + " · частицы: " + DistanceFog.ParticleStatus, hintStyle);
        y += 18f;

        GUI.Label(new Rect(18f, y, w - 36f, 20f),
            string.Format("зоны: статика {0} · база {1} · слоты {2}",
                FogObstacles.StaticCount, FogObstacles.BaseCount, FogObstacles.Count),
            hintStyle);
        y += 20f;

        GUI.Label(new Rect(18f, y, w - 36f, 20f),
            string.Format("цвет: сырой #{0} · итог #{1}",
                ColorUtility.ToHtmlStringRGB(DistanceFog.RawFogColor),
                ColorUtility.ToHtmlStringRGB(DistanceFog.FinalFogColor)),
            hintStyle);
        y += 26f;

        if (host)
        {
            bool wasDebug = FogSettings.DebugMode > 0;
            bool debugNow = DrawToggle(
                new Rect(18f, y, w - 36f, 22f),
                wasDebug,
                "Отладка: зенит (2)");
            if (debugNow != wasDebug)
                FogSettings.DebugMode = debugNow ? 2 : 0;
            y += 30f;
        }

        GUI.Label(new Rect(18f, y, w - 36f, 40f),
            host
                ? "NumPad5 / F7 — открыть меню. Изменения сразу применяются всем игрокам."
                : "NumPad5 / F7 — открыть меню. Менять силу тумана может только хост.",
            hintStyle);
        y += 44f;

        return y;
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