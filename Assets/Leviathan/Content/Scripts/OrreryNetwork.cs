using HarmonyLib;
using StarVortex;

/// <summary>
/// Orrery-owned presentation contract over the current shared ship-state
/// transport. Gameplay remains owner-authoritative. This wrapper intentionally
/// hides the historical LeviathanNetwork name from all other Orrery systems so
/// the transport can be renamed/factored later without rewriting class logic.
/// </summary>
public static class OrreryNetwork
{
    public const byte SharedSlotId = 6;
    public const byte PayloadVersion = 1;
    public const int MaxPresentedSatellites = 8;
    private const byte PayloadEndSentinel = 0xA7;

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
        return true;
    }

    public static void PublishLocal(GameShip owner)
    {
        PresentationState state;
        if (!TryBuildLocalState(owner, out state))
            return;

        LeviathanNetwork.SlotWriter writer =
            LeviathanNetwork.BeginSlot(SharedSlotId);

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
        LeviathanNetwork.EndSlot(writer);
    }

    public static bool TryReadRemote(
        GameShip remoteOwner,
        out PresentationState state)
    {
        state = default(PresentationState);

        LeviathanNetwork.SlotReader reader;
        if (remoteOwner == null ||
            !LeviathanNetwork.TryReadSlot(remoteOwner, SharedSlotId, out reader))
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

        if (reader.Byte() != PayloadEndSentinel)
        {
            state = default(PresentationState);
            return false;
        }

        if ((byte)state.Phase > (byte)OrreryCastPhase.Invoking ||
            state.FormulaCapacity > MaxPresentedSatellites ||
            state.LockedCount > state.FormulaCapacity)
        {
            state = default(PresentationState);
            return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(NetWorldBridge), "Teardown")]
public static class OrreryNetworkTeardownPatch
{
    public static void Postfix()
    {
        OrreryRuntime.Reset();
    }
}
