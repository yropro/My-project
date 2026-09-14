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
        public float ExpiresAt;
        public GameShip BoundShip;
    }

    [Serializable]
    private sealed class GrantEnvelopeView
    {
        public int targetPlayerId;
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
            envelope.targetPlayerId < 0)
        {
            return;
        }

        float duration = new FloatBits
        {
            UInt = unchecked((uint)envelope.payloadA)
        }.Float;
        Remember(session, envelope.targetPlayerId, duration);
    }

    public static void ObserveLocalRequest(
        int targetPlayerId,
        ushort effectId,
        CoreCrossOwnerEffects.GrantPayload payload,
        bool accepted)
    {
        if (!accepted || effectId != OrreryColdFusion.CrossOwnerEffectId ||
            targetPlayerId < 0 || NetSession.instance == null)
        {
            return;
        }

        float duration = new FloatBits { UInt = payload.A }.Float;
        Remember(NetSession.instance, targetPlayerId, duration);
    }

    private static void Remember(
        NetSession session,
        int playerId,
        float durationSeconds)
    {
        if (session == null || playerId < 0 ||
            float.IsNaN(durationSeconds) ||
            float.IsInfinity(durationSeconds) ||
            durationSeconds <= 0f)
        {
            return;
        }

        float now = Time.time;
        float expiresAt = now + durationSeconds;
        int matching = -1;
        int free = -1;
        int oldest = -1;
        float oldestExpiry = float.PositiveInfinity;

        for (int i = 0; i < leases.Length; i++)
        {
            Lease lease = leases[i];
            if (lease.Active && lease.PlayerId == playerId)
            {
                matching = i;
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

        int index = matching >= 0 ? matching : (free >= 0 ? free : oldest);
        if (index < 0)
            return;

        Lease next = matching >= 0 ? leases[index] : default(Lease);
        next.Active = true;
        next.PlayerId = playerId;
        next.ExpiresAt = matching >= 0
            ? Mathf.Max(next.ExpiresAt, expiresAt)
            : expiresAt;

        GameShip target = ResolvePlayerShip(session, playerId);
        if (target != null)
        {
            if (next.BoundShip != null &&
                !object.ReferenceEquals(next.BoundShip, target))
            {
                OrreryColdFusionPresentation.Hide(next.BoundShip);
            }

            next.BoundShip = target;
            OrreryColdFusionPresentation.Show(
                target,
                Mathf.Max(0.001f, next.ExpiresAt - now));
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

            if (session == null)
                continue;

            GameShip current = ResolvePlayerShip(session, lease.PlayerId);
            if (current == null || object.ReferenceEquals(current, lease.BoundShip))
                continue;

            if (lease.BoundShip != null)
                OrreryColdFusionPresentation.Hide(lease.BoundShip);

            lease.BoundShip = current;
            leases[i] = lease;
            OrreryColdFusionPresentation.Show(current, remaining);
        }
    }

    /// <summary>
    /// Bare Unity destruction can be a harmless remote-replica replacement. Keep
    /// the player-id lease in that case and let Tick attach it to the replacement
    /// replica for only the authored remaining duration.
    /// </summary>
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

            lease.BoundShip = null;
            leases[i] = lease;
        }
    }

    /// <summary>
    /// Native GameShip.Destroyed is an actual gameplay death/retirement boundary,
    /// not merely a replica object replacement. The timed gameplay effect dies with
    /// that ship, so its presentation lease must not migrate onto a respawned ship.
    /// </summary>
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
