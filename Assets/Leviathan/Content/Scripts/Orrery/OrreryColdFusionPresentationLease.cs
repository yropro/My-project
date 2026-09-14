using HarmonyLib;
using StarVortex;
using System;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Stable player-identity lease for Cold Fusion presentation.
///
/// The reliable cross-owner grant can arrive before a RemoteShipDriver exists, or
/// while Star Vortex is replacing that player's replica. The gameplay grant is
/// already authoritative elsewhere; this bounded lease only remembers how long
/// the presentation should still exist and rebinds it to whichever local/remote
/// GameShip currently represents the target player.
/// </summary>
public static class OrreryColdFusionPresentationLease
{
    private const int MaxLeases = 16;

    private struct Lease
    {
        public bool Active;
        public int PlayerId;
        public int SourcePlayerId;
        public int Sequence;
        public float ExpiresAt;
        public GameShip BoundShip;
    }

    [Serializable]
    private sealed class GrantEnvelopeView
    {
        public int sourcePlayerId;
        public int targetPlayerId;
        public int sequence;
        public int effectId;
        public int payloadA;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct FloatBits
    {
        [FieldOffset(0)] public float Float;
        [FieldOffset(0)] public uint UInt;
    }

    private static readonly Lease[] leases = new Lease[MaxLeases];

    private static readonly FieldInfo ActiveBridgeField =
        AccessTools.Field(typeof(NetSession), "activeBridge");
    private static readonly FieldInfo RepsField =
        AccessTools.Field(typeof(NetWorldBridge), "reps");
    private static readonly FieldInfo NextGrantSequenceField =
        AccessTools.Field(typeof(CoreCrossOwnerEffects), "nextSequence");

    public static void ObserveGrant(NetSession session, string json)
    {
        if (session == null || string.IsNullOrEmpty(json))
            return;

        GrantEnvelopeView envelope;
        try
        {
            envelope = JsonUtility.FromJson<GrantEnvelopeView>(json);
        }
        catch (Exception)
        {
            return;
        }

        if (envelope == null ||
            envelope.effectId != OrreryColdFusion.CrossOwnerEffectId ||
            envelope.sourcePlayerId < 0 ||
            envelope.targetPlayerId < 0 ||
            envelope.sequence <= 0)
        {
            return;
        }

        float duration = new FloatBits
        {
            UInt = unchecked((uint)envelope.payloadA)
        }.Float;
        Remember(
            session,
            envelope.sourcePlayerId,
            envelope.targetPlayerId,
            envelope.sequence,
            duration);
    }

    public static void ObserveLocalRequest(
        int targetPlayerId,
        ushort effectId,
        CoreCrossOwnerEffects.GrantPayload payload,
        bool accepted)
    {
        NetSession session = NetSession.instance;
        if (!accepted || effectId != OrreryColdFusion.CrossOwnerEffectId ||
            targetPlayerId < 0 || session == null || session.localPlayerId < 0)
        {
            return;
        }

        // This postfix runs synchronously after RequestGrant increments the source
        // sequence. Reusing that identity lets the later reliable relay be treated
        // as the same presentation event instead of restarting its duration.
        int sequence = ReadCurrentLocalGrantSequence();
        if (sequence <= 0)
            return;

        float duration = new FloatBits { UInt = payload.A }.Float;
        Remember(
            session,
            session.localPlayerId,
            targetPlayerId,
            sequence,
            duration);
    }

    private static int ReadCurrentLocalGrantSequence()
    {
        if (NextGrantSequenceField == null)
            return 0;

        object raw = NextGrantSequenceField.GetValue(null);
        return raw is int ? (int)raw : 0;
    }

    private static void Remember(
        NetSession session,
        int sourcePlayerId,
        int playerId,
        int sequence,
        float durationSeconds)
    {
        if (session == null || sourcePlayerId < 0 || playerId < 0 ||
            sequence <= 0 || float.IsNaN(durationSeconds) ||
            float.IsInfinity(durationSeconds) || durationSeconds <= 0f)
        {
            return;
        }

        float now = Time.time;
        int matchingTarget = -1;
        int free = -1;
        int oldest = -1;
        float oldestExpiry = float.PositiveInfinity;

        for (int i = 0; i < leases.Length; i++)
        {
            Lease lease = leases[i];
            if (lease.Active && lease.PlayerId == playerId)
            {
                matchingTarget = i;
                break;
            }

            if (!lease.Active)
            {
                if (free < 0)
                    free = i;
                continue;
            }

            if (lease.ExpiresAt < oldestExpiry)
            {
                oldestExpiry = lease.ExpiresAt;
                oldest = i;
            }
        }

        int index = matchingTarget >= 0
            ? matchingTarget
            : (free >= 0 ? free : oldest);
        if (index < 0)
            return;

        Lease next = matchingTarget >= 0 ? leases[index] : default(Lease);
        bool duplicateGrant = matchingTarget >= 0 &&
            next.SourcePlayerId == sourcePlayerId &&
            next.Sequence == sequence;

        // One source owns a monotonic grant sequence for the session. Ignore an
        // older same-source relay after a newer observation has already refreshed
        // this target; grants from another source remain independent refreshes.
        if (matchingTarget >= 0 &&
            next.SourcePlayerId == sourcePlayerId &&
            sequence < next.Sequence)
        {
            return;
        }

        next.Active = true;
        next.PlayerId = playerId;
        if (!duplicateGrant)
        {
            next.SourcePlayerId = sourcePlayerId;
            next.Sequence = sequence;
            next.ExpiresAt = now + durationSeconds;
        }

        float remaining = next.ExpiresAt - now;
        if (remaining <= 0f)
        {
            if (next.BoundShip != null)
                OrreryColdFusionPresentation.Hide(next.BoundShip);
            leases[index] = default(Lease);
            return;
        }

        GameShip target = ResolvePlayerShip(session, playerId);
        if (target != null)
        {
            bool rebound = next.BoundShip == null ||
                !object.ReferenceEquals(next.BoundShip, target);

            if (next.BoundShip != null && rebound)
                OrreryColdFusionPresentation.Hide(next.BoundShip);

            next.BoundShip = target;
            if (!duplicateGrant || rebound)
                OrreryColdFusionPresentation.Show(target, remaining);
        }

        leases[index] = next;
    }

    public static void Tick()
    {
        float now = Time.time;
        NetSession session = NetSession.instance;

        for (int i = 0; i < leases.Length; i++)
        {
            Lease lease = leases[i];
            if (!lease.Active)
                continue;

            float remaining = lease.ExpiresAt - now;
            if (remaining <= 0f)
            {
                if (lease.BoundShip != null)
                    OrreryColdFusionPresentation.Hide(lease.BoundShip);
                leases[i] = default(Lease);
                continue;
            }

            // A live binding needs no replica lookup. OnDestroy clears BoundShip,
            // so reflection is paid only while waiting for initial/replacement
            // replica availability rather than every frame of every active aura.
            if (lease.BoundShip != null)
                continue;

            if (session == null)
                continue;

            GameShip current = ResolvePlayerShip(session, lease.PlayerId);
            if (current == null)
                continue;

            lease.BoundShip = current;
            leases[i] = lease;
            OrreryColdFusionPresentation.Show(current, remaining);
        }
    }

    public static void OnShipDestroyed(GameShip ship)
    {
        if (object.ReferenceEquals(ship, null))
            return;

        for (int i = 0; i < leases.Length; i++)
        {
            Lease lease = leases[i];
            if (!lease.Active ||
                !object.ReferenceEquals(lease.BoundShip, ship))
            {
                continue;
            }

            // OnDestroy may only be a replica replacement. Keep the player lease
            // alive so Tick can bind it to the replacement for remaining duration.
            lease.BoundShip = null;
            leases[i] = lease;
        }
    }

    public static void OnShipDied(GameShip ship)
    {
        if (object.ReferenceEquals(ship, null))
            return;

        for (int i = 0; i < leases.Length; i++)
        {
            Lease lease = leases[i];
            if (!lease.Active ||
                !object.ReferenceEquals(lease.BoundShip, ship))
            {
                continue;
            }

            // GameShip.Destroyed is a gameplay death/retirement boundary. Do not
            // migrate this effect's presentation onto a respawned ship.
            OrreryColdFusionPresentation.Hide(ship);
            leases[i] = default(Lease);
        }
    }

    public static void Reset()
    {
        for (int i = 0; i < leases.Length; i++)
            leases[i] = default(Lease);
    }

    private static GameShip ResolvePlayerShip(NetSession session, int playerId)
    {
        if (session == null || playerId < 0)
            return null;

        if (playerId == session.localPlayerId)
        {
            return WorldController.instance == null
                ? null
                : WorldController.instance.GetCurrentPlayerShip();
        }

        if (ActiveBridgeField == null || RepsField == null)
            return null;

        NetWorldBridge bridge = ActiveBridgeField.GetValue(session) as NetWorldBridge;
        if (bridge == null)
            return null;

        IDictionary reps = RepsField.GetValue(bridge) as IDictionary;
        if (reps == null || !reps.Contains(playerId))
            return null;

        RemoteShipDriver driver = reps[playerId] as RemoteShipDriver;
        return driver == null ? null : driver.gameShip;
    }
}

[HarmonyPatch(typeof(OrreryColdFusionPresentation),
    nameof(OrreryColdFusionPresentation.ObserveGrant))]
public static class OrreryColdFusionPresentationLeaseGrantPatch
{
    public static void Postfix(NetSession session, string json)
    {
        OrreryColdFusionPresentationLease.ObserveGrant(session, json);
    }
}

[HarmonyPatch(typeof(OrreryColdFusionPresentation),
    nameof(OrreryColdFusionPresentation.ObserveLocalRequest))]
public static class OrreryColdFusionPresentationLeaseLocalPatch
{
    public static void Postfix(
        int targetPlayerId,
        ushort effectId,
        CoreCrossOwnerEffects.GrantPayload payload,
        bool accepted)
    {
        OrreryColdFusionPresentationLease.ObserveLocalRequest(
            targetPlayerId,
            effectId,
            payload,
            accepted);
    }
}

[HarmonyPatch(typeof(OrreryColdFusionPresentation),
    nameof(OrreryColdFusionPresentation.Tick))]
public static class OrreryColdFusionPresentationLeaseTickPatch
{
    public static void Postfix()
    {
        OrreryColdFusionPresentationLease.Tick();
    }
}

[HarmonyPatch(typeof(GameShip), "Destroyed")]
public static class OrreryColdFusionPresentationLeaseShipDiedPatch
{
    public static void Prefix(GameShip __instance)
    {
        OrreryColdFusionPresentationLease.OnShipDied(__instance);
    }
}

[HarmonyPatch(typeof(GameShip), "OnDestroy")]
public static class OrreryColdFusionPresentationLeaseShipDestroyedPatch
{
    public static void Prefix(GameShip __instance)
    {
        OrreryColdFusionPresentationLease.OnShipDestroyed(__instance);
    }
}

[HarmonyPatch(typeof(OrreryColdFusionPresentation),
    nameof(OrreryColdFusionPresentation.Reset))]
public static class OrreryColdFusionPresentationLeaseResetPatch
{
    public static void Postfix()
    {
        OrreryColdFusionPresentationLease.Reset();
    }
}
