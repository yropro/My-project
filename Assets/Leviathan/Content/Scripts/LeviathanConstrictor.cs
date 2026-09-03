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
// While an Assault weapon (ram / scythe / blades) is equipped, its own passive
// contact damage and lunge are suppressed and its blades are hidden. Instead,
// every Leviathan body segment touching an enemy deals contact damage derived
// from that exact weapon.
//
// Baseline anchor: two frigate-scale segments touching a target are equivalent
// to two scythe blades touching it in the weapon's non-lunge state. Passive
// scythe contact is 1/4 of a lunge tick (Assault.attackMultiplier == 4).
//
// Per target, contacting segments are ranked and decayed:
//     hit 1, 2 -> 1.0
//     hit 3    -> 0.6
//     hit 4    -> 0.36 ...
// Series converges to 3.5 segment-units, so total output is hard-capped at
// 1.75x vanilla scythe passive before the rank scalar.
//
// Rank scales damage (100% -> 200% across five ranks) and the status proc
// fraction (0.20 -> 0.40). Crit modifier is never scaled: expected crit damage
// is base * (1 + c*m), so scaling base, c and m together would decay the crit
// contribution cubically while base decays linearly.
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

    // ---- Tuning -------------------------------------------------------------

    // Contacts that equal "one weapon's worth" of contact damage. Frigate-scale.
    private const float BaselineContacts = 2f;

    // Every contact past the second contributes this fraction of the previous.
    private const float ContactDecay = 0.6f;

    // Contacts beyond this add < 1% and are skipped. 0.6^10 is ~0.006.
    private const int MaxRankedContacts = 12;

    // Damage scalar: rank 1 = 1.00, rank 5 = 2.00.
    private const float DamageScalarBase = 1.00f;
    private const float DamageScalarPerRank = 0.25f;

    // Status proc fraction, replacing Assault's flat 0.2 passive damper.
    // Rank 1 = 0.20, rank 5 = 0.40.
    private const float ProcFractionBase = 0.20f;
    private const float ProcFractionPerRank = 0.05f;

    // Assault's own per-tick fraction of listed Damage. Not a tuning knob --
    // this mirrors GetDamageData and must stay at 0.2 for parity.
    private const float EngineTickFraction = 0.2f;

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

    private static readonly Dictionary<GameShip, Collider2D> ColliderCache =
        new Dictionary<GameShip, Collider2D>();

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

        int rank = GetRank(player);

        if (rank < 1)
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

        float tickRate = player.ApplyModifier(
            Modifier.Type.BeamTickRate,
            Item.Category.None,
            0.2f,
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
    }

    // =========================================================================
    // Contact gathering
    // =========================================================================

    private static void GatherContacts(GameShip player)
    {
        Contacts.Clear();
        ContactTargets.Clear();

        List<Squadron.SquadronShip> ships = player.squadron.ships;

        if (ships == null)
            return;

        // Index 0 is the player head. Body segments and tail follow in chain
        // order, which is what gives the decay a stable head-first ranking.
        for (int i = 1; i < ships.Count; i++)
        {
            GameShip segment = ships[i].ship;

            if (segment == null || !segment.gameObject.activeInHierarchy)
                continue;

            Collider2D collider = GetSegmentCollider(segment);

            if (collider == null)
                continue;

            // PhysicsController returns a single shared buffer that the next
            // overlap call overwrites. Every hit must be consumed into
            // Contacts before the loop queries the following segment.
            Collider2D[] hits = PhysicsController.instance.OverlapCollider(collider);

            for (int h = 0; h < hits.Length; h++)
            {
                Collider2D hit = hits[h];

                if (!hit)
                    break;                      // null terminator, not a guard

                GameObject obj = hit.gameObject.CompareTag("Shield")
                    ? hit.transform.parent.gameObject
                    : hit.gameObject;

                if (obj.CompareTag("Container") || obj.CompareTag("Projectile"))
                    continue;

                GameShip target;

                if (!GameShip.TryGetShip(obj, out target) || target == null)
                    continue;

                // Faction and squadron checks inside CanBeDamagedBy already
                // reject the head and sibling segments.
                if (!target.CanBeDamagedBy(segment, false))
                    continue;

                if (target.IsDodging())
                    continue;

                List<GameShip> list;

                if (!Contacts.TryGetValue(target, out list))
                {
                    list = new List<GameShip>();
                    Contacts[target] = list;
                    ContactTargets.Add(target);
                }

                list.Add(segment);
            }
        }
    }

    private static Collider2D GetSegmentCollider(GameShip segment)
    {
        Collider2D cached;

        if (ColliderCache.TryGetValue(segment, out cached) && cached)
            return cached;

        Collider2D found = null;
        Collider2D[] candidates = segment.GetComponentsInChildren<Collider2D>(false);

        for (int i = 0; i < candidates.Length; i++)
        {
            // Skip the shield bubble; hull contact is what should constrict.
            if (candidates[i].gameObject.CompareTag("Shield"))
                continue;

            found = candidates[i];
            break;
        }

        ColliderCache[segment] = found;
        return found;
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
        float damageScalar =
            DamageScalarBase + DamageScalarPerRank * (rank - 1);

        float procFraction =
            ProcFractionBase + ProcFractionPerRank * (rank - 1);

        float baseCritChance = src.GetCritChance();
        float baseCritModifier = src.GetCritModifier();      // never scaled
        float baseStatusChance = src.StatusEffectChance;     // undamped; we damp
        float dps = src.CalculateDPS(Activatable.Modified.Global);
        float knockback = src.ApplyModifierToPercentage(Modifier.Type.Knockback, 0f, true);
        string weaponName = src.GetName(false, false);

        for (int t = 0; t < ContactTargets.Count; t++)
        {
            GameShip target = ContactTargets[t];

            if (target == null)
                continue;

            List<GameShip> segmentsHere = Contacts[target];
            int ranked = Mathf.Min(segmentsHere.Count, MaxRankedContacts);

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

                // Hits 1 and 2 are full; each subsequent hit is 0.6 of the last.
                float weight = i < 2
                    ? 1f
                    : Mathf.Pow(ContactDecay, i - 1);

                bool crit = Modifier.CritRoll(baseCritChance * weight, target);

                float mul = crit ? (1f + baseCritModifier) : 1f;
                mul *= EngineTickFraction;
                mul /= BaselineContacts;
                mul *= weight;
                mul *= damageScalar;

                float damage = src.Damage * mul;
                float status = baseStatusChance * procFraction * weight;

                BuildDamageData(src, damage, dps);

                target.SetLastDamageDirection(
                    ((Vector2)(target.transform.position - segment.transform.position)).normalized
                );

                target.lastDamagedByWeaponName = weaponName;
                target.lastDamagedByShipName = player.GetName();
                target.lastDamagedByFaction = player.faction;

                RouteDamage(
                    target,
                    src.damageType,
                    DamageBuffer,
                    status,
                    crit,
                    target.transform.position,
                    player,
                    src.HasCustomizer(Customizer.Type.BypassDamageLimit),
                    knockback * tickRate,
                    src
                );
            }
        }
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

    private static void RouteDamage(
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

            return;
        }

        // NetCombat.RouteDamage is internal. It resolves authority by target
        // ownership: the attacker computes crit and damage locally and the
        // resulting values are sent, so nothing here needs to agree across
        // machines. A false return means "deferred to the network", NOT
        // "failed" -- never retry on it.
        RouteDamageMethod.Invoke(
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

    // =========================================================================
    // Helpers
    // =========================================================================

    private static int GetRank(GameShip player)
    {
        Pilot pilot = GameShip.GetPlayerSourcePilot(player);

        return pilot == null
            ? 0
            : pilot.GetUpgradeLevel(ConstrictorUpgrade);
    }
}

// =============================================================================
// Patches
// =============================================================================

/// <summary>
/// Assault.UpdateAssault gates both StartAttack (the lunge) and DoDamageTick
/// (passive blade contact) behind one visibility/slot check. Skipping it kills
/// both while leaving base.FixedUpdate and UpdateDrift to keep the Activatable
/// state coherent. Assault.active is only the held-button flag, so nothing
/// latches when the attack never starts.
/// </summary>
[HarmonyPatch]
public static class LeviathanAssaultSuppressPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(Assault), "UpdateAssault");
    }

    public static bool Prefix(Assault __instance)
    {
        return !LeviathanConstrictor.IsSuppressed(__instance);
    }
}