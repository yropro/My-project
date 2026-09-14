using HarmonyLib;
using StarVortex;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Presentation only. Plasma uses explicit groups in the shared multiplexed Orrery
/// bank. Preferred record locations are only packing hints; readers identify the
/// stroke and burn-refresh groups by codec/group headers wherever they were packed.
/// Gameplay spread, damage and reinfection lockout remain owner-authoritative in
/// OrreryPlasmaBolt.
/// </summary>
public static class OrreryPlasmaBoltPresentation
{
    private const int RefreshEntryCount = 6;
    private const float RefreshTimeout = 1.25f;
    private static readonly uint[] targetScratch = new uint[RefreshEntryCount];
    private static readonly byte[] remainingScratch = new byte[RefreshEntryCount];
    private static readonly byte[] kindScratch = new byte[RefreshEntryCount];
    private static readonly OrreryNetwork.Channel<StrokeState> StrokeChannel =
        new OrreryNetwork.Channel<StrokeState>(OrreryPresentationNetwork.CodecPlasmaBolt, WireStroke, 0);
    private static readonly OrreryNetwork.Channel<RefreshState> BurnChannel =
        new OrreryNetwork.Channel<RefreshState>(OrreryPresentationNetwork.CodecPlasmaBolt, WireRefresh, 1);
    private static readonly OrreryNetwork.Channel<RefreshState> ImmolationChannel =
        new OrreryNetwork.Channel<RefreshState>(OrreryPresentationNetwork.CodecPlasmaBolt, WireRefresh, 2);

    internal struct StrokeState { public Vector2 Start, End; public float Width; }
    internal struct RefreshEntry { public uint Target; public byte Remaining; }
    internal struct RefreshState
    {
        public byte Count;
        public RefreshEntry E0, E1, E2, E3, E4, E5;
        public RefreshEntry Get(int index)
        {
            switch (index) { case 0: return E0; case 1: return E1; case 2: return E2;
                case 3: return E3; case 4: return E4; default: return E5; }
        }
        public void Add(uint target, byte remaining)
        {
            var entry = new RefreshEntry { Target = target, Remaining = remaining };
            switch (Count++) { case 0: E0 = entry; break; case 1: E1 = entry; break;
                case 2: E2 = entry; break; case 3: E3 = entry; break;
                case 4: E4 = entry; break; case 5: E5 = entry; break; }
        }
    }
    internal static void WireStroke(ref CoreWire wire, ref StrokeState state)
    {
        wire.Position(ref state.Start); wire.Position(ref state.End); wire.Positive(ref state.Width);
    }
    internal static void WireRefresh(ref CoreWire wire, ref RefreshState state)
    {
        wire.Byte(ref state.Count);
        if (!wire.Ok || state.Count < 1 || state.Count > RefreshEntryCount) { wire.Fail(); return; }
        WireEntry(ref wire, ref state.E0);
        if (state.Count > 1) WireEntry(ref wire, ref state.E1);
        if (state.Count > 2) WireEntry(ref wire, ref state.E2);
        if (state.Count > 3) WireEntry(ref wire, ref state.E3);
        if (state.Count > 4) WireEntry(ref wire, ref state.E4);
        if (state.Count > 5) WireEntry(ref wire, ref state.E5);
    }
    private static void WireEntry(ref CoreWire wire, ref RefreshEntry entry)
    {
        wire.UInt32(ref entry.Target); wire.Byte(ref entry.Remaining);
        if (entry.Target == 0 || entry.Remaining == 0) wire.Fail();
    }

    private static bool strokeGenerationInitialized;
    private static int strokeOwnerInstanceId;
    private static ushort strokeCastSequence;
    private static uint strokeGenerationCounter;
    private static uint burnRefreshGenerationCounter;

    private static readonly FieldInfo bridgeField =
        AccessTools.Field(typeof(NetSession), "activeBridge");

    private sealed class BurnVisual
    {
        public GameShip Target;
        public byte Kind;
        public StatusEffectLayer Layer;
        public float ExpiresAt, RefreshUntil;
    }

    private sealed class RemoteState
    {
        public bool BoltInitialized;
        public uint BoltGeneration;
        public bool BurnRefreshInitialized;
        public uint BurnRefreshGeneration;
        public bool ImmolationRefreshInitialized;
        public uint ImmolationRefreshGeneration;
        public readonly OrreryPlasmaBolt.BoltVisualState Bolt =
            new OrreryPlasmaBolt.BoltVisualState();
        public readonly BurnVisual[] Burns =
            new BurnVisual[OrrerySpellCompendium.PlasmaBolt.MaxActiveInfections];

        public RemoteState()
        {
            for (int i = 0; i < Burns.Length; i++)
                Burns[i] = new BurnVisual();
        }
    }

    private static readonly Dictionary<GameShip, RemoteState> remotes =
        new Dictionary<GameShip, RemoteState>(4);

    public static void Publish()
    {
        CoreOwnerContext context = CoreClassRuntime.CurrentContext;
        GameShip owner = context == null || !context.IsValid ? null : context.Ship;
        if (owner == null || !OrreryRuntime.IsActive(owner))
            return;

        ushort cast;
        Vector2 start, end;
        float width;
        bool bolt;
        int count;
        if (!OrreryPlasmaBolt.ReadPresentation(
                targetScratch,
                remainingScratch,
                kindScratch,
                out cast,
                out start,
                out end,
                out width,
                out bolt,
                out count))
        {
            return;
        }

        if (bolt)
        {
            var stroke = new StrokeState { Start = start, End = end, Width = width };
            StrokeChannel.Publish(ResolveStrokeGeneration(owner, cast), ref stroke);
        }
        var burns = default(RefreshState);
        var immolations = default(RefreshState);
        for (int i = 0; i < count; i++)
        {
            if (kindScratch[i] == 0) burns.Add(targetScratch[i], remainingScratch[i]);
            else immolations.Add(targetScratch[i], remainingScratch[i]);
        }
        uint generation = NextGeneration(ref burnRefreshGenerationCounter);
        if (burns.Count > 0) BurnChannel.Publish(generation, ref burns);
        if (immolations.Count > 0) ImmolationChannel.Publish(generation, ref immolations);
    }

    public static void Tick(GameShip owner)
    {
        if (owner == null || !owner.IsRemotePlayer())
            return;

        uint boltGeneration, burnGeneration, immolationGeneration;
        var stroke = default(StrokeState);
        var burns = default(RefreshState);
        var immolations = default(RefreshState);
        bool hasStroke = StrokeChannel.TryRead(owner, ref stroke, out boltGeneration);
        bool hasBurnRefresh = BurnChannel.TryRead(owner, ref burns, out burnGeneration);
        bool hasImmolationRefresh = ImmolationChannel.TryRead(owner, ref immolations, out immolationGeneration);

        RemoteState state;
        bool hasState = remotes.TryGetValue(owner, out state) && state != null;
        if (!hasState && !hasStroke && !hasBurnRefresh && !hasImmolationRefresh)
            return;

        if (!hasState)
        {
            state = new RemoteState();
            remotes.Add(owner, state);
        }

        if (hasStroke &&
            (!state.BoltInitialized || state.BoltGeneration != boltGeneration))
        {
            state.BoltInitialized = true;
            state.BoltGeneration = boltGeneration;
            OrreryPlasmaBolt.ShowBoltVisual(state.Bolt, owner, stroke.Start, stroke.End, stroke.Width);
        }

        if (hasBurnRefresh && (!state.BurnRefreshInitialized || state.BurnRefreshGeneration != burnGeneration))
        {
            state.BurnRefreshInitialized = true;
            state.BurnRefreshGeneration = burnGeneration;
            ApplyRefresh(state, ref burns, 0);
        }
        if (hasImmolationRefresh && (!state.ImmolationRefreshInitialized || state.ImmolationRefreshGeneration != immolationGeneration))
        {
            state.ImmolationRefreshInitialized = true;
            state.ImmolationRefreshGeneration = immolationGeneration;
            ApplyRefresh(state, ref immolations, 1);
        }

        // The physical presentation bank is multiplexed. A group can be absent
        // from one send because another higher-priority presentation claimed the
        // bounded records, so absence is not allowed to destroy already-observed
        // timer-backed visuals. The bolt and each burn already carry their own
        // authoritative presentation lifetime/refresh backstop.
        OrreryPlasmaBolt.TickBoltVisual(state.Bolt, owner, Time.time);
        for (int i = 0; i < state.Burns.Length; i++)
        {
            BurnVisual burn = state.Burns[i];
            if (burn.Target == null || burn.Target.health <= 0f ||
                Time.time >= burn.ExpiresAt ||
                Time.unscaledTime >= burn.RefreshUntil)
            {
                ClearBurn(burn);
            }
        }

        if (!hasStroke && !hasBurnRefresh && !hasImmolationRefresh &&
            Time.time >= state.Bolt.BoltVisibleUntil &&
            !HasActiveBurns(state))
        {
            Forget(owner);
        }
    }

    private static bool HasActiveBurns(RemoteState state)
    {
        if (state == null)
            return false;

        for (int i = 0; i < state.Burns.Length; i++)
        {
            BurnVisual burn = state.Burns[i];
            if (burn != null && burn.Target != null &&
                Time.time < burn.ExpiresAt &&
                Time.unscaledTime < burn.RefreshUntil)
            {
                return true;
            }
        }
        return false;
    }

    private static void ApplyRefresh(RemoteState state, ref RefreshState refresh, byte kind)
    {
        NetWorldBridge bridge = NetSession.instance == null || bridgeField == null
            ? null : bridgeField.GetValue(NetSession.instance) as NetWorldBridge;
        if (bridge == null) return;
        for (int i = 0; i < refresh.Count; i++)
        {
            RefreshEntry entry = refresh.Get(i);
            GameShip target = bridge.ResolveNetTarget(entry.Target) as GameShip;
            if (target != null && target.health > 0f)
                RefreshBurn(state, target, entry.Remaining * 0.05f, kind);
        }
    }

    private static void RefreshBurn(
        RemoteState state,
        GameShip target,
        float remaining, byte kind)
    {
        BurnVisual available = null;
        for (int i = 0; i < state.Burns.Length; i++)
        {
            BurnVisual burn = state.Burns[i];
            if (object.ReferenceEquals(burn.Target, target) && burn.Kind == kind)
            {
                available = burn;
                break;
            }

            if (available == null &&
                (burn.Target == null ||
                 Time.time >= burn.ExpiresAt ||
                 Time.unscaledTime >= burn.RefreshUntil))
            {
                available = burn;
            }
        }

        if (available == null)
            return;
        if (!object.ReferenceEquals(available.Target, target) || available.Kind != kind)
            ClearBurn(available);

        available.Target = target;
        available.Kind = kind;
        available.ExpiresAt = Time.time + remaining;
        available.RefreshUntil = Time.unscaledTime + RefreshTimeout;
        if (available.Layer == null)
            available.Layer = OrreryPlasmaBolt.SpawnFauxBurnVisual(target);
    }

    private static void ClearBurn(BurnVisual burn)
    {
        if (burn.Layer != null)
        {
            burn.Layer.Deactivate();
            burn.Layer.PoolDestroy();
        }
        burn.Layer = null;
        burn.Target = null;
        burn.ExpiresAt = burn.RefreshUntil = 0f;
    }

    public static void ForgetTarget(GameShip target)
    {
        foreach (RemoteState state in remotes.Values)
        {
            for (int i = 0; i < state.Burns.Length; i++)
            {
                if (object.ReferenceEquals(state.Burns[i].Target, target))
                    ClearBurn(state.Burns[i]);
            }
        }
    }

    public static void Forget(GameShip owner)
    {
        if (object.ReferenceEquals(owner, null))
            return;

        RemoteState state;
        if (!remotes.TryGetValue(owner, out state))
            return;

        OrreryPlasmaBolt.DestroyBoltVisual(state.Bolt);
        for (int i = 0; i < state.Burns.Length; i++)
            ClearBurn(state.Burns[i]);
        remotes.Remove(owner);
    }

    public static void Reset()
    {
        foreach (RemoteState state in remotes.Values)
        {
            OrreryPlasmaBolt.DestroyBoltVisual(state.Bolt);
            for (int i = 0; i < state.Burns.Length; i++)
                ClearBurn(state.Burns[i]);
        }
        remotes.Clear();

        strokeGenerationInitialized = false;
        strokeOwnerInstanceId = 0;
        strokeCastSequence = 0;
        strokeGenerationCounter = 0u;
        burnRefreshGenerationCounter = 0u;
    }

    private static uint ResolveStrokeGeneration(GameShip owner, ushort castSequence)
    {
        int ownerInstanceId = owner == null ? 0 : owner.GetInstanceID();
        if (!strokeGenerationInitialized ||
            strokeOwnerInstanceId != ownerInstanceId ||
            strokeCastSequence != castSequence)
        {
            strokeGenerationInitialized = true;
            strokeOwnerInstanceId = ownerInstanceId;
            strokeCastSequence = castSequence;
            NextGeneration(ref strokeGenerationCounter);
        }
        return strokeGenerationCounter;
    }

    private static uint NextGeneration(ref uint value)
    {
        value++;
        if (value == 0u)
            value = 1u;
        return value;
    }

}
