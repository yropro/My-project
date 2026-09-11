using StarVortex;
using System;
using System.Collections.Generic;

public enum OrreryElement : byte
{
    None = 0,
    Fire = 1,
    Ice = 2,
    Lightning = 3,

    // Reserved generic fourth element slot. It intentionally has no fantasy
    // name until the class design chooses one.
    Fourth = 4
}

public enum OrreryCastPhase : byte
{
    Idle = 0,
    Assembling = 1,
    Ready = 2,
    Invoking = 3
}

/// <summary>
/// Canonical unordered elemental composition. Counts, not lock order, define a
/// spell. Eight 4-bit counters allow future elements while remaining a compact
/// value type suitable for dictionary keys and networking.
/// </summary>
public struct OrreryRecipeKey : IEquatable<OrreryRecipeKey>
{
    public uint PackedCounts;

    public bool IsValid { get { return PackedCounts != 0u; } }

    public int RuneCount
    {
        get
        {
            int total = 0;
            for (int i = 0; i < 8; i++)
                total += (int)((PackedCounts >> (i * 4)) & 0x0Fu);
            return total;
        }
    }

    public int GetCount(OrreryElement element)
    {
        int id = (int)element;
        if (id <= 0 || id > 8)
            return 0;

        int shift = (id - 1) * 4;
        return (int)((PackedCounts >> shift) & 0x0Fu);
    }

    public OrreryRecipeKey Add(OrreryElement element)
    {
        int id = (int)element;
        if (id <= 0 || id > 8)
            return this;

        int shift = (id - 1) * 4;
        uint count = (PackedCounts >> shift) & 0x0Fu;
        if (count >= 0x0Fu)
            return this;

        OrreryRecipeKey next = this;
        next.PackedCounts += 1u << shift;
        return next;
    }

    public bool Equals(OrreryRecipeKey other)
    {
        return PackedCounts == other.PackedCounts;
    }

    public override bool Equals(object obj)
    {
        return obj is OrreryRecipeKey && Equals((OrreryRecipeKey)obj);
    }

    public override int GetHashCode()
    {
        return unchecked((int)PackedCounts);
    }

    public override string ToString()
    {
        return "F" + GetCount(OrreryElement.Fire) +
            " I" + GetCount(OrreryElement.Ice) +
            " L" + GetCount(OrreryElement.Lightning) +
            " X" + GetCount(OrreryElement.Fourth);
    }

    public static OrreryRecipeKey Pure(OrreryElement element, int count)
    {
        OrreryRecipeKey key = default(OrreryRecipeKey);
        for (int i = 0; i < count; i++)
            key = key.Add(element);
        return key;
    }
}

public struct OrreryCastInvocation
{
    public GameShip Owner;
    public OrreryRecipeKey Recipe;
    public int Sequence;
    public int LockedCount;
    public int RequiredCount;
    public bool Complete;
    public byte LastLockedSatelliteId;
    public ushort LockedMask;
    public uint PackedElementsBySatellite;
}

/// <summary>
/// Owner-authoritative rune assembly state. It knows nothing about input
/// bindings, spell implementations, orbit motion or tree node names.
/// </summary>
public static class OrreryCasting
{
    public const int MaxFormulaSatellites = 8;

    private sealed class RuntimeState
    {
        public readonly byte[] LockedSatelliteIds = new byte[MaxFormulaSatellites];
        public readonly OrreryElement[] Elements = new OrreryElement[MaxFormulaSatellites];
        public int RequiredCount;
        public int LockedCount;
        public int Sequence;
        public OrreryCastPhase Phase;
        public OrreryRecipeKey Recipe;
        public byte LastLockedSatelliteId;
        public ushort LastInvokedSpellId;
    }

    private static readonly Dictionary<GameShip, RuntimeState> states =
        new Dictionary<GameShip, RuntimeState>(4);

    public static void Begin(GameShip owner, int requiredFormulaPieces)
    {
        if (owner == null)
            return;

        RuntimeState state;
        if (!states.TryGetValue(owner, out state) || state == null)
        {
            state = new RuntimeState();
            states[owner] = state;
        }

        state.RequiredCount = Math.Max(
            1,
            Math.Min(MaxFormulaSatellites, requiredFormulaPieces));
        ReleaseAllLocks(owner, state);
        state.LastInvokedSpellId = 0;
        state.Phase = OrreryCastPhase.Assembling;
    }

    /// <summary>
    /// Samples an exact satellite angle at lock time. Lock order is deliberately
    /// supplied by the caller because inner->outer vs outer->inner is unresolved
    /// class design, not casting-framework policy.
    /// </summary>
    public static bool TryLock(
        GameShip owner,
        byte satelliteId,
        float orbitalAngleDegrees,
        out OrreryElement capturedElement,
        out bool formulaComplete)
    {
        capturedElement = OrreryElement.None;
        formulaComplete = false;

        RuntimeState state;
        if (owner == null || satelliteId == 0 ||
            !states.TryGetValue(owner, out state) || state == null)
        {
            return false;
        }

        if (state.Phase == OrreryCastPhase.Invoking ||
            state.LockedCount >= state.RequiredCount)
        {
            return false;
        }

        OrrerySatellites.SatelliteContext satellite;
        if (!OrrerySatellites.TryGetSatellite(owner, satelliteId, out satellite) ||
            satellite == null || satellite.Disabled ||
            satellite.Kind != OrrerySatellites.SatelliteKind.Formula ||
            IsLocked(state, satelliteId))
        {
            return false;
        }

        OrreryRuntime.ResolvedState resolved = OrreryRuntime.GetResolvedState(owner);
        if (resolved == null || !resolved.Active || resolved.Sectors == null)
            return false;

        capturedElement = resolved.Sectors.Resolve(orbitalAngleDegrees);
        if (capturedElement == OrreryElement.None)
            return false;

        int index = state.LockedCount;
        state.LockedSatelliteIds[index] = satelliteId;
        state.Elements[index] = capturedElement;
        state.LockedCount++;
        state.LastLockedSatelliteId = satelliteId;
        state.Recipe = state.Recipe.Add(capturedElement);
        state.Phase = state.LockedCount >= state.RequiredCount
            ? OrreryCastPhase.Ready
            : OrreryCastPhase.Assembling;

        OrrerySatellites.ApplyCastingLock(
            owner,
            satelliteId,
            true,
            capturedElement);

        formulaComplete = state.Phase == OrreryCastPhase.Ready;
        return true;
    }

    /// <summary>
    /// Creates an immutable invocation snapshot. The caller decides whether a
    /// partial manual invocation is legal; V0 has not finalized that policy.
    /// </summary>
    public static bool TryInvoke(
        GameShip owner,
        bool allowPartial,
        out OrreryCastInvocation invocation)
    {
        invocation = default(OrreryCastInvocation);

        RuntimeState state;
        if (owner == null || !states.TryGetValue(owner, out state) || state == null ||
            state.LockedCount <= 0 || state.Phase == OrreryCastPhase.Invoking)
        {
            return false;
        }

        bool complete = state.LockedCount >= state.RequiredCount;
        if (!complete && !allowPartial)
            return false;

        state.Sequence++;
        if (state.Sequence <= 0)
            state.Sequence = 1;
        state.Phase = OrreryCastPhase.Invoking;

        invocation.Owner = owner;
        invocation.Recipe = state.Recipe;
        invocation.Sequence = state.Sequence;
        invocation.LockedCount = state.LockedCount;
        invocation.RequiredCount = state.RequiredCount;
        invocation.Complete = complete;
        invocation.LastLockedSatelliteId = state.LastLockedSatelliteId;
        BuildPackedPresentation(state, out invocation.LockedMask,
            out invocation.PackedElementsBySatellite);

        OrrerySpellRegistry.SpellDefinition spell;
        state.LastInvokedSpellId =
            OrrerySpellRegistry.TryResolve(state.Recipe, out spell) && spell != null
                ? spell.Id
                : (ushort)0;

        return true;
    }

    /// <summary>
    /// Finishes invocation and releases all formula locks except an optional
    /// retained satellite. Passing 0 implements baseline V0; Continuity can later
    /// retain exactly one without changing recipe assembly internals.
    /// </summary>
    public static void CompleteInvocation(GameShip owner, byte retainedSatelliteId)
    {
        RuntimeState state;
        if (owner == null || !states.TryGetValue(owner, out state) || state == null)
            return;

        if (retainedSatelliteId == 0 || !IsLocked(state, retainedSatelliteId))
        {
            ReleaseAllLocks(owner, state);
            state.Phase = OrreryCastPhase.Assembling;
            return;
        }

        OrreryElement retainedElement = OrreryElement.None;
        for (int i = 0; i < state.LockedCount; i++)
        {
            byte id = state.LockedSatelliteIds[i];
            if (id == retainedSatelliteId)
            {
                retainedElement = state.Elements[i];
                continue;
            }

            OrrerySatellites.ApplyCastingLock(owner, id, false, OrreryElement.None);
        }

        Array.Clear(state.LockedSatelliteIds, 0, state.LockedSatelliteIds.Length);
        Array.Clear(state.Elements, 0, state.Elements.Length);
        state.LockedSatelliteIds[0] = retainedSatelliteId;
        state.Elements[0] = retainedElement;
        state.LockedCount = 1;
        state.Recipe = default(OrreryRecipeKey).Add(retainedElement);
        state.LastLockedSatelliteId = retainedSatelliteId;
        state.Phase = state.RequiredCount <= 1
            ? OrreryCastPhase.Ready
            : OrreryCastPhase.Assembling;
    }

    public static void Cancel(GameShip owner)
    {
        RuntimeState state;
        if (owner == null || !states.TryGetValue(owner, out state) || state == null)
            return;

        ReleaseAllLocks(owner, state);
        state.Phase = OrreryCastPhase.Assembling;
    }

    internal static void OnSatelliteUnavailable(GameShip owner, byte satelliteId)
    {
        RuntimeState state;
        if (owner == null || satelliteId == 0 ||
            !states.TryGetValue(owner, out state) || state == null)
        {
            return;
        }

        int removeIndex = -1;
        for (int i = 0; i < state.LockedCount; i++)
        {
            if (state.LockedSatelliteIds[i] == satelliteId)
            {
                removeIndex = i;
                break;
            }
        }

        if (removeIndex < 0)
            return;

        for (int i = removeIndex; i < state.LockedCount - 1; i++)
        {
            state.LockedSatelliteIds[i] = state.LockedSatelliteIds[i + 1];
            state.Elements[i] = state.Elements[i + 1];
        }

        state.LockedCount--;
        state.LockedSatelliteIds[state.LockedCount] = 0;
        state.Elements[state.LockedCount] = OrreryElement.None;
        RebuildRecipe(state);
        state.LastLockedSatelliteId = state.LockedCount > 0
            ? state.LockedSatelliteIds[state.LockedCount - 1]
            : (byte)0;
        if (state.Phase != OrreryCastPhase.Invoking)
        {
            state.Phase = state.LockedCount >= state.RequiredCount
                ? OrreryCastPhase.Ready
                : OrreryCastPhase.Assembling;
        }
    }

    internal static bool TryGetPresentation(
        GameShip owner,
        out OrreryCastPhase phase,
        out int requiredCount,
        out int lockedCount,
        out int sequence,
        out ushort spellId,
        out byte lastLockedSatelliteId,
        out ushort lockedMask,
        out uint packedElements)
    {
        phase = OrreryCastPhase.Idle;
        requiredCount = 0;
        lockedCount = 0;
        sequence = 0;
        spellId = 0;
        lastLockedSatelliteId = 0;
        lockedMask = 0;
        packedElements = 0u;

        RuntimeState state;
        if (owner == null || !states.TryGetValue(owner, out state) || state == null)
            return false;

        phase = state.Phase;
        requiredCount = state.RequiredCount;
        lockedCount = state.LockedCount;
        sequence = state.Sequence;
        spellId = state.LastInvokedSpellId;
        lastLockedSatelliteId = state.LastLockedSatelliteId;
        BuildPackedPresentation(state, out lockedMask, out packedElements);
        return true;
    }

    public static OrreryRecipeKey GetCurrentRecipe(GameShip owner)
    {
        RuntimeState state;
        return owner != null && states.TryGetValue(owner, out state) && state != null
            ? state.Recipe
            : default(OrreryRecipeKey);
    }

    public static int GetLockedCount(GameShip owner)
    {
        RuntimeState state;
        return owner != null && states.TryGetValue(owner, out state) && state != null
            ? state.LockedCount
            : 0;
    }

    public static void Forget(GameShip owner)
    {
        RuntimeState state;
        if (owner != null && states.TryGetValue(owner, out state) && state != null)
            ReleaseAllLocks(owner, state);

        if (owner != null)
            states.Remove(owner);
    }

    public static void Reset()
    {
        states.Clear();
    }

    private static bool IsLocked(RuntimeState state, byte satelliteId)
    {
        for (int i = 0; i < state.LockedCount; i++)
        {
            if (state.LockedSatelliteIds[i] == satelliteId)
                return true;
        }
        return false;
    }

    private static void ReleaseAllLocks(GameShip owner, RuntimeState state)
    {
        for (int i = 0; i < state.LockedCount; i++)
        {
            byte id = state.LockedSatelliteIds[i];
            if (id != 0)
                OrrerySatellites.ApplyCastingLock(owner, id, false, OrreryElement.None);
        }

        Array.Clear(state.LockedSatelliteIds, 0, state.LockedSatelliteIds.Length);
        Array.Clear(state.Elements, 0, state.Elements.Length);
        state.LockedCount = 0;
        state.Recipe = default(OrreryRecipeKey);
        state.LastLockedSatelliteId = 0;
    }

    private static void RebuildRecipe(RuntimeState state)
    {
        OrreryRecipeKey recipe = default(OrreryRecipeKey);
        for (int i = 0; i < state.LockedCount; i++)
            recipe = recipe.Add(state.Elements[i]);
        state.Recipe = recipe;
    }

    private static void BuildPackedPresentation(
        RuntimeState state,
        out ushort lockedMask,
        out uint packedElements)
    {
        lockedMask = 0;
        packedElements = 0u;

        for (int i = 0; i < state.LockedCount; i++)
        {
            int id = state.LockedSatelliteIds[i];
            if (id <= 0 || id > MaxFormulaSatellites)
                continue;

            int satelliteIndex = id - 1;
            lockedMask |= (ushort)(1 << satelliteIndex);

            uint element = (uint)state.Elements[i] & 0x07u;
            packedElements |= element << (satelliteIndex * 3);
        }
    }
}
