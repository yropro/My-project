using HarmonyLib;
using StarVortex;
using System.Collections.Generic;
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
        // coupled by default. Projectile scale is presentation-only and is applied
        // by OrreryFireballLifecycleSafety, which already owns pooled-scale restore.
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

    private struct BeamWidthBaseline
    {
        public float Main;
        public float End;
        public float Additional;
    }

    private static readonly FieldInfo BeamMaxWidthField =
        AccessTools.Field(typeof(Beam), "maxWidth");
    private static readonly FieldInfo BeamMaxEndWidthField =
        AccessTools.Field(typeof(Beam), "maxEndWidth");
    private static readonly FieldInfo BeamAdditionalMaxWidthField =
        AccessTools.Field(typeof(Beam), "additionalMaxWidth");

    // Cryo presentation projectiles are pooled. Cache only while live and restore
    // before PoolDestroy so repeated casts never compound transform scale.
    private static readonly Dictionary<Projectile, Vector3> cryoBaseScales =
        new Dictionary<Projectile, Vector3>(32);
    private static readonly Dictionary<Beam, BeamWidthBaseline> beamBaseWidths =
        new Dictionary<Beam, BeamWidthBaseline>(8);

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

        BeamWidthBaseline baseline;
        if (!beamBaseWidths.TryGetValue(beam, out baseline))
        {
            baseline.Main = GetFloatField(BeamMaxWidthField, beam);
            baseline.End = GetFloatField(BeamMaxEndWidthField, beam);
            baseline.Additional = GetFloatField(BeamAdditionalMaxWidthField, beam);
            beamBaseWidths[beam] = baseline;
        }

        float multiplier = Mathf.Max(0f, Tuning.TeslaBeamVisualWidthMultiplier);
        SetFloatField(BeamMaxWidthField, beam, baseline.Main * multiplier);
        SetFloatField(BeamMaxEndWidthField, beam, baseline.End * multiplier);
        SetFloatField(
            BeamAdditionalMaxWidthField,
            beam,
            baseline.Additional * multiplier);
    }

    public static void ApplyCryoProjectileVisualScale(Projectile projectile)
    {
        if (projectile == null || projectile.transform == null ||
            !OrrerySpellPresentationSafety.IsPresentationOnly(projectile))
        {
            return;
        }

        Vector3 baseScale;
        if (!cryoBaseScales.TryGetValue(projectile, out baseScale))
        {
            baseScale = projectile.transform.localScale;
            cryoBaseScales[projectile] = baseScale;
        }
        else
        {
            projectile.transform.localScale = baseScale;
        }

        projectile.transform.localScale = baseScale * Mathf.Max(
            0f,
            Tuning.CryoVisualProjectileScaleMultiplier);
    }

    public static void RestoreCryoProjectileVisualScale(Projectile projectile)
    {
        if (projectile == null)
            return;

        Vector3 baseScale;
        if (cryoBaseScales.TryGetValue(projectile, out baseScale))
        {
            if (projectile.transform != null)
                projectile.transform.localScale = baseScale;
            cryoBaseScales.Remove(projectile);
        }
    }

    public static void ForgetBeam(Beam beam)
    {
        if (beam != null)
            beamBaseWidths.Remove(beam);
    }

    public static void ResetVisualBookkeeping()
    {
        if (cryoBaseScales.Count > 0)
        {
            foreach (KeyValuePair<Projectile, Vector3> pair in cryoBaseScales)
            {
                if (pair.Key != null && pair.Key.transform != null)
                    pair.Key.transform.localScale = pair.Value;
            }
            cryoBaseScales.Clear();
        }

        beamBaseWidths.Clear();
    }

    private static float GetFloatField(FieldInfo field, Beam beam)
    {
        if (field == null || beam == null)
            return 0f;

        object value = field.GetValue(beam);
        return value is float ? (float)value : 0f;
    }

    private static void SetFloatField(FieldInfo field, Beam beam, float value)
    {
        if (field != null && beam != null)
            field.SetValue(beam, value);
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

[HarmonyPatch(typeof(Beam), "OnDestroy")]
public static class OrrerySpellSizingBeamDestroyPatch
{
    public static void Postfix(Beam __instance)
    {
        OrrerySpellSizing.ForgetBeam(__instance);
    }
}

/// <summary>
/// II visual scale is presentation-only and pool-safe. FF scale is owned by the
/// fireball lifecycle tracker so reflection/capture cleanup restores it exactly.
/// </summary>
[HarmonyPatch(typeof(Projectile), "Init")]
public static class OrrerySpellSizingProjectileVisualPatch
{
    public static void Postfix(Projectile __instance)
    {
        OrrerySpellSizing.ApplyCryoProjectileVisualScale(__instance);
    }
}

[HarmonyPatch(typeof(Projectile), "PoolDestroy")]
public static class OrrerySpellSizingProjectilePoolPatch
{
    [HarmonyPriority(Priority.First)]
    public static void Prefix(Projectile __instance)
    {
        OrrerySpellSizing.RestoreCryoProjectileVisualScale(__instance);
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class OrrerySpellSizingWorldDestroyPatch
{
    public static void Postfix()
    {
        OrrerySpellSizing.ResetVisualBookkeeping();
    }
}
