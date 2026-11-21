using System;
using System.Collections.Generic;
using UnityEngine;

// Единое поле препятствий: до 6 AABB вокруг игрока.
// Боксы не «включаются» по факту нахождения игрока внутри — они живут
// по дистанции и плавно проявляются/гаснут. Поэтому при выходе из дома
// старый пласт не сбрасывается: просто меняется вес слота.
internal static class FogObstacles
{
    public const int Max = 6;
    public const int KTrader = 0;
    public const int KPoi = 1;
    public const int KAnchor = 2;
    public const int KBase = 3;

    public static readonly Vector4[] BoxMin = new Vector4[Max];
    public static readonly Vector4[] BoxMax = new Vector4[Max];
    public static int Count;
    public static float InsideWeight;
    public static int InsideKind = -1;

    // Полуширина мягкой кромки, м. Полная кромка = 2 * edge.
    private static readonly float[] KindEdge = { 3.0f, 2.5f, 2.0f, 2.0f };
    // Приоритет при борьбе за слот: торговец важнее дома.
    private static readonly float[] KindPrio = { 12f, 0f, 20f, 20f };

    private sealed class Zone
    {
        public long id;
        public int kind;
        public Vector3 min;
        public Vector3 max;
    }

    private struct Cand
    {
        public Zone zone;
        public float dn;
        public float score;
    }

    private sealed class Slot
    {
        public long id = -1;
        public int kind;
        public Vector3 min;
        public Vector3 max;
        public Vector3 goalMin;
        public Vector3 goalMax;
        public float weight;
        public float target;
        public float score;
        public bool evict;
    }

    private static readonly Slot[] slots = new Slot[Max];
    private static readonly List<Zone> statics = new List<Zone>();
    private static readonly List<Zone> bigPoi = new List<Zone>();
    private static readonly List<Zone> dyn = new List<Zone>();
    private static readonly List<Cand> cands = new List<Cand>(64);

    private static int staticKey = -1;
    private static int baseKey = -1;
    private static float nextScan;
    private static int lastFrame = -1;
    private static float inW;

    private const int BaseCandCap = 64;       // стартовая ёмкость списков
    private const float BaseRange = 600f;     // дальше этого claim'ы не рассматриваем
    private const float BasePad = 3f;         // чистая зона на 3 м наружу от claim

    private struct BaseCand
    {
        public Vector3i pos;
        public float d2;
    }

    private static readonly List<BaseCand> baseCands = new List<BaseCand>(BaseCandCap);
    private static readonly HashSet<Vector3i> claimSeen = new HashSet<Vector3i>();
    private static readonly Comparison<BaseCand> baseCmp =
        (a, b) => a.d2.CompareTo(b.d2);

    private static int HashV(int x, int y, int z)
    {
        unchecked
        {
            int h = x * 73856093 ^ y * 19349663 ^ z * 83492791;
            h ^= h >> 13;
            h *= 0x5bd1e995;
            h ^= h >> 15;
            return h;
        }
    }

    private static void AddClaim(Vector3 pp, Vector3i c)
    {
        if (!claimSeen.Add(c)) return;         // дедупликация
        float dx = c.x - pp.x;
        float dz = c.z - pp.z;
        float d2 = dx * dx + dz * dz;
        if (d2 > BaseRange * BaseRange) return;
        baseCands.Add(new BaseCand { pos = c, d2 = d2 });
    }

    static FogObstacles()
    {
        for (int i = 0; i < Max; i++) slots[i] = new Slot();
    }

    public static string KindName(int kind)
    {
        switch (kind)
        {
            case KTrader: return "торговец";
            case KPoi: return "дом/POI";
            case KAnchor: return "якорь POI";
            case KBase: return "база";
            default: return "объём";
        }
    }

    public static void Reset()
    {
        for (int i = 0; i < Max; i++)
        {
            Slot s = slots[i];
            s.id = -1;
            s.kind = 0;
            s.weight = 0f;
            s.target = 0f;
            s.score = 0f;
            s.evict = false;
            BoxMin[i] = Vector4.zero;
            BoxMax[i] = Vector4.zero;
        }

        statics.Clear();
        bigPoi.Clear();
        dyn.Clear();
        cands.Clear();
        staticKey = -1;
        baseKey = -1;
        nextScan = 0f;
        lastFrame = -1;
        inW = 0f;
        Count = 0;
        InsideWeight = 0f;
        InsideKind = -1;
    }

    // Вызывать раз в кадр до DistanceFog.Apply.
    public static void Update(EntityPlayerLocal player, Vector3 cam,
        float fogEnd, bool sheltered)
    {
        if (player == null || player.world == null) return;

        // GetDesiredDensity может зваться несколько раз за кадр.
        if (Time.frameCount == lastFrame) return;
        lastFrame = Time.frameCount;

        if (Time.time >= nextScan)
        {
            nextScan = Time.time + 0.2f;
            Scan(player, cam, fogEnd, sheltered);
        }

        Step(cam, Time.deltaTime);
    }

    private static void Scan(EntityPlayerLocal player, Vector3 cam,
        float fogEnd, bool sheltered)
    {
        EnsureStatics(player.world);
        UpdateBaseZones(player);
        UpdateAnchors(player, cam, fogEnd, sheltered);

        cands.Clear();
        float range = fogEnd + 16f;
        AddRange(statics, cam, range);
        AddRange(dyn, cam, range);

        for (int i = 0; i < cands.Count; i++)
        {
            Cand c = cands[i];
            // Уже занятый слот получает бонус: набор не дёргается на границе.
            c.score = c.dn - KindPrio[c.zone.kind]
                - (FindSlot(c.zone.id) >= 0 ? 10f : 0f);
            cands[i] = c;
        }
        cands.Sort((a, b) => a.score.CompareTo(b.score));

        for (int i = 0; i < Max; i++) slots[i].target = 0f;

        for (int i = 0; i < cands.Count; i++)
        {
            Cand c = cands[i];

            // Гасим только там, где туман уже насыщен: End+6..End+16.
            float t = 1f - S((c.dn - (fogEnd + 6f)) / 10f);
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;

            int si = FindSlot(c.zone.id);
            if (si >= 0)
            {
                Slot s = slots[si];
                s.goalMin = c.zone.min;
                s.goalMax = c.zone.max;
                s.score = c.score;
                if (!s.evict) s.target = t;
            }
            else if (t > 0.01f)
            {
                int free = -1;
                for (int k = 0; k < Max; k++)
                {
                    if (slots[k].id < 0) { free = k; break; }
                }

                if (free >= 0)
                {
                    Slot s = slots[free];
                    s.id = c.zone.id;
                    s.kind = c.zone.kind;
                    s.min = s.goalMin = c.zone.min;
                    s.max = s.goalMax = c.zone.max;
                    s.weight = 0f;
                    s.target = t;
                    s.evict = false;
                    s.score = c.score;
                }
                else
                {
                    // Все слоты заняты: вытесняем худший, только если новый
                    // заметно лучше (15 м запаса).
                    Slot worst = null;
                    for (int k = 0; k < Max; k++)
                    {
                        Slot s = slots[k];
                        if (!s.evict && (worst == null || s.score > worst.score))
                            worst = s;
                    }

                    if (worst != null && c.score + 15f < worst.score)
                    {
                        worst.evict = true;
                        worst.target = 0f;
                    }
                }
            }
        }
    }

    private static void AddRange(List<Zone> list, Vector3 cam, float range)
    {
        for (int i = 0; i < list.Count; i++)
        {
            Zone z = list[i];
            if (cam.y > z.max.y + 40f || cam.y < z.min.y - 40f) continue;
            float dn = HDist(cam, z.min, z.max);
            if (dn < range)
                cands.Add(new Cand { zone = z, dn = dn });
        }
    }

    private static void Step(Vector3 cam, float dt)
    {
        int n = 0;
        float inTarget = 0f;
        int inKind = -1;

        for (int i = 0; i < Max; i++)
        {
            Slot s = slots[i];
            if (s.id < 0) continue;

            float sdBefore = BoxSdf(cam, s.min, s.max);
            bool inside = sdBefore < 1f;

            // Внутри бокса вес набирается быстро, чтобы не было секунды
            // тумана в комнате при входе. Снаружи — обычный fade.
            float speed = s.target > s.weight
                ? (inside ? 5f : 1f)
                : 0.5f;
            s.weight = Mathf.MoveTowards(s.weight, s.target, dt * speed);

            s.min = Vector3.MoveTowards(s.min, s.goalMin, 3f * dt);
            s.max = Vector3.MoveTowards(s.max, s.goalMax, 3f * dt);

            if (s.target <= 0f && s.weight < 0.003f)
            {
                s.id = -1;
                s.evict = false;
                s.weight = 0f;
                continue;
            }

            float w = S(s.weight);
            BoxMin[n] = new Vector4(s.min.x, s.min.y, s.min.z, w);
            BoxMax[n] = new Vector4(s.max.x, s.max.y, s.max.z, KindEdge[s.kind]);
            n++;

            float sd = BoxSdf(cam, s.min, s.max);
            float wi = w * (1f - S((sd + 1f) / 6f));
            if (wi > inTarget)
            {
                inTarget = wi;
                inKind = s.kind;
            }
        }

        Count = n;
        for (int i = n; i < Max; i++)
        {
            BoxMin[i] = Vector4.zero;
            BoxMax[i] = Vector4.zero;
        }

        inW = Mathf.MoveTowards(inW, inTarget,
            dt * (inTarget > inW ? 4f : 0.8f));
        InsideWeight = inW;
        InsideKind = inW > 0.05f ? inKind : -1;
    }

    // --- Статика: торговцы и POI ---

    private static void EnsureStatics(World world)
    {
        if (world == null) return;

        DynamicPrefabDecorator dec = world.ChunkCache != null
            && world.ChunkCache.ChunkProvider != null
            ? world.ChunkCache.ChunkProvider.GetDynamicPrefabDecorator()
            : null;
        int poiCount = dec != null && dec.poiPrefabs != null
            ? dec.poiPrefabs.Count : 0;
        int trCount = world.TraderAreas != null ? world.TraderAreas.Count : 0;
        int key = poiCount * 4096 + trCount;
        if (key == staticKey) return;
        staticKey = key;

        statics.Clear();
        bigPoi.Clear();

        if (world.TraderAreas != null)
        {
            for (int i = 0; i < world.TraderAreas.Count; i++)
            {
                TraderArea ta = world.TraderAreas[i];
                if (ta == null || ta.PrefabSize.x <= 0 || ta.PrefabSize.z <= 0)
                    continue;

                Vector3i p = ta.Position;
                Vector3i s = ta.PrefabSize;
                Vector3 mn = new Vector3(
                    p.x - 0.5f, p.y - 2f, p.z - 0.5f);
                Vector3 mx = new Vector3(
                    p.x + s.x + 0.5f,
                    p.y + Mathf.Max(s.y, 8) + 3f,
                    p.z + s.z + 0.5f);
                statics.Add(MakeZone(KTrader, mn, mx));
            }
        }

        if (dec != null && dec.poiPrefabs != null)
        {
            for (int i = 0; i < dec.poiPrefabs.Count; i++)
            {
                PrefabInstance pi = dec.poiPrefabs[i];
                if (pi == null) continue;

                Vector3i p = pi.boundingBoxPosition;
                Vector3i s = pi.boundingBoxSize;
                if (s.x <= 0 || s.z <= 0) continue;

                Vector3 mn = new Vector3(
                    p.x - 0.5f, p.y, p.z - 0.5f);
                Vector3 mx = new Vector3(
                    p.x + s.x + 0.5f,
                    p.y + Mathf.Max(s.y, 4) + 2f,
                    p.z + s.z + 0.5f);

                bool compact = Mathf.Max(s.x, s.z) <= 40
                    && s.x * s.z <= 1100;
                Zone z = MakeZone(KPoi, mn, mx);
                (compact ? statics : bigPoi).Add(z);
            }
        }
    }

    // --- Своя база: land claim / bedroll ---

    private static void UpdateBaseZones(EntityPlayerLocal player)
    {
        int key = 0;
        List<Zone> found = new List<Zone>(BaseCandCap);

        try
        {
            GameManager gm = GameManager.Instance;
            PersistentPlayerList players = gm != null ? gm.persistentPlayers : null;

            if (players != null)
            {
                Vector3 pp = player.position;
                claimSeen.Clear();
                baseCands.Clear();

                // 1) Все claim'ы всех игроков.
                var map = players.m_lpBlockMap;
                if (map != null)
                {
                    foreach (var kv in map)
                        AddClaim(pp, kv.Key);
                }

                // 2) Свои claim'ы отдельно — подстраховка, если карта пуста.
                PersistentPlayerData me =
                    players.GetPlayerDataFromEntityID(player.entityId);
                if (me != null)
                {
                    List<Vector3i> own = me.GetLandProtectionBlocks();
                    if (own != null)
                    {
                        for (int i = 0; i < own.Count; i++)
                            AddClaim(pp, own[i]);
                    }
                }

                if (baseCands.Count > 0)
                {
                    baseCands.Sort(baseCmp);   // ближайшие первыми
                    int half = Mathf.Max(8,
                        GameStats.GetInt(EnumGameStats.LandClaimSize) / 2);
                    int n = baseCands.Count;

                    unchecked
                    {
                        for (int i = 0; i < n; i++)
                        {
                            Vector3i c = baseCands[i].pos;
                            // Сумма хешей не зависит от порядка → стабильный ключ.
                            key += HashV(c.x, c.y, c.z);

                            Vector3 mn = new Vector3(
                                c.x - half - BasePad, c.y - 4f,
                                c.z - half - BasePad);
                            Vector3 mx = new Vector3(
                                c.x + half + BasePad, c.y + 16f,
                                c.z + half + BasePad);
                            found.Add(MakeZone(KBase, mn, mx));
                        }
                        key = key * 31 + n;
                    }
                }
                else if (me != null && me.HasBedrollPos)
                {
                    // Bedroll — только если claim'ов нет вообще.
                    Vector3i b = me.BedrollPos;
                    unchecked { key = HashV(b.x, b.y, b.z) + 0x2545F491; }

                    Vector3 mn = new Vector3(
                        b.x - 6f, b.y - 3f, b.z - 6f);
                    Vector3 mx = new Vector3(
                        b.x + 6f, b.y + 8f, b.z + 6f);
                    found.Add(MakeZone(KBase, mn, mx));
                }
            }
        }
        catch
        {
            // API земли может отличаться в другой версии игры — без базы.
        }

        if (key == -1) key = -2;      // -1 зарезервирован как «ещё не считали»
        if (key == baseKey) return;
        baseKey = key;

        dyn.RemoveAll(z => z.kind == KBase);
        dyn.AddRange(found);
    }

    // --- Большие POI: статичный якорь вокруг игрока ---

    private static void UpdateAnchors(EntityPlayerLocal player, Vector3 cam,
        float fogEnd, bool sheltered)
    {
        for (int i = dyn.Count - 1; i >= 0; i--)
        {
            Zone z = dyn[i];
            if (z.kind == KAnchor &&
                HDist(cam, z.min, z.max) > fogEnd + 20f)
                dyn.RemoveAt(i);
        }

        if (!sheltered) return;

        Vector3 p = player.position;
        if (Containing(statics, p, 0f) != null) return;
        if (Containing(dyn, p, 0f) != null) return;

        Zone big = Containing(bigPoi, p, 2f);
        if (big == null) return;

        float x0 = Mathf.Max(p.x - 11f, big.min.x);
        float z0 = Mathf.Max(p.z - 11f, big.min.z);
        float x1 = Mathf.Min(p.x + 11f, big.max.x);
        float z1 = Mathf.Min(p.z + 11f, big.max.z);
        if (x1 - x0 < 4f || z1 - z0 < 4f) return;

        Vector3 mn = new Vector3(x0, p.y - 3f, z0);
        Vector3 mx = new Vector3(x1, p.y + 8f, z1);
        dyn.Add(MakeZone(KAnchor, mn, mx));

        int count = 0;
        int oldest = -1;
        for (int i = 0; i < dyn.Count; i++)
        {
            if (dyn[i].kind != KAnchor) continue;
            if (oldest < 0) oldest = i;
            count++;
        }
        if (count > 3) dyn.RemoveAt(oldest);
    }

    // --- Геометрия ---

    private static Zone MakeZone(int kind, Vector3 mn, Vector3 mx)
    {
        long h = 17;
        h = h * 31 + kind;
        h = h * 31 + Mathf.RoundToInt(mn.x * 10f);
        h = h * 31 + Mathf.RoundToInt(mn.y * 10f);
        h = h * 31 + Mathf.RoundToInt(mn.z * 10f);
        h = h * 31 + Mathf.RoundToInt(mx.x * 10f);
        h = h * 31 + Mathf.RoundToInt(mx.y * 10f);
        h = h * 31 + Mathf.RoundToInt(mx.z * 10f);
        if (h == 0) h = 1;

        return new Zone { id = h, kind = kind, min = mn, max = mx };
    }

    private static float S(float x)
    {
        x = Mathf.Clamp01(x);
        return x * x * (3f - 2f * x);
    }

    private static float HDist(Vector3 p, Vector3 mn, Vector3 mx)
    {
        float dx = Mathf.Max(Mathf.Max(mn.x - p.x, p.x - mx.x), 0f);
        float dz = Mathf.Max(Mathf.Max(mn.z - p.z, p.z - mx.z), 0f);
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private static float BoxSdf(Vector3 p, Vector3 mn, Vector3 mx)
    {
        Vector3 c = (mn + mx) * 0.5f;
        Vector3 h = (mx - mn) * 0.5f;
        float qx = Mathf.Abs(p.x - c.x) - h.x;
        float qy = Mathf.Abs(p.y - c.y) - h.y;
        float qz = Mathf.Abs(p.z - c.z) - h.z;
        float ox = Mathf.Max(qx, 0f);
        float oy = Mathf.Max(qy, 0f);
        float oz = Mathf.Max(qz, 0f);
        return Mathf.Sqrt(ox * ox + oy * oy + oz * oz)
            + Mathf.Min(Mathf.Max(qx, Mathf.Max(qy, qz)), 0f);
    }

    private static Zone Containing(List<Zone> list, Vector3 p, float margin)
    {
        for (int i = 0; i < list.Count; i++)
        {
            Zone z = list[i];
            if (p.x > z.min.x - margin && p.x < z.max.x + margin &&
                p.y > z.min.y - margin && p.y < z.max.y + margin &&
                p.z > z.min.z - margin && p.z < z.max.z + margin)
                return z;
        }
        return null;
    }

    private static int FindSlot(long id)
    {
        for (int i = 0; i < Max; i++)
        {
            if (slots[i].id == id) return i;
        }
        return -1;
    }
}
