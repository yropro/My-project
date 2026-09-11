using StarVortex;
using System;
using System.Collections.Generic;

/// <summary>
/// Canonical Orrery satellite identity/publication boundary.
/// Builders publish intended and live satellite sets here. Consumers query this
/// API rather than inferring ownership from names, squadron order or prefab ids.
/// </summary>
public static class OrrerySatellites
{
    public enum SatelliteKind : byte
    {
        Formula = 1,
        Metamagic = 2,
        Resonance = 3,
        Other = 255
    }

    public struct IntentEntry
    {
        public byte SatelliteId;
        public SatelliteKind Kind;
        public float OrbitRadiusMeters;
        public float AngularSpeedDegreesPerSecond;

        public IntentEntry(byte satelliteId, SatelliteKind kind,
            float orbitRadiusMeters, float angularSpeedDegreesPerSecond)
        {
            SatelliteId = satelliteId;
            Kind = kind;
            OrbitRadiusMeters = orbitRadiusMeters;
            AngularSpeedDegreesPerSecond = angularSpeedDegreesPerSecond;
        }
    }

    public sealed class SatelliteIntent
    {
        private readonly IntentEntry[] entries;
        public readonly int Revision;
        public int Count { get { return entries.Length; } }

        internal SatelliteIntent(int revision, IntentEntry[] source)
        {
            Revision = revision;
            entries = source ?? new IntentEntry[0];
        }

        public IntentEntry Get(int index)
        {
            if (index < 0 || index >= entries.Length)
                throw new ArgumentOutOfRangeException("index");
            return entries[index];
        }
    }

    public struct PublishEntry
    {
        public GameShip Ship;
        public byte SatelliteId;
        public SatelliteKind Kind;
        public float OrbitRadiusMeters;
        public float AngularSpeedDegreesPerSecond;

        public PublishEntry(GameShip ship, byte satelliteId, SatelliteKind kind,
            float orbitRadiusMeters, float angularSpeedDegreesPerSecond)
        {
            Ship = ship;
            SatelliteId = satelliteId;
            Kind = kind;
            OrbitRadiusMeters = orbitRadiusMeters;
            AngularSpeedDegreesPerSecond = angularSpeedDegreesPerSecond;
        }
    }

    public sealed class SatelliteContext
    {
        public GameShip Owner { get; internal set; }
        public GameShip Ship { get; internal set; }
        public byte SatelliteId { get; internal set; }
        public SatelliteKind Kind { get; internal set; }
        public float OrbitRadiusMeters { get; internal set; }
        public float AngularSpeedDegreesPerSecond { get; internal set; }
        public bool Disabled { get; internal set; }
        public bool Locked { get; internal set; }
        public OrreryElement CapturedElement { get; internal set; }

        public CoreCombat.ContributorKey Contributor
        {
            get { return OrreryCombat.Satellite(SatelliteId); }
        }
    }

    public sealed class SatelliteSnapshot
    {
        private readonly SatelliteContext[] satellites;
        public readonly GameShip Owner;
        public readonly int Revision;
        public int Count { get { return satellites.Length; } }

        internal SatelliteSnapshot(GameShip owner, int revision,
            SatelliteContext[] source)
        {
            Owner = owner;
            Revision = revision;
            satellites = source ?? new SatelliteContext[0];
        }

        public SatelliteContext Get(int index)
        {
            if (index < 0 || index >= satellites.Length)
                throw new ArgumentOutOfRangeException("index");
            return satellites[index];
        }
    }

    private static readonly Dictionary<GameShip, SatelliteIntent> intentsByOwner =
        new Dictionary<GameShip, SatelliteIntent>(4);
    private static readonly Dictionary<GameShip, SatelliteSnapshot> snapshotsByOwner =
        new Dictionary<GameShip, SatelliteSnapshot>(4);
    private static readonly Dictionary<GameShip, SatelliteContext> contextBySatellite =
        new Dictionary<GameShip, SatelliteContext>(24);
    private static readonly HashSet<byte> idValidation = new HashSet<byte>();
    private static readonly HashSet<GameShip> shipValidation = new HashSet<GameShip>();
    private static int revision;

    public static int Revision { get { return revision; } }

    public static bool PublishIntent(GameShip owner, IList<IntentEntry> entries)
    {
        if (owner == null || entries == null)
            return false;

        idValidation.Clear();
        IntentEntry[] copy = new IntentEntry[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            IntentEntry entry = entries[i];
            if (entry.SatelliteId == 0 || !idValidation.Add(entry.SatelliteId))
            {
                idValidation.Clear();
                return false;
            }
            copy[i] = entry;
        }
        idValidation.Clear();

        revision++;
        intentsByOwner[owner] = new SatelliteIntent(revision, copy);
        return true;
    }

    public static SatelliteIntent GetIntent(GameShip owner)
    {
        SatelliteIntent intent;
        return owner != null && intentsByOwner.TryGetValue(owner, out intent)
            ? intent : null;
    }

    public static void InvalidateIntent(GameShip owner)
    {
        if (owner != null && intentsByOwner.Remove(owner))
            revision++;
    }

    public static bool PublishLiveSatellites(GameShip owner,
        IList<PublishEntry> entries)
    {
        if (owner == null || entries == null)
            return false;

        idValidation.Clear();
        shipValidation.Clear();
        SatelliteContext[] next = new SatelliteContext[entries.Count];

        for (int i = 0; i < entries.Count; i++)
        {
            PublishEntry entry = entries[i];
            if (entry.Ship == null || entry.SatelliteId == 0 ||
                !idValidation.Add(entry.SatelliteId) ||
                !shipValidation.Add(entry.Ship))
            {
                idValidation.Clear();
                shipValidation.Clear();
                return false;
            }

            SatelliteContext context = new SatelliteContext();
            context.Owner = owner;
            context.Ship = entry.Ship;
            context.SatelliteId = entry.SatelliteId;
            context.Kind = entry.Kind;
            context.OrbitRadiusMeters = entry.OrbitRadiusMeters;
            context.AngularSpeedDegreesPerSecond = entry.AngularSpeedDegreesPerSecond;
            next[i] = context;
        }

        idValidation.Clear();
        shipValidation.Clear();
        RemoveSatelliteMappings(owner);
        revision++;
        SatelliteSnapshot snapshot = new SatelliteSnapshot(owner, revision, next);
        snapshotsByOwner[owner] = snapshot;

        for (int i = 0; i < next.Length; i++)
            contextBySatellite[next[i].Ship] = next[i];

        return true;
    }

    public static SatelliteSnapshot GetSnapshot(GameShip owner)
    {
        SatelliteSnapshot snapshot;
        return owner != null && snapshotsByOwner.TryGetValue(owner, out snapshot)
            ? snapshot : null;
    }

    public static bool TryGetSatelliteContext(GameShip ship, out GameShip owner,
        out SatelliteContext context)
    {
        owner = null;
        context = null;
        if (ship == null || !contextBySatellite.TryGetValue(ship, out context) ||
            context == null)
        {
            context = null;
            return false;
        }

        owner = context.Owner;
        return owner != null;
    }

    public static bool TryGetSatellite(GameShip owner, byte satelliteId,
        out SatelliteContext context)
    {
        context = null;
        if (owner == null || satelliteId == 0)
            return false;

        SatelliteSnapshot snapshot;
        if (!snapshotsByOwner.TryGetValue(owner, out snapshot) || snapshot == null)
            return false;

        for (int i = 0; i < snapshot.Count; i++)
        {
            SatelliteContext candidate = snapshot.Get(i);
            if (candidate != null && candidate.SatelliteId == satelliteId)
            {
                context = candidate;
                return true;
            }
        }
        return false;
    }

    public static void CollectSatellites(GameShip owner, List<GameShip> output)
    {
        if (output == null)
            return;
        SatelliteSnapshot snapshot = GetSnapshot(owner);
        if (snapshot == null)
            return;
        for (int i = 0; i < snapshot.Count; i++)
        {
            SatelliteContext context = snapshot.Get(i);
            if (context != null && context.Ship != null)
                output.Add(context.Ship);
        }
    }

    public static bool SetDisabled(GameShip satelliteShip, bool disabled)
    {
        SatelliteContext context;
        GameShip owner;
        if (!TryGetSatelliteContext(satelliteShip, out owner, out context))
            return false;
        if (context.Disabled == disabled)
            return true;

        context.Disabled = disabled;
        if (disabled)
        {
            context.Locked = false;
            context.CapturedElement = OrreryElement.None;
            OrreryCasting.OnSatelliteUnavailable(owner, context.SatelliteId);
        }
        return true;
    }

    internal static bool ApplyCastingLock(GameShip owner, byte satelliteId,
        bool locked, OrreryElement capturedElement)
    {
        SatelliteContext context;
        if (!TryGetSatellite(owner, satelliteId, out context) || context == null)
            return false;
        if (locked && context.Disabled)
            return false;

        context.Locked = locked;
        context.CapturedElement = locked ? capturedElement : OrreryElement.None;
        return true;
    }

    public static ushort GetDisabledMask(GameShip owner, int maxSatelliteId)
    {
        int limit = Math.Max(0, Math.Min(16, maxSatelliteId));
        ushort mask = 0;
        SatelliteSnapshot snapshot = GetSnapshot(owner);
        if (snapshot == null)
            return mask;

        for (int i = 0; i < snapshot.Count; i++)
        {
            SatelliteContext context = snapshot.Get(i);
            if (context == null || !context.Disabled)
                continue;
            int id = context.SatelliteId;
            if (id > 0 && id <= limit)
                mask |= (ushort)(1 << (id - 1));
        }
        return mask;
    }

    public static void InvalidateLiveSatellites(GameShip owner)
    {
        if (owner == null)
            return;
        bool removed = snapshotsByOwner.ContainsKey(owner);
        RemoveSatelliteMappings(owner);
        snapshotsByOwner.Remove(owner);
        if (removed)
            revision++;
    }

    public static void Reset()
    {
        intentsByOwner.Clear();
        snapshotsByOwner.Clear();
        contextBySatellite.Clear();
        idValidation.Clear();
        shipValidation.Clear();
        revision++;
    }

    private static void RemoveSatelliteMappings(GameShip owner)
    {
        SatelliteSnapshot existing;
        if (!snapshotsByOwner.TryGetValue(owner, out existing) || existing == null)
            return;

        for (int i = 0; i < existing.Count; i++)
        {
            SatelliteContext context = existing.Get(i);
            if (context == null || context.Ship == null)
                continue;
            contextBySatellite.Remove(context.Ship);
            CoreCombat.ReleaseContributorObject(context.Ship);
        }
    }
}
