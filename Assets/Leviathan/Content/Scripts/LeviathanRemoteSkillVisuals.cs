using HarmonyLib;
using StarVortex;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Remote-player presentation bridge for Leviathan Assault-source skills.
///
/// Star Vortex already replicates remote player equipment, Pilot upgrades and
/// activation edges through RemoteShipDriver. The owner-only Leviathan runtime
/// intentionally does not run for remote players, so this class applies only the
/// presentation/native-suppression pieces that remote replicas need.
///
/// Custom Constrictor/Predator damage remains owner-authoritative and is NOT
/// reproduced here.
/// </summary>
public static class LeviathanRemoteSkillVisuals
{
    private static readonly FieldInfo ParentShipField =
        AccessTools.Field(typeof(Equippable), "parentShip");

    private static readonly FieldInfo AssaultBladesField =
        AccessTools.Field(typeof(Assault), "blades");

    private static readonly HashSet<Assault> HiddenRemoteAssaults =
        new HashSet<Assault>();

    private static bool TryGetRemoteAssaultContext(
        Assault assault,
        out GameShip player,
        out int constrictorRank,
        out int predatorRank)
    {
        player = null;
        constrictorRank = 0;
        predatorRank = 0;

        if (assault == null || ParentShipField == null)
            return false;

        player = ParentShipField.GetValue(assault) as GameShip;

        if (player == null || !player.IsRemotePlayer())
            return false;

        Pilot pilot = GameShip.GetPlayerSourcePilot(player);

        if (pilot == null ||
            pilot.GetUpgradeLevel(LeviathanMod.GrowthUpgrade) < 1)
        {
            return false;
        }

        constrictorRank =
            pilot.GetUpgradeLevel(LeviathanMod.ConstrictorUpgrade);

        predatorRank =
            pilot.GetUpgradeLevel(LeviathanMod.PredatorUpgrade);

        return constrictorRank >= 1 || predatorRank >= 1;
    }

    public static bool ShouldSuppressRemoteBladeDamage(Assault assault)
    {
        GameShip player;
        int constrictorRank;
        int predatorRank;

        return TryGetRemoteAssaultContext(
            assault,
            out player,
            out constrictorRank,
            out predatorRank
        );
    }

    public static bool ShouldSuppressRemoteAssaultStart(Assault assault)
    {
        GameShip player;
        int constrictorRank;
        int predatorRank;

        if (!TryGetRemoteAssaultContext(
                assault,
                out player,
                out constrictorRank,
                out predatorRank))
        {
            return false;
        }

        // Constrictor by itself removes the native Assault lunge entirely.
        // Predator reuses that native StartAttack -> Lunge lifecycle, so allow
        // it through when Predator is ranked; LeviathanPredator then scales the
        // remote replica's lunge distance/duration to match the owner.
        return constrictorRank >= 1 && predatorRank < 1;
    }

    public static void RefreshRemoteAssaultVisual(Assault assault)
    {
        if (assault == null)
            return;

        GameShip player;
        int constrictorRank;
        int predatorRank;

        bool shouldHide = TryGetRemoteAssaultContext(
            assault,
            out player,
            out constrictorRank,
            out predatorRank
        );

        if (shouldHide)
        {
            SetBladesVisible(assault, false);
            HiddenRemoteAssaults.Add(assault);
            return;
        }

        if (HiddenRemoteAssaults.Remove(assault))
            SetBladesVisible(assault, true);
    }

    public static void Forget(Assault assault)
    {
        if (assault != null)
            HiddenRemoteAssaults.Remove(assault);
    }

    private static void SetBladesVisible(Assault assault, bool visible)
    {
        if (assault == null || AssaultBladesField == null)
            return;

        IEnumerable blades =
            AssaultBladesField.GetValue(assault) as IEnumerable;

        if (blades == null)
            return;

        foreach (object blade in blades)
        {
            if (blade == null)
                continue;

            Type bladeType = blade.GetType();

            GameObject obj =
                AccessTools.Field(bladeType, "obj")?.GetValue(blade)
                    as GameObject;

            if (obj)
                obj.SetActive(visible);

            SwishTrail swish =
                AccessTools.Field(bladeType, "swish")?.GetValue(blade)
                    as SwishTrail;

            if (swish)
                swish.gameObject.SetActive(visible);
        }
    }
}

// Remote replicas normally replay native Assault damage. Leviathan's Assault is
// only a stat/activation source, so suppress that native remote damage just as
// the owner-side Constrictor runtime does. Custom Leviathan damage is still sent
// by the owning player through NetCombat and is not duplicated here.
[HarmonyPatch(typeof(Assault), "DoDamageTick")]
public static class LeviathanRemoteAssaultDamagePatch
{
    public static bool Prefix(Assault __instance)
    {
        return !LeviathanRemoteSkillVisuals
            .ShouldSuppressRemoteBladeDamage(__instance);
    }
}

// Constrictor-only owners do not lunge when activating their Assault source.
// Mirror that on remote replicas. Predator is allowed through so its own lunge
// patch can apply the matching remote presentation.
[HarmonyPatch(typeof(Assault), "StartAttack")]
public static class LeviathanRemoteAssaultStartPatch
{
    public static bool Prefix(Assault __instance)
    {
        return !LeviathanRemoteSkillVisuals
            .ShouldSuppressRemoteAssaultStart(__instance);
    }
}

// Native Assault.LateUpdate rewrites blade pose/visibility every rendered frame.
// Reassert the hidden Leviathan-source presentation after native rendering runs.
[HarmonyPatch(typeof(Assault), "LateUpdate")]
public static class LeviathanRemoteAssaultVisualPatch
{
    public static void Postfix(Assault __instance)
    {
        LeviathanRemoteSkillVisuals.RefreshRemoteAssaultVisual(__instance);
    }
}

[HarmonyPatch(typeof(Assault), "Unequip")]
public static class LeviathanRemoteAssaultUnequipPatch
{
    public static void Prefix(Assault __instance)
    {
        LeviathanRemoteSkillVisuals.Forget(__instance);
    }
}
