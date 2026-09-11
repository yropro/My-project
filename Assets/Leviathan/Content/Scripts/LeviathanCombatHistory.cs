using StarVortex;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Bounded source-owner-local combat observations.
///
/// Common combat questions are answered from compact relationship/owner
/// summaries.  High-frequency damage does not append retained heap events.
/// Only explicitly meaningful discrete outcomes/semantic events use the fixed
/// per-owner ring.
/// </summary>
public static class LeviathanCombatHistory
{
    public enum SemanticObservationKind : byte
    {
        None = 0,
        Contact = 1,
        Affected = 2,
        Entered = 3,
        Overlap = 4,
        PullContact = 5,
        Applied = 6,
        Custom = 255
    }

    public enum MeaningfulEventKind : byte
    {
        None = 0,
        CombatOutcome = 1,
        SemanticObservation = 2,
        SemanticMarker = 3
    }

    public struct RelationshipSummary
    {
        public float LastAttemptTime;
        public float LastProcessedTime;
        public float LastConfirmedTime;
        public float LastDamageTime;
        public float LastStatusTime;
        public float LastKillTime;
        public float LastObservationTime;

        // Category-specific authored ordering. OccurredAt is the primary key;
        // EventId deterministically orders multiple authored events sharing the
        // same Time.time value. LastEventTime/LastEventId represent the newest
        // authored damage transaction overall for compatibility/contributor
        // queries; confirmation arrival order never participates.
        public uint LastAttemptEventId;
        public uint LastProcessedEventId;
        public uint LastDamageEventId;
        public uint LastStatusEventId;
        public uint LastKillEventId;
        public float LastEventTime;
        public uint LastEventId;
        public LeviathanCombat.ContributorKey LastContributor;
        public byte LastNativeStatusType;
        public SemanticObservationKind LastObservationKind;

        public uint TotalAttemptCount;
        public uint TotalProcessedCount;
        public uint TotalDamageCount;
        public uint TotalStatusCount;
        public uint TotalKillCount;
        public uint TotalObservationCount;
    }

    public struct OwnerSemanticSummary
    {
        public LeviathanCombat.CombatEntityKey LastTarget;
        public float LastAttemptTime;
        public float LastProcessedTime;
        public float LastConfirmedTime;
        public float LastDamageTime;
        public float LastStatusTime;
        public float LastKillTime;
        public float LastObservationTime;

        public uint LastAttemptEventId;
        public uint LastProcessedEventId;
        public uint LastDamageEventId;
        public uint LastStatusEventId;
        public uint LastKillEventId;
        public float LastEventTime;
        public uint LastEventId;

        public uint TotalAttemptCount;
        public uint TotalProcessedCount;
        public uint TotalDamageCount;
        public uint TotalKillCount;
        public uint TotalStatusCount;
        public uint TotalObservationCount;
    }

    /// <summary>
    /// One deliberately retained discrete event. Sequence is ring insertion
    /// order only; EventId/OccurredAt remain the authored causal ordering data.
    /// </summary>
    public struct MeaningfulEvent
    {
        public ulong Sequence;
        public MeaningfulEventKind Kind;
        public SemanticObservationKind ObservationKind;
        public LeviathanCombat.SemanticKey Semantic;
        public LeviathanCombat.CombatEntityKey SourceOwner;
        public LeviathanCombat.CombatEntityKey Target;
        public LeviathanCombat.ContributorKey Contributor;
        public uint EventId;
        public ushort AttackInstanceId;
        public float OccurredAt;
        public float ConfirmedAt;
        public LeviathanCombat.OutcomeFlags Outcomes;
        public float HealthDamage;
        public float ShieldDamage;
        public byte NativeStatusType;
        public LeviathanCombat.StatusDisposition StatusDisposition;
    }

    private struct RelationshipKey : IEquatable<RelationshipKey>
    {
        public LeviathanCombat.CombatEntityKey SourceOwner;
        public LeviathanCombat.CombatEntityKey Target;
        public LeviathanCombat.SemanticKey Semantic;

        public bool Equals(RelationshipKey other)
        {
            return SourceOwner.Equals(other.SourceOwner) &&
                   Target.Equals(other.Target) &&
                   Semantic.Equals(other.Semantic);
        }

        public override bool Equals(object obj)
        {
            return obj is RelationshipKey && Equals((RelationshipKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = SourceOwner.GetHashCode();
                hash = (hash * 397) ^ Target.GetHashCode();
                hash = (hash * 397) ^ Semantic.GetHashCode();
                return hash;
            }
        }
    }

    private struct OwnerSemanticKey : IEquatable<OwnerSemanticKey>
    {
        public LeviathanCombat.CombatEntityKey SourceOwner;
        public LeviathanCombat.SemanticKey Semantic;

        public bool Equals(OwnerSemanticKey other)
        {
            return SourceOwner.Equals(other.SourceOwner) &&
                   Semantic.Equals(other.Semantic);
        }

        public override bool Equals(object obj)
        {
            return obj is OwnerSemanticKey && Equals((OwnerSemanticKey)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (SourceOwner.GetHashCode() * 397) ^ Semantic.GetHashCode();
            }
        }
    }

    private sealed class OwnerRing
    {
        public readonly MeaningfulEvent[] Events =
            new MeaningfulEvent[MeaningfulEventsPerOwner];
        public ulong NextSequence = 1UL;
        public int Count;
        public float LastTouchedAt;
    }

    private struct ContributorStamp
    {
        public LeviathanCombat.ContributorKey Contributor;
        public float LastConfirmedDamageOccurredAt;
    }

    private sealed class UniqueContributorTracker
    {
        public readonly ContributorStamp[] Stamps =
            new ContributorStamp[ContributorsPerRelationship];
        public int Count;
        public float LastTouchedAt;

        public void ResetForReuse()
        {
            // ContributorStamp is a value type and Count is the authoritative
            // populated length, so there is no need to clear all 128 entries.
            Count = 0;
            LastTouchedAt = 0f;
        }
    }

    private struct UniqueContributorRequest
    {
        public float MaxWindowSeconds;
    }

    private const int MaxRelationships = 8192;
    private const int MaxOwnerSemanticSummaries = 2048;
    private const int MaxOwnerRings = 32;
    private const int MaxUniqueContributorTrackers = 512;
    private const int MaxUniqueContributorRequests = MaxRelationships;
    private const float MaintenanceIntervalSeconds = 2f;
    private const float ContributorTrackerMinimumIdleSeconds = 30f;
    private const float ContributorTrackerGraceSeconds = 5f;
    private const float OwnerRingIdleRecycleSeconds = 600f;

    public const int MeaningfulEventsPerOwner = 1024;
    public const int ContributorsPerRelationship = 128;

    private static readonly Dictionary<RelationshipKey, RelationshipSummary>
        Relationships = new Dictionary<RelationshipKey, RelationshipSummary>(512);

    private static readonly Dictionary<OwnerSemanticKey, OwnerSemanticSummary>
        OwnerSummaries = new Dictionary<OwnerSemanticKey, OwnerSemanticSummary>(128);

    private static readonly Dictionary<LeviathanCombat.CombatEntityKey, OwnerRing>
        Rings = new Dictionary<LeviathanCombat.CombatEntityKey, OwnerRing>(8);

    private static readonly Dictionary<RelationshipKey, UniqueContributorTracker>
        ContributorTrackers = new Dictionary<RelationshipKey, UniqueContributorTracker>(32);

    // Fixed-capacity reuse pool for the heavyweight 128-stamp trackers. Once
    // warm, idle-window recycling does not allocate replacement tracker arrays.
    private static readonly UniqueContributorTracker[] ContributorTrackerPool =
        new UniqueContributorTracker[MaxUniqueContributorTrackers];
    private static int contributorTrackerPoolCount;
    private static int contributorTrackerAllocatedCount;

    // Capability registration is much smaller than the 128-stamp tracker. It
    // survives idle tracker recycling so the first hit after a quiet period can
    // transparently recreate the data tracker instead of being lost.
    private static readonly Dictionary<RelationshipKey, UniqueContributorRequest>
        ContributorRequests = new Dictionary<RelationshipKey, UniqueContributorRequest>(32);

    private static readonly List<RelationshipKey> RelationshipScratch =
        new List<RelationshipKey>(128);
    private static readonly List<OwnerSemanticKey> OwnerSummaryScratch =
        new List<OwnerSemanticKey>(64);
    private static readonly List<LeviathanCombat.CombatEntityKey> OwnerScratch =
        new List<LeviathanCombat.CombatEntityKey>(16);

    private static float nextMaintenanceAt;

    public static int RelationshipCount { get { return Relationships.Count; } }
    public static int UniqueContributorTrackerCount { get { return ContributorTrackers.Count; } }

    // ---------------------------------------------------------------------
    // Recording - called by LeviathanCombat
    // ---------------------------------------------------------------------

    internal static void RecordAttempt(
        LeviathanCombat.CombatEntityKey sourceOwner,
        LeviathanCombat.CombatEntityKey target,
        LeviathanCombat.SemanticKey semantic,
        LeviathanCombat.ContributorKey contributor,
        uint eventId,
        float occurredAt)
    {
        RunMaintenance(false);

        if (!ValidRelationship(sourceOwner, target, semantic))
            return;

        RelationshipKey relationKey = MakeRelationshipKey(sourceOwner, target, semantic);
        RelationshipSummary relation;
        if (!TryGetOrCreateRelationship(relationKey, out relation))
            return;

        relation.TotalAttemptCount = SaturatingIncrement(relation.TotalAttemptCount);
        if (IsAuthoredLater(occurredAt, eventId,
            relation.LastAttemptTime, relation.LastAttemptEventId))
        {
            relation.LastAttemptTime = occurredAt;
            relation.LastAttemptEventId = eventId;
        }
        UpdateLastAuthored(ref relation, occurredAt, eventId, contributor);
        Relationships[relationKey] = relation;

        OwnerSemanticKey ownerKey = MakeOwnerKey(sourceOwner, semantic);
        OwnerSemanticSummary owner;
        if (TryGetOrCreateOwnerSummary(ownerKey, out owner))
        {
            owner.TotalAttemptCount = SaturatingIncrement(owner.TotalAttemptCount);
            if (IsAuthoredLater(occurredAt, eventId,
                owner.LastAttemptTime, owner.LastAttemptEventId))
            {
                owner.LastAttemptTime = occurredAt;
                owner.LastAttemptEventId = eventId;
            }
            UpdateOwnerLastTarget(ref owner, target, occurredAt, eventId);
            OwnerSummaries[ownerKey] = owner;
        }
    }

    internal static void RecordOutcome(
        LeviathanCombat.CombatOutcome outcome,
        bool recordSummary,
        bool retainMeaningful)
    {
        RunMaintenance(false);

        if (!ValidRelationship(outcome.SourceOwner, outcome.Target, outcome.Semantic))
            return;

        // Summary and meaningful-ring retention are independent capabilities.
        // TrackingFlags.Summary now controls both attempt and confirmed summary
        // mutation; a caller may still retain a discrete meaningful outcome
        // without allocating/updating a cumulative relationship entry.
        if (recordSummary)
        {
            RelationshipKey relationKey = MakeRelationshipKey(
                outcome.SourceOwner, outcome.Target, outcome.Semantic);
            RelationshipSummary relation;
            if (TryGetOrCreateRelationship(relationKey, out relation))
            {
                if ((outcome.Outcomes & LeviathanCombat.OutcomeFlags.Processed) != 0)
                {
                    relation.TotalProcessedCount =
                        SaturatingIncrement(relation.TotalProcessedCount);
                    if (IsAuthoredLater(outcome.OccurredAt, outcome.EventId,
                        relation.LastProcessedTime, relation.LastProcessedEventId))
                    {
                        relation.LastProcessedTime = outcome.OccurredAt;
                        relation.LastProcessedEventId = outcome.EventId;
                    }
                }

                relation.LastConfirmedTime =
                    Mathf.Max(relation.LastConfirmedTime, outcome.ConfirmedAt);

                if ((outcome.Outcomes & LeviathanCombat.OutcomeFlags.Damaged) != 0)
                {
                    relation.TotalDamageCount =
                        SaturatingIncrement(relation.TotalDamageCount);
                    if (IsAuthoredLater(outcome.OccurredAt, outcome.EventId,
                        relation.LastDamageTime, relation.LastDamageEventId))
                    {
                        relation.LastDamageTime = outcome.OccurredAt;
                        relation.LastDamageEventId = outcome.EventId;
                    }
                    UpdateContributorTracker(
                        relationKey, outcome.Contributor, outcome.OccurredAt);
                }

                if ((outcome.Outcomes & LeviathanCombat.OutcomeFlags.Destroyed) != 0)
                {
                    relation.TotalKillCount =
                        SaturatingIncrement(relation.TotalKillCount);
                    if (IsAuthoredLater(outcome.OccurredAt, outcome.EventId,
                        relation.LastKillTime, relation.LastKillEventId))
                    {
                        relation.LastKillTime = outcome.OccurredAt;
                        relation.LastKillEventId = outcome.EventId;
                    }
                }

                if ((outcome.Outcomes & LeviathanCombat.OutcomeFlags.StatusInflicted) != 0)
                {
                    relation.TotalStatusCount =
                        SaturatingIncrement(relation.TotalStatusCount);
                    if (IsAuthoredLater(outcome.OccurredAt, outcome.EventId,
                        relation.LastStatusTime, relation.LastStatusEventId))
                    {
                        relation.LastStatusTime = outcome.OccurredAt;
                        relation.LastStatusEventId = outcome.EventId;
                        relation.LastNativeStatusType = outcome.NativeStatusType;
                    }
                }

                UpdateLastAuthored(
                    ref relation, outcome.OccurredAt, outcome.EventId, outcome.Contributor);
                Relationships[relationKey] = relation;
            }

            OwnerSemanticKey ownerKey =
                MakeOwnerKey(outcome.SourceOwner, outcome.Semantic);
            OwnerSemanticSummary owner;
            if (TryGetOrCreateOwnerSummary(ownerKey, out owner))
            {
                if ((outcome.Outcomes & LeviathanCombat.OutcomeFlags.Processed) != 0)
                {
                    owner.TotalProcessedCount =
                        SaturatingIncrement(owner.TotalProcessedCount);
                    if (IsAuthoredLater(outcome.OccurredAt, outcome.EventId,
                        owner.LastProcessedTime, owner.LastProcessedEventId))
                    {
                        owner.LastProcessedTime = outcome.OccurredAt;
                        owner.LastProcessedEventId = outcome.EventId;
                    }
                }

                owner.LastConfirmedTime =
                    Mathf.Max(owner.LastConfirmedTime, outcome.ConfirmedAt);

                if ((outcome.Outcomes & LeviathanCombat.OutcomeFlags.Damaged) != 0)
                {
                    owner.TotalDamageCount =
                        SaturatingIncrement(owner.TotalDamageCount);
                    if (IsAuthoredLater(outcome.OccurredAt, outcome.EventId,
                        owner.LastDamageTime, owner.LastDamageEventId))
                    {
                        owner.LastDamageTime = outcome.OccurredAt;
                        owner.LastDamageEventId = outcome.EventId;
                    }
                }

                if ((outcome.Outcomes & LeviathanCombat.OutcomeFlags.Destroyed) != 0)
                {
                    owner.TotalKillCount =
                        SaturatingIncrement(owner.TotalKillCount);
                    if (IsAuthoredLater(outcome.OccurredAt, outcome.EventId,
                        owner.LastKillTime, owner.LastKillEventId))
                    {
                        owner.LastKillTime = outcome.OccurredAt;
                        owner.LastKillEventId = outcome.EventId;
                    }
                }

                if ((outcome.Outcomes & LeviathanCombat.OutcomeFlags.StatusInflicted) != 0)
                {
                    owner.TotalStatusCount =
                        SaturatingIncrement(owner.TotalStatusCount);
                    if (IsAuthoredLater(outcome.OccurredAt, outcome.EventId,
                        owner.LastStatusTime, owner.LastStatusEventId))
                    {
                        owner.LastStatusTime = outcome.OccurredAt;
                        owner.LastStatusEventId = outcome.EventId;
                    }
                }

                UpdateOwnerLastTarget(
                    ref owner, outcome.Target, outcome.OccurredAt, outcome.EventId);

                OwnerSummaries[ownerKey] = owner;
            }
        }

        if (retainMeaningful)
            AppendOutcome(outcome);
    }

    internal static void RecordObservation(
        LeviathanCombat.CombatEntityKey sourceOwner,
        LeviathanCombat.CombatEntityKey target,
        LeviathanCombat.SemanticKey semantic,
        LeviathanCombat.ContributorKey contributor,
        SemanticObservationKind kind,
        float occurredAt,
        bool retainMeaningful)
    {
        RunMaintenance(false);

        if (!ValidRelationship(sourceOwner, target, semantic) ||
            kind == SemanticObservationKind.None)
        {
            return;
        }

        RelationshipKey relationKey = MakeRelationshipKey(sourceOwner, target, semantic);
        RelationshipSummary relation;
        if (!TryGetOrCreateRelationship(relationKey, out relation))
            return;

        relation.TotalObservationCount = SaturatingIncrement(relation.TotalObservationCount);
        if (occurredAt >= relation.LastObservationTime)
        {
            relation.LastObservationTime = occurredAt;
            relation.LastObservationKind = kind;
            relation.LastContributor = contributor;
        }
        Relationships[relationKey] = relation;

        OwnerSemanticKey ownerKey = MakeOwnerKey(sourceOwner, semantic);
        OwnerSemanticSummary owner;
        if (TryGetOrCreateOwnerSummary(ownerKey, out owner))
        {
            owner.TotalObservationCount = SaturatingIncrement(owner.TotalObservationCount);
            if (occurredAt >= owner.LastObservationTime)
                owner.LastObservationTime = occurredAt;
            UpdateOwnerLastTarget(ref owner, target, occurredAt, 0U);
            OwnerSummaries[ownerKey] = owner;
        }

        if (retainMeaningful)
        {
            MeaningfulEvent evt = new MeaningfulEvent();
            evt.Kind = MeaningfulEventKind.SemanticObservation;
            evt.ObservationKind = kind;
            evt.Semantic = semantic;
            evt.SourceOwner = sourceOwner;
            evt.Target = target;
            evt.Contributor = contributor;
            evt.OccurredAt = occurredAt;
            evt.ConfirmedAt = occurredAt;
            AppendMeaningful(sourceOwner, ref evt);
        }
    }

    /// <summary>
    /// Records a skill-owned discrete marker such as Prey Kill without pretending
    /// it was damage.  It updates the semantic observation summary and optionally
    /// enters the meaningful ring.
    /// </summary>
    public static void RecordSemanticMarker(
        LeviathanCombat.CombatEntityKey sourceOwner,
        LeviathanCombat.CombatEntityKey target,
        LeviathanCombat.SemanticKey semantic,
        float occurredAt,
        bool retainMeaningful)
    {
        // A semantic marker is deliberately not a damage outcome and not a
        // physical-contact observation.  It still participates in the cheap
        // relationship/owner summaries, but retains its own ring vocabulary.
        RecordObservation(sourceOwner, target, semantic,
            default(LeviathanCombat.ContributorKey),
            SemanticObservationKind.Applied, occurredAt, false);

        if (retainMeaningful)
        {
            MeaningfulEvent evt = new MeaningfulEvent();
            evt.Kind = MeaningfulEventKind.SemanticMarker;
            evt.ObservationKind = SemanticObservationKind.Applied;
            evt.Semantic = semantic;
            evt.SourceOwner = sourceOwner;
            evt.Target = target;
            evt.OccurredAt = occurredAt;
            evt.ConfirmedAt = occurredAt;
            AppendMeaningful(sourceOwner, ref evt);
        }
    }

    // ---------------------------------------------------------------------
    // Summary queries
    // ---------------------------------------------------------------------

    public static bool TryGetRelationshipSummary(
        LeviathanCombat.CombatEntityKey sourceOwner,
        LeviathanCombat.CombatEntityKey target,
        LeviathanCombat.SemanticKey semantic,
        out RelationshipSummary summary)
    {
        return Relationships.TryGetValue(
            MakeRelationshipKey(sourceOwner, target, semantic), out summary);
    }

    public static bool TryGetRelationshipSummary(
        GameShip sourceOwner,
        GameShip target,
        LeviathanCombat.SemanticKey semantic,
        out RelationshipSummary summary)
    {
        summary = default(RelationshipSummary);
        LeviathanCombat.CombatEntityKey ownerKey;
        LeviathanCombat.CombatEntityKey targetKey;
        if (!LeviathanCombat.TryGetEntityKey(sourceOwner, out ownerKey) ||
            !LeviathanCombat.TryGetEntityKey(target, out targetKey))
        {
            return false;
        }

        return TryGetRelationshipSummary(ownerKey, targetKey, semantic, out summary);
    }

    public static bool TryGetOwnerSummary(
        LeviathanCombat.CombatEntityKey sourceOwner,
        LeviathanCombat.SemanticKey semantic,
        out OwnerSemanticSummary summary)
    {
        return OwnerSummaries.TryGetValue(MakeOwnerKey(sourceOwner, semantic), out summary);
    }

    public static uint GetAttemptCount(
        LeviathanCombat.CombatEntityKey sourceOwner,
        LeviathanCombat.CombatEntityKey target,
        LeviathanCombat.SemanticKey semantic)
    {
        RelationshipSummary summary;
        return TryGetRelationshipSummary(sourceOwner, target, semantic, out summary)
            ? summary.TotalAttemptCount : 0U;
    }

    public static uint GetDamageCount(
        LeviathanCombat.CombatEntityKey sourceOwner,
        LeviathanCombat.CombatEntityKey target,
        LeviathanCombat.SemanticKey semantic)
    {
        RelationshipSummary summary;
        return TryGetRelationshipSummary(sourceOwner, target, semantic, out summary)
            ? summary.TotalDamageCount : 0U;
    }

    public static float GetLastDamageTime(
        LeviathanCombat.CombatEntityKey sourceOwner,
        LeviathanCombat.CombatEntityKey target,
        LeviathanCombat.SemanticKey semantic)
    {
        RelationshipSummary summary;
        return TryGetRelationshipSummary(sourceOwner, target, semantic, out summary)
            ? summary.LastDamageTime : 0f;
    }

    public static float GetLastObservationTime(
        LeviathanCombat.CombatEntityKey sourceOwner,
        LeviathanCombat.CombatEntityKey target,
        LeviathanCombat.SemanticKey semantic)
    {
        RelationshipSummary summary;
        return TryGetRelationshipSummary(sourceOwner, target, semantic, out summary)
            ? summary.LastObservationTime : 0f;
    }

    public static LeviathanCombat.CombatEntityKey GetLastTarget(
        LeviathanCombat.CombatEntityKey sourceOwner,
        LeviathanCombat.SemanticKey semantic)
    {
        OwnerSemanticSummary summary;
        return TryGetOwnerSummary(sourceOwner, semantic, out summary)
            ? summary.LastTarget : default(LeviathanCombat.CombatEntityKey);
    }

    public static bool WasObservedSince(
        LeviathanCombat.CombatEntityKey sourceOwner,
        LeviathanCombat.CombatEntityKey target,
        LeviathanCombat.SemanticKey semantic,
        float occurredAtInclusive)
    {
        RelationshipSummary summary;
        return TryGetRelationshipSummary(sourceOwner, target, semantic, out summary) &&
            summary.LastObservationTime >= occurredAtInclusive;
    }

    // ---------------------------------------------------------------------
    // Meaningful event ring
    // ---------------------------------------------------------------------

    public static bool TryReadNextMeaningfulEvent(
        LeviathanCombat.CombatEntityKey sourceOwner,
        ref ulong cursor,
        out MeaningfulEvent evt)
    {
        evt = default(MeaningfulEvent);

        OwnerRing ring;
        if (!Rings.TryGetValue(sourceOwner, out ring) || ring == null || ring.Count == 0)
            return false;

        ulong oldest = ring.NextSequence - (ulong)ring.Count;
        if (cursor + 1UL < oldest)
            cursor = oldest - 1UL;

        ulong wanted = cursor + 1UL;
        if (wanted >= ring.NextSequence)
            return false;

        int index = (int)((wanted - 1UL) % MeaningfulEventsPerOwner);
        MeaningfulEvent candidate = ring.Events[index];
        if (candidate.Sequence != wanted)
        {
            // Ring was overwritten between cursor reads. Advance to current oldest.
            cursor = oldest - 1UL;
            wanted = oldest;
            index = (int)((wanted - 1UL) % MeaningfulEventsPerOwner);
            candidate = ring.Events[index];
            if (candidate.Sequence != wanted)
                return false;
        }

        evt = candidate;
        cursor = wanted;
        return true;
    }

    // ---------------------------------------------------------------------
    // Optional UniqueContributorWindow tracker
    // ---------------------------------------------------------------------

    public static bool EnableUniqueContributorWindow(
        LeviathanCombat.CombatEntityKey sourceOwner,
        LeviathanCombat.CombatEntityKey target,
        LeviathanCombat.SemanticKey semantic,
        float expectedWindowSeconds = 1f)
    {
        if (!ValidRelationship(sourceOwner, target, semantic))
            return false;

        if (expectedWindowSeconds < 0f)
            expectedWindowSeconds = 0f;

        RelationshipKey key = MakeRelationshipKey(sourceOwner, target, semantic);
        float now = Time.unscaledTime;

        RelationshipSummary relationship;
        if (!TryGetOrCreateRelationship(key, out relationship))
            return false;

        UniqueContributorRequest request;
        if (ContributorRequests.TryGetValue(key, out request))
        {
            if (expectedWindowSeconds > request.MaxWindowSeconds)
                request.MaxWindowSeconds = expectedWindowSeconds;
            ContributorRequests[key] = request;
        }
        else
        {
            if (ContributorRequests.Count >= MaxUniqueContributorRequests)
                return false;

            request = new UniqueContributorRequest();
            request.MaxWindowSeconds = Mathf.Max(1f, expectedWindowSeconds);
            ContributorRequests.Add(key, request);
        }

        RunMaintenance(false);
        UniqueContributorTracker tracker;
        return TryGetOrCreateContributorTracker(key, now, out tracker);
    }

    public static void DisableUniqueContributorWindow(
        LeviathanCombat.CombatEntityKey sourceOwner,
        LeviathanCombat.CombatEntityKey target,
        LeviathanCombat.SemanticKey semantic)
    {
        RelationshipKey key = MakeRelationshipKey(sourceOwner, target, semantic);
        ReleaseContributorTracker(key);
        ContributorRequests.Remove(key);
    }

    public static int CountUniqueContributors(
        LeviathanCombat.CombatEntityKey sourceOwner,
        LeviathanCombat.CombatEntityKey target,
        LeviathanCombat.SemanticKey semantic,
        float windowSeconds,
        float nowOccurredAt)
    {
        if (windowSeconds < 0f)
            windowSeconds = 0f;

        RelationshipKey key = MakeRelationshipKey(sourceOwner, target, semantic);
        UniqueContributorRequest request;
        if (!ContributorRequests.TryGetValue(key, out request))
        {
            return 0;
        }

        float nowUnscaled = Time.unscaledTime;
        if (windowSeconds > request.MaxWindowSeconds)
            request.MaxWindowSeconds = windowSeconds;
        ContributorRequests[key] = request;

        UniqueContributorTracker tracker;
        if (!TryGetOrCreateContributorTracker(key, nowUnscaled, out tracker))
            return 0;

        tracker.LastTouchedAt = nowUnscaled;
        RunMaintenance(false);

        float threshold = nowOccurredAt - windowSeconds;
        int count = 0;
        for (int i = 0; i < tracker.Count; i++)
        {
            if (tracker.Stamps[i].Contributor.IsValid &&
                tracker.Stamps[i].LastConfirmedDamageOccurredAt >= threshold)
            {
                count++;
            }
        }

        return count;
    }

    // ---------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------

    /// <summary>
    /// Removes live cumulative/windowed history for one skill owned by this
    /// source. The mixed per-owner meaningful ring is intentionally retained as
    /// bounded historical observation; a replacement skill runtime starts its
    /// own cursor at the current tail and therefore cannot consume old events.
    /// </summary>
    public static void ResetOwnerSkill(
        LeviathanCombat.CombatEntityKey sourceOwner,
        byte skillId)
    {
        if (!sourceOwner.IsValid || skillId == LeviathanCombat.SkillIds.None)
            return;

        RelationshipScratch.Clear();
        foreach (KeyValuePair<RelationshipKey, RelationshipSummary> pair in Relationships)
        {
            if (pair.Key.SourceOwner.Equals(sourceOwner) &&
                pair.Key.Semantic.SkillId == skillId)
            {
                RelationshipScratch.Add(pair.Key);
            }
        }
        for (int i = 0; i < RelationshipScratch.Count; i++)
        {
            Relationships.Remove(RelationshipScratch[i]);
            ReleaseContributorTracker(RelationshipScratch[i]);
            ContributorRequests.Remove(RelationshipScratch[i]);
        }
        RelationshipScratch.Clear();

        OwnerSummaryScratch.Clear();
        foreach (KeyValuePair<OwnerSemanticKey, OwnerSemanticSummary> pair in OwnerSummaries)
        {
            if (pair.Key.SourceOwner.Equals(sourceOwner) &&
                pair.Key.Semantic.SkillId == skillId)
            {
                OwnerSummaryScratch.Add(pair.Key);
            }
        }
        for (int i = 0; i < OwnerSummaryScratch.Count; i++)
            OwnerSummaries.Remove(OwnerSummaryScratch[i]);
        OwnerSummaryScratch.Clear();
    }

    public static void ResetOwner(LeviathanCombat.CombatEntityKey sourceOwner)
    {
        if (!sourceOwner.IsValid)
            return;

        RelationshipScratch.Clear();
        foreach (KeyValuePair<RelationshipKey, RelationshipSummary> pair in Relationships)
            if (pair.Key.SourceOwner.Equals(sourceOwner)) RelationshipScratch.Add(pair.Key);
        for (int i = 0; i < RelationshipScratch.Count; i++)
        {
            Relationships.Remove(RelationshipScratch[i]);
            ReleaseContributorTracker(RelationshipScratch[i]);
            ContributorRequests.Remove(RelationshipScratch[i]);
        }
        RelationshipScratch.Clear();

        OwnerSummaryScratch.Clear();
        foreach (KeyValuePair<OwnerSemanticKey, OwnerSemanticSummary> pair in OwnerSummaries)
            if (pair.Key.SourceOwner.Equals(sourceOwner)) OwnerSummaryScratch.Add(pair.Key);
        for (int i = 0; i < OwnerSummaryScratch.Count; i++)
            OwnerSummaries.Remove(OwnerSummaryScratch[i]);
        OwnerSummaryScratch.Clear();

        Rings.Remove(sourceOwner);
    }

    public static void ResetTarget(LeviathanCombat.CombatEntityKey target)
    {
        if (!target.IsValid)
            return;

        RelationshipScratch.Clear();
        foreach (KeyValuePair<RelationshipKey, RelationshipSummary> pair in Relationships)
            if (pair.Key.Target.Equals(target)) RelationshipScratch.Add(pair.Key);
        for (int i = 0; i < RelationshipScratch.Count; i++)
        {
            Relationships.Remove(RelationshipScratch[i]);
            ReleaseContributorTracker(RelationshipScratch[i]);
            ContributorRequests.Remove(RelationshipScratch[i]);
        }
        RelationshipScratch.Clear();
    }

    public static void Reset()
    {
        Relationships.Clear();
        OwnerSummaries.Clear();
        Rings.Clear();
        ContributorTrackers.Clear();
        ContributorRequests.Clear();
        Array.Clear(ContributorTrackerPool, 0, ContributorTrackerPool.Length);
        contributorTrackerPoolCount = 0;
        contributorTrackerAllocatedCount = 0;
        RelationshipScratch.Clear();
        OwnerSummaryScratch.Clear();
        OwnerScratch.Clear();
        nextMaintenanceAt = 0f;
    }

    /// <summary>
    /// Periodic bounded maintenance. Cumulative relationship summaries are not
    /// age-pruned generically because some mechanics intentionally define them
    /// for the target's whole lifetime. They are recycled by target lifecycle
    /// cleanup instead. Window trackers are safe to age out once they have been
    /// idle longer than every window they have actually been asked to answer.
    /// </summary>
    public static void RunMaintenance(bool force)
    {
        float now = Time.unscaledTime;
        if (!force && now < nextMaintenanceAt)
            return;

        nextMaintenanceAt = now + MaintenanceIntervalSeconds;

        RelationshipScratch.Clear();
        foreach (KeyValuePair<RelationshipKey, UniqueContributorTracker> pair in ContributorTrackers)
        {
            UniqueContributorTracker tracker = pair.Value;
            if (tracker == null)
            {
                RelationshipScratch.Add(pair.Key);
                continue;
            }

            UniqueContributorRequest request;
            float maxWindow = ContributorRequests.TryGetValue(pair.Key, out request)
                ? request.MaxWindowSeconds
                : 0f;

            float requiredIdle = Mathf.Max(
                ContributorTrackerMinimumIdleSeconds,
                maxWindow + ContributorTrackerGraceSeconds);

            if (now - tracker.LastTouchedAt >= requiredIdle)
                RelationshipScratch.Add(pair.Key);
        }

        for (int i = 0; i < RelationshipScratch.Count; i++)
            ReleaseContributorTracker(RelationshipScratch[i]);
        RelationshipScratch.Clear();

    }

    // ---------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------

    private static bool ValidRelationship(
        LeviathanCombat.CombatEntityKey sourceOwner,
        LeviathanCombat.CombatEntityKey target,
        LeviathanCombat.SemanticKey semantic)
    {
        return sourceOwner.IsValid && target.IsValid && semantic.IsValid;
    }

    private static RelationshipKey MakeRelationshipKey(
        LeviathanCombat.CombatEntityKey sourceOwner,
        LeviathanCombat.CombatEntityKey target,
        LeviathanCombat.SemanticKey semantic)
    {
        RelationshipKey key = new RelationshipKey();
        key.SourceOwner = sourceOwner;
        key.Target = target;
        key.Semantic = semantic;
        return key;
    }

    private static OwnerSemanticKey MakeOwnerKey(
        LeviathanCombat.CombatEntityKey sourceOwner,
        LeviathanCombat.SemanticKey semantic)
    {
        OwnerSemanticKey key = new OwnerSemanticKey();
        key.SourceOwner = sourceOwner;
        key.Semantic = semantic;
        return key;
    }

    private static bool TryGetOrCreateRelationship(
        RelationshipKey key,
        out RelationshipSummary summary)
    {
        if (Relationships.TryGetValue(key, out summary))
            return true;

        if (Relationships.Count >= MaxRelationships)
        {
            RunMaintenance(true);
        }

        if (Relationships.Count >= MaxRelationships)
        {
            summary = default(RelationshipSummary);
            return false;
        }

        summary = default(RelationshipSummary);
        Relationships.Add(key, summary);
        return true;
    }

    private static bool TryGetOrCreateOwnerSummary(
        OwnerSemanticKey key,
        out OwnerSemanticSummary summary)
    {
        if (OwnerSummaries.TryGetValue(key, out summary))
            return true;

        if (OwnerSummaries.Count >= MaxOwnerSemanticSummaries)
        {
            summary = default(OwnerSemanticSummary);
            return false;
        }

        summary = default(OwnerSemanticSummary);
        OwnerSummaries.Add(key, summary);
        return true;
    }

    private static void UpdateLastAuthored(
        ref RelationshipSummary summary,
        float occurredAt,
        uint eventId,
        LeviathanCombat.ContributorKey contributor)
    {
        if (eventId == 0U)
            return;

        if (IsAuthoredLater(occurredAt, eventId,
            summary.LastEventTime, summary.LastEventId))
        {
            summary.LastEventTime = occurredAt;
            summary.LastEventId = eventId;
            summary.LastContributor = contributor;
        }
    }

    private static void UpdateOwnerLastTarget(
        ref OwnerSemanticSummary summary,
        LeviathanCombat.CombatEntityKey target,
        float occurredAt,
        uint eventId)
    {
        if (IsAuthoredLater(occurredAt, eventId,
            summary.LastEventTime, summary.LastEventId))
        {
            summary.LastEventTime = occurredAt;
            summary.LastEventId = eventId;
            summary.LastTarget = target;
        }
    }

    private static bool IsAuthoredLater(
        float candidateTime,
        uint candidateEventId,
        float currentTime,
        uint currentEventId)
    {
        if (candidateTime > currentTime)
            return true;
        if (candidateTime < currentTime)
            return false;

        // Non-damage semantic observations do not receive combat EventIds. At
        // the same Time.time they must not displace an authored damage event,
        // while two eventless observations retain ordinary call order.
        if (candidateEventId == 0U)
            return currentEventId == 0U;
        if (currentEventId == 0U)
            return true;

        return IsNewerEventId(candidateEventId, currentEventId);
    }

    private static bool IsNewerEventId(uint candidate, uint current)
    {
        if (candidate == 0U)
            return false;
        if (current == 0U)
            return true;
        return unchecked((int)(candidate - current)) > 0;
    }

    private static uint SaturatingIncrement(uint value)
    {
        return value == uint.MaxValue ? uint.MaxValue : value + 1U;
    }

    private static void AppendOutcome(LeviathanCombat.CombatOutcome outcome)
    {
        MeaningfulEvent evt = new MeaningfulEvent();
        evt.Kind = MeaningfulEventKind.CombatOutcome;
        evt.Semantic = outcome.Semantic;
        evt.SourceOwner = outcome.SourceOwner;
        evt.Target = outcome.Target;
        evt.Contributor = outcome.Contributor;
        evt.EventId = outcome.EventId;
        evt.AttackInstanceId = outcome.AttackInstanceId;
        evt.OccurredAt = outcome.OccurredAt;
        evt.ConfirmedAt = outcome.ConfirmedAt;
        evt.Outcomes = outcome.Outcomes;
        evt.HealthDamage = outcome.HealthDamage;
        evt.ShieldDamage = outcome.ShieldDamage;
        evt.NativeStatusType = outcome.NativeStatusType;
        evt.StatusDisposition = outcome.StatusDisposition;
        AppendMeaningful(outcome.SourceOwner, ref evt);
    }

    private static void AppendMeaningful(
        LeviathanCombat.CombatEntityKey sourceOwner,
        ref MeaningfulEvent evt)
    {
        RunMaintenance(false);

        OwnerRing ring;
        if (!Rings.TryGetValue(sourceOwner, out ring))
        {
            if (Rings.Count >= MaxOwnerRings)
            {
                PruneIdleOwnerRings(Time.unscaledTime);
                if (Rings.Count >= MaxOwnerRings)
                    return;
            }
            ring = new OwnerRing();
            Rings.Add(sourceOwner, ring);
        }

        ulong sequence = ring.NextSequence++;
        if (ring.NextSequence == 0UL)
            ring.NextSequence = 1UL;

        evt.Sequence = sequence;
        int index = (int)((sequence - 1UL) % MeaningfulEventsPerOwner);
        ring.Events[index] = evt;
        if (ring.Count < MeaningfulEventsPerOwner)
            ring.Count++;
        ring.LastTouchedAt = Time.unscaledTime;
    }

    private static void PruneIdleOwnerRings(float nowUnscaled)
    {
        OwnerScratch.Clear();
        foreach (KeyValuePair<LeviathanCombat.CombatEntityKey, OwnerRing> pair in Rings)
        {
            OwnerRing ring = pair.Value;
            if (ring == null ||
                nowUnscaled - ring.LastTouchedAt >= OwnerRingIdleRecycleSeconds)
            {
                OwnerScratch.Add(pair.Key);
            }
        }

        for (int i = 0; i < OwnerScratch.Count; i++)
            Rings.Remove(OwnerScratch[i]);
        OwnerScratch.Clear();
    }

    private static void UpdateContributorTracker(
        RelationshipKey key,
        LeviathanCombat.ContributorKey contributor,
        float occurredAt)
    {
        if (!contributor.IsValid)
            return;

        UniqueContributorRequest request;
        if (!ContributorRequests.TryGetValue(key, out request))
            return;

        float nowUnscaled = Time.unscaledTime;
        UniqueContributorTracker tracker;
        if (!TryGetOrCreateContributorTracker(key, nowUnscaled, out tracker))
            return;

        tracker.LastTouchedAt = nowUnscaled;

        int oldestIndex = -1;
        float oldestTime = float.PositiveInfinity;
        for (int i = 0; i < tracker.Count; i++)
        {
            ContributorStamp stamp = tracker.Stamps[i];
            if (stamp.Contributor.Equals(contributor))
            {
                if (occurredAt > stamp.LastConfirmedDamageOccurredAt)
                {
                    stamp.LastConfirmedDamageOccurredAt = occurredAt;
                    tracker.Stamps[i] = stamp;
                }
                return;
            }

            if (stamp.LastConfirmedDamageOccurredAt < oldestTime)
            {
                oldestTime = stamp.LastConfirmedDamageOccurredAt;
                oldestIndex = i;
            }
        }

        ContributorStamp next = new ContributorStamp();
        next.Contributor = contributor;
        next.LastConfirmedDamageOccurredAt = occurredAt;

        if (tracker.Count < ContributorsPerRelationship)
        {
            tracker.Stamps[tracker.Count++] = next;
        }
        else if (oldestIndex >= 0)
        {
            // Hard bound: preserve the most recent contributors if a malformed
            // or future build genuinely exceeds the documented 128 cap.
            tracker.Stamps[oldestIndex] = next;
        }
    }

    private static bool TryGetOrCreateContributorTracker(
        RelationshipKey key,
        float nowUnscaled,
        out UniqueContributorTracker tracker)
    {
        if (ContributorTrackers.TryGetValue(key, out tracker) && tracker != null)
        {
            tracker.LastTouchedAt = nowUnscaled;
            return true;
        }

        if (!ContributorRequests.ContainsKey(key))
        {
            tracker = null;
            return false;
        }

        if (ContributorTrackers.Count >= MaxUniqueContributorTrackers)
        {
            RunMaintenance(true);
            if (ContributorTrackers.Count >= MaxUniqueContributorTrackers)
            {
                tracker = null;
                return false;
            }
        }

        tracker = AcquireContributorTracker();
        if (tracker == null)
            return false;

        tracker.LastTouchedAt = nowUnscaled;
        ContributorTrackers[key] = tracker;
        return true;
    }

    private static UniqueContributorTracker AcquireContributorTracker()
    {
        if (contributorTrackerPoolCount > 0)
        {
            int index = --contributorTrackerPoolCount;
            UniqueContributorTracker tracker = ContributorTrackerPool[index];
            ContributorTrackerPool[index] = null;
            if (tracker != null)
            {
                tracker.ResetForReuse();
                return tracker;
            }
        }

        if (contributorTrackerAllocatedCount >= MaxUniqueContributorTrackers)
            return null;

        contributorTrackerAllocatedCount++;
        return new UniqueContributorTracker();
    }

    private static void ReleaseContributorTracker(RelationshipKey key)
    {
        UniqueContributorTracker tracker;
        if (!ContributorTrackers.TryGetValue(key, out tracker))
            return;

        ContributorTrackers.Remove(key);

        if (tracker == null)
            return;

        tracker.ResetForReuse();

        if (contributorTrackerPoolCount < ContributorTrackerPool.Length)
        {
            ContributorTrackerPool[contributorTrackerPoolCount++] = tracker;
        }
        else
        {
            // Defensive only: active + pooled trackers should equal the fixed
            // allocation count. If bookkeeping is corrupted, prefer dropping
            // the optional tracker over growing another unbounded structure.
            contributorTrackerAllocatedCount = Mathf.Max(
                ContributorTrackers.Count + contributorTrackerPoolCount,
                0);
        }
    }
}
