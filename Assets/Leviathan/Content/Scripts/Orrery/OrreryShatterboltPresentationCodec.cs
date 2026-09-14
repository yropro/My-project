using StarVortex;
using UnityEngine;

/// <summary>
/// Shatterbolt-specific codec over the shared multiplexed Orrery presentation bank.
/// Owns only Shatterbolt payload meaning and its bounded cumulative-history wire
/// shape. Gameplay and remote visual lifetime remain elsewhere.
/// </summary>
public static class OrreryShatterboltPresentationCodec
{

    private static readonly OrreryNetwork.Channel<ShatterboltWireState> Channel =
        new OrreryNetwork.Channel<ShatterboltWireState>(
            OrreryPresentationNetwork.CodecShatterbolt, WireShatterbolt);

    /// <summary>
    /// Wire-only view of the Shatterbolt payload. Deliberately separate from
    /// OrreryNetwork.PresentationState: CoreWire.TryDecode starts from
    /// default(T) and commits the whole struct, so decoding straight into
    /// PresentationState would blank every field this codec does not own.
    /// </summary>
    internal struct ShatterboltWireState
    {
        public bool OrbActive;
        public byte ImpactCount;
        public float ExplosionRadiusMeters;
        public Vector2 OrbPosition;
        public Vector2 Impact0;
        public Vector2 Impact1;
        public Vector2 Impact2;
        public Vector2 Impact3;
        public Vector2 Impact4;
        public Vector2 Impact5;
        public Vector2 Impact6;
        public Vector2 Impact7;
        public Vector2 Impact8;
        public Vector2 Impact9;

        public Vector2 GetImpact(int index)
        {
            switch (index)
            {
                case 0: return Impact0;
                case 1: return Impact1;
                case 2: return Impact2;
                case 3: return Impact3;
                case 4: return Impact4;
                case 5: return Impact5;
                case 6: return Impact6;
                case 7: return Impact7;
                case 8: return Impact8;
                default: return Impact9;
            }
        }

        public void SetImpact(int index, Vector2 value)
        {
            switch (index)
            {
                case 0: Impact0 = value; break;
                case 1: Impact1 = value; break;
                case 2: Impact2 = value; break;
                case 3: Impact3 = value; break;
                case 4: Impact4 = value; break;
                case 5: Impact5 = value; break;
                case 6: Impact6 = value; break;
                case 7: Impact7 = value; break;
                case 8: Impact8 = value; break;
                default: Impact9 = value; break;
            }
        }
    }

    /// <summary>
    /// Orb flag and impact count lead, so both directions take the same
    /// branches. Impacts are unrolled rather than looped because each is a
    /// distinct field and CoreWire takes them by ref; the count gate above
    /// each one is what makes the layout conditional.
    /// </summary>
    internal static void WireShatterbolt(
        ref CoreWire wire,
        ref ShatterboltWireState state)
    {
        wire.Flags(ref state.OrbActive);
        wire.Byte(ref state.ImpactCount);
        if (!wire.Ok)
            return;
        if (state.ImpactCount > OrrerySpellCompendium.Shatterbolt.MaximumImpacts)
        {
            wire.Fail();
            return;
        }

        wire.Positive(ref state.ExplosionRadiusMeters);
        if (state.OrbActive)
            wire.Position(ref state.OrbPosition);

        if (state.ImpactCount > 0) wire.Position(ref state.Impact0);
        if (state.ImpactCount > 1) wire.Position(ref state.Impact1);
        if (state.ImpactCount > 2) wire.Position(ref state.Impact2);
        if (state.ImpactCount > 3) wire.Position(ref state.Impact3);
        if (state.ImpactCount > 4) wire.Position(ref state.Impact4);
        if (state.ImpactCount > 5) wire.Position(ref state.Impact5);
        if (state.ImpactCount > 6) wire.Position(ref state.Impact6);
        if (state.ImpactCount > 7) wire.Position(ref state.Impact7);
        if (state.ImpactCount > 8) wire.Position(ref state.Impact8);
        if (state.ImpactCount > 9) wire.Position(ref state.Impact9);
    }

    // The gameplay invocation currently exposes an 8-bit presentation sequence.
    // Lift that into a transport-owned 32-bit generation by observing owner or
    // sequence changes. This remains codec-local; the shared bank only transports
    // and validates the resulting identity.
    private static bool generationInitialized;
    private static int generationOwnerInstanceId;
    private static byte generationCastSequence;
    private static uint generationCounter;

    public static void Publish(GameShip owner, OrreryNetwork.PresentationState state)
    {
        if (owner == null || !state.ShatterboltPresent)
            return;

        int impactCount = Mathf.Clamp(
            state.ShatterboltImpactCount,
            0,
            OrrerySpellCompendium.Shatterbolt.MaximumImpacts);

        // Radius, impact bound and record count are all enforced by the format
        // and the channel; an over-large or non-finite frame simply fails to
        // encode and nothing is published.
        ShatterboltWireState wireState = default(ShatterboltWireState);
        wireState.OrbActive = state.ShatterboltOrbActive;
        wireState.ImpactCount = (byte)impactCount;
        wireState.ExplosionRadiusMeters = state.ShatterboltExplosionRadiusMeters;
        wireState.OrbPosition = state.ShatterboltOrbPosition;
        for (int i = 0; i < impactCount; i++)
            wireState.SetImpact(i, state.GetShatterboltImpact(i));

        Channel.Publish(
            ResolveGeneration(owner, state.ShatterboltCastSequence),
            ref wireState);
    }

    public static bool TryRead(
        GameShip remoteOwner,
        ref OrreryNetwork.PresentationState state)
    {
        ShatterboltWireState wireState = default(ShatterboltWireState);
        uint generation;
        if (!Channel.TryRead(remoteOwner, ref wireState, out generation))
            return false;

        OrreryNetwork.PresentationState decoded = state;
        decoded.ShatterboltGeneration = generation;
        decoded.ShatterboltCastSequence = (byte)(generation & 0xFFu);
        decoded.ShatterboltImpactCount = wireState.ImpactCount;
        decoded.ShatterboltExplosionRadiusMeters = wireState.ExplosionRadiusMeters;
        decoded.ShatterboltOrbActive = wireState.OrbActive;
        decoded.ShatterboltOrbPosition = wireState.OrbPosition;
        for (int i = 0; i < wireState.ImpactCount; i++)
            decoded.SetShatterboltImpact(i, wireState.GetImpact(i));

        decoded.ShatterboltPresent = true;
        state = decoded;
        return true;
    }

    private static uint ResolveGeneration(GameShip owner, byte castSequence)
    {
        int ownerInstanceId = owner.GetInstanceID();
        if (!generationInitialized ||
            generationOwnerInstanceId != ownerInstanceId ||
            generationCastSequence != castSequence)
        {
            generationInitialized = true;
            generationOwnerInstanceId = ownerInstanceId;
            generationCastSequence = castSequence;
            generationCounter++;
            if (generationCounter == 0u)
                generationCounter++;
        }

        return generationCounter;
    }

}
