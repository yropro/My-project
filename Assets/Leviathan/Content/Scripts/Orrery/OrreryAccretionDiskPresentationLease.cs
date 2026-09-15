using StarVortex;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Recipient-authored snapshots replace unconfirmed cast-grant visuals.
/// Missing optional data does not cancel an effect. Explicit terminal snapshots,
/// monotone samples and bounded local deadlines prevent resurrection/restarts.</summary>
public static class OrreryAccretionDiskPresentationLease
{
    public struct Snapshot
    {
        public bool Active;
        public uint Sample;
        public float RemainingSeconds, DurationSeconds, RadiusWorld;
        public byte Capacity;
    }
    private struct Lease
    {
        public uint Generation, Sample;
        public bool Ended;
        public float ExpiresAt, Duration;
        public byte Capacity;
    }
    private static readonly OrreryNetwork.Channel<Snapshot> channel =
        new OrreryNetwork.Channel<Snapshot>(OrreryPresentationNetwork.CodecAccretion, Wire);
    private static readonly Dictionary<GameShip, Lease> leases = new Dictionary<GameShip, Lease>(16);
    private static uint nextSample;

    // Active payload: 18 bytes; terminal payload: 5 bytes. The enclosing channel
    // supplies generation. The format has no scene/clock-dependent branches.
    public static void Wire(ref CoreWire wire, ref Snapshot state)
    {
        wire.Flags(ref state.Active);
        wire.UInt32(ref state.Sample);
        if (state.Sample == 0u) wire.Fail();
        if (!state.Active) return;
        wire.Positive(ref state.RemainingSeconds);
        wire.Positive(ref state.DurationSeconds);
        wire.Positive(ref state.RadiusWorld);
        wire.Byte(ref state.Capacity);
        if (state.RemainingSeconds > state.DurationSeconds || state.DurationSeconds > 3600f ||
            state.RadiusWorld > 100000f) wire.Fail();
    }
    public static void Publish()
    {
        Snapshot snapshot;
        uint generation;
        if (!OrreryAccretionDisk.TryGetPresentation(out snapshot, out generation)) return;
        do { unchecked { nextSample++; } } while (nextSample == 0u);
        snapshot.Sample = nextSample;
        channel.Publish(generation, ref snapshot);
    }
    public static void Render(GameShip owner, float deltaTime)
    {
        Snapshot snapshot = default(Snapshot);
        uint generation;
        if (owner == null || owner.health <= 0f ||
            !channel.TryRead(owner, ref snapshot, out generation)) return;
        Lease lease;
        bool known = leases.TryGetValue(owner, out lease);
        bool same = known && lease.Generation == generation;
        if (known && !same && !Newer(generation, lease.Generation)) return;
        if (same && (!Newer(snapshot.Sample, lease.Sample) || lease.Ended)) return;
        if (!known && leases.Count >= 16) return;
        float expires = Time.time + snapshot.RemainingSeconds;
        if (same)
        {
            expires = Mathf.Min(expires, lease.ExpiresAt);
            if (snapshot.Active && snapshot.DurationSeconds != lease.Duration) return;
            snapshot.Capacity = (byte)Mathf.Min(snapshot.Capacity, lease.Capacity);
        }
        bool ended = !snapshot.Active || expires <= Time.time;
        leases[owner] = new Lease { Generation = generation, Sample = snapshot.Sample,
            Ended = ended, ExpiresAt = expires, Duration = snapshot.DurationSeconds,
            Capacity = snapshot.Capacity };
        if (ended) { OrreryAccretionDiskPresentation.Hide(owner); return; }
        OrreryAccretionDiskPresentation.Show(owner, expires - Time.time,
            OrreryUnits.WorldToMeters(snapshot.RadiusWorld), snapshot.Capacity / 255f,
            generation, Mathf.Clamp01((expires - Time.time) / snapshot.DurationSeconds));
    }
    public static void Died(GameShip owner)
    {
        if (object.ReferenceEquals(owner, null)) return;
        Lease lease;
        if (leases.TryGetValue(owner, out lease))
        {
            lease.Ended = true;
            leases[owner] = lease;
        }
        OrreryAccretionDiskPresentation.Hide(owner);
    }
    public static void Forget(GameShip owner)
    {
        if (object.ReferenceEquals(owner, null)) return;
        leases.Remove(owner);
        OrreryAccretionDiskPresentation.Hide(owner);
    }
    public static void Reset()
    {
        leases.Clear();
        OrreryAccretionDiskPresentation.Reset();
    }
    private static bool Newer(uint a, uint b) { return unchecked((int)(a - b)) > 0; }
}
