using StarVortex;
using UnityEngine;

/// <summary>
/// Orrery-owned presentation contract over the current shared ship-state
/// transport. Gameplay remains owner-authoritative. This wrapper intentionally
/// hides the historical CoreNetwork name from all other Orrery systems so
/// the transport can be renamed/factored later without rewriting class logic.
/// </summary>
public static class OrreryNetwork
{
    public const byte SharedSlotId = CoreNetwork.SlotOrrery;
    public const byte PayloadVersion = 2;
    public const int MaxPresentedSatellites = 8;
    private const byte PayloadEndSentinel = 0xA7;

    // Shatterbolt's full four-impact history is encoded as chained signed byte
    // offsets. 2m steps cover the 240m first leg and every <=160m later hop while
    // keeping the entire Orrery slot below CoreNetwork's 32-byte ceiling.
    private const float ShatterboltPositionStepMeters = 2f;
    private const byte ShatterboltFlagPresent = 1 << 0;
    private const byte ShatterboltFlagOrbActive = 1 << 1;
    private const int MaxNetworkedShatterboltImpacts = 4;

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
        public byte ShatterboltCastSequence;
        public byte ShatterboltImpactCount;
        public Vector2 ShatterboltOrbPosition;
        public Vector2 ShatterboltImpact0;
        public Vector2 ShatterboltImpact1;
        public Vector2 ShatterboltImpact2;
        public Vector2 ShatterboltImpact3;

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

        CoreNetwork.SlotWriter writer =
            CoreNetwork.BeginSlot(SharedSlotId);

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

        byte shatterboltFlags = 0;
        if (state.ShatterboltPresent)
            shatterboltFlags |= ShatterboltFlagPresent;
        if (state.ShatterboltOrbActive)
            shatterboltFlags |= ShatterboltFlagOrbActive;

        writer.Byte(shatterboltFlags);
        writer.Byte(state.ShatterboltCastSequence);
        writer.Byte(state.ShatterboltImpactCount);

        Vector2 ownerPosition = owner.transform.position;
        Vector2 orbAnchor = state.ShatterboltImpactCount > 0
            ? state.GetShatterboltImpact(state.ShatterboltImpactCount - 1)
            : ownerPosition;
        Vector2 orbDelta = state.ShatterboltOrbPosition - orbAnchor;
        writer.Byte(EncodeShatterboltDelta(orbDelta.x));
        writer.Byte(EncodeShatterboltDelta(orbDelta.y));

        Vector2 impactAnchor = ownerPosition;
        for (int i = 0; i < MaxNetworkedShatterboltImpacts; i++)
        {
            if (i < state.ShatterboltImpactCount)
            {
                Vector2 impact = state.GetShatterboltImpact(i);
                Vector2 delta = impact - impactAnchor;
                writer.Byte(EncodeShatterboltDelta(delta.x));
                writer.Byte(EncodeShatterboltDelta(delta.y));
                impactAnchor = impact;
            }
            else
            {
                writer.Byte(0);
                writer.Byte(0);
            }
        }

        writer.Byte(PayloadEndSentinel);
        CoreNetwork.EndSlot(writer);
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

        byte shatterboltFlags = reader.Byte();
        state.ShatterboltPresent =
            (shatterboltFlags & ShatterboltFlagPresent) != 0;
        state.ShatterboltOrbActive =
            (shatterboltFlags & ShatterboltFlagOrbActive) != 0;
        state.ShatterboltCastSequence = reader.Byte();
        state.ShatterboltImpactCount = reader.Byte();

        byte orbDeltaX = reader.Byte();
        byte orbDeltaY = reader.Byte();

        Vector2 ownerPosition = remoteOwner.transform.position;
        Vector2 impactAnchor = ownerPosition;
        for (int i = 0; i < MaxNetworkedShatterboltImpacts; i++)
        {
            byte deltaX = reader.Byte();
            byte deltaY = reader.Byte();

            if (i >= state.ShatterboltImpactCount)
                continue;

            Vector2 impact = impactAnchor + new Vector2(
                DecodeShatterboltDelta(deltaX),
                DecodeShatterboltDelta(deltaY));
            state.SetShatterboltImpact(i, impact);
            impactAnchor = impact;
        }

        Vector2 orbAnchor = state.ShatterboltImpactCount > 0
            ? state.GetShatterboltImpact(state.ShatterboltImpactCount - 1)
            : ownerPosition;
        state.ShatterboltOrbPosition = orbAnchor + new Vector2(
            DecodeShatterboltDelta(orbDeltaX),
            DecodeShatterboltDelta(orbDeltaY));

        if (reader.Byte() != PayloadEndSentinel)
        {
            state = default(PresentationState);
            return false;
        }

        if ((byte)state.Phase > (byte)OrreryCastPhase.Invoking ||
            state.FormulaCapacity > MaxPresentedSatellites ||
            state.LockedCount > state.FormulaCapacity ||
            state.ShatterboltImpactCount > MaxNetworkedShatterboltImpacts ||
            (state.ShatterboltOrbActive && !state.ShatterboltPresent))
        {
            state = default(PresentationState);
            return false;
        }

        return true;
    }

    private static byte EncodeShatterboltDelta(float worldDelta)
    {
        float meters = OrreryUnits.WorldToMeters(worldDelta);
        int quantized = Mathf.Clamp(
            Mathf.RoundToInt(meters / ShatterboltPositionStepMeters),
            -127,
            127);
        return unchecked((byte)(sbyte)quantized);
    }

    private static float DecodeShatterboltDelta(byte encoded)
    {
        sbyte quantized = unchecked((sbyte)encoded);
        return quantized *
            ShatterboltPositionStepMeters *
            OrreryUnits.WorldUnitsPerMeter;
    }
}
