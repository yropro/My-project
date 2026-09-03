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
/// the Assault blade damage during the lunge with one head-contact hit per
/// target.
/// </summary>
public static class LeviathanPredatorRuntime
{
    // Damage relative to one aggregate native Assault lunge contact. Native
    // Assault divides its packet by blade count, so we multiply blade count
    // back out before applying these values.
    private const float PredatorDamageRank1 = 1.50f;
    private const float PredatorDamagePerAdditionalRank = 0.125f;

    // Each point of section-size value above the Growth-1 / all-Frigate
    // baseline of 5.0 adds 8%. At Growth 5 with seventeen max-size (1.5)
    // sections this is 2.64x; Predator 5 then reaches 5.28x reference damage.
    private const float BaselineSectionValue = 5.0f;
    private const float SectionValueDamageStep = 0.08f;

    // Flat native crit-chance bonus per Predator rank.
    private const float CritChancePerRank = 0.02f;

    private static readonly FieldInfo ParentShipField =
        AccessTools.Field(typeof(Equippable), "parentShip");

    private static readonly FieldInfo ObjectColliderField =
        AccessTools.Field(typeof(Damageable), "objectCollider");

    private static readonly FieldInfo LungeTimerField =
        AccessTools.Field(typeof(GameShip), "lungeTimer");

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

        float movementFactor = GetLungeMovementFactor(rank);

        // Keep native Assault.Duration unchanged. Shortening distance therefore
        // shortens speed by the same factor while keeping animation/state timing
        // perfectly aligned with vanilla Assault.StartAttack/EndAttack.
        distance *= movementFactor;

        activeAssault = assault;
        activePlayer = player;
        hitTargets.Clear();

        Debug.Log(
            "[Leviathan] Predator lunge armed. Rank = " +
            rank +
            ", distance/speed = " +
            movementFactor.ToString("0.00") +
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
            ScanHeadContacts();
    }

    public static void CancelForPlayer(GameShip player)
    {
        if (player != null && player == activePlayer)
            Cancel();
    }

    public static void Cancel()
    {
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
        switch (Mathf.Clamp(rank, 1, 5))
        {
            case 1:
                return 1.75f;
            case 2:
                return 1.60f;
            case 3:
                return 1.45f;
            case 4:
                return 1.35f;
            default:
                return 1.25f;
        }
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
            LeviathanMod.Controller.GetActiveSectionCount(player) < 5)
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

    private static float GetLungeMovementFactor(int rank)
    {
        switch (Mathf.Clamp(rank, 1, 5))
        {
            case 1:
                return 0.70f;
            case 2:
                return 0.75f;
            case 3:
                return 0.80f;
            case 4:
                return 0.85f;
            default:
                return 0.90f;
        }
    }

    private static float GetPredatorDamageMultiplier(int rank)
    {
        int effectiveRank = Mathf.Clamp(rank, 1, 5);
        return PredatorDamageRank1 +
            (effectiveRank - 1) * PredatorDamagePerAdditionalRank;
    }

    private static float GetPredatorCritBonus(int rank)
    {
        return Mathf.Clamp(rank, 1, 5) * CritChancePerRank;
    }

    private static void ScanHeadContacts()
    {
        if (activePlayer == null ||
            activeAssault == null ||
            ObjectColliderField == null ||
            PhysicsController.instance == null)
        {
            return;
        }

        Collider2D headCollider =
            ObjectColliderField.GetValue(activePlayer) as Collider2D;

        if (headCollider == null || !headCollider.enabled)
            return;

        Collider2D[] overlaps =
            PhysicsController.instance.OverlapCollider(headCollider);

        if (overlaps == null)
            return;

        for (int i = 0; i < overlaps.Length; i++)
        {
            Collider2D other = overlaps[i];

            if (other == null || other == headCollider)
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

            // A target is consumed the first time the head contacts it during
            // this lunge. Multiple colliders/contact frames cannot multi-hit it.
            hitTargets.Add(target);

            Vector2 point = other.ClosestPoint(activePlayer.transform.position);
            DealPredatorHit(target, point);
        }
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
        float sectionValue = LeviathanMod.Controller == null
            ? BaselineSectionValue
            : LeviathanMod.Controller.GetActiveSectionSizeValue(activePlayer);

        if (sectionValue <= 0f)
            sectionValue = BaselineSectionValue;

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
