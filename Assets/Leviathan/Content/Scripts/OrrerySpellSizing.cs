using HarmonyLib;
using StarVortex;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Authoritative V0 spell-geometry tuning.
///
/// Gameplay distances are authored in meters and converted only at the native
/// Star Vortex boundary. Visual-only multipliers are explicitly named so art
/// correction cannot silently change hit geometry.
/// </summary>
public static class OrrerySpellSizing
{
    public static class Tuning
    {
        // FF: native ExplosiveProjectile uses this same radius for physics and
        // ExplosiveArea visual scale, so gameplay and explosion presentation stay
        // coupled by default. Projectile scale is presentation-only.
        public const float FireballExplosionRadiusMeters = 40f;
        public const float FireballProjectileVisualScaleMultiplier = 1f;

        // II: mechanical cone range/angle remain OrrerySpellRuntime tuning. These
        // values only make the presentation burst derive from those dimensions.
        public const float CryoVisualRangeMultiplier = 1f;
        public const float CryoVisualAngleMultiplier = 1f;
        public const float CryoVisualProjectileScaleMultiplier = 1f;

        // LL: defaults intentionally mirror the native Tesla Coil asset but are
        // expressed as Orrery-authored values. Beam length already derives from
        // MaxRange natively, so there is no separate visual-length lie.
        public const float TeslaRangeMeters = 195f;
        public const int TeslaChainCount = 1;
        public const float TeslaChainRangeMeters = 97.5f;
        public const float TeslaChainDamageMultiplier = 0.50f;
        public const float TeslaBeamVisualWidthMultiplier = 1f;
    }

    private static readonly FieldInfo BeamMaxWidthField =
        AccessTools.Field(typeof(Beam), "maxWidth");
    private static readonly FieldInfo BeamMaxEndWidthField =
        AccessTools.Field(typeof(Beam), "maxEndWidth");
    private static readonly FieldInfo BeamAdditionalMaxWidthField =
        AccessTools.Field(typeof(Beam), "additionalMaxWidth");

    public static bool IsHiddenOrreryTesla(BeamWeapon beam)
    {
        return beam != null && beam.parentShip != null &&
            OrreryRuntime.IsActive(beam.parentShip) &&
            beam.damageType == Damageable.DamageType.Electric &&
            !OrreryWeaponSuppression.ShouldSuppress(beam);
    }

    public static bool IsHiddenOrreryCryo(Launcher launcher)
    {
        ChargingLauncher cryo = launcher as ChargingLauncher;
        return cryo != null && cryo.parentShip != null &&
            OrreryRuntime.IsActive(cryo.parentShip) &&
            cryo.damageType == Damageable.DamageType.Cold &&
            !OrreryWeaponSuppression.ShouldSuppress(cryo) &&
            Mathf.Approximately(cryo.BaseDamage, 0f) &&
            Mathf.Approximately(cryo.BaseStatusEffectChance, 0f);
    }

    public static bool IsHiddenOrreryFireball(Launcher launcher)
    {
        return launcher != null && launcher.parentShip != null &&
            OrreryRuntime.IsActive(launcher.parentShip) &&
            launcher.damageType == Damageable.DamageType.Thermal &&
            !OrreryWeaponSuppression.ShouldSuppress(launcher) &&
            launcher.BaseExplosiveRadius > 0f;
    }

    public static void ApplyLauncherGeometry(Launcher launcher)
    {
        if (IsHiddenOrreryFireball(launcher))
        {
            launcher.BaseExplosiveRadius = OrreryUnits.MetersToWorld(
                Tuning.FireballExplosionRadiusMeters);
            return;
        }

        if (!IsHiddenOrreryCryo(launcher))
            return;

        ChargingLauncher cryo = (ChargingLauncher)launcher;
        cryo.shotAngle = Mathf.RoundToInt(
            Mathf.Clamp(
                OrrerySpellRuntime.Tuning.CryoConeAngleDegrees *
                    Tuning.CryoVisualAngleMultiplier,
                0f,
                360f));

        float desiredRangeWorld = OrreryUnits.MetersToWorld(
            OrrerySpellRuntime.Tuning.CryoConeRangeMeters *
                Mathf.Max(0f, Tuning.CryoVisualRangeMultiplier));
        float velocity = Mathf.Max(0.01f, cryo.Velocity);
        cryo.BaseAutoDestroyTime = desiredRangeWorld / velocity;
    }

    public static void ApplyTeslaGeometry(BeamWeapon beam)
    {
        if (!IsHiddenOrreryTesla(beam))
            return;

        float rangeWorld = OrreryUnits.MetersToWorld(Tuning.TeslaRangeMeters);
        beam.BaseMaxRange = rangeWorld;
        beam.BaseChainTargets = Mathf.Max(0, Tuning.TeslaChainCount);
        beam.BaseChainDamage = Mathf.Max(0f, Tuning.TeslaChainDamageMultiplier);
        beam.BaseChainRange = rangeWorld > 0.0001f
            ? OrreryUnits.MetersToWorld(Tuning.TeslaChainRangeMeters) / rangeWorld
            : 0f;
    }

    public static void ApplyTeslaBeamWidth(Beam beam, BeamWeapon parentBeamWeapon)
    {
        if (beam == null || !IsHiddenOrreryTesla(parentBeamWeapon))
            return;

        float multiplier = Mathf.Max(0f, Tuning.TeslaBeamVisualWidthMultiplier);
        ScaleField(BeamMaxWidthField, beam, multiplier);
        ScaleField(BeamMaxEndWidthField, beam, multiplier);
        ScaleField(BeamAdditionalMaxWidthField, beam, multiplier);
    }

    private static void ScaleField(
        FieldInfo field,
        Beam beam,
        float multiplier)
    {
        if (field == null || beam == null)
            return;

        object value = field.GetValue(beam);
        if (value is float)
            field.SetValue(beam, (float)value * multiplier);
    }
}

/// <summary>
/// Launcher.ShootProjectile snapshots spread/range before Projectile.Init, so
/// apply FF/II geometry immediately before native spawning.
/// </summary>
[HarmonyPatch(typeof(Launcher), "ShootProjectile")]
public static class OrrerySpellSizingLauncherPatch
{
    public static void Prefix(Launcher __instance)
    {
        OrrerySpellSizing.ApplyLauncherGeometry(__instance);
    }
}

/// <summary>
/// BeamWeapon range/chain values are read dynamically by Beam.GetMaxRange and
/// chain traversal. Refreshing the hidden Tesla adapter each fixed step keeps the
/// authored Orrery geometry authoritative without rebuilding beam objects.
/// </summary>
[HarmonyPatch(typeof(BeamWeapon), "FixedUpdate")]
public static class OrrerySpellSizingTeslaRuntimePatch
{
    public static void Prefix(BeamWeapon __instance)
    {
        OrrerySpellSizing.ApplyTeslaGeometry(__instance);
    }
}

/// <summary>
/// Native beam line length already follows MaxRange. Width is presentation-only
/// in the native Tesla implementation, so scale only the cached renderer maxima.
/// </summary>
[HarmonyPatch]
public static class OrrerySpellSizingBeamInitPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Beam),
            "Init",
            new System.Type[]
            {
                typeof(BeamWeapon),
                typeof(GameShip),
                typeof(bool),
                typeof(float),
                typeof(int),
                typeof(float),
                typeof(bool),
                typeof(bool),
                typeof(bool),
                typeof(bool),
                typeof(bool),
                typeof(GameObject)
            });
    }

    public static void Postfix(Beam __instance, BeamWeapon parentBeamWeapon)
    {
        OrrerySpellSizing.ApplyTeslaBeamWidth(__instance, parentBeamWeapon);
    }
}

/// <summary>
/// Projectile visual scale is deliberately separate from mechanics. II shards
/// cannot deal damage or become targeting decoys through the presentation-safety
/// boundary, and FF explosion radius remains owned by the launcher/AoE contract.
/// </summary>
[HarmonyPatch(typeof(Projectile), "Init")]
public static class OrrerySpellSizingProjectileVisualPatch
{
    public static void Postfix(Projectile __instance, Launcher parentLauncher)
    {
        if (__instance == null || parentLauncher == null)
            return;

        if (OrrerySpellPresentationSafety.IsPresentationOnly(__instance))
        {
            __instance.transform.localScale *= Mathf.Max(
                0f,
                OrrerySpellSizing.Tuning.CryoVisualProjectileScaleMultiplier);
            return;
        }

        GameShip originalOwner;
        if (OrreryFireballLifecycleSafety.TryGetOriginalOwner(
                __instance,
                out originalOwner))
        {
            __instance.transform.localScale *= Mathf.Max(
                0f,
                OrrerySpellSizing.Tuning.FireballProjectileVisualScaleMultiplier);
        }
    }
}
