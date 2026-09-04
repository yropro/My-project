using HarmonyLib;
using StarVortex;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using static StarVortex.Damageable;

/// <summary>
/// Predator converts the player's normal Assault activation into a heavier,
/// shorter Leviathan lunge. Movement/cooldown still use the native Assault and
/// GameShip systems; this class only scales those native values and replaces
/// the Assault blade damage during the lunge with one native-Ram-hitbox contact
/// hit per target.
/// </summary>
public static class LeviathanPredatorRuntime
{
    // =========================================================================
    // BALANCE TUNING
    // =========================================================================
    // Arrays are Rank 1 -> Rank 5. These are the main Predator feel/balance knobs.

    // Damage relative to one aggregate native Assault lunge contact, before the
    // Leviathan section-size multiplier is applied.
    private static readonly float[] DamageMultiplierByRank =
    {
        1.50f,  // Rank 1
        1.625f, // Rank 2
        1.75f,  // Rank 3
        1.875f, // Rank 4
        2.00f   // Rank 5
    };

    // Fraction of the native Assault lunge distance.
    private static readonly float[] LungeDistanceMultiplierByRank =
    {
        0.70f, // Rank 1
        0.75f, // Rank 2
        0.80f, // Rank 3
        0.85f, // Rank 4
        0.90f  // Rank 5
    };

    // Multiplier on native lunge duration. Larger = slower at the same distance.
    // 1.25 duration means 80% of the previous Predator movement speed.
    private static readonly float[] LungeDurationMultiplierByRank =
    {
        1.6f, // Rank 1
        1.5f, // Rank 2
        1.3f, // Rank 3
        1.3f, // Rank 4
        1.2f  // Rank 5
    };

    // Multiplier on the equipped Assault weapon's native cooldown.
    private static readonly float[] CooldownMultiplierByRank =
    {
        2.15f, // Rank 1
        2.00f, // Rank 2
        1.85f, // Rank 3
        1.70f, // Rank 4
        1.55f  // Rank 5
    };

    // Flat crit chance added to the equipped Assault weapon.
    private static readonly float[] CritChanceBonusByRank =
    {
        0.05f, // Rank 1
        0.08f, // Rank 2
        0.11f, // Rank 3
        0.14f, // Rank 4
        0.17f  // Rank 5
    };

    // Growth / ship-size damage scaling.
    // Every active section uses the MAIN SHIP's class value. Individual body
    // segment classes are intentionally ignored.
    private const float FrigateSectionValue = 1.00f;
    private const float DestroyerSectionValue = 1.20f;
    private const float CruiserSectionValue = 1.30f;
    private const float BattleshipSectionValue = 1.40f;
    private const float DreadnoughtSectionValue = 1.50f;

    // Growth rank 1 / Frigate remains the no-bonus baseline.
    private static readonly float BaselineSectionValue =
        (LeviathanGrowth.GetBodySegmentCountForRank(1) + 2) *
        FrigateSectionValue;

    private const float SectionValueDamageStep = 0.08f;

    // =========================================================================
    // NATIVE / MECHANICAL CONSTANTS
    // =========================================================================

    private static readonly FieldInfo ParentShipField =
        AccessTools.Field(typeof(Equippable), "parentShip");

    private static readonly FieldInfo LungeTimerField =
        AccessTools.Field(typeof(GameShip), "lungeTimer");

    private static readonly FieldInfo AttackTimerField =
        AccessTools.Field(typeof(Assault), "attackTimer");

    private static readonly FieldInfo BladesField =
        AccessTools.Field(typeof(Assault), "blades");

    // NetCombat.RouteDamage's first parameter is the game's internal
    // IDamageable interface, which cannot safely be named from a mod assembly.
    // Resolve the DamageData[] overload by shape, as Constrictor does.
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
                     m.GetParameters()[2].ParameterType == typeof(DamageData[])
            );

    private static Assault startingAssault;
    private static Assault activeAssault;
    private static GameShip activePlayer;

    // Predator contact detection uses the actual native Corrosive Ram blade
    // prefab/collider rather than the player's hull collider. The probe is
    // instantiated only for an active Predator lunge and all visuals are hidden.
    private static AssaultItemBase ramHitboxItemBase;
    private static Assault ramHitboxTemplate;
    private static GameObject ramHitboxObject;
    private static Collider2D ramHitboxCollider;
    private static Vector3 ramHitboxBaseScale;
    private static bool warnedNoRamHitbox;

    private static readonly HashSet<GameShip> hitTargets =
        new HashSet<GameShip>();

    public static bool TryBeginAssaultStart(Assault assault)
    {
        GameShip player;
        int rank;

        if (!TryGetPredatorContext(assault, out player, out rank))
            return false;

        startingAssault = assault;
        return true;
    }

    public static void EndAssaultStart(Assault assault)
    {
        if (startingAssault == assault)
            startingAssault = null;
    }

    public static bool PrepareNativeLunge(
        GameShip player,
        ref float distance,
        ref float duration)
    {
        Assault assault = startingAssault;

        GameShip assaultPlayer;
        int rank;

        if (assault == null ||
            player == null ||
            !TryGetPredatorContext(assault, out assaultPlayer, out rank) ||
            assaultPlayer != player)
        {
            return false;
        }

        // If some linked/secondary Assault attempts to start while the player is
        // already lunging, vanilla GameShip.Lunge will reject it. Do not let that
        // failed attempt replace the Predator attack that is already in flight.
        if (GetLungeTimer(player) > 0f)
            return false;

        float distanceMultiplier = GetLungeDistanceMultiplier(rank);
        float durationMultiplier = GetLungeDurationMultiplier(rank);

        // Distance and duration are independent balance knobs. Speed is therefore:
        // distanceMultiplier / durationMultiplier relative to native Assault.
        distance *= distanceMultiplier;
        duration *= durationMultiplier;

        activeAssault = assault;
        activePlayer = player;
        hitTargets.Clear();

        BuildRamHitboxProbe(player);

        Debug.Log(
            "[Leviathan] Predator lunge armed. Rank = " +
            rank +
            ", distance = " +
            distanceMultiplier.ToString("0.00") +
            "x, duration = " +
            durationMultiplier.ToString("0.00") +
            "x, speed = " +
            (distanceMultiplier / durationMultiplier).ToString("0.00") +
            "x."
        );

        return true;
    }

    public static void NativeLungeResult(bool wasPredatorLunge, bool succeeded)
    {
        if (wasPredatorLunge && !succeeded)
            Cancel();
    }

    public static bool ShouldSuppressNativeAssaultDamage(Assault assault)
    {
        // Suppress the native blade packet for the entire Predator attack, not
        // just while lungeTimer is positive. This prevents a final vanilla
        // Scythe/Ram tick on the frame the lunge expires.
        return assault != null && assault == activeAssault;
    }

    public static void EndPredatorAttack(Assault assault)
    {
        if (assault != null && assault == activeAssault)
            Cancel();
    }

    public static bool IsPredatorLunging(GameShip player)
    {
        return player != null &&
            player == activePlayer &&
            activeAssault != null &&
            GetLungeTimer(player) > 0f;
    }

    private static float GetLungeTimer(GameShip player)
    {
        if (player == null || LungeTimerField == null)
            return 0f;

        object value = LungeTimerField.GetValue(player);
        return value is float ? (float)value : 0f;
    }

    public static void FixedTick()
    {
        if (activePlayer == null || activeAssault == null)
            return;

        if (!IsCurrentPlayer(activePlayer))
        {
            Cancel();
            return;
        }

        if (IsPredatorLunging(activePlayer))
        {
            UpdateRamHitboxProbePose();
            ScanRamHitboxContacts();
        }
    }

    public static void CancelForPlayer(GameShip player)
    {
        if (player != null && player == activePlayer)
            Cancel();
    }

    public static void Cancel()
    {
        DestroyRamHitboxProbe();
        startingAssault = null;
        activeAssault = null;
        activePlayer = null;
        hitTargets.Clear();
    }

    public static bool TryGetPredatorRank(
        Assault assault,
        out GameShip player,
        out int rank)
    {
        return TryGetPredatorContext(assault, out player, out rank);
    }

    public static float GetCooldownMultiplier(int rank)
    {
        return GetRankValue(CooldownMultiplierByRank, rank);
    }

    private static bool TryGetPredatorContext(
        Assault assault,
        out GameShip player,
        out int rank)
    {
        player = null;
        rank = 0;

        if (assault == null ||
            ParentShipField == null ||
            LeviathanMod.Controller == null)
        {
            return false;
        }

        player = ParentShipField.GetValue(assault) as GameShip;

        if (player == null || !IsCurrentPlayer(player))
            return false;

        Pilot pilot = GameShip.GetPlayerSourcePilot(player);

        if (pilot == null ||
            pilot.GetUpgradeLevel(LeviathanMod.GrowthUpgrade) < 1 ||
            LeviathanMod.Controller.GetActiveSectionCount(player) <
                LeviathanGrowth.GetBodySegmentCountForRank(1) + 2)
        {
            return false;
        }

        rank = pilot.GetUpgradeLevel(LeviathanMod.PredatorUpgrade);
        return rank >= 1;
    }

    private static bool IsCurrentPlayer(GameShip player)
    {
        return player != null &&
            WorldController.instance != null &&
            WorldController.instance.GetCurrentPlayerShip() == player;
    }

    private static float GetLungeDistanceMultiplier(int rank)
    {
        return GetRankValue(LungeDistanceMultiplierByRank, rank);
    }

    private static float GetLungeDurationMultiplier(int rank)
    {
        return GetRankValue(LungeDurationMultiplierByRank, rank);
    }

    private static float GetPredatorDamageMultiplier(int rank)
    {
        return GetRankValue(DamageMultiplierByRank, rank);
    }

    private static float GetPredatorCritBonus(int rank)
    {
        return GetRankValue(CritChanceBonusByRank, rank);
    }

    private static float GetRankValue(float[] values, int rank)
    {
        int index = Mathf.Clamp(rank, 1, values.Length) - 1;
        return values[index];
    }

    private static void BuildRamHitboxProbe(GameShip player)
    {
        DestroyRamHitboxProbe();

        if (player == null)
            return;

        AssaultItemBase itemBase = FindRamHitboxItemBase();

        if (itemBase == null ||
            itemBase.assault == null ||
            itemBase.blade == null)
        {
            WarnNoRamHitbox(
                "Could not find a native Ram AssaultItemBase/blade prefab; " +
                "Predator contact damage is disabled for this lunge."
            );
            return;
        }

        GameObject probe = UnityEngine.Object.Instantiate(
            itemBase.blade,
            player.transform
        );

        if (probe == null)
        {
            WarnNoRamHitbox(
                "Could not instantiate the native Ram blade prefab; " +
                "Predator contact damage is disabled for this lunge."
            );
            return;
        }

        Collider2D collider =
            probe.GetComponentInChildren<Collider2D>(true);

        if (collider == null)
        {
            UnityEngine.Object.Destroy(probe);

            WarnNoRamHitbox(
                "Native Ram blade prefab had no Collider2D; " +
                "Predator contact damage is disabled for this lunge."
            );
            return;
        }

        probe.name = "Leviathan Predator Ram Hitbox";

        ramHitboxItemBase = itemBase;
        ramHitboxTemplate = itemBase.assault;
        ramHitboxObject = probe;
        ramHitboxCollider = collider;
        ramHitboxBaseScale = probe.transform.localScale;

        // Keep the exact native Ram object/collider/layer, but make the probe
        // completely invisible. Collider2D itself is not a Renderer, so it
        // remains available to PhysicsController.OverlapCollider.
        Renderer[] renderers =
            probe.GetComponentsInChildren<Renderer>(true);

        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] != null)
                renderers[i].enabled = false;
        }

        SwishTrail[] swishes =
            probe.GetComponentsInChildren<SwishTrail>(true);

        for (int i = 0; i < swishes.Length; i++)
        {
            if (swishes[i] != null)
                swishes[i].enabled = false;
        }

        ramHitboxCollider.enabled = true;
        UpdateRamHitboxProbePose();

        Debug.Log(
            "[Leviathan] Predator using native Ram collider from '" +
            itemBase.name + "'."
        );
    }

    private static AssaultItemBase FindRamHitboxItemBase()
    {
        if (ramHitboxItemBase != null &&
            ramHitboxItemBase.assault != null &&
            ramHitboxItemBase.blade != null)
        {
            return ramHitboxItemBase;
        }

        AssaultItemBase[] itemBases =
            Resources.FindObjectsOfTypeAll<AssaultItemBase>();

        AssaultItemBase firstRam = null;

        for (int i = 0; i < itemBases.Length; i++)
        {
            AssaultItemBase itemBase = itemBases[i];

            if (itemBase == null ||
                itemBase.assault == null ||
                itemBase.blade == null ||
                itemBase.assault.variant != Assault.Variant.Ram)
            {
                continue;
            }

            if (firstRam == null)
                firstRam = itemBase;

            // Corrosive Ram is the exact native geometry requested for
            // Predator. If assets are renamed/localized this still selects by
            // the Assault data rather than display text.
            if (itemBase.assault.damageType == DamageType.Corrosive)
            {
                ramHitboxItemBase = itemBase;
                return ramHitboxItemBase;
            }
        }

        // There is currently one native Ram geometry. This fallback keeps the
        // system working if the Corrosive damage assignment changes in a later
        // game build while still using a genuine Ram blade collider.
        ramHitboxItemBase = firstRam;
        return ramHitboxItemBase;
    }

    private static void UpdateRamHitboxProbePose()
    {
        if (ramHitboxObject == null ||
            ramHitboxCollider == null ||
            ramHitboxTemplate == null ||
            activePlayer == null)
        {
            return;
        }

        float attackProgress = 0f;

        if (activeAssault != null &&
            AttackTimerField != null &&
            activeAssault.Duration > 0.0001f)
        {
            object timerValue = AttackTimerField.GetValue(activeAssault);

            if (timerValue is float)
            {
                attackProgress = Mathf.Clamp01(
                    (float)timerValue / activeAssault.Duration
                );
            }
        }

        // This is Assault.RamPunch(t) verbatim:
        // 0 -> 1 over the first 40% of the attack, then 1 -> 0 over 60%,
        // using the native smoothstep curve x*x*(3 - 2*x).
        float punch = NativeRamPunch(attackProgress);

        // Native Assault.GetBladeRadius(): shield world radius + mountOffset.
        float bladeRadius =
            activePlayer.GetShieldWorldRadius() + ramHitboxTemplate.mountOffset;

        // Native RamForwardFactor is 1.0 at the base Ram duplicate rank. We are
        // borrowing Corrosive Ram's physical hitbox, not its legendary duplicate
        // behavior, so use the normal one-Ram position.
        float forward =
            bladeRadius + ramHitboxTemplate.mountOffset * punch;

        Vector3 scale =
            ramHitboxBaseScale * (1f + 0.5f * punch);

        Transform transform = ramHitboxObject.transform;
        transform.localPosition = new Vector3(forward, 0f, 0f);
        transform.localRotation = Quaternion.identity;
        transform.localScale = scale;
    }

    private static float NativeRamPunch(float t)
    {
        if (t <= 0f || t >= 1f)
            return 0f;

        float x = t >= 0.4f
            ? (1f - t) / 0.6f
            : t / 0.4f;

        x = Mathf.Clamp01(x);
        return x * x * (3f - 2f * x);
    }

    private static void ScanRamHitboxContacts()
    {
        if (activePlayer == null ||
            activeAssault == null ||
            ramHitboxCollider == null ||
            !ramHitboxCollider.enabled ||
            PhysicsController.instance == null)
        {
            return;
        }

        Collider2D[] overlaps =
            PhysicsController.instance.OverlapCollider(ramHitboxCollider);

        if (overlaps == null)
            return;

        for (int i = 0; i < overlaps.Length; i++)
        {
            Collider2D other = overlaps[i];

            // PhysicsController uses a shared null-terminated overlap buffer.
            if (other == null)
                break;

            if (other == ramHitboxCollider)
                continue;

            GameShip target = other.GetComponentInParent<GameShip>();

            if (target == null ||
                target == activePlayer ||
                hitTargets.Contains(target) ||
                (LeviathanMod.Controller != null &&
                    LeviathanMod.Controller.IsLeviathanSegment(target)) ||
                !target.CanBeDamagedBy(activePlayer, false) ||
                target.IsDodging())
            {
                continue;
            }

            // Same one-hit-per-target rule as before; only the detection volume
            // changed from the broad player hull collider to native Ram geometry.
            hitTargets.Add(target);

            Vector2 point = other.ClosestPoint(
                ramHitboxCollider.bounds.center
            );

            DealPredatorHit(target, point);
        }
    }

    private static void DestroyRamHitboxProbe()
    {
        ramHitboxCollider = null;
        ramHitboxTemplate = null;

        if (ramHitboxObject != null)
            UnityEngine.Object.Destroy(ramHitboxObject);

        ramHitboxObject = null;
        ramHitboxBaseScale = Vector3.one;
    }

    private static void WarnNoRamHitbox(string message)
    {
        if (warnedNoRamHitbox)
            return;

        warnedNoRamHitbox = true;
        Debug.LogError("[Leviathan] " + message);
    }

    private static void DealPredatorHit(GameShip target, Vector2 hitPoint)
    {
        if (target == null ||
            activePlayer == null ||
            activeAssault == null ||
            RouteDamageMethod == null)
        {
            return;
        }

        Pilot pilot = GameShip.GetPlayerSourcePilot(activePlayer);

        if (pilot == null)
            return;

        int rank = pilot.GetUpgradeLevel(LeviathanMod.PredatorUpgrade);

        if (rank < 1)
            return;

        float critChance = Mathf.Clamp01(
            activeAssault.GetCritChance() + GetPredatorCritBonus(rank)
        );

        bool crit = Modifier.CritRoll(critChance, target);
        DamageData[] nativePacket = activeAssault.GetDamageData(crit);

        if (nativePacket == null || nativePacket.Length == 0)
            return;

        DamageData[] predatorPacket = new DamageData[nativePacket.Length];
        Array.Copy(nativePacket, predatorPacket, nativePacket.Length);

        int bladeCount = GetNativeBladeCount(activeAssault);

        int sectionCount = LeviathanMod.Controller == null
            ? 0
            : LeviathanMod.Controller.GetActiveSectionCount(activePlayer);

        if (sectionCount <= 0)
            sectionCount =
                LeviathanGrowth.GetBodySegmentCountForRank(1) + 2;

        float headSectionValue = GetHeadSectionValue(activePlayer);
        float sectionValue = sectionCount * headSectionValue;

        float sizeMultiplier = Mathf.Max(
            1f,
            1f +
                (sectionValue - BaselineSectionValue) *
                SectionValueDamageStep
        );

        float damageMultiplier =
            GetPredatorDamageMultiplier(rank) * sizeMultiplier;

        // GetDamageData() is the native per-blade Assault lunge packet while
        // attacking. Recombine its blade split first, then apply Predator.
        float packetScale = bladeCount * damageMultiplier;

        for (int i = 0; i < predatorPacket.Length; i++)
        {
            DamageData datum = predatorPacket[i];
            datum.damage *= packetScale;
            datum.dps *= packetScale;
            predatorPacket[i] = datum;
        }

        Vector2 damageDirection =
            (target.transform.position - activePlayer.transform.position);

        if (damageDirection.sqrMagnitude > 0.0001f)
            damageDirection.Normalize();
        else
            damageDirection = activePlayer.transform.right;

        target.SetLastDamageDirection(damageDirection);
        target.lastDamagedByWeaponName = activeAssault.GetName(false, false);
        target.lastDamagedByShipName = activePlayer.GetName();
        target.lastDamagedByFaction = activePlayer.faction;

        float statusEffectChance = activeAssault.GetStatusEffectChance();
        bool bypassDamageLimit =
            activeAssault.HasCustomizer((Customizer.Type)20);

        RouteDamageMethod.Invoke(
            null,
            new object[]
            {
                target,
                activeAssault.damageType,
                predatorPacket,
                statusEffectChance,
                crit,
                hitPoint,
                activePlayer,
                bypassDamageLimit,  // native BypassDamageLimit customizer
                0f,                 // knockbackPower; tune separately later
                activeAssault,      // native slotSource / attribution
                0f,                 // impaleDps
                0f,                 // impaleDuration
                false,              // forceAttackerLocal
                0f                  // impaleRotation
            }
        );

        Debug.Log(
            "[Leviathan] Predator hit " +
            target.GetName() +
            ". Rank = " +
            rank +
            ", sections = " +
            sectionCount.ToString() +
            ", head size = " +
            headSectionValue.ToString("0.00") +
            ", section value = " +
            sectionValue.ToString("0.00") +
            ", size scale = " +
            sizeMultiplier.ToString("0.00") +
            ", Predator scale = " +
            GetPredatorDamageMultiplier(rank).ToString("0.00") +
            ", crit = " +
            crit.ToString() +
            "."
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

    private static int GetNativeBladeCount(Assault assault)
    {
        if (assault != null && BladesField != null)
        {
            IList blades = BladesField.GetValue(assault) as IList;

            if (blades != null && blades.Count > 0)
                return blades.Count;
        }

        // Native BuildBlades creates one blade for Ram and two for Scythe.
        return assault != null && assault.variant == Assault.Variant.Scythe
            ? 2
            : 1;
    }
}

// -----------------------------------------------------------------------------
// Native Assault / Lunge hooks
// -----------------------------------------------------------------------------

[HarmonyPatch]
public static class LeviathanPredatorAssaultStartPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(Assault), "StartAttack");
    }

    public static void Prefix(Assault __instance, out bool __state)
    {
        __state = LeviathanPredatorRuntime.TryBeginAssaultStart(__instance);
    }

    public static void Postfix(Assault __instance, bool __state)
    {
        if (__state)
            LeviathanPredatorRuntime.EndAssaultStart(__instance);
    }
}

[HarmonyPatch]
public static class LeviathanPredatorAssaultEndPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Assault),
            "EndAttack",
            new Type[] { typeof(bool) }
        );
    }

    public static void Postfix(Assault __instance)
    {
        LeviathanPredatorRuntime.EndPredatorAttack(__instance);
    }
}

[HarmonyPatch]
public static class LeviathanPredatorLungePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(GameShip),
            "Lunge",
            new Type[]
            {
                typeof(Vector2),
                typeof(float),
                typeof(float)
            }
        );
    }

    public static void Prefix(
        GameShip __instance,
        ref float __1,
        ref float __2,
        out bool __state)
    {
        __state = LeviathanPredatorRuntime.PrepareNativeLunge(
            __instance,
            ref __1,
            ref __2
        );
    }

    public static void Postfix(bool __state, bool __result)
    {
        LeviathanPredatorRuntime.NativeLungeResult(__state, __result);
    }
}

[HarmonyPatch]
public static class LeviathanPredatorSuppressBladeDamagePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(Assault), "DoDamageTick");
    }

    public static bool Prefix(Assault __instance)
    {
        return !LeviathanPredatorRuntime.ShouldSuppressNativeAssaultDamage(
            __instance
        );
    }
}

[HarmonyPatch]
public static class LeviathanPredatorCooldownPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Activatable),
            "SetCooldown",
            new Type[] { typeof(float?) }
        );
    }

    public static void Prefix(
        Activatable __instance,
        ref float? __0)
    {
        // Native Assault.EndAttack calls SetCooldown(null). Preserve any
        // explicit override supplied by some other game system.
        if (__0.HasValue)
            return;

        Assault assault = __instance as Assault;

        GameShip player;
        int rank;

        if (assault == null ||
            !LeviathanPredatorRuntime.TryGetPredatorRank(
                assault,
                out player,
                out rank))
        {
            return;
        }

        __0 = assault.Cooldown *
            LeviathanPredatorRuntime.GetCooldownMultiplier(rank);
    }
}
