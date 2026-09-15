using StarVortex;
using UnityEngine;

/// <summary>
/// Bounded player-identity lease for ally-targeted Accretion presentation.
/// Gameplay already lives on the protected player's authority; this only keeps
/// the placeholder aura attached across delayed/replaced remote replicas.
/// </summary>
public static class OrreryAccretionDiskPresentationLease
{
    private const int MaxLeases = 16;

    private struct Lease
    {
        public bool Active;
        public int PlayerId;
        public int SourcePlayerId;
        public int Sequence;
        public float ExpiresAt;
        public float RadiusMeters;
        public GameShip BoundShip;
    }

    private static readonly Lease[] leases = new Lease[MaxLeases];

    public static void ObserveGrant(
        NetSession session,
        CoreCrossOwnerEffects.GrantNotice notice)
    {
        float duration;
        float radius;
        if (!OrreryAccretionDisk.TryReadPresentationGrant(
                notice.Payload,
                out duration,
                out radius))
        {
            return;
        }

        Remember(
            session,
            notice.SourcePlayerId,
            notice.TargetPlayerId,
            notice.Sequence,
            duration,
            radius);
    }

    private static void Remember(
        NetSession session,
        int sourcePlayerId,
        int playerId,
        int sequence,
        float durationSeconds,
        float radiusMeters)
    {
        if (session == null || sourcePlayerId < 0 || playerId < 0 ||
            sequence <= 0 || durationSeconds <= 0f || radiusMeters <= 0f)
        {
            return;
        }

        float now = Time.time;
        int match = -1;
        int free = -1;
        int oldest = -1;
        float oldestExpiry = float.PositiveInfinity;

        for (int i = 0; i < leases.Length; i++)
        {
            Lease lease = leases[i];
            if (lease.Active && lease.PlayerId == playerId)
            {
                match = i;
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

        int index = match >= 0 ? match : (free >= 0 ? free : oldest);
        if (index < 0)
            return;

        Lease next = match >= 0 ? leases[index] : default(Lease);
        bool duplicate = match >= 0 &&
            next.SourcePlayerId == sourcePlayerId &&
            next.Sequence == sequence;

        if (match >= 0 && next.SourcePlayerId == sourcePlayerId &&
            sequence < next.Sequence)
        {
            return;
        }

        next.Active = true;
        next.PlayerId = playerId;
        if (!duplicate)
        {
            next.SourcePlayerId = sourcePlayerId;
            next.Sequence = sequence;
            next.ExpiresAt = now + durationSeconds;
            next.RadiusMeters = radiusMeters;
        }

        float remaining = next.ExpiresAt - now;
        if (remaining <= 0f)
        {
            Clear(index);
            return;
        }

        GameShip target;
        if (CoreProjectileAuthority.TryGetPlayerShip(playerId, out target) &&
            target != null)
        {
            bool rebound = next.BoundShip == null ||
                !object.ReferenceEquals(next.BoundShip, target);
            if (next.BoundShip != null && rebound)
                OrreryAccretionDiskPresentation.Hide(next.BoundShip);

            next.BoundShip = target;
            if (!duplicate || rebound)
            {
                OrreryAccretionDiskPresentation.Show(
                    target,
                    remaining,
                    next.RadiusMeters,
                    1f);
            }
        }

        leases[index] = next;
    }

    public static void Tick()
    {
        float now = Time.time;
        for (int i = 0; i < leases.Length; i++)
        {
            Lease lease = leases[i];
            if (!lease.Active)
                continue;

            float remaining = lease.ExpiresAt - now;
            if (remaining <= 0f)
            {
                Clear(i);
                continue;
            }

            if (lease.BoundShip != null)
                continue;

            GameShip current;
            if (!CoreProjectileAuthority.TryGetPlayerShip(
                    lease.PlayerId,
                    out current) || current == null)
            {
                continue;
            }

            lease.BoundShip = current;
            leases[i] = lease;
            OrreryAccretionDiskPresentation.Show(
                current,
                remaining,
                lease.RadiusMeters,
                1f);
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
            if (leases[i].Active &&
                object.ReferenceEquals(leases[i].BoundShip, ship))
            {
                Clear(i);
            }
        }
    }

    public static void Reset()
    {
        for (int i = 0; i < leases.Length; i++)
            Clear(i);
    }

    private static void Clear(int index)
    {
        if (index < 0 || index >= leases.Length)
            return;

        Lease lease = leases[index];
        if (lease.Active && lease.BoundShip != null)
            OrreryAccretionDiskPresentation.Hide(lease.BoundShip);
        leases[index] = default(Lease);
    }
}
