using StarVortex;
using UnityEngine;

/// <summary>
/// Orrery-owned presentation contract over the shared Core ship-state transport.
/// Gameplay remains owner-authoritative. Base casting/satellite state has its own
/// slot; spell-specific transient presentation lives in a second bounded slot so
/// individual spells cannot consume the entire class casting budget.
/// </summary>
public static class OrreryNetwork
{
    public const byte SharedSlotId = CoreNetwork.SlotOrrery;
    public const byte PayloadVersion = 3;
    public const int MaxPresentedSatellites = 8;

    // Core's default dynamic slots currently occupy 1-6. Orrery owns slot 6 for
    // class casting state and claims 7 for spell-specific presentation payloads.
    // Keep this stable once shipped.
    private const byte SpellPresentationSlotId = 7;
    private const byte SpellPayloadVersion = 1;
    private const byte PayloadEndSentinel = 0xA7;
    private const byte SpellPayloadEndSentinel = 0xB7;

    // Impact history uses chained signed-byte deltas at 3m precision. Canonical
    // Shatterbolt focus rolls can extend the 240m first-leg range to 276m and the
    // 160m chain range to 200m, both comfortably inside +/-381m. The moving orb
    // uses signed 16-bit deltas at 2m precision so a mid-flight reacquisition can
    // never clamp merely because it moved farther than one normal leg from the
    // most recent impact anchor.
    private const float ShatterboltImpactStepMeters = 3f;
    private const float ShatterboltOrbStepMeters = 2f;
    private const byte ShatterboltFlagPresent = 1 << 0;
    private const byte ShatterboltFlagOrbActive = 1 << 1;
    private const int MaxNetworkedShatterboltImpacts =
        OrrerySpellCompendium.Shatterbolt.MaximumImpacts;

    static OrreryNetwork()
    {
        CoreNetwork.RegisterSlot(
            SpellPresentationSlotId,
            CoreClassId.Orrery,
            "Orrery spell presentation");
    }

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
            PublishShatterbolt(owner, state);
    }

    private static void PublishShatterbolt(
        GameShip owner,
        PresentationState state)
    {
        CoreNetwork.SlotWriter writer =
            CoreNetwork.BeginSlot(SpellPresentationSlotId);

        byte flags = ShatterboltFlagPresent;
        if (state.ShatterboltOrbActive)
            flags |= ShatterboltFlagOrbActive;

        writer.Byte(SpellPayloadVersion);
        writer.Byte(flags);
        writer.Byte(state.ShatterboltCastSequence);
        writer.Byte(state.ShatterboltImpactCount);

        Vector2 ownerPosition = owner.transform.position;
        Vector2 orbAnchor = state.ShatterboltImpactCount > 0
            ? state.GetShatterboltImpact(state.ShatterboltImpactCount - 1)
            : ownerPosition;
        Vector2 orbDelta = state.ShatterboltOrbPosition - orbAnchor;
        WriteSigned16(
            ref writer,
            EncodeSigned16Delta(orbDelta.x, ShatterboltOrbStepMeters));
        WriteSigned16(
            ref writer,
            EncodeSigned16Delta(orbDelta.y, ShatterboltOrbStepMeters));

        Vector2 impactAnchor = ownerPosition;
        for (int i = 0; i < MaxNetworkedShatterboltImpacts; i++)
        {
            if (i < state.ShatterboltImpactCount)
            {
                Vector2 impact = state.GetShatterboltImpact(i);
                Vector2 delta = impact - impactAnchor;
                writer.Byte(EncodeSignedByteDelta(
                    delta.x,
                    ShatterboltImpactStepMeters));
                writer.Byte(EncodeSignedByteDelta(
                    delta.y,
                    ShatterboltImpactStepMeters));
                impactAnchor = impact;
            }
            else
            {
                writer.Byte(0);
                writer.Byte(0);
            }
        }

        writer.Byte(SpellPayloadEndSentinel);
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

        if (reader.Byte() != PayloadEndSentinel ||
            (byte)state.Phase > (byte)OrreryCastPhase.Invoking ||
            state.FormulaCapacity > MaxPresentedSatellites ||
            state.LockedCount > state.FormulaCapacity)
        {
            state = default(PresentationState);
            return false;
        }

        // Spell presentation is deliberately isolated. A malformed/missing spell
        // slot suppresses only that spell's VFX, never otherwise-valid Orrery
        // satellite/casting presentation.
        TryReadShatterbolt(remoteOwner, ref state);
        return true;
    }

    private static bool TryReadShatterbolt(
        GameShip remoteOwner,
        ref PresentationState state)
    {
        CoreNetwork.SlotReader reader;
        if (!CoreNetwork.TryReadSlot(
                remoteOwner,
                SpellPresentationSlotId,
                out reader))
        {
            return false;
        }

        if (reader.Byte() != SpellPayloadVersion)
            return false;

        byte flags = reader.Byte();
        const byte knownFlags =
            ShatterboltFlagPresent | ShatterboltFlagOrbActive;
        if ((flags & ~knownFlags) != 0 ||
            (flags & ShatterboltFlagPresent) == 0)
        {
            return false;
        }

        byte castSequence = reader.Byte();
        byte impactCount = reader.Byte();
        if (impactCount > MaxNetworkedShatterboltImpacts)
            return false;

        short orbDeltaX = ReadSigned16(ref reader);
        short orbDeltaY = ReadSigned16(ref reader);

        Vector2 impact0 = Vector2.zero;
        Vector2 impact1 = Vector2.zero;
        Vector2 impact2 = Vector2.zero;
        Vector2 impact3 = Vector2.zero;
        Vector2 impact4 = Vector2.zero;
        Vector2 impact5 = Vector2.zero;
        Vector2 impact6 = Vector2.zero;
        Vector2 impact7 = Vector2.zero;
        Vector2 impact8 = Vector2.zero;
        Vector2 impact9 = Vector2.zero;

        Vector2 ownerPosition = remoteOwner.transform.position;
        Vector2 impactAnchor = ownerPosition;
        for (int i = 0; i < MaxNetworkedShatterboltImpacts; i++)
        {
            byte deltaX = reader.Byte();
            byte deltaY = reader.Byte();
            if (i >= impactCount)
                continue;

            Vector2 impact = impactAnchor + new Vector2(
                DecodeSignedByteDelta(deltaX, ShatterboltImpactStepMeters),
                DecodeSignedByteDelta(deltaY, ShatterboltImpactStepMeters));
            switch (i)
            {
                case 0: impact0 = impact; break;
                case 1: impact1 = impact; break;
                case 2: impact2 = impact; break;
                case 3: impact3 = impact; break;
                case 4: impact4 = impact; break;
                case 5: impact5 = impact; break;
                case 6: impact6 = impact; break;
                case 7: impact7 = impact; break;
                case 8: impact8 = impact; break;
                case 9: impact9 = impact; break;
            }
            impactAnchor = impact;
        }

        if (reader.Byte() != SpellPayloadEndSentinel)
            return false;

        state.ShatterboltPresent = true;
        state.ShatterboltOrbActive =
            (flags & ShatterboltFlagOrbActive) != 0;
        state.ShatterboltCastSequence = castSequence;
        state.ShatterboltImpactCount = impactCount;
        state.ShatterboltImpact0 = impact0;
        state.ShatterboltImpact1 = impact1;
        state.ShatterboltImpact2 = impact2;
        state.ShatterboltImpact3 = impact3;
        state.ShatterboltImpact4 = impact4;
        state.ShatterboltImpact5 = impact5;
        state.ShatterboltImpact6 = impact6;
        state.ShatterboltImpact7 = impact7;
        state.ShatterboltImpact8 = impact8;
        state.ShatterboltImpact9 = impact9;

        Vector2 orbAnchor = impactCount > 0
            ? state.GetShatterboltImpact(impactCount - 1)
            : ownerPosition;
        state.ShatterboltOrbPosition = orbAnchor + new Vector2(
            DecodeSigned16Delta(orbDeltaX, ShatterboltOrbStepMeters),
            DecodeSigned16Delta(orbDeltaY, ShatterboltOrbStepMeters));
        return true;
    }

    private static byte EncodeSignedByteDelta(
        float worldDelta,
        float stepMeters)
    {
        float meters = OrreryUnits.WorldToMeters(worldDelta);
        int quantized = Mathf.Clamp(
            Mathf.RoundToInt(meters / Mathf.Max(0.001f, stepMeters)),
            -127,
            127);
        return unchecked((byte)(sbyte)quantized);
    }

    private static float DecodeSignedByteDelta(
        byte encoded,
        float stepMeters)
    {
        sbyte quantized = unchecked((sbyte)encoded);
        return quantized * stepMeters * OrreryUnits.WorldUnitsPerMeter;
    }

    private static short EncodeSigned16Delta(
        float worldDelta,
        float stepMeters)
    {
        float meters = OrreryUnits.WorldToMeters(worldDelta);
        int quantized = Mathf.Clamp(
            Mathf.RoundToInt(meters / Mathf.Max(0.001f, stepMeters)),
            short.MinValue + 1,
            short.MaxValue);
        return (short)quantized;
    }

    private static float DecodeSigned16Delta(
        short encoded,
        float stepMeters)
    {
        return encoded * stepMeters * OrreryUnits.WorldUnitsPerMeter;
    }

    private static void WriteSigned16(
        ref CoreNetwork.SlotWriter writer,
        short value)
    {
        ushort raw = unchecked((ushort)value);
        writer.Byte((byte)(raw & 0xFF));
        writer.Byte((byte)(raw >> 8));
    }

    private static short ReadSigned16(ref CoreNetwork.SlotReader reader)
    {
        ushort raw = (ushort)(reader.Byte() | (reader.Byte() << 8));
        return unchecked((short)raw);
    }
}
