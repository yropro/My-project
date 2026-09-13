using HarmonyLib;
using StarVortex;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Presentation only. Slot 10: version, packet/cast sequences, bolt flag,
/// start/end and width (26 bytes). Slot 11: up to six net-id/remaining-time
/// pairs (31 bytes). Burns rotate on actual sends and expire without refresh.
/// Core commits both slots atomically. No damage or infection decisions here.
/// </summary>
public static class OrreryPlasmaBoltPresentation
{
    private const byte HeaderSlot = 10, BurnSlot = 11, Version = 1;
    private const int BatchSize = 6;
    private const float RefreshTimeout = 1.25f;
    private static readonly uint[] targetScratch = new uint[BatchSize];
    private static readonly byte[] remainingScratch = new byte[BatchSize];
    private static ushort packetSequence;
    private static readonly FieldInfo bridgeField = AccessTools.Field(typeof(NetSession), "activeBridge");

    private sealed class BurnVisual
    {
        public GameShip Target;
        public StatusEffectLayer Layer;
        public float ExpiresAt, RefreshUntil;
    }

    private sealed class RemoteState
    {
        public bool Initialized, BoltInitialized;
        public ushort PacketSequence, BoltSequence;
        public readonly OrreryPlasmaBolt.BoltVisualState Bolt = new OrreryPlasmaBolt.BoltVisualState();
        public readonly BurnVisual[] Burns = new BurnVisual[OrrerySpellCompendium.PlasmaBolt.MaxActiveInfections];
        public RemoteState()
        {
            for (int i = 0; i < Burns.Length; i++) Burns[i] = new BurnVisual();
        }
    }

    private static readonly Dictionary<GameShip, RemoteState> remotes = new Dictionary<GameShip, RemoteState>(4);

    static OrreryPlasmaBoltPresentation()
    {
        CoreNetwork.RegisterSlot(HeaderSlot, CoreClassId.Orrery, "Plasma Bolt presentation");
        CoreNetwork.RegisterSlot(BurnSlot, CoreClassId.Orrery, "Plasma Burn refresh batch");
    }

    public static void EnsureInitialized() { }

    public static void Publish()
    {
        ushort cast;
        Vector2 start, end;
        float width;
        bool bolt;
        int count;
        if (!OrreryPlasmaBolt.ReadPresentation(targetScratch, remainingScratch,
            out cast, out start, out end, out width, out bolt, out count))
            return;
        CoreNetwork.SlotWriter writer = CoreNetwork.BeginSlot(HeaderSlot);
        writer.Byte(Version);
        WriteUShort(ref writer, ++packetSequence);
        WriteUShort(ref writer, cast);
        writer.Byte(bolt ? (byte)1 : (byte)0);
        OrreryNetwork.WritePosition(ref writer, start);
        OrreryNetwork.WritePosition(ref writer, end);
        OrreryNetwork.WriteFloat(ref writer, width);
        CoreNetwork.EndSlot(writer);
        writer = CoreNetwork.BeginSlot(BurnSlot);
        writer.Byte((byte)count);
        for (int i = 0; i < count; i++)
        {
            WriteUInt(ref writer, targetScratch[i]);
            writer.Byte(remainingScratch[i]);
        }
        CoreNetwork.EndSlot(writer);
    }

    public static void Tick(GameShip owner)
    {
        if (owner == null || !owner.IsRemotePlayer()) return;
        CoreNetwork.SlotReader header, burns;
        if (!CoreNetwork.TryReadSlot(owner, HeaderSlot, out header) ||
            !CoreNetwork.TryReadSlot(owner, BurnSlot, out burns) || header.Length != 26)
        {
            Forget(owner);
            return;
        }
        if (header.Byte() != Version) { Forget(owner); return; }
        ushort packet = ReadUShort(ref header), cast = ReadUShort(ref header);
        byte bolt = header.Byte();
        Vector2 start = OrreryNetwork.ReadPosition(ref header), end = OrreryNetwork.ReadPosition(ref header);
        float width = OrreryNetwork.ReadFloat(ref header);
        int count = burns.Byte();
        if (bolt > 1 || count > BatchSize || burns.Length != 1 + count * 5 ||
            !OrreryNetwork.IsFinite(start) || !OrreryNetwork.IsFinite(end) ||
            !OrreryNetwork.IsFinite(width) || width <= 0f)
        { Forget(owner); return; }
        // Validate the whole batch before touching any visual state.
        for (int i = 0; i < count; i++)
        {
            targetScratch[i] = ReadUInt(ref burns);
            remainingScratch[i] = burns.Byte();
            if (targetScratch[i] == 0 || remainingScratch[i] == 0 || remainingScratch[i] > 100)
            { Forget(owner); return; }
        }
        RemoteState state;
        if (!remotes.TryGetValue(owner, out state))
        {
            state = new RemoteState();
            remotes.Add(owner, state);
        }
        if (!state.Initialized || state.PacketSequence != packet)
        {
            state.Initialized = true;
            state.PacketSequence = packet;
            if (bolt != 0 && (!state.BoltInitialized || state.BoltSequence != cast))
            {
                state.BoltInitialized = true;
                state.BoltSequence = cast;
                OrreryPlasmaBolt.ShowBoltVisual(state.Bolt, start, end, width);
            }
            NetWorldBridge bridge = NetSession.instance == null || bridgeField == null
                ? null : bridgeField.GetValue(NetSession.instance) as NetWorldBridge;
            if (bridge != null)
            {
                for (int i = 0; i < count; i++)
                {
                    GameShip target = bridge.ResolveNetTarget(targetScratch[i]) as GameShip;
                    if (target != null && target.health > 0f)
                        RefreshBurn(state, target, remainingScratch[i] * 0.05f);
                }
            }
        }
        OrreryPlasmaBolt.HideExpiredBoltVisual(state.Bolt, Time.time);
        for (int i = 0; i < state.Burns.Length; i++)
        {
            BurnVisual burn = state.Burns[i];
            if (burn.Target == null || burn.Target.health <= 0f ||
                Time.time >= burn.ExpiresAt || Time.unscaledTime >= burn.RefreshUntil)
                ClearBurn(burn);
        }
    }

    private static void RefreshBurn(RemoteState state, GameShip target, float remaining)
    {
        BurnVisual available = null;
        for (int i = 0; i < state.Burns.Length; i++)
        {
            BurnVisual burn = state.Burns[i];
            if (object.ReferenceEquals(burn.Target, target)) { available = burn; break; }
            if (available == null && (burn.Target == null || Time.time >= burn.ExpiresAt ||
                Time.unscaledTime >= burn.RefreshUntil)) available = burn;
        }
        if (available == null) return;
        if (!object.ReferenceEquals(available.Target, target)) ClearBurn(available);
        available.Target = target;
        available.ExpiresAt = Time.time + remaining;
        available.RefreshUntil = Time.unscaledTime + RefreshTimeout;
        if (available.Layer == null) available.Layer = OrreryPlasmaBolt.SpawnFauxBurnVisual(target);
    }

    private static void ClearBurn(BurnVisual burn)
    {
        if (burn.Layer != null)
        {
            burn.Layer.Deactivate();
            burn.Layer.PoolDestroy();
        }
        burn.Layer = null; burn.Target = null;
        burn.ExpiresAt = burn.RefreshUntil = 0f;
    }

    public static void ForgetTarget(GameShip target)
    {
        foreach (RemoteState state in remotes.Values)
            for (int i = 0; i < state.Burns.Length; i++)
                if (object.ReferenceEquals(state.Burns[i].Target, target)) ClearBurn(state.Burns[i]);
    }

    public static void Forget(GameShip owner)
    {
        if (object.ReferenceEquals(owner, null)) return;
        RemoteState state;
        if (!remotes.TryGetValue(owner, out state)) return;
        OrreryPlasmaBolt.DestroyBoltVisual(state.Bolt);
        for (int i = 0; i < state.Burns.Length; i++) ClearBurn(state.Burns[i]);
        remotes.Remove(owner);
    }

    public static void Reset()
    {
        foreach (RemoteState state in remotes.Values)
        {
            OrreryPlasmaBolt.DestroyBoltVisual(state.Bolt);
            for (int i = 0; i < state.Burns.Length; i++) ClearBurn(state.Burns[i]);
        }
        remotes.Clear();
    }

    private static void WriteUShort(ref CoreNetwork.SlotWriter writer, ushort value)
    { writer.Byte((byte)value); writer.Byte((byte)(value >> 8)); }
    private static ushort ReadUShort(ref CoreNetwork.SlotReader reader)
    { return (ushort)(reader.Byte() | (reader.Byte() << 8)); }
    private static void WriteUInt(ref CoreNetwork.SlotWriter writer, uint value)
    { WriteUShort(ref writer, (ushort)value); WriteUShort(ref writer, (ushort)(value >> 16)); }
    private static uint ReadUInt(ref CoreNetwork.SlotReader reader)
    { return (uint)ReadUShort(ref reader) | ((uint)ReadUShort(ref reader) << 16); }
}

[HarmonyPatch(typeof(CoreNetwork), "AppendLocalExtension")]
public static class OrreryPlasmaBoltSendPatch
{
    public static void Prefix() { OrreryPlasmaBoltPresentation.Publish(); }
}

[HarmonyPatch(typeof(CoreNetwork), "RegisterDefaultSlots")]
public static class OrreryPlasmaBoltRegisterSlotsPatch
{
    public static void Postfix() { OrreryPlasmaBoltPresentation.EnsureInitialized(); }
}

[HarmonyPatch(typeof(RemoteShipDriver), "Render")]
public static class OrreryPlasmaBoltRemoteRenderPatch
{
    public static void Postfix(RemoteShipDriver __instance)
    { if (__instance != null) OrreryPlasmaBoltPresentation.Tick(__instance.gameShip); }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class OrreryPlasmaBoltRemoteResetPatch
{
    public static void Prefix() { OrreryPlasmaBoltPresentation.Reset(); }
}
