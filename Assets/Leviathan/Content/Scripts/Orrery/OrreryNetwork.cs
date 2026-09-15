using StarVortex;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Orrery-owned presentation contract over the shared Core ship-state transport.
/// Gameplay remains owner-authoritative. Base casting/satellite state keeps its
/// dedicated Core slot; transient spell presentation is delegated to explicit
/// spell codecs over OrreryPresentationNetwork's six-record bank.
/// </summary>
public static class OrreryNetwork
{
    private struct TimedEffectState { public float RemainingSeconds; }
    private static readonly Channel<TimedEffectState> coldFusionChannel =
        new Channel<TimedEffectState>(OrreryPresentationNetwork.CodecColdFusion,
            WireTimedEffect);
    private static readonly System.Collections.Generic.Dictionary<GameShip, uint> coldFusionRevisions =
        new System.Collections.Generic.Dictionary<GameShip, uint>();

    private static void WireTimedEffect(ref CoreWire wire, ref TimedEffectState state)
    {
        wire.Positive(ref state.RemainingSeconds);
    }

    internal static void PublishTimedEffects(GameShip owner)
    {
        uint revision; float remaining;
        if (!CoreTimedShipEffects.TryGetPresentation(owner, OrreryColdFusion.TimedEffectId,
                out revision, out remaining)) return;
        var state = new TimedEffectState { RemainingSeconds = remaining };
        coldFusionChannel.Publish(revision, ref state);
    }

    private static void RenderTimedEffects(GameShip owner, float deltaTime)
    {
        var state = default(TimedEffectState);
        uint revision, previous;
        if (!coldFusionChannel.TryRead(owner, ref state, out revision)) return;
        if (coldFusionRevisions.TryGetValue(owner, out previous) && previous == revision) return;
        coldFusionRevisions[owner] = revision;
        OrreryColdFusionPresentation.Show(owner, state.RemainingSeconds);
    }

    /// <summary>A typed presentation channel. Define one static channel per
    /// codec, then supply one format method for both directions. Buffers are
    /// reused on Unity's main thread; callbacks must not reenter the channel.
    /// Generation is the identity of the cast, not a packet sequence.</summary>
    public sealed class Channel<T> where T : struct
    {
        private readonly byte codecId;
        private readonly byte groupId;
        private readonly CoreWireFormat<T> format;
        private readonly byte[] outgoing = new byte[116];
        private readonly byte[] incoming = new byte[116];

        public Channel(byte codecId, CoreWireFormat<T> format, byte groupId = 0)
        {
            if (codecId == 0 || groupId > OrreryPresentationNetwork.MaximumGroupId || format == null)
                throw new System.ArgumentException("Invalid Orrery presentation channel.");
            this.codecId = codecId;
            this.groupId = groupId;
            this.format = format;
        }

        public bool Publish(uint generation, ref T state)
        {
            int length;
            if (!CoreWire.TryEncode(outgoing, 0, outgoing.Length, format, ref state, out length))
                return false;
            int parts = 1;
            while (parts < OrreryPresentationNetwork.MaximumPartsPerGroup &&
                length > OrreryPresentationNetwork.GetPayloadCapacity(parts)) parts++;
            return OrreryPresentationNetwork.WriteGroup(0, codecId, groupId,
                parts, generation, outgoing, length);
        }

        public bool TryRead(GameShip owner, ref T state, out uint generation)
        {
            generation = 0;
            for (int parts = 1; parts <= OrreryPresentationNetwork.MaximumPartsPerGroup; parts++)
            {
                OrreryPresentationNetwork.GroupReader group;
                if (!OrreryPresentationNetwork.TryReadGroup(owner, 0, codecId, groupId, parts, out group))
                    continue;
                if (group.Length > incoming.Length || group.Generation == 0 ||
                    (parts > 1 && group.Length <=
                        OrreryPresentationNetwork.GetPayloadCapacity(parts - 1))) return false;
                for (int i = 0; i < group.Length; i++) incoming[i] = group.Byte();
                if (!CoreWire.TryDecode(incoming, 0, group.Length, format, ref state)) return false;
                generation = group.Generation;
                return true;
            }
            return false;
        }
    }

    private static bool initialized;

    /// <summary>The Orrery networking composition root. New presentation codecs
    /// register here; they do not patch engine send/render/destruction methods.</summary>
    public static void Initialize()
    {
        if (initialized) return;
        OrreryPresentationNetwork.EnsureInitialized();
        OrreryAccretionDisk.EnsureInitialized();
        CoreNetworkPresentation.Register("Orrery/send",
            publish: OrreryPresentationNetwork.PublishForSend);
        CoreNetworkPresentation.Register("Orrery/Magma-Tesla-Cryo",
            render: OrreryLegacySpellRemotePresentation.Tick,
            forget: OrreryLegacySpellRemotePresentation.Forget,
            reset: OrreryLegacySpellRemotePresentation.Reset);
        CoreNetworkPresentation.Register("Orrery/Shatterbolt",
            render: OrreryShatterboltRemotePresentation.Tick,
            forget: OrreryShatterboltRemotePresentation.Forget,
            reset: OrreryShatterboltRemotePresentation.Reset);
        CoreNetworkPresentation.Register("Orrery/Plasma",
            render: RenderPlasma,
            forget: ForgetPlasma,
            reset: OrreryPlasmaBoltPresentation.Reset);
        CoreNetworkPresentation.Register("Orrery/sectors",
            render: (owner, dt) => OrreryRemoteSectorPresentation.Tick(owner),
            forget: OrreryRemoteSectorPresentation.Forget,
            reset: () => OrrerySectorPresentation.Hide());
        CoreCrossOwnerEffects.RegisterObserver(OrreryColdFusion.CrossOwnerEffectId,
            OrreryColdFusionPresentationLease.ObserveGrant);
        CoreTimedShipEffects.RegisterPresentation(OrreryColdFusion.TimedEffectId,
            OrreryColdFusionPresentation.Show);
        CoreNetworkPresentation.Register("Orrery/ColdFusion",
            render: RenderTimedEffects,
            update: OrreryColdFusionPresentation.Tick,
            forget: owner => { coldFusionRevisions.Remove(owner); OrreryColdFusionPresentation.Hide(owner); },
            died: owner => OrreryColdFusionPresentation.Hide(owner),
            reset: () => { coldFusionRevisions.Clear(); OrreryColdFusionPresentation.Reset(); });
        CoreNetworkPresentation.Register("Orrery/ColdFusion-lease",
            update: dt => OrreryColdFusionPresentationLease.Tick(),
            forget: OrreryColdFusionPresentationLease.OnShipDestroyed,
            died: OrreryColdFusionPresentationLease.OnShipDied,
            reset: OrreryColdFusionPresentationLease.Reset);
        CoreNetworkPresentation.Register("Orrery/AccretionDisk",
            render: OrreryAccretionDiskPresentationLease.Render,
            update: OrreryAccretionDiskPresentation.Tick,
            forget: OrreryAccretionDiskPresentationLease.Forget,
            died: OrreryAccretionDiskPresentationLease.Died,
            reset: OrreryAccretionDiskPresentationLease.Reset);
        initialized = true;
    }

    private static void RenderPlasma(GameShip owner, float deltaTime)
    {
        CoreNetwork.SlotReader common;
        if (!CoreNetwork.TryReadSlot(owner, SharedSlotId, out common) ||
            common.Byte() != PayloadVersion)
        {
            OrreryPlasmaBoltPresentation.Forget(owner);
            return;
        }
        OrreryPlasmaBoltPresentation.Tick(owner);
    }

    private static void ForgetPlasma(GameShip owner)
    {
        OrreryPlasmaBoltPresentation.ForgetTarget(owner);
        OrreryPlasmaBoltPresentation.Forget(owner);
    }

    public const byte SharedSlotId = CoreNetwork.SlotOrrery;
    public const byte PayloadVersion = 3;
    public const int MaxPresentedSatellites = 8;

    private const byte PayloadEndSentinel = 0xA7;
    private const int MaxNetworkedShatterboltImpacts =
        OrrerySpellCompendium.Shatterbolt.MaximumImpacts;

    public struct PresentationState
    {
        public bool Active;
        public OrreryCastPhase Phase;
        public byte FormulaCapacity;
        public byte LockedCount;
        public byte InvocationSequence;
        public ushort SpellId;
        public byte LastLockedSatelliteId;
        public ushort LockedMask;
        public ushort DisabledMask;
        public uint PackedElementsBySatellite;

        public bool ShatterboltPresent;
        public bool ShatterboltOrbActive;
        public uint ShatterboltGeneration;
        public byte ShatterboltCastSequence;
        public byte ShatterboltImpactCount;
        public Vector2 ShatterboltOrbPosition;
        public float ShatterboltExplosionRadiusMeters;
        public Vector2 ShatterboltImpact0;
        public Vector2 ShatterboltImpact1;
        public Vector2 ShatterboltImpact2;
        public Vector2 ShatterboltImpact3;
        public Vector2 ShatterboltImpact4;
        public Vector2 ShatterboltImpact5;
        public Vector2 ShatterboltImpact6;
        public Vector2 ShatterboltImpact7;
        public Vector2 ShatterboltImpact8;
        public Vector2 ShatterboltImpact9;

        public OrreryElement GetElement(byte satelliteId)
        {
            if (satelliteId == 0 || satelliteId > MaxPresentedSatellites)
                return OrreryElement.None;

            int shift = (satelliteId - 1) * 3;
            return (OrreryElement)((PackedElementsBySatellite >> shift) & 0x07u);
        }

        public bool IsLocked(byte satelliteId)
        {
            return satelliteId > 0 && satelliteId <= MaxPresentedSatellites &&
                (LockedMask & (1 << (satelliteId - 1))) != 0;
        }

        public bool IsDisabled(byte satelliteId)
        {
            return satelliteId > 0 && satelliteId <= MaxPresentedSatellites &&
                (DisabledMask & (1 << (satelliteId - 1))) != 0;
        }

        public Vector2 GetShatterboltImpact(int index)
        {
            switch (index)
            {
                case 0: return ShatterboltImpact0;
                case 1: return ShatterboltImpact1;
                case 2: return ShatterboltImpact2;
                case 3: return ShatterboltImpact3;
                case 4: return ShatterboltImpact4;
                case 5: return ShatterboltImpact5;
                case 6: return ShatterboltImpact6;
                case 7: return ShatterboltImpact7;
                case 8: return ShatterboltImpact8;
                case 9: return ShatterboltImpact9;
                default: return Vector2.zero;
            }
        }

        public void SetShatterboltImpact(int index, Vector2 position)
        {
            switch (index)
            {
                case 0: ShatterboltImpact0 = position; break;
                case 1: ShatterboltImpact1 = position; break;
                case 2: ShatterboltImpact2 = position; break;
                case 3: ShatterboltImpact3 = position; break;
                case 4: ShatterboltImpact4 = position; break;
                case 5: ShatterboltImpact5 = position; break;
                case 6: ShatterboltImpact6 = position; break;
                case 7: ShatterboltImpact7 = position; break;
                case 8: ShatterboltImpact8 = position; break;
                case 9: ShatterboltImpact9 = position; break;
            }
        }
    }

    public static bool TryBuildLocalState(
        GameShip owner,
        out PresentationState state)
    {
        state = default(PresentationState);
        if (owner == null || !OrreryRuntime.IsActive(owner))
            return false;

        OrreryCastPhase phase;
        int requiredCount;
        int lockedCount;
        int sequence;
        ushort spellId;
        byte lastLockedSatelliteId;
        ushort lockedMask;
        uint packedElements;

        if (!OrreryCasting.TryGetPresentation(
            owner,
            out phase,
            out requiredCount,
            out lockedCount,
            out sequence,
            out spellId,
            out lastLockedSatelliteId,
            out lockedMask,
            out packedElements))
        {
            return false;
        }

        state.Active = true;
        state.Phase = phase;
        state.FormulaCapacity = (byte)requiredCount;
        state.LockedCount = (byte)lockedCount;
        state.InvocationSequence = (byte)(sequence & 0xFF);
        state.SpellId = spellId;
        state.LastLockedSatelliteId = lastLockedSatelliteId;
        state.LockedMask = lockedMask;
        state.DisabledMask = OrrerySatellites.GetDisabledMask(
            owner,
            MaxPresentedSatellites);
        state.PackedElementsBySatellite = packedElements;

        OrreryShatterbolt.PresentationSnapshot shatterbolt;
        if (OrreryShatterbolt.TryGetPresentation(owner, out shatterbolt) &&
            shatterbolt.Present)
        {
            state.ShatterboltPresent = true;
            state.ShatterboltOrbActive = shatterbolt.OrbActive;
            state.ShatterboltCastSequence = shatterbolt.CastSequence;
            state.ShatterboltImpactCount = (byte)Mathf.Clamp(
                shatterbolt.ImpactCount,
                0,
                MaxNetworkedShatterboltImpacts);
            state.ShatterboltOrbPosition = shatterbolt.OrbPosition;
            state.ShatterboltExplosionRadiusMeters = shatterbolt.ExplosionRadiusMeters;

            for (int i = 0; i < state.ShatterboltImpactCount; i++)
                state.SetShatterboltImpact(i, shatterbolt.GetImpact(i));
        }

        return true;
    }

    public static void PublishLocal(GameShip owner)
    {
        PresentationState state;
        if (!TryBuildLocalState(owner, out state))
            return;

        CoreNetwork.SlotWriter writer = CoreNetwork.BeginSlot(SharedSlotId);
        writer.Byte(PayloadVersion);
        writer.Byte((byte)state.Phase);
        writer.Byte(state.FormulaCapacity);
        writer.Byte(state.LockedCount);
        writer.Byte(state.InvocationSequence);
        writer.Byte((byte)(state.SpellId & 0xFF));
        writer.Byte((byte)(state.SpellId >> 8));
        writer.Byte(state.LastLockedSatelliteId);
        writer.Byte((byte)(state.LockedMask & 0xFF));
        writer.Byte((byte)(state.LockedMask >> 8));
        writer.Byte((byte)(state.DisabledMask & 0xFF));
        writer.Byte((byte)(state.DisabledMask >> 8));
        writer.Byte((byte)(state.PackedElementsBySatellite & 0xFF));
        writer.Byte((byte)((state.PackedElementsBySatellite >> 8) & 0xFF));
        writer.Byte((byte)((state.PackedElementsBySatellite >> 16) & 0xFF));
        writer.Byte(PayloadEndSentinel);
        CoreNetwork.EndSlot(writer);

        if (state.ShatterboltPresent)
            OrreryShatterboltPresentationCodec.Publish(owner, state);
    }

    public static bool TryReadRemote(
        GameShip remoteOwner,
        out PresentationState state)
    {
        state = default(PresentationState);

        CoreNetwork.SlotReader reader;
        if (remoteOwner == null ||
            !CoreNetwork.TryReadSlot(remoteOwner, SharedSlotId, out reader))
        {
            return false;
        }

        byte version = reader.Byte();
        if (version != PayloadVersion)
            return false;

        state.Active = true;
        state.Phase = (OrreryCastPhase)reader.Byte();
        state.FormulaCapacity = reader.Byte();
        state.LockedCount = reader.Byte();
        state.InvocationSequence = reader.Byte();
        state.SpellId = (ushort)(reader.Byte() | (reader.Byte() << 8));
        state.LastLockedSatelliteId = reader.Byte();
        state.LockedMask = (ushort)(reader.Byte() | (reader.Byte() << 8));
        state.DisabledMask = (ushort)(reader.Byte() | (reader.Byte() << 8));
        state.PackedElementsBySatellite =
            (uint)reader.Byte() |
            ((uint)reader.Byte() << 8) |
            ((uint)reader.Byte() << 16);

        if (reader.Byte() != PayloadEndSentinel ||
            (byte)state.Phase > (byte)OrreryCastPhase.Invoking ||
            state.FormulaCapacity > MaxPresentedSatellites ||
            state.LockedCount > state.FormulaCapacity)
        {
            state = default(PresentationState);
            return false;
        }

        // Spell presentation is deliberately isolated. A malformed/missing bank
        // group suppresses only that spell's VFX, never otherwise-valid Orrery
        // satellite/casting presentation.
        OrreryShatterboltPresentationCodec.TryRead(remoteOwner, ref state);
        return true;
    }

    internal static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    internal static bool IsFinite(Vector2 value)
    {
        return IsFinite(value.x) && IsFinite(value.y);
    }

    // Explicit bit reinterpretation keeps serialization allocation-free on the
    // game's .NET runtime. Wire order is little-endian on every platform.
    [StructLayout(LayoutKind.Explicit)]
    private struct FloatBits
    {
        [FieldOffset(0)] public float Value;
        [FieldOffset(0)] public uint Bits;
    }

    internal static void WriteFloat(ref CoreNetwork.SlotWriter writer, float value)
    {
        FloatBits bits = new FloatBits { Value = value };
        writer.Byte((byte)bits.Bits);
        writer.Byte((byte)(bits.Bits >> 8));
        writer.Byte((byte)(bits.Bits >> 16));
        writer.Byte((byte)(bits.Bits >> 24));
    }

    internal static float ReadFloat(ref CoreNetwork.SlotReader reader)
    {
        uint bits = (uint)reader.Byte() | ((uint)reader.Byte() << 8) |
            ((uint)reader.Byte() << 16) | ((uint)reader.Byte() << 24);
        return new FloatBits { Bits = bits }.Value;
    }

    internal static void WritePosition(ref CoreNetwork.SlotWriter writer, Vector2 position)
    {
        WriteFloat(ref writer, position.x);
        WriteFloat(ref writer, position.y);
    }

    internal static Vector2 ReadPosition(ref CoreNetwork.SlotReader reader)
    {
        return new Vector2(ReadFloat(ref reader), ReadFloat(ref reader));
    }
}
