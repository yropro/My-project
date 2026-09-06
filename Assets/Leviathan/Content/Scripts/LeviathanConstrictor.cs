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
// While an Assault weapon (ram / scythe / blades) is being used as a Leviathan
// skill source, its physical blades and native blade damage are suppressed.
// Constrictor replaces passive contact damage; Predator may retain the native
// StartAttack/Lunge lifecycle while replacing the active hit.
//
// Baseline anchor: two frigate-scale segments touching a target are equivalent
// to two scythe blades touching it in the weapon's non-lunge state. Passive
// scythe contact is 1/4 of a lunge tick (Assault.attackMultiplier == 4).
//
// Per target, contacting Leviathan sections (head, bodies, tail) are ranked
// head-first and decayed:
//     hit 1, 2 -> 1.0
//     hit 3    -> 0.6
//     hit 4    -> 0.36 ...
// The infinite series converges to 3.5 section-units, so even when the entire
// Leviathan is wrapped around one target, output approaches 1.75x vanilla
// scythe passive before the rank scalar. Every touching section still hits.
//
// Rank scales damage and the status proc fraction (0.20 -> 0.40).
// Constrictor also improves handling through the same native modifier types
// used by Vanguard: Acceleration, Boost, Turn Speed and Maneuverability gain
// +5% per rank. Growth's added high-speed resistance is reduced from 10% at
// Rank 1 to 60% at Rank 5.
//
// Crit modifier is never scaled: expected crit damage is base * (1 + c*m), so
// scaling base, c and m together would decay the crit contribution cubically
// while base decays linearly.
//
// Constrictor inherits the equipped Assault's native damage type, DamageVsX,
// status, crit, on-crit, Conduit and on-kill traits. Native knockback and
// Gladiator hull leech are intentionally excluded for usability/balance.
//
// Growth / ship-size scaling mirrors Predator's tuning model: every active
// Leviathan section uses the MAIN SHIP's class value, with Growth Rank 1 +
// Frigate as the no-bonus baseline. Constrictor uses a smaller default step
// because additional touching sections already increase total contact damage.
//
// =============================================================================

public static class LeviathanConstrictor
{
    // ---- Skill identity -----------------------------------------------------

    // Registration lives in LeviathanSkillSystem alongside Growth: appending to
    // Upgrade.upgrades is only half the job, and the UI entry comes from
    // LeviathanSkillUI calling UpgradeClassDisplay.AddUpgrade.
    private static Upgrade.Key ConstrictorUpgrade
    {
        get { return LeviathanMod.ConstrictorUpgrade; }
    }

    // =========================================================================
    // BALANCE TUNING
    // =========================================================================
    // Arrays are Rank 1 -> Rank 5.

    // Overall passive contact-damage multiplier.
    private static readonly float[] DamageMultiplierByRank =
    {
        1.90f, // Rank 1
        2.00f, // Rank 2
        2.10f, // Rank 3
        2.20f, // Rank 4
        2.30f  // Rank 5
    };

    // Fraction of the equipped Assault weapon's status chance used by Constrictor.
    private static readonly float[] StatusProcFractionByRank =
    {
        0.20f, // Rank 1
        0.25f, // Rank 2
        0.30f, // Rank 3
        0.35f, // Rank 4
        0.40f  // Rank 5
    };

    // Fraction of Growth's added high-speed resistance removed by rank.
    // Endpoints requested: 10% at Rank 1, 60% at Rank 5.
    private static readonly float[] AirResistanceReductionByRank =
    {
        0.100f, // Rank 1
        0.225f, // Rank 2
        0.350f, // Rank 3
        0.475f, // Rank 4
        0.600f  // Rank 5
    };

    // Native Vanguard-style handling modifiers. These are deliberately separate
    // knobs even though they currently share the same value.
    private const float AccelerationBonusPerRank = 0.05f;
    private const float BoostBonusPerRank = 0.05f;
    private const float TurnSpeedBonusPerRank = 0.05f;
    private const float ManeuverabilityBonusPerRank = 0.05f;

    // Growth / ship-size damage scaling. This deliberately mirrors Predator's
    // knob layout so both Leviathan Assault skills are easy to tune together.
    //
    // Every active section uses the MAIN SHIP's class value. Individual body
    // segment classes are intentionally ignored.
    private const float FrigateSectionValue = 1.00f;
    private const float DestroyerSectionValue = 1.20f;
    private const float CruiserSectionValue = 1.30f;
    private const float BattleshipSectionValue = 1.40f;
    private const float DreadnoughtSectionValue = 1.50f;

    // Growth Rank 1 / Frigate is the no-bonus baseline.
    private static readonly float BaselineSectionValue =
        (LeviathanGrowth.GetBodySegmentCountForRank(1) + 2) *
        FrigateSectionValue;

    // Each section-value point above baseline adds this much Constrictor damage.
    // Predator currently uses 0.08; Constrictor starts lower because its total
    // output already scales when multiple sections physically touch a target.
    private const float SectionValueDamageStep = 0.20f;

    // Number of full-value segment contacts before decay starts.
    private const int FullWeightContacts = 2;

    // Every contact after FullWeightContacts contributes this fraction of the
    // previous contact.
    private const float ContactDecay = 0.60f;

    // Contacts that equal one aggregate weapon's worth of passive contact damage.
    private const float BaselineContacts = 2.00f;

    // =========================================================================
    // NATIVE / MECHANICAL CONSTANTS
    // =========================================================================

    // Assault's own per-tick fraction of listed Damage. Keep at 0.2 for native
    // Assault.GetDamageData parity.
    private const float EngineTickFraction = 0.20f;

    // Base interval passed through the player's BeamTickRate modifier.
    private const float BaseContactTickRate = 0.20f;

    // ---- Native access ------------------------------------------------------

    // NetCombat has two RouteDamage overloads (a DamageData[] form and a
    // scalar damage/dps form), so AccessTools.Method by name alone throws
    // AmbiguousMatchException. The parameter-type array can't disambiguate
    // either, because parameter 0 is the internal IDamageable. Match on shape
    // instead: the array form takes 14 parameters with DamageData[] at index 2.
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

    private static readonly FieldInfo AssaultBladesField =
        AccessTools.Field(typeof(Assault), "blades");

    // Native Assault.GetDamageData applies these before calculating a critical
    // hit's damage, so Constrictor invokes the same protected Activatable method.
    private static readonly MethodInfo ApplyOnCritStatusEffectsMethod =
        AccessTools.Method(
            typeof(Activatable),
            "ApplyOnCritStatusEffects",
            new Type[] { typeof(GameShip) }
        );

    // Conduit.RelayHit is internal because its target parameter is the internal
    // IDamageable interface. Resolve the DamageData[] overload by method shape.
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

    // ---- State --------------------------------------------------------------

    // (segment, target) -> Time.fixedTime of last applied hit.
    private static readonly Dictionary<ContactKey, float> LastHits =
        new Dictionary<ContactKey, float>();

    private static readonly List<ContactKey> StaleHits =
        new List<ContactKey>();

    // Rebuilt every tick. target -> contacting segments, head-first.
    private static readonly Dictionary<GameShip, List<GameShip>> Contacts =
        new Dictionary<GameShip, List<GameShip>>();

    private static readonly List<GameShip> ContactTargets =
        new List<GameShip>();

    // section -> every non-shield collider that contributes to its physical
    // footprint. A section only contributes once per target even if several of
    // these colliders overlap the same ship.
    private static readonly Dictionary<GameShip, Collider2D[]> ColliderCache =
        new Dictionary<GameShip, Collider2D[]>();

    // Reused while gathering one section so compound colliders cannot add the
    // same (section, target) pair more than once.
    private static readonly HashSet<GameShip> SectionTargets =
        new HashSet<GameShip>();

    private static readonly Damageable.DamageData[] DamageBuffer =
        new Damageable.DamageData[8];

    // The Assault instance currently suppressed, so the patch stays O(1).
    private static Assault suppressed;

    private struct ContactKey : IEquatable<ContactKey>
    {
        public readonly GameShip segment;
        public readonly GameShip target;

        public ContactKey(GameShip segment, GameShip target)
        {
            this.segment = segment;
            this.target = target;
        }

        public bool Equals(ContactKey other)
        {
            return segment == other.segment && target == other.target;
        }

        public override int GetHashCode()
        {
            int a = segment == null ? 0 : segment.GetInstanceID();
            int b = target == null ? 0 : target.GetInstanceID();
            return (a * 397) ^ b;
        }
    }

    // =========================================================================
    // Tick entry point
    // =========================================================================

    /// <summary>
    /// Call once per FixedUpdate from LeviathanController with the live player.
    /// </summary>
    public static void Tick(GameShip player)
    {
        if (player == null || player.squadron == null)
        {
            ReleaseSuppression();
            return;
        }

        // Assault becomes an invisible Leviathan stat source when either
        // Constrictor or Predator is active. Only Constrictor rank controls the
        // passive contact-damage calculation below.
        int rank = GetRank(player);
        int predatorRank = GetPredatorRank(player);

        if (LeviathanMod.Controller == null ||
            LeviathanMod.Controller.GetActiveSectionCount(player) <
                LeviathanGrowth.GetBodySegmentCountForRank(1) + 2 ||
            (rank < 1 && predatorRank < 1))
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

        // Predator-only still hides/disables the physical Assault weapon, but
        // there is no replacement passive contact damage until Constrictor is
        // actually ranked.
        if (rank < 1)
            return;

        if (!player.IsVisible() ||
            player.IsDisabled() ||
            player.IsWeaponsOffline())
        {
            return;
        }

        float tickRate = player.ApplyModifier(
            Modifier.Type.BeamTickRate,
            Item.Category.None,
            BaseContactTickRate,
            true
        );

        GatherContacts(player);
        ApplyContactDamage(player, src, rank, tickRate);
        PruneLastHits(tickRate);
    }

    /// <summary>
    /// Call from ClearPlayerShip / teardown so a suppressed weapon is restored.
    /// </summary>
    public static void Reset()
    {
        ReleaseSuppression();
        LastHits.Clear();
        Contacts.Clear();
        ContactTargets.Clear();
        ColliderCache.Clear();
        SectionTargets.Clear();
    }

    // =========================================================================
    // Contact gathering
    // =========================================================================

    private static void GatherContacts(GameShip player)
    {
        Contacts.Clear();
        ContactTargets.Clear();

        List<Squadron.SquadronShip> ships = player.squadron.ships;

        if (ships == null || PhysicsController.instance == null)
            return;

        // Index 0 is the player head. Body segments and tail follow in chain
        // order, giving the decay a stable head-first ranking. The head is an
        // intentional Constrictor contact section for usability.
        for (int i = 0; i < ships.Count; i++)
        {
            GameShip section = i == 0 ? player : ships[i].ship;

            if (section == null || !section.gameObject.activeInHierarchy)
                continue;

            Collider2D[] colliders = GetSectionColliders(section);

            if (colliders == null || colliders.Length == 0)
                continue;

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

                // PhysicsController returns a single shared buffer that the next
                // overlap call overwrites, so consume every result before querying
                // the next collider in this compound section footprint.
                Collider2D[] hits =
                    PhysicsController.instance.OverlapCollider(collider);

                for (int h = 0; h < hits.Length; h++)
                {
                    Collider2D hit = hits[h];

                    if (!hit)
                        break;                  // null terminator, not a guard

                    GameObject obj = hit.gameObject.CompareTag("Shield")
                        ? hit.transform.parent.gameObject
                        : hit.gameObject;

                    if (obj.CompareTag("Container") || obj.CompareTag("Projectile"))
                        continue;

                    GameShip target;

                    if (!GameShip.TryGetShip(obj, out target) || target == null)
                        continue;

                    // Faction and squadron checks inside CanBeDamagedBy reject
                    // the player's own head and sibling Leviathan sections.
                    if (!target.CanBeDamagedBy(section, false))
                        continue;

                    if (target.IsDodging())
                        continue;

                    // One Leviathan section may have several colliders, and a
                    // target may expose several colliders. Either way, this
                    // section counts exactly once against this target this tick.
                    if (!SectionTargets.Add(target))
                        continue;

                    List<GameShip> list;

                    if (!Contacts.TryGetValue(target, out list))
                    {
                        list = new List<GameShip>();
                        Contacts[target] = list;
                        ContactTargets.Add(target);
                    }

                    list.Add(section);
                }
            }
        }
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

            // Shield bubbles are intentionally excluded. Constrictor should use
            // the physical footprint of the ship section itself.
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

    // =========================================================================
    // Damage application
    // =========================================================================

    private static void ApplyContactDamage(
        GameShip player,
        Assault src,
        int rank,
        float tickRate)
    {
        float damageScalar = GetRankValue(DamageMultiplierByRank, rank);
        float procFraction = GetRankValue(StatusProcFractionByRank, rank);
        float growthSizeScalar = GetGrowthSizeDamageMultiplier(player);
        string weaponName = src.GetName(false, false);

        for (int t = 0; t < ContactTargets.Count; t++)
        {
            GameShip target = ContactTargets[t];

            if (target == null)
                continue;

            List<GameShip> segmentsHere = Contacts[target];

            // Every simultaneously touching Leviathan section is allowed to
            // contribute. ContactDecay makes late sections increasingly small,
            // so no arbitrary contact-count cutoff is needed.
            int ranked = segmentsHere.Count;

            for (int i = 0; i < ranked; i++)
            {
                GameShip segment = segmentsHere[i];

                if (segment == null)
                    continue;

                ContactKey key = new ContactKey(segment, target);
                float last;

                if (LastHits.TryGetValue(key, out last) &&
                    Time.fixedTime - last < tickRate)
                {
                    continue;
                }

                LastHits[key] = Time.fixedTime;

                float weight = i < FullWeightContacts
                    ? 1f
                    : Mathf.Pow(
                        ContactDecay,
                        i - FullWeightContacts + 1
                    );

                // Native Assault rolls its live crit chance separately for every
                // blade contact. Do the same so OnCritCritChance can affect later
                // section hits during the same Constrictor tick.
                bool crit =
                    Modifier.CritRoll(
                        src.GetCritChance() * weight,
                        target
                    );

                // Native Assault.GetDamageData applies on-crit statuses BEFORE
                // reading Damage and DPS, allowing OnCritDamageIncrease to affect
                // the critical hit that triggered it. Preserve that ordering.
                if (crit && player && player.health > 0f)
                    ApplyNativeOnCritEffects(src, player);

                float critModifier = src.GetCritModifier();
                float mul = crit ? (1f + critModifier) : 1f;

                mul *= EngineTickFraction;
                mul /= BaselineContacts;
                mul *= weight;
                mul *= damageScalar;
                mul *= growthSizeScalar;

                // Read live modified values after the on-crit effects above.
                float damage = src.Damage * mul;
                float dps =
                    src.CalculateDPS(Activatable.Modified.Global);

                float status =
                    src.StatusEffectChance *
                    procFraction *
                    weight;

                BuildDamageData(src, damage, dps);

                target.SetLastDamageDirection(
                    ((Vector2)(
                        target.transform.position -
                        segment.transform.position
                    )).normalized
                );

                target.lastDamagedByWeaponName = weaponName;
                target.lastDamagedByShipName = player.GetName();
                target.lastDamagedByFaction = player.faction;

                bool bypassDamageLimit =
                    src.HasCustomizer(
                        Customizer.Type.BypassDamageLimit
                    );

                // Knockback is intentionally disabled for Constrictor usability.
                // Passing 0 also prevents network-routed player hits from getting
                // native Assault knockback.
                bool destroyed = RouteDamage(
                    target,
                    src.damageType,
                    DamageBuffer,
                    status,
                    crit,
                    target.transform.position,
                    player,
                    bypassDamageLimit,
                    0f,
                    src
                );

                // Native Assault relays every successful blade hit to linked
                // Conduits using the actual host hit's DamageData.
                RelayConduitHit(
                    src,
                    player,
                    target,
                    DamageBuffer,
                    target.transform.position,
                    bypassDamageLimit
                );

                // Gladiator hull leech is deliberately NOT called here.

                // In local/single-player damage RouteDamage returns whether this
                // hit destroyed the target. Remote network kills are confirmed
                // through Activatable.NetConfirmDamageResult, which already runs
                // the native death-surge path for the supplied slotSource.
                if (destroyed &&
                    target &&
                    !target.IsDrone())
                {
                    ApplyLocalOnKillEffects(
                        src,
                        player,
                        target,
                        target.transform.position,
                        dps
                    );
                }
            }
        }
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
        if (src == null ||
            player == null ||
            target == null)
        {
            return;
        }

        // Mirrors native Assault.DoBladeDamage. Reanimate is not duplicated here:
        // passing src as slotSource into NetCombat.RouteDamage already supplies
        // native pending-kill / network reanimate context.
        float shedChance =
            src.ApplyModifierToPercentage(
                Modifier.Type.OnEnemyDeathShed,
                0f,
                true
            );

        if (shedChance > 0f)
            target.ShedStatusEffects(shedChance, true, player);

        // Public native helper used by projectiles and network-confirmed weapon
        // kills. It reads all six OnEnemyDeath*Surge modifiers from src.
        Projectile.ProcessDeathSurge(
            src,
            player,
            position,
            dps
        );
    }

    /// <summary>
    /// Mirrors Assault.GetDamageData: index 0 is the base hit, 1-7 are the
    /// DamageVsX modifier deltas. Reuses one buffer, as the native code does.
    /// </summary>
    private static void BuildDamageData(Assault src, float damage, float dps)
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

    // Note: NetCombat.RouteDamage's first parameter is IDamageable, which is
    // an *internal* interface and therefore cannot be named from a mod
    // assembly. GameShip reaches it through Damageable, and reflection binds
    // the argument at invoke time, so taking GameShip here is equivalent.
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

        // NetCombat.RouteDamage is internal. It resolves authority by target
        // ownership: the attacker computes crit and damage locally and the
        // resulting values are sent, so nothing here needs to agree across
        // machines. A false return means "deferred to the network", NOT
        // "failed" -- never retry on it.
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
                0f,        // impaleDps
                0f,        // impaleDuration
                false,     // forceAttackerLocal
                0f         // impaleRotation
            }
        );

        return result is bool && (bool)result;
    }

    private static void PruneLastHits(float tickRate)
    {
        float window = tickRate * 4f;

        StaleHits.Clear();

        foreach (KeyValuePair<ContactKey, float> pair in LastHits)
        {
            if (!pair.Key.segment ||
                !pair.Key.target ||
                Time.fixedTime - pair.Value > window)
            {
                StaleHits.Add(pair.Key);
            }
        }

        for (int i = 0; i < StaleHits.Count; i++)
            LastHits.Remove(StaleHits[i]);
    }

    // =========================================================================
    // Assault suppression
    // =========================================================================

    private static Assault FindEquippedAssault(GameShip player)
    {
        if (player.slots == null)
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

        SetBladesVisible(suppressed, true);
        suppressed = null;
    }

    /// <summary>
    /// Assault.blades is a private List of a private nested Blade class. Each
    /// entry owns the blade GameObject plus a SwishTrail that BuildBlade
    /// reparents to world space, so both need hiding.
    /// </summary>
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

    public static bool IsSuppressed(Assault assault)
    {
        return suppressed != null && suppressed == assault;
    }

    public static void EnsureHidden(Assault assault)
    {
        if (IsSuppressed(assault))
            SetBladesVisible(assault, false);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    public static float GetAirResistanceReduction(GameShip player)
    {
        int rank = GetRank(player);

        if (rank < 1)
            return 0f;

        return GetRankValue(
            AirResistanceReductionByRank,
            rank
        );
    }

    public static void AddNativeHandlingModifiers(
        List<Modifier> modifiers,
        int rank)
    {
        if (modifiers == null || rank < 1)
            return;

        int effectiveRank = Mathf.Clamp(rank, 1, 5);

        // Vanguard's native paths:
        //   Thruster Power -> Accelerate / Dodge / Boost
        //   Turn Speed     -> TurnSpeed
        //   Optimized Frame-> ControlMultiplier
        //
        // Constrictor intentionally omits Dodge because its requested handling
        // package is Acceleration, Boost, Turn Speed and Maneuverability.
        AddGlobalPercentageModifier(
            modifiers,
            Modifier.Type.Accelerate,
            effectiveRank * AccelerationBonusPerRank
        );

        AddGlobalPercentageModifier(
            modifiers,
            Modifier.Type.Boost,
            effectiveRank * BoostBonusPerRank
        );

        AddGlobalPercentageModifier(
            modifiers,
            Modifier.Type.TurnSpeed,
            effectiveRank * TurnSpeedBonusPerRank
        );

        AddGlobalPercentageModifier(
            modifiers,
            Modifier.Type.ControlMultiplier,
            effectiveRank * ManeuverabilityBonusPerRank
        );
    }

    private static void AddGlobalPercentageModifier(
        List<Modifier> modifiers,
        Modifier.Type type,
        float value)
    {
        Modifier modifier = Modifier.GetModifier(type);

        if (modifier == null)
            return;

        modifier.SetValue(value);
        modifier.global = true;
        modifiers.Add(modifier);
    }

    private static float GetGrowthSizeDamageMultiplier(GameShip player)
    {
        int sectionCount = LeviathanMod.Controller == null
            ? 0
            : LeviathanMod.Controller.GetActiveSectionCount(player);

        if (sectionCount <= 0)
        {
            sectionCount =
                LeviathanGrowth.GetBodySegmentCountForRank(1) + 2;
        }

        float headSectionValue = GetHeadSectionValue(player);
        float sectionValue = sectionCount * headSectionValue;

        return Mathf.Max(
            1f,
            1f +
                (sectionValue - BaselineSectionValue) *
                SectionValueDamageStep
        );
    }

    private static float GetHeadSectionValue(GameShip player)
    {
        if (player == null)
            return FrigateSectionValue;

        int shipClass = (int)player.GetShipClass();

        switch (shipClass)
        {
            case 4:
                return DestroyerSectionValue;
            case 5:
                return CruiserSectionValue;
            case 6:
                return BattleshipSectionValue;
            case 7:
            case 8:
                return DreadnoughtSectionValue;
            case 3:
            default:
                return FrigateSectionValue;
        }
    }

    private static float GetRankValue(float[] values, int rank)
    {
        int index = Mathf.Clamp(rank, 1, values.Length) - 1;
        return values[index];
    }

    private static int GetRank(GameShip player)
    {
        Pilot pilot = GameShip.GetPlayerSourcePilot(player);

        return pilot == null
            ? 0
            : Mathf.Clamp(
                pilot.GetUpgradeLevel(ConstrictorUpgrade),
                0,
                5
            );
    }

    private static int GetPredatorRank(GameShip player)
    {
        Pilot pilot = GameShip.GetPlayerSourcePilot(player);

        return pilot == null
            ? 0
            : pilot.GetUpgradeLevel(LeviathanMod.PredatorUpgrade);
    }

    public static bool ShouldSuppressNativeAssaultStart(Assault assault)
    {
        if (!IsSuppressed(assault))
            return false;

        // If Predator exists, the native StartAttack -> GameShip.Lunge path is
        // deliberately retained because Predator converts that lunge. Without
        // Predator, Constrictor owns the weapon and the native Assault lunge is
        // disabled along with the blade damage/visuals.
        GameShip player = WorldController.instance == null
            ? null
            : WorldController.instance.GetCurrentPlayerShip();

        return player != null && GetPredatorRank(player) < 1;
    }
}

// =============================================================================
// Patches
// =============================================================================

/// <summary>
/// Inject Constrictor's handling bonuses at the same layer used by native
/// Vanguard upgrades. Pilot.GetModifiers calls Upgrade.GetModifiers and
/// GameShip.RegenerateModifiers then merges these global modifiers normally.
/// </summary>
[HarmonyPatch]
public static class LeviathanConstrictorNativeHandlingPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Upgrade),
            "GetModifiers",
            new Type[]
            {
                typeof(int),
                typeof(int),
                typeof(Ship.Class)
            }
        );
    }

    public static void Postfix(
        Upgrade __instance,
        int __0,
        ref List<Modifier> __result)
    {
        if (__instance == null ||
            __instance.key != LeviathanMod.ConstrictorUpgrade ||
            __0 < 1)
        {
            return;
        }

        if (__result == null)
            __result = new List<Modifier>();

        LeviathanConstrictor.AddNativeHandlingModifiers(
            __result,
            __0
        );
    }
}

/// <summary>
/// Reduce only the extra velocity loss applied by Growth's Leviathan cruising
/// resistance. Capturing before/after velocity avoids duplicating Growth's
/// resistance formula and automatically preserves its threshold, curve and
/// Predator-lunge exception.
/// </summary>
[HarmonyPatch]
public static class LeviathanConstrictorAirResistancePatch
{
    private struct ResistanceState
    {
        public Rigidbody2D body;
        public Vector2 velocityBefore;
        public float reduction;
        public bool valid;
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

        __state.body = body;
        __state.velocityBefore = body.velocity;
        __state.reduction = Mathf.Clamp01(reduction);
        __state.valid = true;
    }

    public static void Postfix(ResistanceState __state)
    {
        if (!__state.valid || !__state.body)
            return;

        Vector2 velocityAfter = __state.body.velocity;

        // ApplyHighSpeedResistance only moves velocity toward zero. Refund the
        // requested fraction of whatever Growth actually removed this call.
        Vector2 removed =
            __state.velocityBefore - velocityAfter;

        __state.body.velocity =
            velocityAfter + removed * __state.reduction;
    }
}

/// <summary>
/// Keep Assault.UpdateAssault alive so Predator can reuse the native activation,
/// cooldown and StartAttack -> GameShip.Lunge lifecycle. Only the physical
/// blade/ram damage is suppressed here; Constrictor supplies passive contact
/// damage and Predator supplies active lunge damage.
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
/// Constrictor by itself disables the Assault's native lunge. If Predator is
/// ranked, StartAttack is allowed through because Predator converts that same
/// native lunge path into its Leviathan attack.
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
        // Native StartAttack may touch blade/trail presentation. Reassert the
        // Leviathan-hidden state after the native activation path has run.
        LeviathanConstrictor.EnsureHidden(__instance);
    }
}

