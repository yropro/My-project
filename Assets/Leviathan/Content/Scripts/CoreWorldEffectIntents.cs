using HarmonyLib;
using StarVortex;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Reliable client-to-star-authority intent lane for world effects whose gameplay
/// must run where NPC ships/projectiles are authoritative.
///
/// This is intentionally separate from CoreNetwork presentation state and from
/// CoreCrossOwnerEffects (which targets another player's local ship). A client
/// authors one immutable effect snapshot; the host validates sender/star identity
/// and invokes the registered gameplay handler exactly once. The host never trusts
/// source identity from JSON.
///
/// Continuous simulation is not networked per tick. The receiver starts its own
/// bounded deterministic runtime from the snapshot. This keeps area slows and
/// projectile interception out of transient VFX transport while avoiding a stream
/// of per-projectile RPCs.
/// </summary>
public static class CoreWorldEffectIntents
{
    public const byte ReservedMessageTypeValue = 125;
    public static readonly NetMessageType MessageType =
        (NetMessageType)ReservedMessageTypeValue;

    public const int MaxRecentIntents = 32;
    public const float RecentIntentSeconds = 20f;
    private const int PayloadVersion = 1;

    public struct Payload
    {
        public uint A, B, C, D, E, F, G, H, I, J, K, L;

        public Payload(
            uint a, uint b, uint c, uint d, uint e, uint f,
            uint g, uint h, uint i, uint j, uint k, uint l)
        {
            A = a; B = b; C = c; D = d; E = e; F = f;
            G = g; H = h; I = i; J = j; K = k; L = l;
        }
    }

    public delegate bool IntentHandler(int sourcePlayerId, Payload payload);

    [Serializable]
    private sealed class Envelope
    {
        public int version;
        public int sourcePlayerId;
        public int starId;
        public int sequence;
        public int effectId;
        public int a, b, c, d, e, f, g, h, i, j, k, l;
    }

    private struct RecentIntent
    {
        public bool Active;
        public int SourcePlayerId;
        public int Sequence;
        public float SeenAt;
    }

    private static readonly RecentIntent[] recent =
        new RecentIntent[MaxRecentIntents];
    private static readonly Dictionary<ushort, IntentHandler> handlers =
        new Dictionary<ushort, IntentHandler>();
    private static readonly FieldInfo ConnToPlayerField =
        AccessTools.Field(typeof(NetSession), "connToPlayer");

    private static bool initialized;
    private static bool messageTypeAvailable = true;
    private static int nextSequence;
    private static int recentCursor;

    public static void EnsureInitialized()
    {
        if (initialized)
            return;

        initialized = true;
        messageTypeAvailable = !Enum.IsDefined(
            typeof(NetMessageType),
            ReservedMessageTypeValue);

        if (!messageTypeAvailable)
        {
            Debug.LogError(
                "[CoreWorldEffectIntents] Native NetMessageType now uses reserved " +
                ReservedMessageTypeValue + "; world-effect intents are disabled " +
                "until the Core reservation is migrated.");
        }
    }

    public static void RegisterHandler(ushort effectId, IntentHandler handler)
    {
        if (effectId == 0 || handler == null)
            throw new ArgumentException("Invalid world-effect handler.");

        EnsureInitialized();

        IntentHandler existing;
        if (handlers.TryGetValue(effectId, out existing))
        {
            if (!object.ReferenceEquals(existing, handler))
            {
                Debug.LogWarning(
                    "[CoreWorldEffectIntents] Effect " + effectId +
                    " already has a handler; keeping first registration.");
            }
            return;
        }

        handlers.Add(effectId, handler);
    }

    /// <summary>
    /// Sends one immutable world-effect snapshot from a non-host player to the
    /// star authority. Host and single-player callers should execute locally and
    /// therefore do not use this lane.
    /// </summary>
    public static bool RequestHost(ushort effectId, Payload payload)
    {
        EnsureInitialized();

        NetSession session = NetSession.instance;
        if (!messageTypeAvailable || !NetSession.InSession || session == null ||
            NetSession.IsHost || effectId == 0)
        {
            return false;
        }

        int sourcePlayerId = session.localPlayerId;
        NetPlayer source = session.GetPlayer(sourcePlayerId);
        if (sourcePlayerId < 0 || source == null || source.currentStarId < 0 ||
            !handlers.ContainsKey(effectId))
        {
            return false;
        }

        unchecked
        {
            nextSequence++;
            if (nextSequence <= 0)
                nextSequence = 1;
        }

        Envelope envelope = new Envelope();
        envelope.version = PayloadVersion;
        envelope.sourcePlayerId = sourcePlayerId;
        envelope.starId = source.currentStarId;
        envelope.sequence = nextSequence;
        envelope.effectId = effectId;
        envelope.a = unchecked((int)payload.A);
        envelope.b = unchecked((int)payload.B);
        envelope.c = unchecked((int)payload.C);
        envelope.d = unchecked((int)payload.D);
        envelope.e = unchecked((int)payload.E);
        envelope.f = unchecked((int)payload.F);
        envelope.g = unchecked((int)payload.G);
        envelope.h = unchecked((int)payload.H);
        envelope.i = unchecked((int)payload.I);
        envelope.j = unchecked((int)payload.J);
        envelope.k = unchecked((int)payload.K);
        envelope.l = unchecked((int)payload.L);

        session.SendStarEntityMessage(
            MessageType,
            JsonUtility.ToJson(envelope),
            envelope.starId);
        return true;
    }

    internal static void ReceiveAtHost(
        NetSession session,
        object connectionKey,
        string json)
    {
        if (!messageTypeAvailable || session == null || string.IsNullOrEmpty(json))
            return;

        int actualSourcePlayerId;
        if (!TryResolveSender(session, connectionKey, out actualSourcePlayerId))
            return;

        Envelope envelope = Parse(json);
        if (envelope == null)
            return;

        envelope.sourcePlayerId = actualSourcePlayerId;
        if (!ValidateEnvelope(session, envelope))
            return;

        float now = Time.unscaledTime;
        PruneRecent(now);
        if (WasRecentlyApplied(envelope.sourcePlayerId, envelope.sequence))
            return;

        IntentHandler handler;
        ushort effectId = (ushort)envelope.effectId;
        if (!handlers.TryGetValue(effectId, out handler) || handler == null)
            return;

        Payload payload = new Payload(
            unchecked((uint)envelope.a), unchecked((uint)envelope.b),
            unchecked((uint)envelope.c), unchecked((uint)envelope.d),
            unchecked((uint)envelope.e), unchecked((uint)envelope.f),
            unchecked((uint)envelope.g), unchecked((uint)envelope.h),
            unchecked((uint)envelope.i), unchecked((uint)envelope.j),
            unchecked((uint)envelope.k), unchecked((uint)envelope.l));

        bool applied = false;
        try
        {
            applied = handler(envelope.sourcePlayerId, payload);
        }
        catch (Exception ex)
        {
            Debug.LogError(
                "[CoreWorldEffectIntents] Handler " + effectId +
                " failed: " + ex);
        }

        if (applied)
            Remember(envelope.sourcePlayerId, envelope.sequence, now);
    }

    public static void ResetWorld()
    {
        for (int i = 0; i < recent.Length; i++)
            recent[i] = default(RecentIntent);
        recentCursor = 0;
    }

    private static bool ValidateEnvelope(NetSession session, Envelope envelope)
    {
        if (session == null || envelope == null ||
            envelope.version != PayloadVersion ||
            envelope.sourcePlayerId < 0 || envelope.starId < 0 ||
            envelope.sequence <= 0 || envelope.effectId <= 0 ||
            envelope.effectId > ushort.MaxValue ||
            !handlers.ContainsKey((ushort)envelope.effectId))
        {
            return false;
        }

        NetPlayer source = session.GetPlayer(envelope.sourcePlayerId);
        return source != null && source.currentStarId == envelope.starId;
    }

    private static Envelope Parse(string json)
    {
        try { return JsonUtility.FromJson<Envelope>(json); }
        catch (Exception) { return null; }
    }

    private static bool TryResolveSender(
        NetSession session,
        object connectionKey,
        out int playerId)
    {
        playerId = -1;
        if (session == null || connectionKey == null || ConnToPlayerField == null)
            return false;

        IDictionary map = ConnToPlayerField.GetValue(session) as IDictionary;
        if (map == null || !map.Contains(connectionKey))
            return false;

        object value = map[connectionKey];
        if (!(value is int))
            return false;

        playerId = (int)value;
        return playerId >= 0;
    }

    private static void PruneRecent(float now)
    {
        for (int i = 0; i < recent.Length; i++)
        {
            if (recent[i].Active && now - recent[i].SeenAt > RecentIntentSeconds)
                recent[i] = default(RecentIntent);
        }
    }

    private static bool WasRecentlyApplied(int sourcePlayerId, int sequence)
    {
        for (int i = 0; i < recent.Length; i++)
        {
            if (recent[i].Active &&
                recent[i].SourcePlayerId == sourcePlayerId &&
                recent[i].Sequence == sequence)
            {
                return true;
            }
        }
        return false;
    }

    private static void Remember(int sourcePlayerId, int sequence, float now)
    {
        RecentIntent value = new RecentIntent();
        value.Active = true;
        value.SourcePlayerId = sourcePlayerId;
        value.Sequence = sequence;
        value.SeenAt = now;
        recent[recentCursor] = value;
        recentCursor = (recentCursor + 1) % recent.Length;
    }
}

[HarmonyPatch]
public static class CoreWorldEffectIntentsHostDispatchPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(NetSession), "DispatchAsHost");
    }

    public static bool Prefix(NetSession __instance, object[] __args)
    {
        if (__args == null || __args.Length < 3 ||
            !(__args[1] is NetMessageType) ||
            (NetMessageType)__args[1] != CoreWorldEffectIntents.MessageType)
        {
            return true;
        }

        CoreWorldEffectIntents.ReceiveAtHost(
            __instance,
            __args[0],
            __args[2] as string);
        return false;
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class CoreWorldEffectIntentsWorldDestroyedPatch
{
    public static void Prefix()
    {
        CoreWorldEffectIntents.ResetWorld();
    }
}
