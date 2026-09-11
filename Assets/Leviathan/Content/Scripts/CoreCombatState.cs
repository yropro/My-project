using StarVortex;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Bounded local registry for temporary semantic combat truth.
///
/// State is intentionally distinct from combat events/history.  OwnerTarget and
/// TargetGlobal states are source-qualified by default, so two owners applying
/// the same semantic state to one target do not overwrite one another.
///
/// TargetGlobal means "installed on the target authority".  This local registry
/// does not imply that applying TargetGlobal on an attacker has replicated it to
/// a remote target.  The sparse reliable authority-transfer channel is a later
/// phase; it can call the same key-based methods after delivery.
/// </summary>
public static class CoreCombatState
{
    public enum Scope : byte
    {
        OwnerSelf = 1,
        OwnerTarget = 2,
        TargetGlobal = 3
    }

    public struct Snapshot
    {
        public CoreCombat.SemanticKey Semantic;
        public Scope StateScope;
        public CoreCombat.CombatEntityKey SourceOwner;
        public CoreCombat.CombatEntityKey Target;
        public int Stacks;
        public float Magnitude;
        public float AppliedAt;
        public float RefreshedAt;
        public float ExpiresAt;
        public uint LastAuthoredEventId;
        public byte Flags;

        public bool HasExpiry { get { return ExpiresAt > 0f; } }
    }

    private struct StateKey : IEquatable<StateKey>
    {
        public Scope StateScope;
        public CoreCombat.CombatEntityKey SourceOwner;
        public CoreCombat.CombatEntityKey Target;
        public CoreCombat.SemanticKey Semantic;

        public bool Equals(StateKey other)
        {
            return StateScope == other.StateScope &&
                   SourceOwner.Equals(other.SourceOwner) &&
                   Target.Equals(other.Target) &&
                   Semantic.Equals(other.Semantic);
        }

        public override bool Equals(object obj)
        {
            return obj is StateKey && Equals((StateKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)StateScope;
                hash = (hash * 397) ^ SourceOwner.GetHashCode();
                hash = (hash * 397) ^ Target.GetHashCode();
                hash = (hash * 397) ^ Semantic.GetHashCode();
                return hash;
            }
        }
    }

    private struct StateRecord
    {
        public int Stacks;
        public float Magnitude;
        public float AppliedAt;
        public float RefreshedAt;
        public float ExpiresAt;
        public uint LastAuthoredEventId;
        public byte Flags;
    }

    private const int MaxStates = 4096;
    private const float PruneIntervalSeconds = 1f;

    private static readonly Dictionary<StateKey, StateRecord> States =
        new Dictionary<StateKey, StateRecord>(256);

    private static readonly List<StateKey> PruneScratch =
        new List<StateKey>(128);

    private static float nextPruneAt;

    public static int Count { get { return States.Count; } }

    // ---------------------------------------------------------------------
    // GameShip convenience API
    // ---------------------------------------------------------------------

    public static bool Apply(
        GameShip sourceOwner,
        GameShip target,
        CoreCombat.SemanticKey semantic,
        Scope scope,
        float durationSeconds,
        int stacks = 1,
        float magnitude = 0f,
        byte flags = 0)
    {
        CoreCombat.CombatEntityKey ownerKey;
        CoreCombat.CombatEntityKey targetKey;
        if (!ResolveKeys(sourceOwner, target, scope, out ownerKey, out targetKey))
            return false;

        return Apply(ownerKey, targetKey, semantic, scope, durationSeconds,
            stacks, magnitude, flags);
    }

    /// <summary>
    /// Applies state using a source-side gameplay occurrence time. This is
    /// local-only: absolute Unity Time.time values are never sent between peers.
    ///
    /// Used when a remote-authority combat result returns later than the action
    /// that caused it. Expiry remains anchored to OccurredAt so latency/jitter
    /// cannot lengthen gameplay durations. A late older confirmation is not
    /// allowed to rewind a state that was already refreshed by a newer event.
    /// </summary>
    public static bool ApplyAt(
        GameShip sourceOwner,
        GameShip target,
        CoreCombat.SemanticKey semantic,
        Scope scope,
        float occurredAt,
        float durationSeconds,
        int stacks = 1,
        float magnitude = 0f,
        byte flags = 0)
    {
        return ApplyAt(sourceOwner, target, semantic, scope, occurredAt, 0U,
            durationSeconds, stacks, magnitude, flags);
    }

    /// <summary>
    /// Event-ordered OccurredAt application for correlated combat outcomes.
    /// When several authored events share one Time.time value, EventId is the
    /// deterministic secondary ordering key; result-arrival order is ignored.
    /// </summary>
    public static bool ApplyAt(
        GameShip sourceOwner,
        GameShip target,
        CoreCombat.SemanticKey semantic,
        Scope scope,
        float occurredAt,
        uint eventId,
        float durationSeconds,
        int stacks = 1,
        float magnitude = 0f,
        byte flags = 0)
    {
        CoreCombat.CombatEntityKey ownerKey;
        CoreCombat.CombatEntityKey targetKey;
        if (!ResolveKeys(sourceOwner, target, scope, out ownerKey, out targetKey))
            return false;

        return ApplyAt(ownerKey, targetKey, semantic, scope, occurredAt, eventId,
            durationSeconds, stacks, magnitude, flags);
    }

    public static bool Set(
        GameShip sourceOwner,
        GameShip target,
        CoreCombat.SemanticKey semantic,
        Scope scope,
        float durationSeconds,
        int stacks,
        float magnitude = 0f,
        byte flags = 0)
    {
        return Apply(sourceOwner, target, semantic, scope, durationSeconds,
            stacks, magnitude, flags);
    }

    public static bool Refresh(
        GameShip sourceOwner,
        GameShip target,
        CoreCombat.SemanticKey semantic,
        Scope scope,
        float durationSeconds)
    {
        CoreCombat.CombatEntityKey ownerKey;
        CoreCombat.CombatEntityKey targetKey;
        if (!ResolveKeys(sourceOwner, target, scope, out ownerKey, out targetKey))
            return false;

        return Refresh(ownerKey, targetKey, semantic, scope, durationSeconds);
    }

    public static bool AddStacks(
        GameShip sourceOwner,
        GameShip target,
        CoreCombat.SemanticKey semantic,
        Scope scope,
        int amount,
        float refreshDurationSeconds = -1f)
    {
        CoreCombat.CombatEntityKey ownerKey;
        CoreCombat.CombatEntityKey targetKey;
        if (!ResolveKeys(sourceOwner, target, scope, out ownerKey, out targetKey))
            return false;

        return AddStacks(ownerKey, targetKey, semantic, scope, amount,
            refreshDurationSeconds);
    }

    public static bool SetStacks(
        GameShip sourceOwner,
        GameShip target,
        CoreCombat.SemanticKey semantic,
        Scope scope,
        int stacks)
    {
        CoreCombat.CombatEntityKey ownerKey;
        CoreCombat.CombatEntityKey targetKey;
        if (!ResolveKeys(sourceOwner, target, scope, out ownerKey, out targetKey))
            return false;

        return SetStacks(ownerKey, targetKey, semantic, scope, stacks);
    }

    public static bool Remove(
        GameShip sourceOwner,
        GameShip target,
        CoreCombat.SemanticKey semantic,
        Scope scope)
    {
        CoreCombat.CombatEntityKey ownerKey;
        CoreCombat.CombatEntityKey targetKey;
        if (!ResolveKeys(sourceOwner, target, scope, out ownerKey, out targetKey))
            return false;

        return Remove(ownerKey, targetKey, semantic, scope);
    }

    public static bool Has(
        GameShip sourceOwner,
        GameShip target,
        CoreCombat.SemanticKey semantic,
        Scope scope)
    {
        CoreCombat.CombatEntityKey ownerKey;
        CoreCombat.CombatEntityKey targetKey;
        if (!ResolveKeys(sourceOwner, target, scope, out ownerKey, out targetKey))
            return false;

        return Has(ownerKey, targetKey, semantic, scope);
    }

    public static int GetStacks(
        GameShip sourceOwner,
        GameShip target,
        CoreCombat.SemanticKey semantic,
        Scope scope)
    {
        CoreCombat.CombatEntityKey ownerKey;
        CoreCombat.CombatEntityKey targetKey;
        if (!ResolveKeys(sourceOwner, target, scope, out ownerKey, out targetKey))
            return 0;

        return GetStacks(ownerKey, targetKey, semantic, scope);
    }

    public static float GetRemainingSeconds(
        GameShip sourceOwner,
        GameShip target,
        CoreCombat.SemanticKey semantic,
        Scope scope)
    {
        CoreCombat.CombatEntityKey ownerKey;
        CoreCombat.CombatEntityKey targetKey;
        if (!ResolveKeys(sourceOwner, target, scope, out ownerKey, out targetKey))
            return 0f;

        return GetRemainingSeconds(ownerKey, targetKey, semantic, scope);
    }

    public static bool TryGet(
        GameShip sourceOwner,
        GameShip target,
        CoreCombat.SemanticKey semantic,
        Scope scope,
        out Snapshot snapshot)
    {
        snapshot = default(Snapshot);

        CoreCombat.CombatEntityKey ownerKey;
        CoreCombat.CombatEntityKey targetKey;
        if (!ResolveKeys(sourceOwner, target, scope, out ownerKey, out targetKey))
            return false;

        return TryGet(ownerKey, targetKey, semantic, scope, out snapshot);
    }

    // ---------------------------------------------------------------------
    // Key API.  This is also the future TargetGlobal authority-delivery seam.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Key-based OccurredAt application seam. TargetGlobal authority delivery
    /// can use this after converting a transmitted duration to the target
    /// authority's own local clock; callers must not transmit occurredAt itself.
    /// </summary>
    public static bool ApplyAt(
        CoreCombat.CombatEntityKey sourceOwner,
        CoreCombat.CombatEntityKey target,
        CoreCombat.SemanticKey semantic,
        Scope scope,
        float occurredAt,
        float durationSeconds,
        int stacks = 1,
        float magnitude = 0f,
        byte flags = 0)
    {
        return ApplyAt(sourceOwner, target, semantic, scope, occurredAt, 0U,
            durationSeconds, stacks, magnitude, flags);
    }

    public static bool ApplyAt(
        CoreCombat.CombatEntityKey sourceOwner,
        CoreCombat.CombatEntityKey target,
        CoreCombat.SemanticKey semantic,
        Scope scope,
        float occurredAt,
        uint eventId,
        float durationSeconds,
        int stacks = 1,
        float magnitude = 0f,
        byte flags = 0)
    {
        if (!ValidateKeyParts(sourceOwner, target, semantic, scope))
            return false;

        float now = Time.time;
        if (occurredAt <= 0f)
            occurredAt = now;

        float expiresAt = ExpiryFromDuration(occurredAt, durationSeconds);

        // A delayed confirmation whose authored duration has already elapsed is
        // a confirmed historical fact, but it is not an active state now.
        if (expiresAt > 0f && expiresAt <= now)
            return false;

        PruneExpired(now, false);

        StateKey key = MakeKey(sourceOwner, target, semantic, scope);
        StateRecord existing;
        bool hadExisting = States.TryGetValue(key, out existing) &&
            !IsExpired(existing, now);

        // Confirmation order is not causal order. OccurredAt is primary and
        // EventId breaks same-frame ties in authored order. An older result may
        // never rewind stacks/magnitude/expiry simply because it arrived later.
        if (hadExisting && !IsAuthoredLaterOrEqual(occurredAt, eventId,
            existing.RefreshedAt, existing.LastAuthoredEventId))
        {
            return true;
        }

        if (!hadExisting && States.Count >= MaxStates)
        {
            PruneExpired(now, true);
            if (States.Count >= MaxStates)
                return false;
        }

        StateRecord record = existing;
        record.Stacks = Mathf.Max(0, stacks);
        record.Magnitude = magnitude;
        record.Flags = flags;
        record.RefreshedAt = occurredAt;
        record.ExpiresAt = expiresAt;
        record.LastAuthoredEventId = eventId;
        if (!hadExisting)
            record.AppliedAt = occurredAt;

        States[key] = record;
        return true;
    }

    public static bool Apply(
        CoreCombat.CombatEntityKey sourceOwner,
        CoreCombat.CombatEntityKey target,
        CoreCombat.SemanticKey semantic,
        Scope scope,
        float durationSeconds,
        int stacks = 1,
        float magnitude = 0f,
        byte flags = 0)
    {
        if (!ValidateKeyParts(sourceOwner, target, semantic, scope))
            return false;

        float now = Time.time;
        PruneExpired(now, false);

        StateKey key = MakeKey(sourceOwner, target, semantic, scope);
        StateRecord existing;
        bool hadExisting = States.TryGetValue(key, out existing) &&
            !IsExpired(existing, now);

        if (!hadExisting && States.Count >= MaxStates)
        {
            PruneExpired(now, true);
            if (States.Count >= MaxStates)
                return false;
        }

        StateRecord record = existing;
        record.Stacks = Mathf.Max(0, stacks);
        record.Magnitude = magnitude;
        record.Flags = flags;
        record.RefreshedAt = now;
        record.ExpiresAt = ExpiryFromDuration(now, durationSeconds);
        record.LastAuthoredEventId = 0U;

        if (!hadExisting)
            record.AppliedAt = now;

        States[key] = record;
        return true;
    }

    public static bool Refresh(
        CoreCombat.CombatEntityKey sourceOwner,
        CoreCombat.CombatEntityKey target,
        CoreCombat.SemanticKey semantic,
        Scope scope,
        float durationSeconds)
    {
        StateKey key;
        StateRecord record;
        float now = Time.time;
        if (!TryGetLiveRecord(sourceOwner, target, semantic, scope, now,
            out key, out record))
        {
            return false;
        }

        record.RefreshedAt = now;
        record.ExpiresAt = ExpiryFromDuration(now, durationSeconds);
        record.LastAuthoredEventId = 0U;
        States[key] = record;
        return true;
    }

    public static bool AddStacks(
        CoreCombat.CombatEntityKey sourceOwner,
        CoreCombat.CombatEntityKey target,
        CoreCombat.SemanticKey semantic,
        Scope scope,
        int amount,
        float refreshDurationSeconds = -1f)
    {
        StateKey key;
        StateRecord record;
        float now = Time.time;
        if (!TryGetLiveRecord(sourceOwner, target, semantic, scope, now,
            out key, out record))
        {
            return false;
        }

        long next = (long)record.Stacks + amount;
        if (next < 0) next = 0;
        if (next > int.MaxValue) next = int.MaxValue;
        record.Stacks = (int)next;
        record.RefreshedAt = now;
        record.LastAuthoredEventId = 0U;

        if (refreshDurationSeconds >= 0f)
            record.ExpiresAt = ExpiryFromDuration(now, refreshDurationSeconds);

        States[key] = record;
        return true;
    }

    public static bool SetStacks(
        CoreCombat.CombatEntityKey sourceOwner,
        CoreCombat.CombatEntityKey target,
        CoreCombat.SemanticKey semantic,
        Scope scope,
        int stacks)
    {
        StateKey key;
        StateRecord record;
        float now = Time.time;
        if (!TryGetLiveRecord(sourceOwner, target, semantic, scope, now,
            out key, out record))
        {
            return false;
        }

        record.Stacks = Mathf.Max(0, stacks);
        record.RefreshedAt = now;
        record.LastAuthoredEventId = 0U;
        States[key] = record;
        return true;
    }

    public static bool SetMagnitude(
        CoreCombat.CombatEntityKey sourceOwner,
        CoreCombat.CombatEntityKey target,
        CoreCombat.SemanticKey semantic,
        Scope scope,
        float magnitude)
    {
        StateKey key;
        StateRecord record;
        float now = Time.time;
        if (!TryGetLiveRecord(sourceOwner, target, semantic, scope, now,
            out key, out record))
        {
            return false;
        }

        record.Magnitude = magnitude;
        record.RefreshedAt = now;
        record.LastAuthoredEventId = 0U;
        States[key] = record;
        return true;
    }

    public static bool Remove(
        CoreCombat.CombatEntityKey sourceOwner,
        CoreCombat.CombatEntityKey target,
        CoreCombat.SemanticKey semantic,
        Scope scope)
    {
        if (!ValidateKeyParts(sourceOwner, target, semantic, scope))
            return false;

        return States.Remove(MakeKey(sourceOwner, target, semantic, scope));
    }

    public static bool Has(
        CoreCombat.CombatEntityKey sourceOwner,
        CoreCombat.CombatEntityKey target,
        CoreCombat.SemanticKey semantic,
        Scope scope)
    {
        StateKey key;
        StateRecord record;
        return TryGetLiveRecord(sourceOwner, target, semantic, scope, Time.time,
            out key, out record);
    }

    public static int GetStacks(
        CoreCombat.CombatEntityKey sourceOwner,
        CoreCombat.CombatEntityKey target,
        CoreCombat.SemanticKey semantic,
        Scope scope)
    {
        StateKey key;
        StateRecord record;
        return TryGetLiveRecord(sourceOwner, target, semantic, scope, Time.time,
            out key, out record) ? record.Stacks : 0;
    }

    public static float GetRemainingSeconds(
        CoreCombat.CombatEntityKey sourceOwner,
        CoreCombat.CombatEntityKey target,
        CoreCombat.SemanticKey semantic,
        Scope scope)
    {
        StateKey key;
        StateRecord record;
        float now = Time.time;
        if (!TryGetLiveRecord(sourceOwner, target, semantic, scope, now,
            out key, out record))
        {
            return 0f;
        }

        if (record.ExpiresAt <= 0f)
            return float.PositiveInfinity;

        return Mathf.Max(0f, record.ExpiresAt - now);
    }

    public static bool TryGet(
        CoreCombat.CombatEntityKey sourceOwner,
        CoreCombat.CombatEntityKey target,
        CoreCombat.SemanticKey semantic,
        Scope scope,
        out Snapshot snapshot)
    {
        snapshot = default(Snapshot);

        StateKey key;
        StateRecord record;
        if (!TryGetLiveRecord(sourceOwner, target, semantic, scope, Time.time,
            out key, out record))
        {
            return false;
        }

        snapshot.Semantic = semantic;
        snapshot.StateScope = scope;
        snapshot.SourceOwner = sourceOwner;
        snapshot.Target = key.Target;
        snapshot.Stacks = record.Stacks;
        snapshot.Magnitude = record.Magnitude;
        snapshot.AppliedAt = record.AppliedAt;
        snapshot.RefreshedAt = record.RefreshedAt;
        snapshot.ExpiresAt = record.ExpiresAt;
        snapshot.LastAuthoredEventId = record.LastAuthoredEventId;
        snapshot.Flags = record.Flags;
        return true;
    }

    /// <summary>
    /// Removes states authored by one skill for this owner. This is a lifecycle
    /// operation (respec/skill-runtime replacement), not a hot combat query.
    /// TargetGlobal remains source-qualified, so only this owner's instances are
    /// removed.
    /// </summary>
    public static void ResetOwnerSkill(
        CoreCombat.CombatEntityKey sourceOwner,
        byte skillId)
    {
        if (!sourceOwner.IsValid || skillId == CoreCombat.SkillIds.None)
            return;

        PruneScratch.Clear();
        foreach (KeyValuePair<StateKey, StateRecord> pair in States)
        {
            if (pair.Key.SourceOwner.Equals(sourceOwner) &&
                pair.Key.Semantic.SkillId == skillId)
            {
                PruneScratch.Add(pair.Key);
            }
        }

        RemoveScratchKeys();
    }

    /// <summary>Removes every state authored by this owner runtime.</summary>
    public static void ResetOwner(CoreCombat.CombatEntityKey sourceOwner)
    {
        if (!sourceOwner.IsValid)
            return;

        PruneScratch.Clear();
        foreach (KeyValuePair<StateKey, StateRecord> pair in States)
        {
            if (pair.Key.SourceOwner.Equals(sourceOwner))
                PruneScratch.Add(pair.Key);
        }

        RemoveScratchKeys();
    }

    /// <summary>Removes states whose target identity no longer exists.</summary>
    public static void ResetTarget(CoreCombat.CombatEntityKey target)
    {
        if (!target.IsValid)
            return;

        PruneScratch.Clear();
        foreach (KeyValuePair<StateKey, StateRecord> pair in States)
        {
            if (pair.Key.Target.Equals(target))
                PruneScratch.Add(pair.Key);
        }

        RemoveScratchKeys();
    }

    public static void Reset()
    {
        States.Clear();
        PruneScratch.Clear();
        nextPruneAt = 0f;
    }

    public static void Prune()
    {
        PruneExpired(Time.time, true);
    }

    // ---------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------

    private static bool ResolveKeys(
        GameShip sourceOwner,
        GameShip target,
        Scope scope,
        out CoreCombat.CombatEntityKey sourceOwnerKey,
        out CoreCombat.CombatEntityKey targetKey)
    {
        sourceOwnerKey = default(CoreCombat.CombatEntityKey);
        targetKey = default(CoreCombat.CombatEntityKey);

        if (!CoreCombat.TryGetEntityKey(sourceOwner, out sourceOwnerKey))
            return false;

        if (scope == Scope.OwnerSelf)
            return true;

        return CoreCombat.TryGetEntityKey(target, out targetKey);
    }

    private static bool ValidateKeyParts(
        CoreCombat.CombatEntityKey sourceOwner,
        CoreCombat.CombatEntityKey target,
        CoreCombat.SemanticKey semantic,
        Scope scope)
    {
        if (!sourceOwner.IsValid || !semantic.IsValid)
            return false;

        if (scope != Scope.OwnerSelf && !target.IsValid)
            return false;

        return scope == Scope.OwnerSelf ||
               scope == Scope.OwnerTarget ||
               scope == Scope.TargetGlobal;
    }

    private static StateKey MakeKey(
        CoreCombat.CombatEntityKey sourceOwner,
        CoreCombat.CombatEntityKey target,
        CoreCombat.SemanticKey semantic,
        Scope scope)
    {
        StateKey key = new StateKey();
        key.StateScope = scope;
        key.SourceOwner = sourceOwner;
        key.Target = scope == Scope.OwnerSelf
            ? default(CoreCombat.CombatEntityKey)
            : target;
        key.Semantic = semantic;
        return key;
    }

    private static bool TryGetLiveRecord(
        CoreCombat.CombatEntityKey sourceOwner,
        CoreCombat.CombatEntityKey target,
        CoreCombat.SemanticKey semantic,
        Scope scope,
        float now,
        out StateKey key,
        out StateRecord record)
    {
        key = default(StateKey);
        record = default(StateRecord);

        if (!ValidateKeyParts(sourceOwner, target, semantic, scope))
            return false;

        key = MakeKey(sourceOwner, target, semantic, scope);
        if (!States.TryGetValue(key, out record))
            return false;

        if (IsExpired(record, now))
        {
            States.Remove(key);
            return false;
        }

        return true;
    }

    private static bool IsExpired(StateRecord record, float now)
    {
        return record.ExpiresAt > 0f && now >= record.ExpiresAt;
    }

    private static bool IsAuthoredLaterOrEqual(
        float candidateTime,
        uint candidateEventId,
        float currentTime,
        uint currentEventId)
    {
        if (candidateTime > currentTime)
            return true;
        if (candidateTime < currentTime)
            return false;

        // Eventless local state operations retain ordinary call-order behavior.
        // A correlated combat result at the same Time.time outranks an eventless
        // write; an eventless delayed write does not displace an authored one.
        if (candidateEventId == 0U)
            return currentEventId == 0U;
        if (currentEventId == 0U)
            return true;

        return candidateEventId == currentEventId ||
            unchecked((int)(candidateEventId - currentEventId)) > 0;
    }

    private static float ExpiryFromDuration(float now, float durationSeconds)
    {
        return durationSeconds > 0f ? now + durationSeconds : 0f;
    }

    private static void PruneExpired(float now, bool force)
    {
        if (!force && now < nextPruneAt)
            return;

        nextPruneAt = now + PruneIntervalSeconds;
        PruneScratch.Clear();

        foreach (KeyValuePair<StateKey, StateRecord> pair in States)
        {
            if (IsExpired(pair.Value, now))
                PruneScratch.Add(pair.Key);
        }

        RemoveScratchKeys();
    }

    private static void RemoveScratchKeys()
    {
        for (int i = 0; i < PruneScratch.Count; i++)
            States.Remove(PruneScratch[i]);

        PruneScratch.Clear();
    }
}
