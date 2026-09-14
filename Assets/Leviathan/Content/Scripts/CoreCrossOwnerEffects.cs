using HarmonyLib;
using StarVortex;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Small reliable owner-to-owner gameplay-intent lane for cooperative effects.
///
/// Gameplay is never applied to a remote replica. A source owner sends one
/// reliable targeted grant through NetSession; the host validates the real
/// sender and star membership, then forwards it. Only the target player's own
/// client invokes the registered handler against its authoritative local ship.
///
/// One reserved message type carries all Core cross-owner grants. Spells only
/// know RequestGrant(effectId, payload), so transport changes remain isolated
/// here instead of leaking networking into individual mechanics.
/// </summary>
public static class CoreCrossOwnerEffects
{
    // Current native NetMessageType ends well below this value. Fail closed if a
    // future game build claims it so an update cannot silently reinterpret Core
    // gameplay traffic as a native message.
    public const byte ReservedMessageTypeValue = 126;
    public static readonly NetMessageType MessageType =
        (NetMessageType)ReservedMessageTypeValue;

    public const int MaxRecentGrants = 32;
    public const float RecentGrantSeconds = 15f;

    private const int PayloadVersion = 1;

    public struct GrantPayload
    {
        public uint A;
        public uint B;
        public uint C;
        public uint D;
        public uint E;
        public uint F;

        public GrantPayload(
            uint a, uint b, uint c, uint d, uint e, uint f)
        {
            A = a;
            B = b;
            C = c;
            D = d;
            E = e;
            F = f;
        }
    }

    public delegate bool GrantHandler(
        GameShip localTarget,
        int sourcePlayerId,
        GrantPayload payload);

    [Serializable]
    private sealed class GrantEnvelope
    {
        public int version;
        public int sourcePlayerId;
        public int targetPlayerId;
        public int starId;
        public int sequence;
        public int effectId;
        public int payloadA;
        public int payloadB;
        public int payloadC;
        public int payloadD;
        public int payloadE;
        public int payloadF;
    }

    private struct RecentGrant
    {
        public bool Active;
        public int SourcePlayerId;
        public int Sequence;
        public float SeenAt;
    }

    private static readonly RecentGrant[] recent =
        new RecentGrant[MaxRecentGrants];
    private static readonly Dictionary<ushort, GrantHandler> handlers =
        new Dictionary<ushort, GrantHandler>();

    /// <summary>Host-validated presentation notification, not a recipient ACK.
    /// Only Core parses the envelope; effects never patch private receive paths.</summary>
    public struct GrantNotice
    {
        public int SourcePlayerId, TargetPlayerId, Sequence;
        public ushort EffectId;
        public GrantPayload Payload;
    }
    private static readonly Dictionary<ushort, Action<NetSession, GrantNotice>> observers =
        new Dictionary<ushort, Action<NetSession, GrantNotice>>();

    public static void RegisterObserver(ushort effectId, Action<NetSession, GrantNotice> observer)
    {
        if (effectId == 0 || observer == null) throw new ArgumentException("Invalid grant observer.");
        if (observers.ContainsKey(effectId)) throw new InvalidOperationException("Duplicate grant observer.");
        observers.Add(effectId, observer);
    }

    private static void Notify(NetSession session, GrantEnvelope envelope)
    {
        Action<NetSession, GrantNotice> observer;
        if (!observers.TryGetValue((ushort)envelope.effectId, out observer)) return;
        try
        {
            observer(session, new GrantNotice {
                SourcePlayerId = envelope.sourcePlayerId, TargetPlayerId = envelope.targetPlayerId,
                Sequence = envelope.sequence, EffectId = (ushort)envelope.effectId,
                Payload = new GrantPayload(unchecked((uint)envelope.payloadA), unchecked((uint)envelope.payloadB),
                    unchecked((uint)envelope.payloadC), unchecked((uint)envelope.payloadD),
                    unchecked((uint)envelope.payloadE), unchecked((uint)envelope.payloadF))
            });
        }
        catch (Exception ex) { Debug.LogWarning("[CoreCrossOwnerEffects] Presentation observer failed: " + ex.Message); }
    }

    private static readonly FieldInfo ConnToPlayerField =
        AccessTools.Field(typeof(NetSession), "connToPlayer");

    private static bool initialized;
    private static bool messageTypeAvailable = true;
    private static int nextSequence;
    private static int recentCursor;

    /// <summary>
    /// Sequence assigned to the most recent successfully-authored local grant.
    /// Presentation observers use this synchronously after RequestGrant returns
    /// so the immediate local observation and later reliable relay share one
    /// event identity without reflecting Core's private storage.
    /// </summary>
    internal static int CurrentLocalSequence
    {
        get { return nextSequence; }
    }

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
                "[CoreCrossOwnerEffects] Native NetMessageType now uses reserved " +
                ReservedMessageTypeValue + "; cross-owner gameplay is disabled " +
                "until the Core reservation is migrated.");
        }
    }

    public static void RegisterHandler(ushort effectId, GrantHandler handler)
    {
        if (effectId == 0 || handler == null)
            return;

        EnsureInitialized();

        GrantHandler existing;
        if (handlers.TryGetValue(effectId, out existing))
        {
            if (!object.ReferenceEquals(existing, handler))
            {
                Debug.LogWarning(
                    "[CoreCrossOwnerEffects] Effect " + effectId +
                    " already has a grant handler; keeping the first registration.");
            }
            return;
        }

        handlers.Add(effectId, handler);
    }

    /// <summary>
    /// Sends one reliable grant from the local player to another player. The
    /// payload is effect-owned immutable snapshot data, not a remote-state key.
    /// </summary>
    public static bool RequestGrant(
        int targetPlayerId,
        ushort effectId,
        GrantPayload payload)
    {
        EnsureInitialized();

        NetSession session = NetSession.instance;
        if (!messageTypeAvailable || !NetSession.InSession || session == null ||
            targetPlayerId < 0 || effectId == 0)
        {
            return false;
        }

        int sourcePlayerId = session.localPlayerId;
        if (sourcePlayerId < 0 || targetPlayerId == sourcePlayerId)
            return false;

        NetPlayer source = session.GetPlayer(sourcePlayerId);
        NetPlayer target = session.GetPlayer(targetPlayerId);
        if (source == null || target == null || source.currentStarId < 0 ||
            source.currentStarId != target.currentStarId)
        {
            return false;
        }

        unchecked
        {
            nextSequence++;
            if (nextSequence <= 0)
                nextSequence = 1;
        }

        GrantEnvelope envelope = new GrantEnvelope();
        envelope.version = PayloadVersion;
        envelope.sourcePlayerId = sourcePlayerId;
        envelope.targetPlayerId = targetPlayerId;
        envelope.starId = source.currentStarId;
        envelope.sequence = nextSequence;
        envelope.effectId = effectId;
        envelope.payloadA = unchecked((int)payload.A);
        envelope.payloadB = unchecked((int)payload.B);
        envelope.payloadC = unchecked((int)payload.C);
        envelope.payloadD = unchecked((int)payload.D);
        envelope.payloadE = unchecked((int)payload.E);
        envelope.payloadF = unchecked((int)payload.F);

        string json = JsonUtility.ToJson(envelope);

        if (NetSession.IsHost)
        {
            // Host-originated traffic already has trusted source identity.
            return ForwardFromHost(session, envelope, json);
        }

        session.SendStarEntityMessage(MessageType, json, envelope.starId);
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

        GrantEnvelope envelope = Parse(json);
        if (envelope == null)
            return;

        // Sender identity is host-owned truth; never trust the JSON claim.
        envelope.sourcePlayerId = actualSourcePlayerId;
        if (!ValidateEnvelope(session, envelope))
            return;

        ForwardFromHost(session, envelope, JsonUtility.ToJson(envelope));
    }

    internal static void ReceiveAtClient(NetSession session, string json)
    {
        if (!messageTypeAvailable || session == null || string.IsNullOrEmpty(json))
            return;

        GrantEnvelope envelope = Parse(json);
        if (envelope == null || !ValidateEnvelope(session, envelope))
        {
            return;
        }

        if (envelope.targetPlayerId != session.localPlayerId || ApplyLocal(session, envelope))
            Notify(session, envelope);
    }

    public static void ResetWorld()
    {
        for (int i = 0; i < recent.Length; i++)
            recent[i] = default(RecentGrant);
        recentCursor = 0;
    }

    private static bool ForwardFromHost(
        NetSession session,
        GrantEnvelope envelope,
        string json)
    {
        if (!ValidateEnvelope(session, envelope))
            return false;

        if (envelope.targetPlayerId == session.localPlayerId && !ApplyLocal(session, envelope))
            return false;

        // SendStarEntityMessage is native ReliableOrdered transport. On the host
        // it relays only to clients in this star; non-target clients receive the
        // tiny envelope but discard it by targetPlayerId.
        session.SendStarEntityMessage(MessageType, json, envelope.starId);
        Notify(session, envelope);
        return true;
    }

    private static bool ApplyLocal(NetSession session, GrantEnvelope envelope)
    {
        if (session == null || envelope == null ||
            envelope.targetPlayerId != session.localPlayerId)
        {
            return false;
        }

        float now = Time.unscaledTime;
        PruneRecent(now);
        if (WasRecentlyApplied(envelope.sourcePlayerId, envelope.sequence))
            return true;

        GameShip localTarget = WorldController.instance == null
            ? null
            : WorldController.instance.GetCurrentPlayerShip();
        if (localTarget == null || !localTarget.IsPlayer())
            return false;

        GrantHandler handler;
        ushort effectId = (ushort)envelope.effectId;
        if (!handlers.TryGetValue(effectId, out handler) || handler == null)
            return false;

        bool applied = false;
        try
        {
            GrantPayload payload = new GrantPayload(
                unchecked((uint)envelope.payloadA),
                unchecked((uint)envelope.payloadB),
                unchecked((uint)envelope.payloadC),
                unchecked((uint)envelope.payloadD),
                unchecked((uint)envelope.payloadE),
                unchecked((uint)envelope.payloadF));
            applied = handler(
                localTarget,
                envelope.sourcePlayerId,
                payload);
        }
        catch (Exception ex)
        {
            Debug.LogError(
                "[CoreCrossOwnerEffects] Grant handler " + effectId +
                " failed: " + ex);
        }

        if (applied)
            Remember(envelope.sourcePlayerId, envelope.sequence, now);
        return applied;
    }

    private static bool ValidateEnvelope(
        NetSession session,
        GrantEnvelope envelope)
    {
        if (session == null || envelope == null ||
            envelope.version != PayloadVersion ||
            envelope.sourcePlayerId < 0 || envelope.targetPlayerId < 0 ||
            envelope.sourcePlayerId == envelope.targetPlayerId ||
            envelope.starId < 0 || envelope.sequence <= 0 ||
            envelope.effectId <= 0 || envelope.effectId > ushort.MaxValue)
        {
            return false;
        }

        if (!handlers.ContainsKey((ushort)envelope.effectId))
            return false;

        NetPlayer source = session.GetPlayer(envelope.sourcePlayerId);
        NetPlayer target = session.GetPlayer(envelope.targetPlayerId);
        return source != null && target != null &&
            source.currentStarId == envelope.starId &&
            target.currentStarId == envelope.starId;
    }

    private static GrantEnvelope Parse(string json)
    {
        try
        {
            return JsonUtility.FromJson<GrantEnvelope>(json);
        }
        catch (Exception)
        {
            return null;
        }
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
            if (recent[i].Active &&
                now - recent[i].SeenAt > RecentGrantSeconds)
            {
                recent[i] = default(RecentGrant);
            }
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
        RecentGrant value = new RecentGrant();
        value.Active = true;
        value.SourcePlayerId = sourcePlayerId;
        value.Sequence = sequence;
        value.SeenAt = now;
        recent[recentCursor] = value;
        recentCursor = (recentCursor + 1) % recent.Length;
    }
}

[HarmonyPatch]
public static class CoreCrossOwnerEffectsHostDispatchPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(NetSession), "DispatchAsHost");
    }

    public static bool Prefix(NetSession __instance, object[] __args)
    {
        if (__args == null || __args.Length < 3 ||
            !(__args[1] is NetMessageType) ||
            (NetMessageType)__args[1] != CoreCrossOwnerEffects.MessageType)
        {
            return true;
        }

        CoreCrossOwnerEffects.ReceiveAtHost(
            __instance,
            __args[0],
            __args[2] as string);
        return false;
    }
}

[HarmonyPatch]
public static class CoreCrossOwnerEffectsClientDispatchPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(NetSession), "DispatchAsClient");
    }

    public static bool Prefix(NetSession __instance, object[] __args)
    {
        if (__args == null || __args.Length < 3 ||
            !(__args[1] is NetMessageType) ||
            (NetMessageType)__args[1] != CoreCrossOwnerEffects.MessageType)
        {
            return true;
        }

        CoreCrossOwnerEffects.ReceiveAtClient(
            __instance,
            __args[2] as string);
        return false;
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class CoreCrossOwnerEffectsWorldDestroyedPatch
{
    public static void Prefix()
    {
        CoreCrossOwnerEffects.ResetWorld();
    }
}
