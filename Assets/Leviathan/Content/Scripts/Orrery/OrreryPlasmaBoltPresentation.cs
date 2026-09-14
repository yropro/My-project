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
    private const int StrokeRecordIndex = 3;
    private const int BurnFirstRecordIndex = 4;
    private const byte StrokeGroupId = 0;
    private const byte BurnGroupId = 1;
    private const int StrokePartCount = 1;
    private const int RefreshEntryCount = 6;
    private const int StrokePayloadBytes = 20;
    private const int BurnPayloadMaxBytes = 1 + RefreshEntryCount * 5;
    private const float RefreshTimeout = 1.25f;

    private static readonly uint[] targetScratch = new uint[RefreshEntryCount];
    private static readonly byte[] remainingScratch = new byte[RefreshEntryCount];
    private static readonly byte[] strokePayload = new byte[StrokePayloadBytes];
    private static readonly byte[] burnPayload = new byte[BurnPayloadMaxBytes];

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
        public StatusEffectLayer Layer;
        public float ExpiresAt, RefreshUntil;
    }

    private sealed class RemoteState
    {
        public bool BoltInitialized;
        public uint BoltGeneration;
        public bool BurnRefreshInitialized;
        public uint BurnRefreshGeneration;
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
                out cast,
                out start,
                out end,
                out width,
                out bolt,
                out count))
        {
            return;
        }

        if (bolt &&
            OrreryNetwork.IsFinite(start) &&
            OrreryNetwork.IsFinite(end) &&
            OrreryNetwork.IsFinite(width) &&
            width > 0f)
        {
            int offset = 0;
            WritePosition(strokePayload, ref offset, start);
            WritePosition(strokePayload, ref offset, end);
            WriteFloat(strokePayload, ref offset, width);

            OrreryPresentationNetwork.WriteGroup(
                StrokeRecordIndex,
                OrreryPresentationNetwork.CodecPlasmaBolt,
                StrokeGroupId,
                StrokePartCount,
                ResolveStrokeGeneration(owner, cast),
                strokePayload,
                StrokePayloadBytes);
        }

        count = Mathf.Clamp(count, 0, RefreshEntryCount);
        if (count <= 0)
            return;

        int burnOffset = 0;
        burnPayload[burnOffset++] = (byte)count;
        for (int i = 0; i < count; i++)
        {
            WriteUInt(burnPayload, ref burnOffset, targetScratch[i]);
            burnPayload[burnOffset++] = remainingScratch[i];
        }

        int partCount = burnOffset <=
            OrreryPresentationNetwork.GetPayloadCapacity(1) ? 1 : 2;
        OrreryPresentationNetwork.WriteGroup(
            BurnFirstRecordIndex,
            OrreryPresentationNetwork.CodecPlasmaBolt,
            BurnGroupId,
            partCount,
            NextGeneration(ref burnRefreshGenerationCounter),
            burnPayload,
            burnOffset);
    }

    public static void Tick(GameShip owner)
    {
        if (owner == null || !owner.IsRemotePlayer())
            return;

        uint boltGeneration;
        Vector2 start;
        Vector2 end;
        float width;
        bool hasStroke = TryReadStroke(
            owner,
            out boltGeneration,
            out start,
            out end,
            out width);

        uint burnGeneration;
        int burnCount;
        bool hasBurnRefresh = TryReadBurnRefresh(
            owner,
            out burnGeneration,
            out burnCount);

        RemoteState state;
        bool hasState = remotes.TryGetValue(owner, out state) && state != null;
        if (!hasState && !hasStroke && !hasBurnRefresh)
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
            OrreryPlasmaBolt.ShowBoltVisual(state.Bolt, owner, start, end, width);
        }

        if (hasBurnRefresh &&
            (!state.BurnRefreshInitialized ||
             state.BurnRefreshGeneration != burnGeneration))
        {
            state.BurnRefreshInitialized = true;
            state.BurnRefreshGeneration = burnGeneration;

            NetWorldBridge bridge = NetSession.instance == null || bridgeField == null
                ? null
                : bridgeField.GetValue(NetSession.instance) as NetWorldBridge;
            if (bridge != null)
            {
                for (int i = 0; i < burnCount; i++)
                {
                    GameShip target =
                        bridge.ResolveNetTarget(targetScratch[i]) as GameShip;
                    if (target != null && target.health > 0f)
                        RefreshBurn(state, target, remainingScratch[i] * 0.05f);
                }
            }
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

        if (!hasStroke && !hasBurnRefresh &&
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

    private static bool TryReadStroke(
        GameShip owner,
        out uint generation,
        out Vector2 start,
        out Vector2 end,
        out float width)
    {
        generation = 0u;
        start = Vector2.zero;
        end = Vector2.zero;
        width = 0f;

        OrreryPresentationNetwork.GroupReader reader;
        if (!OrreryPresentationNetwork.TryReadGroup(
                owner,
                StrokeRecordIndex,
                OrreryPresentationNetwork.CodecPlasmaBolt,
                StrokeGroupId,
                StrokePartCount,
                out reader) ||
            reader.Length != StrokePayloadBytes)
        {
            return false;
        }

        start = ReadPosition(ref reader);
        end = ReadPosition(ref reader);
        width = ReadFloat(ref reader);
        if (reader.Remaining != 0 ||
            !OrreryNetwork.IsFinite(start) ||
            !OrreryNetwork.IsFinite(end) ||
            !OrreryNetwork.IsFinite(width) ||
            width <= 0f)
        {
            return false;
        }

        generation = reader.Generation;
        return generation != 0u;
    }

    private static bool TryReadBurnRefresh(
        GameShip owner,
        out uint generation,
        out int count)
    {
        generation = 0u;
        count = 0;

        OrreryPresentationNetwork.GroupReader reader;
        if (!OrreryPresentationNetwork.TryReadGroup(
                owner,
                BurnFirstRecordIndex,
                OrreryPresentationNetwork.CodecPlasmaBolt,
                BurnGroupId,
                1,
                out reader) &&
            !OrreryPresentationNetwork.TryReadGroup(
                owner,
                BurnFirstRecordIndex,
                OrreryPresentationNetwork.CodecPlasmaBolt,
                BurnGroupId,
                2,
                out reader))
        {
            return false;
        }

        if (reader.Length < 1)
            return false;

        count = reader.Byte();
        if (count < 1 || count > RefreshEntryCount)
            return false;

        int expectedLength = 1 + count * 5;
        int expectedParts = expectedLength <=
            OrreryPresentationNetwork.GetPayloadCapacity(1) ? 1 : 2;
        if (reader.Length != expectedLength ||
            reader.PartCount != expectedParts)
        {
            return false;
        }

        // Validate the complete refresh group before mutating any remote visuals.
        for (int i = 0; i < count; i++)
        {
            targetScratch[i] = ReadUInt(ref reader);
            remainingScratch[i] = reader.Byte();
            if (targetScratch[i] == 0u ||
                remainingScratch[i] == 0 ||
                remainingScratch[i] > 100)
            {
                return false;
            }
        }

        if (reader.Remaining != 0)
            return false;

        generation = reader.Generation;
        return generation != 0u;
    }

    private static void RefreshBurn(
        RemoteState state,
        GameShip target,
        float remaining)
    {
        BurnVisual available = null;
        for (int i = 0; i < state.Burns.Length; i++)
        {
            BurnVisual burn = state.Burns[i];
            if (object.ReferenceEquals(burn.Target, target))
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
        if (!object.ReferenceEquals(available.Target, target))
            ClearBurn(available);

        available.Target = target;
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

    private static void WritePosition(byte[] buffer, ref int offset, Vector2 value)
    {
        WriteFloat(buffer, ref offset, value.x);
        WriteFloat(buffer, ref offset, value.y);
    }

    private static Vector2 ReadPosition(
        ref OrreryPresentationNetwork.GroupReader reader)
    {
        return new Vector2(ReadFloat(ref reader), ReadFloat(ref reader));
    }

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Explicit)]
    private struct FloatBits
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public float Value;
        [System.Runtime.InteropServices.FieldOffset(0)] public uint Bits;
    }

    private static void WriteFloat(byte[] buffer, ref int offset, float value)
    {
        FloatBits bits = new FloatBits { Value = value };
        WriteUInt(buffer, ref offset, bits.Bits);
    }

    private static float ReadFloat(ref OrreryPresentationNetwork.GroupReader reader)
    {
        return new FloatBits { Bits = ReadUInt(ref reader) }.Value;
    }

    private static void WriteUInt(byte[] buffer, ref int offset, uint value)
    {
        buffer[offset++] = (byte)value;
        buffer[offset++] = (byte)(value >> 8);
        buffer[offset++] = (byte)(value >> 16);
        buffer[offset++] = (byte)(value >> 24);
    }

    private static uint ReadUInt(ref OrreryPresentationNetwork.GroupReader reader)
    {
        return (uint)reader.Byte() |
            ((uint)reader.Byte() << 8) |
            ((uint)reader.Byte() << 16) |
            ((uint)reader.Byte() << 24);
    }
}
