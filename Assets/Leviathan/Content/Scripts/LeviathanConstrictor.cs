using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

// =============================================================================
// Constrictor
// =============================================================================
//
// Constrictor converts the first equipped Assault into a Leviathan-wide passive
// contact weapon. The native Assault remains the source of resolved damage,
// damage type, DamageVsX, crit, status, on-crit, Conduit, on-kill and Gladiator
// leech semantics; Constrictor changes how that source is manifested.
//
// BASELINE
// --------
// Native passive Assault contact deals 20% of listed Damage per damage tick,
// split across its physical blades. Constrictor treats TWO simultaneous
// Leviathan section contacts against one target as one complete native passive
// Assault aggregate. One contact is therefore half an aggregate.
//
// Additional simultaneous contacts have symmetric diminishing returns. There is
// deliberately no head-first / tail-first ranking: Growth owns anatomy and
// future branched or multi-Head structures do not have one canonical linear
// order. For N contacts:
//
//   N <= 2: effective contact units = N
//   N >  2: effective contact units = 2 + 2 * (N - 2) / (N + 2)
//
// Two contacts therefore equal 1.0 native passive aggregate; an arbitrarily
// complete wrap approaches 2.0 native passive aggregates before specialization
// damage scaling. Every touching section still receives an actual hit packet.
//
// RANKS
// -----
// The specialization tree exposes one paid five-rank node. Rank 1 enables the
// conversion at native passive aggregate damage. Ranks 2-5 add +25% damage each,
// reaching 2.0x at Rank 5. Other rank effects are resolved through Knobs rather
// than hard-coded node checks.
//
// MULTIPLAYER
// -----------
// Gameplay runs only for the local owner and damage is routed through native
// NetCombat. Remote clients derive Constrictor ownership from synchronized
// specialization and only suppress the source Assault's presentation/lunge.
// There is no irreducible temporal presentation state, so SlotConstrictor is not
// used by this implementation.
//
// =============================================================================

public static class LeviathanConstrictor
{
    // =========================================================================
    // SPECIALIZATION INTERFACE
    // =========================================================================

    public static class Knobs
    {
        // Semantic purchased rank. The tree contributes +1 per invested rank,
        // so gameplay never needs to inspect a node id directly.
        public static readonly LeviathanSpecializationKnob Rank =
            LeviathanSpecializationKnob.Flat(
                "constrictor.rank",
                "Constrictor Rank"
            );

        // Additive percentage relative to the Constrictor baseline.
        public static readonly LeviathanSpecializationKnob FinalDamagePercent =
            LeviathanSpecializationKnob.Percent(
                "constrictor.final_damage_percent",
                "Constrictor Damage"
            );

        // Additive percentage points on the source Assault's fully resolved crit.
        public static readonly LeviathanSpecializationKnob CritChancePoints =
            LeviathanSpecializationKnob.PercentagePoints(
                "constrictor.crit_chance_points",
                "Critical Chance"
            );

        // Absolute fraction of the source Assault's fully resolved status chance.
        // 0.20 means Constrictor contact uses 20% of the source status chance.
        public static readonly LeviathanSpecializationKnob PassiveStatusFraction =
            LeviathanSpecializationKnob.Flat(
                "constrictor.passive_status_fraction",
                "Passive Status Fraction",
                "x"
            );

        public static readonly LeviathanSpecializationKnob AccelerationPercent =
            LeviathanSpecializationKnob.Percent(
                "constrictor.acceleration_percent",
                "Acceleration"
            );

        public static readonly LeviathanSpecializationKnob BoostPercent =
            LeviathanSpecializationKnob.Percent(
                "constrictor.boost_percent",
                "Boost"
            );

        public static readonly LeviathanSpecializationKnob TurnSpeedPercent =
            LeviathanSpecializationKnob.Percent(
                "constrictor.turn_speed_percent",
                "Turn Speed"
            );

        public static readonly LeviathanSpecializationKnob ManeuverabilityPercent =
            LeviathanSpecializationKnob.Percent(
                "constrictor.maneuverability_percent",
                "Maneuverability"
            );

        // Stored as a decimal fraction through PercentagePoints:
        // authored 10 = 0.10 = refund 10% of Growth's actual drag this call.
        public static readonly LeviathanSpecializationKnob AirResistanceReductionPoints =
            LeviathanSpecializationKnob.PercentagePoints(
                "constrictor.air_resistance_reduction_points",
                "Leviathan Air Resistance Reduction"
            );
    }

    public sealed class ResolvedState
    {
        public bool Active;
        public int Rank;

        public float DamageMultiplier = 1f;
        public float CritChanceBonus;
        public float PassiveStatusFraction;

        public float AccelerationMultiplier = 1f;
        public float BoostMultiplier = 1f;
        public float TurnSpeedMultiplier = 1f;
        public float ManeuverabilityMultiplier = 1f;
        public float AirResistanceReduction;
    }

    private sealed class ResolvedCacheEntry
    {
        public GameShip Ship;
        public int ConfigurationRevision;
        public int RegistryRevision;
        public ResolvedState State;
    }

    private static readonly Dictionary<Pilot, ResolvedCacheEntry> ResolvedByPilot =
        new Dictionary<Pilot, ResolvedCacheEntry>();

    private static readonly ResolvedState InactiveState = new ResolvedState();

    // =========================================================================
    // BALANCE / MECHANICAL CONSTANTS
    // =========================================================================

    // Native Assault.GetDamageData passive packet fraction.
    private const float EngineTickFraction = 0.20f;

    // Native Assault.DoDamageTick base cadence before BeamTickRate modifiers.
    private const float BaseContactTickRate = 0.20f;

    // Two simultaneous Leviathan contacts equal one complete native passive
    // Assault aggregate before specialization damage scaling.
    private const float BaselineAggregateContacts = 2.0f;

    // Diminishing-return wrap ceiling. Four effective contact units / baseline
    // two contacts = 2.0 native passive aggregates at the asymptote.
    private const float MaxAggregateContactUnits = 4.0f;
    private const float OverflowHalfSaturationContacts = 4.0f;

    // Detection tolerance only. Real colliders, visuals and physical spacing are
    // untouched. 0.10 = a radial tolerance equal to 10% of collider bound radius.
    private const float ContactHitboxRadiusPadding = 0.10f;

    // =========================================================================
    // NATIVE ACCESS
    // =========================================================================

    private static readonly FieldInfo AssaultBladesField =
        AccessTools.Field(typeof(Assault), "blades");

    private static readonly MethodInfo RouteDamageMethod =
        typeof(NetCombat)
            .GetMethods(
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic
            )
            .FirstOrDefault(
                m => m.Name == "RouteDamage" &&
                     m.GetParameters().Length == 14 &&
                     m.GetParameters()[2].ParameterType ==
                        typeof(Damageable.DamageData[])
            );

    private static readonly MethodInfo ApplyOnCritStatusEffectsMethod =
        AccessTools.Method(
            typeof(Activatable),
            "ApplyOnCritStatusEffects",
            new Type[] { typeof(GameShip) }
        );

    private static readonly MethodInfo ConduitRelayHitMethod =
        typeof(Conduit)
            .GetMethods(
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic
            )
            .FirstOrDefault(
                m => m.Name == "RelayHit" &&
                     m.GetParameters().Length == 6 &&
                     m.GetParameters()[3].ParameterType ==
                        typeof(Damageable.DamageData[])
            );

    private static readonly MethodInfo LeechGladiatorHullMethod =
        AccessTools.Method(
            typeof(Assault),
            "LeechGladiatorHull",
            new Type[] { typeof(GameShip), typeof(Vector2) }
        );

    // =========================================================================
    // CONTACT STATE
    // =========================================================================

    private struct ContactKey : IEquatable<ContactKey>
    {
        public readonly GameShip Section;
        public readonly GameShip Target;

        public ContactKey(GameShip section, GameShip target)
        {
            Section = section;
            Target = target;
        }

        public bool Equals(ContactKey other)
        {
            return Section == other.Section && Target == other.Target;
        }

        public override bool Equals(object obj)
        {
            return obj is ContactKey && Equals((ContactKey)obj);
        }

        public override int GetHashCode()
        {
            int a = Section == null ? 0 : Section.GetInstanceID();
            int b = Target == null ? 0 : Target.GetInstanceID();
            return (a * 397) ^ b;
        }
    }

    private sealed class ContactBucket
    {
        public readonly List<GameShip> Sections = new List<GameShip>(4);
        public int Generation;
        public int LastSeenGeneration;
    }

    private static readonly Dictionary<ContactKey, float> LastHits =
        new Dictionary<ContactKey, float>();

    private static readonly List<ContactKey> StaleHits =
        new List<ContactKey>();

    private static readonly Dictionary<GameShip, ContactBucket> ContactBuckets =
        new Dictionary<GameShip, ContactBucket>();

    private static readonly List<GameShip> ContactTargets =
        new List<GameShip>();

    private static readonly List<GameShip> StaleContactTargets =
        new List<GameShip>();

    private static readonly Dictionary<GameShip, Collider2D[]> ColliderCache =
        new Dictionary<GameShip, Collider2D[]>();

    private static readonly HashSet<GameShip> SectionTargets =
        new HashSet<GameShip>();

    // Growth-owned live anatomy is collected by semantic role. We intentionally
    // keep separate scratch lists because the collection contract fills a caller-
    // owned list; consumers must not infer one flat anatomy order from Squadron.
    private static readonly List<GameShip> HeadSections = new List<GameShip>(4);
    private static readonly List<GameShip> BodySections = new List<GameShip>(24);
    private static readonly List<GameShip> TailSections = new List<GameShip>(8);

    private static readonly Collider2D[] ProximityBuffer =
        new Collider2D[256];

    private static readonly Damageable.DamageData[] DamageBuffer =
        new Damageable.DamageData[8];

    private static int contactGeneration;
    private static int lastPrunedContactGeneration;

    private static GameShip anatomyOwner;
    private static int anatomyRevision = int.MinValue;

    // =========================================================================
    // ASSAULT SOURCE / PRESENTATION STATE
    // =========================================================================

    // Local authoritative source whose native passive blade damage is suppressed.
    private static Assault suppressed;

    // Remote presentation only. Keeps track of Assaults whose blades we hid so
    // we restore only presentation that Constrictor itself changed.
    private static readonly HashSet<Assault> RemoteHiddenAssaults =
        new HashSet<Assault>();

    // =========================================================================
    // RESOLUTION
    // =========================================================================

    public static ResolvedState GetResolvedState(GameShip ship)
    {
        if (ship == null)
            return InactiveState;

        Pilot pilot = GameShip.GetPlayerSourcePilot(ship);
        if (pilot == null)
            return InactiveState;

        bool local = IsCurrentPlayer(ship);

        // Remote specialization is transient and belongs to the exact current
        // replica. Until synchronized, fail closed to native presentation.
        if (!local && ship.IsAnyPlayerShip() &&
            !LeviathanNetwork.HasSynchronizedSpecialization(ship))
        {
            return InactiveState;
        }

        int configurationRevision =
            LeviathanSpecializationRuntime.ConfigurationRevision;

        int registryRevision = LeviathanSpecializationRegistry.Revision;

        ResolvedCacheEntry cached;
        if (ResolvedByPilot.TryGetValue(pilot, out cached) &&
            cached != null &&
            cached.Ship == ship &&
            cached.ConfigurationRevision == configurationRevision &&
            cached.RegistryRevision == registryRevision &&
            cached.State != null)
        {
            return cached.State;
        }

        ResolvedState state = ResolveState(pilot);

        if (cached == null)
        {
            cached = new ResolvedCacheEntry();
            ResolvedByPilot[pilot] = cached;
        }

        cached.Ship = ship;
        cached.ConfigurationRevision = configurationRevision;
        cached.RegistryRevision = registryRevision;
        cached.State = state;

        return state;
    }

    private static ResolvedState ResolveState(Pilot pilot)
    {
        ResolvedState state = new ResolvedState();

        if (pilot == null ||
            !LeviathanSpecializationRuntime.IsTreeUnlocked(
                pilot,
                LeviathanConstrictorTree.TreeId
            ))
        {
            return state;
        }

        int rank = Mathf.Clamp(
            Mathf.RoundToInt(
                LeviathanSpecializationRuntime.GetKnobFlat(
                    pilot,
                    Knobs.Rank
                )
            ),
            0,
            5
        );

        if (rank < 1)
            return state;

        state.Active = true;
        state.Rank = rank;

        state.DamageMultiplier = Mathf.Max(
            0f,
            LeviathanSpecializationRuntime.GetKnobMultiplier(
                pilot,
                Knobs.FinalDamagePercent
            )
        );

        state.CritChanceBonus =
            LeviathanSpecializationRuntime.GetKnobFlat(
                pilot,
                Knobs.CritChancePoints
            );

        state.PassiveStatusFraction = Mathf.Clamp01(
            LeviathanSpecializationRuntime.GetKnobFlat(
                pilot,
                Knobs.PassiveStatusFraction
            )
        );

        state.AccelerationMultiplier = Mathf.Max(
            0f,
            LeviathanSpecializationRuntime.GetKnobMultiplier(
                pilot,
                Knobs.AccelerationPercent
            )
        );

        state.BoostMultiplier = Mathf.Max(
            0f,
            LeviathanSpecializationRuntime.GetKnobMultiplier(
                pilot,
                Knobs.BoostPercent
            )
        );

        state.TurnSpeedMultiplier = Mathf.Max(
            0f,
            LeviathanSpecializationRuntime.GetKnobMultiplier(
                pilot,
                Knobs.TurnSpeedPercent
            )
        );

        state.ManeuverabilityMultiplier = Mathf.Max(
            0f,
            LeviathanSpecializationRuntime.GetKnobMultiplier(
                pilot,
                Knobs.ManeuverabilityPercent
            )
        );

        state.AirResistanceReduction = Mathf.Clamp01(
            LeviathanSpecializationRuntime.GetKnobFlat(
                pilot,
                Knobs.AirResistanceReductionPoints
            )
        );

        return state;
    }

    private static bool TryGetLocalState(
        GameShip ship,
        out ResolvedState state)
    {
        state = InactiveState;

        if (!IsCurrentPlayer(ship))
            return false;

        state = GetResolvedState(ship);
        return state != null && state.Active;
    }

    private static bool IsCurrentPlayer(GameShip ship)
    {
        return ship != null &&
            WorldController.instance != null &&
            WorldController.instance.GetCurrentPlayerShip() == ship;
    }

    // =========================================================================
    // TICK ENTRY POINT
    // =========================================================================

    /// <summary>
    /// Call once per FixedUpdate from LeviathanController for the local player.
    /// </summary>
    public static void Tick(GameShip player)
    {
        if (!IsCurrentPlayer(player))
        {
            ReleaseSuppression();
            return;
        }

        ResolvedState state = GetResolvedState(player);

        // Live anatomy is authoritative. Tree intent without an instantiated
        // Growth snapshot is not enough to run contact gameplay.
        if (state == null ||
            !state.Active ||
            LeviathanGrowth.GetTotalSectionCount(player) <= 0)
        {
            ReleaseSuppression();
            return;
        }

        Assault src = FindEquippedAssault(player);
        if (src == null)
        {
            ReleaseSuppression();
            return;
        }

        ApplySuppression(src);

        if (!player.IsVisible() ||
            player.IsDisabled() ||
            player.IsWeaponsOffline())
        {
            return;
        }

        RefreshAnatomyRevision(player);

        float tickRate = player.ApplyModifier(
            Modifier.Type.BeamTickRate,
            Item.Category.None,
            BaseContactTickRate,
            true
        );

        tickRate = Mathf.Max(0.0001f, tickRate);

        GatherContacts(player);
        ApplyContactDamage(player, src, state, tickRate);
        PruneLastHits(tickRate);
    }

    public static void Reset()
    {
        ReleaseSuppression();

        foreach (Assault assault in RemoteHiddenAssaults)
        {
            if (assault != null)
                SetBladesVisible(assault, true);
        }

        RemoteHiddenAssaults.Clear();
        ResolvedByPilot.Clear();

        LastHits.Clear();
        StaleHits.Clear();
        ContactBuckets.Clear();
        ContactTargets.Clear();
        StaleContactTargets.Clear();
        ColliderCache.Clear();
        SectionTargets.Clear();
        HeadSections.Clear();
        BodySections.Clear();
        TailSections.Clear();

        contactGeneration = 0;
        lastPrunedContactGeneration = 0;
        anatomyOwner = null;
        anatomyRevision = int.MinValue;
    }

    // =========================================================================
    // GROWTH-OWNED LIVE ANATOMY / CONTACT GATHERING
    // =========================================================================

    private static void RefreshAnatomyRevision(GameShip player)
    {
        int revision = LeviathanGrowth.GetAnatomy(player).Revision;

        if (anatomyOwner == player && anatomyRevision == revision)
            return;

        anatomyOwner = player;
        anatomyRevision = revision;

        // Section objects/colliders can be replaced on rebuild even while the
        // specialization configuration itself is unchanged.
        LastHits.Clear();
        ContactBuckets.Clear();
        ContactTargets.Clear();
        ColliderCache.Clear();
        SectionTargets.Clear();
    }

    private static void GatherContacts(GameShip player)
    {
        unchecked
        {
            contactGeneration++;
            if (contactGeneration == int.MinValue)
                contactGeneration = 1;
        }

        ContactTargets.Clear();

        HeadSections.Clear();
        BodySections.Clear();
        TailSections.Clear();

        LeviathanGrowth.CollectHeadShips(player, HeadSections);
        LeviathanGrowth.CollectBodyShips(player, BodySections);
        LeviathanGrowth.CollectTailShips(player, TailSections);

        GatherSectionList(player, HeadSections);
        GatherSectionList(player, BodySections);
        GatherSectionList(player, TailSections);

        PruneContactBuckets();
    }

    private static void GatherSectionList(
        GameShip owner,
        List<GameShip> sections)
    {
        for (int i = 0; i < sections.Count; i++)
        {
            GameShip section = sections[i];

            if (section == null || !section.gameObject.activeInHierarchy)
                continue;

            GatherSectionContacts(owner, section);
        }
    }

    private static void GatherSectionContacts(
        GameShip owner,
        GameShip section)
    {
        Collider2D[] colliders = GetSectionColliders(section);

        if (colliders == null || colliders.Length == 0)
            return;

        SectionTargets.Clear();

        for (int c = 0; c < colliders.Length; c++)
        {
            Collider2D collider = colliders[c];

            if (!collider ||
                !collider.enabled ||
                !collider.gameObject.activeInHierarchy)
            {
                continue;
            }

            Bounds bounds = collider.bounds;
            float colliderRadius = bounds.extents.magnitude;
            float padding = colliderRadius * ContactHitboxRadiusPadding;
            float queryRadius = colliderRadius + padding;

            int hitCount = Physics2D.OverlapCircleNonAlloc(
                bounds.center,
                queryRadius,
                ProximityBuffer
            );

            for (int h = 0; h < hitCount; h++)
            {
                Collider2D hit = ProximityBuffer[h];

                if (!hit ||
                    hit == collider ||
                    !hit.enabled ||
                    !hit.gameObject.activeInHierarchy)
                {
                    continue;
                }

                ColliderDistance2D separation =
                    Physics2D.Distance(collider, hit);

                if (!separation.isValid || separation.distance > padding)
                    continue;

                GameObject obj = hit.gameObject.CompareTag("Shield")
                    ? hit.transform.parent.gameObject
                    : hit.gameObject;

                if (obj == null ||
                    obj.CompareTag("Container") ||
                    obj.CompareTag("Projectile"))
                {
                    continue;
                }

                GameShip target;
                if (!GameShip.TryGetShip(obj, out target) || target == null)
                    continue;

                // Damage attribution is the primary owner, not the touching
                // follower section. This mirrors a native equipped weapon.
                if (!target.CanBeDamagedBy(owner, false) || target.IsDodging())
                    continue;

                // Compound colliders on either ship still count as one contact
                // from this anatomical section to this target this gather.
                if (!SectionTargets.Add(target))
                    continue;

                AddContact(target, section);
            }
        }
    }

    private static void AddContact(GameShip target, GameShip section)
    {
        ContactBucket bucket;

        if (!ContactBuckets.TryGetValue(target, out bucket) || bucket == null)
        {
            bucket = new ContactBucket();
            ContactBuckets[target] = bucket;
        }

        if (bucket.Generation != contactGeneration)
        {
            bucket.Generation = contactGeneration;
            bucket.LastSeenGeneration = contactGeneration;
            bucket.Sections.Clear();
            ContactTargets.Add(target);
        }

        bucket.Sections.Add(section);
    }

    private static Collider2D[] GetSectionColliders(GameShip section)
    {
        Collider2D[] cached;

        if (ColliderCache.TryGetValue(section, out cached) && cached != null)
            return cached;

        Collider2D[] candidates =
            section.GetComponentsInChildren<Collider2D>(true);

        List<Collider2D> found = new List<Collider2D>(candidates.Length);

        for (int i = 0; i < candidates.Length; i++)
        {
            Collider2D collider = candidates[i];

            if (!collider)
                continue;

            // Constrictor uses physical section geometry, not shield bubbles or
            // unrelated projectile/container colliders parented beneath a ship.
            if (collider.gameObject.CompareTag("Shield") ||
                collider.gameObject.CompareTag("Container") ||
                collider.gameObject.CompareTag("Projectile"))
            {
                continue;
            }

            found.Add(collider);
        }

        cached = found.ToArray();
        ColliderCache[section] = cached;
        return cached;
    }

    private static void PruneContactBuckets()
    {
        // Avoid scanning the persistent target cache every physics tick.
        if (contactGeneration - lastPrunedContactGeneration < 120)
            return;

        lastPrunedContactGeneration = contactGeneration;
        StaleContactTargets.Clear();

        foreach (KeyValuePair<GameShip, ContactBucket> pair in ContactBuckets)
        {
            if (!pair.Key ||
                pair.Value == null ||
                contactGeneration - pair.Value.LastSeenGeneration > 600)
            {
                StaleContactTargets.Add(pair.Key);
            }
        }

        for (int i = 0; i < StaleContactTargets.Count; i++)
            ContactBuckets.Remove(StaleContactTargets[i]);
    }

    // =========================================================================
    // DAMAGE
    // =========================================================================

    private static void ApplyContactDamage(
        GameShip player,
        Assault src,
        ResolvedState state,
        float tickRate)
    {
        string weaponName = src.GetName(false, false);

        for (int t = 0; t < ContactTargets.Count; t++)
        {
            GameShip target = ContactTargets[t];

            if (target == null)
                continue;

            ContactBucket bucket;
            if (!ContactBuckets.TryGetValue(target, out bucket) ||
                bucket == null ||
                bucket.Generation != contactGeneration)
            {
                continue;
            }

            int contactCount = bucket.Sections.Count;
            if (contactCount <= 0)
                continue;

            float aggregateUnits = GetAggregateContactUnits(contactCount);
            float perContactWeight = aggregateUnits / contactCount;

            for (int i = 0; i < contactCount; i++)
            {
                GameShip section = bucket.Sections[i];
                if (section == null)
                    continue;

                ContactKey key = new ContactKey(section, target);
                float last;

                if (LastHits.TryGetValue(key, out last) &&
                    Time.fixedTime - last < tickRate)
                {
                    continue;
                }

                LastHits[key] = Time.fixedTime;

                // Every physical section gets an independent native-style crit
                // roll, but the probability is weighted by the same symmetric
                // contact saturation as damage/status. This preserves separate
                // contact rolls without letting a very large wrap create
                // unbounded on-crit proc volume. At one/two contacts the weight
                // is 1.0, exactly matching the source Assault's crit chance.
                float critChance = Mathf.Max(
                    0f,
                    (src.GetCritChance() + state.CritChanceBonus) *
                    perContactWeight
                );

                bool crit = Modifier.CritRoll(critChance, target);

                // Native Assault applies on-crit effects before reading Damage,
                // allowing OnCritDamageIncrease to affect the triggering hit.
                if (crit && player.health > 0f)
                    ApplyNativeOnCritEffects(src, player);

                float critMultiplier = crit
                    ? 1f + src.GetCritModifier()
                    : 1f;

                float packetMultiplier =
                    EngineTickFraction *
                    state.DamageMultiplier *
                    (perContactWeight / BaselineAggregateContacts) *
                    critMultiplier;

                float damage = src.Damage * packetMultiplier;

                // Preserve native Assault's resolved DPS reference. Native
                // per-blade packets also carry the aggregate weapon DPS rather
                // than dividing this field by blade count.
                float dps = src.CalculateDPS(Activatable.Modified.Global);

                // Status follows the same contact saturation weight so adding a
                // very large wrap does not create unbounded status proc volume.
                float statusChance = Mathf.Max(
                    0f,
                    src.StatusEffectChance *
                    state.PassiveStatusFraction *
                    perContactWeight
                );

                BuildDamageData(src, damage, dps);

                Vector2 direction =
                    (Vector2)(target.transform.position - section.transform.position);

                if (direction.sqrMagnitude > 0.0001f)
                    direction.Normalize();
                else
                    direction = section.transform.right;

                target.SetLastDamageDirection(direction);
                target.lastDamagedByWeaponName = weaponName;
                target.lastDamagedByShipName = player.GetName();
                target.lastDamagedByFaction = player.faction;

                bool bypassDamageLimit =
                    src.HasCustomizer(Customizer.Type.BypassDamageLimit);

                Vector2 hitPoint = target.transform.position;

                bool destroyed = RouteDamage(
                    target,
                    src.damageType,
                    DamageBuffer,
                    statusChance,
                    crit,
                    hitPoint,
                    player,
                    bypassDamageLimit,
                    0f, // Deliberate Constrictor divergence: no knockback.
                    src
                );

                RelayConduitHit(
                    src,
                    player,
                    target,
                    DamageBuffer,
                    hitPoint,
                    bypassDamageLimit
                );

                // Native Assault performs local Gladiator leech immediately and
                // Activatable.NetConfirmDamageResult handles deferred remote
                // damage because src is retained as slotSource.
                if (!target.IsNetRemote())
                    ApplyNativeGladiatorLeech(src, target, hitPoint);

                if (destroyed && target && !target.IsDrone())
                {
                    ApplyLocalOnKillEffects(
                        src,
                        player,
                        target,
                        hitPoint,
                        dps
                    );
                }
            }
        }
    }

    private static float GetAggregateContactUnits(int contacts)
    {
        if (contacts <= 0)
            return 0f;

        if (contacts <= (int)BaselineAggregateContacts)
            return contacts;

        float overflow = contacts - BaselineAggregateContacts;
        float available =
            MaxAggregateContactUnits - BaselineAggregateContacts;

        return BaselineAggregateContacts +
            available *
            (overflow / (overflow + OverflowHalfSaturationContacts));
    }

    private static void ApplyNativeOnCritEffects(
        Assault src,
        GameShip player)
    {
        if (src == null ||
            player == null ||
            ApplyOnCritStatusEffectsMethod == null)
        {
            return;
        }

        ApplyOnCritStatusEffectsMethod.Invoke(
            src,
            new object[] { player }
        );
    }

    private static void ApplyNativeGladiatorLeech(
        Assault src,
        GameShip target,
        Vector2 hitPoint)
    {
        if (src == null ||
            target == null ||
            LeechGladiatorHullMethod == null)
        {
            return;
        }

        LeechGladiatorHullMethod.Invoke(
            src,
            new object[] { target, hitPoint }
        );
    }

    private static void RelayConduitHit(
        Assault src,
        GameShip player,
        GameShip target,
        Damageable.DamageData[] damageData,
        Vector2 hitPoint,
        bool bypassDamageLimit)
    {
        if (src == null ||
            player == null ||
            target == null ||
            ConduitRelayHitMethod == null)
        {
            return;
        }

        ConduitRelayHitMethod.Invoke(
            null,
            new object[]
            {
                src,
                player,
                target,
                damageData,
                hitPoint,
                bypassDamageLimit
            }
        );
    }

    private static void ApplyLocalOnKillEffects(
        Assault src,
        GameShip player,
        GameShip target,
        Vector2 position,
        float dps)
    {
        if (src == null || player == null || target == null)
            return;

        float shedChance =
            src.ApplyModifierToPercentage(
                Modifier.Type.OnEnemyDeathShed,
                0f,
                true
            );

        if (shedChance > 0f)
            target.ShedStatusEffects(shedChance, true, player);

        Projectile.ProcessDeathSurge(
            src,
            player,
            position,
            dps
        );
    }

    private static void BuildDamageData(
        Assault src,
        float damage,
        float dps)
    {
        DamageBuffer[0] = new Damageable.DamageData(damage, dps);
        DamageBuffer[1] = Delta(src, Modifier.Type.DamageVsHealth, damage, dps);
        DamageBuffer[2] = Delta(src, Modifier.Type.DamageVsShield, damage, dps);
        DamageBuffer[3] = Delta(src, Modifier.Type.DamageVsBurning, damage, dps);
        DamageBuffer[4] = Delta(src, Modifier.Type.DamageVsCorroding, damage, dps);
        DamageBuffer[5] = Delta(src, Modifier.Type.DamageVsDisabled, damage, dps);
        DamageBuffer[6] = Delta(src, Modifier.Type.DamageVsFrozen, damage, dps);
        DamageBuffer[7] = Delta(src, Modifier.Type.DamageVsRadioactive, damage, dps);
    }

    private static Damageable.DamageData Delta(
        Assault src,
        Modifier.Type type,
        float damage,
        float dps)
    {
        return new Damageable.DamageData(
            type,
            src.ApplyModifier(type, damage, false, true) - damage,
            dps
        );
    }

    private static bool warnedNoRouteDamage;

    private static bool RouteDamage(
        GameShip damageable,
        Damageable.DamageType damageType,
        Damageable.DamageData[] damageData,
        float statusEffectChance,
        bool crit,
        Vector2 fromPosition,
        GameShip fromShip,
        bool bypassDamageLimit,
        float knockbackPower,
        Activatable slotSource)
    {
        if (RouteDamageMethod == null)
        {
            if (!warnedNoRouteDamage)
            {
                warnedNoRouteDamage = true;
                Debug.LogError(
                    "[Leviathan] Could not resolve NetCombat.RouteDamage; " +
                    "Constrictor will deal no damage."
                );
            }

            return false;
        }

        object result = RouteDamageMethod.Invoke(
            null,
            new object[]
            {
                damageable,
                damageType,
                damageData,
                statusEffectChance,
                crit,
                fromPosition,
                fromShip,
                bypassDamageLimit,
                knockbackPower,
                slotSource,
                0f,
                0f,
                false,
                0f
            }
        );

        // False can mean the damage was routed to the remote target owner. It is
        // not a failure signal and must never be retried by Constrictor.
        return result is bool && (bool)result;
    }

    private static void PruneLastHits(float tickRate)
    {
        float window = tickRate * 4f;
        StaleHits.Clear();

        foreach (KeyValuePair<ContactKey, float> pair in LastHits)
        {
            if (!pair.Key.Section ||
                !pair.Key.Target ||
                Time.fixedTime - pair.Value > window)
            {
                StaleHits.Add(pair.Key);
            }
        }

        for (int i = 0; i < StaleHits.Count; i++)
            LastHits.Remove(StaleHits[i]);
    }

    // =========================================================================
    // ASSAULT SOURCE / VISUAL SUPPRESSION
    // =========================================================================

    private static Assault FindEquippedAssault(GameShip player)
    {
        if (player == null || player.slots == null)
            return null;

        for (int i = 0; i < player.slots.Length; i++)
        {
            if (player.slots[i] == null)
                continue;

            Assault assault = player.slots[i].equippable as Assault;
            if (assault != null)
                return assault;
        }

        return null;
    }

    private static GameShip GetParentShip(Assault assault)
    {
        return assault == null ? null : assault.parentShip;
    }

    private static void ApplySuppression(Assault src)
    {
        if (suppressed == src)
            return;

        ReleaseSuppression();
        suppressed = src;
        SetBladesVisible(src, false);
    }

    private static void ReleaseSuppression()
    {
        if (suppressed == null)
            return;

        Assault old = suppressed;
        suppressed = null;

        // A remote presentation entry should never normally overlap the local
        // source, but only restore if no other Constrictor presentation claim
        // remains on this exact Assault object.
        if (!RemoteHiddenAssaults.Contains(old))
            SetBladesVisible(old, true);
    }

    public static bool IsSuppressed(Assault assault)
    {
        return suppressed != null && suppressed == assault;
    }

    private static bool IsConstrictorSource(Assault assault)
    {
        GameShip owner = GetParentShip(assault);
        if (owner == null || !owner.IsAnyPlayerShip())
            return false;

        ResolvedState state = GetResolvedState(owner);
        if (state == null || !state.Active)
            return false;

        return FindEquippedAssault(owner) == assault;
    }

    private static bool IsPredatorTreeActive(GameShip owner)
    {
        if (owner == null)
            return false;

        Pilot pilot = GameShip.GetPlayerSourcePilot(owner);
        if (pilot == null)
            return false;

        if (!IsCurrentPlayer(owner) && owner.IsAnyPlayerShip() &&
            !LeviathanNetwork.HasSynchronizedSpecialization(owner))
        {
            return false;
        }

        return LeviathanSpecializationRuntime.IsTreeUnlocked(
            pilot,
            LeviathanPredatorTree.TreeId
        );
    }

    public static bool ShouldSuppressNativeAssaultStart(Assault assault)
    {
        if (assault == null || !IsConstrictorSource(assault))
            return false;

        // Predator intentionally reuses native StartAttack -> Lunge. Constrictor
        // alone is passive and therefore blocks the native active lunge.
        return !IsPredatorTreeActive(GetParentShip(assault));
    }

    public static void RefreshAssaultPresentation(Assault assault)
    {
        if (assault == null)
            return;

        GameShip owner = GetParentShip(assault);
        bool remote = owner != null &&
            owner.IsAnyPlayerShip() &&
            !IsCurrentPlayer(owner);

        bool shouldHideRemote =
            remote &&
            IsConstrictorSource(assault);

        bool wasHiddenRemote = RemoteHiddenAssaults.Contains(assault);

        if (shouldHideRemote && !wasHiddenRemote)
        {
            RemoteHiddenAssaults.Add(assault);
            SetBladesVisible(assault, false);
        }
        else if (!shouldHideRemote && wasHiddenRemote)
        {
            RemoteHiddenAssaults.Remove(assault);

            if (!IsSuppressed(assault))
                SetBladesVisible(assault, true);
        }
    }

    public static void EnsureHidden(Assault assault)
    {
        if (assault == null)
            return;

        if (IsSuppressed(assault) ||
            RemoteHiddenAssaults.Contains(assault) ||
            IsConstrictorSource(assault))
        {
            SetBladesVisible(assault, false);
        }
    }

    public static void ForgetAssault(Assault assault)
    {
        if (assault == null)
            return;

        bool wasRemoteHidden = RemoteHiddenAssaults.Remove(assault);
        bool wasSuppressed = suppressed == assault;

        if (wasSuppressed)
            suppressed = null;

        if (wasRemoteHidden || wasSuppressed)
            SetBladesVisible(assault, true);
    }

    private static void SetBladesVisible(Assault assault, bool visible)
    {
        if (assault == null || AssaultBladesField == null)
            return;

        System.Collections.IEnumerable blades =
            AssaultBladesField.GetValue(assault) as System.Collections.IEnumerable;

        if (blades == null)
            return;

        foreach (object blade in blades)
        {
            if (blade == null)
                continue;

            Type bladeType = blade.GetType();

            GameObject obj =
                AccessTools.Field(bladeType, "obj")?.GetValue(blade) as GameObject;

            if (obj)
                obj.SetActive(visible);

            SwishTrail swish =
                AccessTools.Field(bladeType, "swish")?.GetValue(blade) as SwishTrail;

            if (swish)
                swish.gameObject.SetActive(visible);
        }
    }

    // =========================================================================
    // MOVEMENT HELPERS
    // =========================================================================

    public static float GetAirResistanceReduction(GameShip player)
    {
        ResolvedState state;

        return TryGetLocalState(player, out state)
            ? state.AirResistanceReduction
            : 0f;
    }
}

// =============================================================================
// HARMONY PATCHES
// =============================================================================

// Constrictor movement modifiers are a specialization layer on top of the
// already-resolved native values, matching the current Growth bridge pattern.

[HarmonyPatch(typeof(GameShip), "get_TurnSpeed")]
public static class LeviathanConstrictorTurnSpeedPatch
{
    public static void Postfix(GameShip __instance, ref float __result)
    {
        LeviathanConstrictor.ResolvedState state =
            LeviathanConstrictor.GetResolvedState(__instance);

        if (state == null || !state.Active)
            return;

        if (WorldController.instance == null ||
            WorldController.instance.GetCurrentPlayerShip() != __instance)
        {
            return;
        }

        __result *= state.TurnSpeedMultiplier;
    }
}

[HarmonyPatch(typeof(Thruster), "get_AccelerationFactor")]
public static class LeviathanConstrictorAccelerationPatch
{
    public static void Postfix(Thruster __instance, ref float __result)
    {
        GameShip ship = __instance == null ? null : __instance.parentShip;

        if (ship == null ||
            WorldController.instance == null ||
            WorldController.instance.GetCurrentPlayerShip() != ship)
        {
            return;
        }

        LeviathanConstrictor.ResolvedState state =
            LeviathanConstrictor.GetResolvedState(ship);

        if (state != null && state.Active)
            __result *= state.AccelerationMultiplier;
    }
}

[HarmonyPatch(typeof(Thruster), "get_BoostFactor")]
public static class LeviathanConstrictorBoostPatch
{
    public static void Postfix(Thruster __instance, ref float __result)
    {
        GameShip ship = __instance == null ? null : __instance.parentShip;

        if (ship == null ||
            WorldController.instance == null ||
            WorldController.instance.GetCurrentPlayerShip() != ship)
        {
            return;
        }

        LeviathanConstrictor.ResolvedState state =
            LeviathanConstrictor.GetResolvedState(ship);

        if (state != null && state.Active)
            __result *= state.BoostMultiplier;
    }
}

[HarmonyPatch(typeof(Thruster), "get_ControlMultiplier")]
public static class LeviathanConstrictorManeuverabilityPatch
{
    public static void Postfix(Thruster __instance, ref float __result)
    {
        GameShip ship = __instance == null ? null : __instance.parentShip;

        if (ship == null ||
            WorldController.instance == null ||
            WorldController.instance.GetCurrentPlayerShip() != ship)
        {
            return;
        }

        LeviathanConstrictor.ResolvedState state =
            LeviathanConstrictor.GetResolvedState(ship);

        if (state != null && state.Active)
            __result *= state.ManeuverabilityMultiplier;
    }
}

/// <summary>
/// Refund a resolved fraction of whatever Growth's current high-speed resistance
/// actually removed. This composes with Growth's evolving curve without copying
/// its threshold/formula into Constrictor.
/// </summary>
[HarmonyPatch]
public static class LeviathanConstrictorAirResistancePatch
{
    public struct ResistanceState
    {
        public Rigidbody2D Body;
        public Vector2 VelocityBefore;
        public float Reduction;
        public bool Valid;
    }

    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(LeviathanController),
            "ApplyHighSpeedResistance",
            Type.EmptyTypes
        );
    }

    public static void Prefix(out ResistanceState __state)
    {
        __state = new ResistanceState();

        if (WorldController.instance == null)
            return;

        GameShip player =
            WorldController.instance.GetCurrentPlayerShip();

        if (player == null)
            return;

        float reduction =
            LeviathanConstrictor.GetAirResistanceReduction(player);

        if (reduction <= 0f)
            return;

        Rigidbody2D body = player.GetRigidBody();
        if (body == null)
            return;

        __state.Body = body;
        __state.VelocityBefore = body.velocity;
        __state.Reduction = Mathf.Clamp01(reduction);
        __state.Valid = true;
    }

    public static void Postfix(ResistanceState __state)
    {
        if (!__state.Valid || !__state.Body)
            return;

        Vector2 velocityAfter = __state.Body.velocity;
        Vector2 removed = __state.VelocityBefore - velocityAfter;

        __state.Body.velocity =
            velocityAfter + removed * __state.Reduction;
    }
}

/// <summary>
/// Owner gameplay: suppress the source Assault's native blade packet. Remote
/// replicas are intentionally not placed in the authoritative `suppressed`
/// state; they only hide presentation through the separate visual path.
/// </summary>
[HarmonyPatch]
public static class LeviathanConstrictorSuppressBladeDamagePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(Assault), "DoDamageTick");
    }

    public static bool Prefix(Assault __instance)
    {
        return !LeviathanConstrictor.IsSuppressed(__instance);
    }
}

/// <summary>
/// Constrictor is passive and suppresses native Assault StartAttack. Predator is
/// the exception because it deliberately converts that native StartAttack/Lunge
/// lifecycle. The same decision is derivable on synchronized remote replicas.
/// </summary>
[HarmonyPatch]
public static class LeviathanConstrictorSuppressNativeLungePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(Assault), "StartAttack");
    }

    public static bool Prefix(Assault __instance)
    {
        return !LeviathanConstrictor.ShouldSuppressNativeAssaultStart(__instance);
    }

    public static void Postfix(Assault __instance)
    {
        LeviathanConstrictor.EnsureHidden(__instance);
    }
}

/// <summary>
/// Slow-changing remote Constrictor presentation is derived from synchronized
/// specialization. No dynamic network slot is needed merely to keep the source
/// Assault blades hidden.
/// </summary>
[HarmonyPatch(typeof(Assault), "FixedUpdate")]
public static class LeviathanConstrictorAssaultPresentationPatch
{
    public static void Postfix(Assault __instance)
    {
        LeviathanConstrictor.RefreshAssaultPresentation(__instance);
    }
}

[HarmonyPatch(typeof(Assault), "Unequip")]
public static class LeviathanConstrictorAssaultUnequipPatch
{
    public static void Prefix(Assault __instance)
    {
        LeviathanConstrictor.ForgetAssault(__instance);
    }
}
