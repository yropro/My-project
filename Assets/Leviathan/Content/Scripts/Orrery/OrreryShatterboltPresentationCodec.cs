using StarVortex;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Shatterbolt-specific codec over the shared multiplexed Orrery presentation bank.
/// Owns only Shatterbolt payload meaning and its bounded cumulative-history wire
/// shape. Gameplay and remote visual lifetime remain elsewhere.
/// </summary>
public static class OrreryShatterboltPresentationCodec
{
    private const int FirstRecordIndex = 0;
    private const int MaximumPartCount = 3;
    private const byte GroupId = 0;
    private const byte FlagOrbActive = 1 << 0;
    private const int MaxPayloadBytes = 86;

    private static readonly byte[] payloadScratch = new byte[MaxPayloadBytes];

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
        int payloadLength = 6 +
            (state.ShatterboltOrbActive ? 8 : 0) +
            impactCount * 8;
        if (payloadLength > MaxPayloadBytes)
            return;

        int offset = 0;
        payloadScratch[offset++] = state.ShatterboltOrbActive
            ? FlagOrbActive
            : (byte)0;
        payloadScratch[offset++] = (byte)impactCount;
        WriteFloat(payloadScratch, ref offset, state.ShatterboltExplosionRadiusMeters);

        if (state.ShatterboltOrbActive)
            WritePosition(payloadScratch, ref offset, state.ShatterboltOrbPosition);

        for (int i = 0; i < impactCount; i++)
            WritePosition(payloadScratch, ref offset, state.GetShatterboltImpact(i));

        int partCount = RequiredPartCount(offset);
        if (partCount <= 0)
            return;

        uint generation = ResolveGeneration(owner, state.ShatterboltCastSequence);
        OrreryPresentationNetwork.WriteGroup(
            FirstRecordIndex,
            OrreryPresentationNetwork.CodecShatterbolt,
            GroupId,
            partCount,
            generation,
            payloadScratch,
            offset);
    }

    public static bool TryRead(
        GameShip remoteOwner,
        ref OrreryNetwork.PresentationState state)
    {
        OrreryPresentationNetwork.GroupReader reader =
            default(OrreryPresentationNetwork.GroupReader);
        bool found = false;
        for (int partCount = 1; partCount <= MaximumPartCount; partCount++)
        {
            if (OrreryPresentationNetwork.TryReadGroup(
                    remoteOwner,
                    FirstRecordIndex,
                    OrreryPresentationNetwork.CodecShatterbolt,
                    GroupId,
                    partCount,
                    out reader))
            {
                found = true;
                break;
            }
        }

        if (!found || reader.Length < 6)
            return false;

        byte flags = reader.Byte();
        if ((flags & ~FlagOrbActive) != 0)
            return false;

        bool orbActive = (flags & FlagOrbActive) != 0;
        int impactCount = reader.Byte();
        if (impactCount > OrrerySpellCompendium.Shatterbolt.MaximumImpacts)
            return false;

        int expectedLength = 6 + (orbActive ? 8 : 0) + impactCount * 8;
        if (reader.Length != expectedLength ||
            expectedLength > MaxPayloadBytes ||
            RequiredPartCount(expectedLength) != reader.PartCount)
        {
            return false;
        }

        float radiusMeters = ReadFloat(ref reader);
        if (!OrreryNetwork.IsFinite(radiusMeters) || radiusMeters <= 0f)
            return false;

        OrreryNetwork.PresentationState decoded = state;
        decoded.ShatterboltGeneration = reader.Generation;
        decoded.ShatterboltCastSequence = (byte)(reader.Generation & 0xFFu);
        decoded.ShatterboltImpactCount = (byte)impactCount;
        decoded.ShatterboltExplosionRadiusMeters = radiusMeters;
        decoded.ShatterboltOrbActive = orbActive;

        if (orbActive)
        {
            decoded.ShatterboltOrbPosition = ReadPosition(ref reader);
            if (!OrreryNetwork.IsFinite(decoded.ShatterboltOrbPosition))
                return false;
        }

        for (int i = 0; i < impactCount; i++)
        {
            Vector2 position = ReadPosition(ref reader);
            if (!OrreryNetwork.IsFinite(position))
                return false;
            decoded.SetShatterboltImpact(i, position);
        }

        if (reader.Remaining != 0)
            return false;

        decoded.ShatterboltPresent = true;
        state = decoded;
        return true;
    }

    private static int RequiredPartCount(int payloadLength)
    {
        for (int parts = 1; parts <= MaximumPartCount; parts++)
        {
            if (payloadLength <= OrreryPresentationNetwork.GetPayloadCapacity(parts))
                return parts;
        }
        return 0;
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

    [StructLayout(LayoutKind.Explicit)]
    private struct FloatBits
    {
        [FieldOffset(0)] public float Value;
        [FieldOffset(0)] public uint Bits;
    }

    private static void WriteFloat(byte[] buffer, ref int offset, float value)
    {
        FloatBits bits = new FloatBits { Value = value };
        buffer[offset++] = (byte)bits.Bits;
        buffer[offset++] = (byte)(bits.Bits >> 8);
        buffer[offset++] = (byte)(bits.Bits >> 16);
        buffer[offset++] = (byte)(bits.Bits >> 24);
    }

    private static float ReadFloat(ref OrreryPresentationNetwork.GroupReader reader)
    {
        uint bits = (uint)reader.Byte() |
            ((uint)reader.Byte() << 8) |
            ((uint)reader.Byte() << 16) |
            ((uint)reader.Byte() << 24);
        return new FloatBits { Bits = bits }.Value;
    }

    private static void WritePosition(byte[] buffer, ref int offset, Vector2 position)
    {
        WriteFloat(buffer, ref offset, position.x);
        WriteFloat(buffer, ref offset, position.y);
    }

    private static Vector2 ReadPosition(ref OrreryPresentationNetwork.GroupReader reader)
    {
        return new Vector2(ReadFloat(ref reader), ReadFloat(ref reader));
    }
}
