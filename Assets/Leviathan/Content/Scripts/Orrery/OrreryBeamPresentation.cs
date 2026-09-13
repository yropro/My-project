using HarmonyLib;
using StarVortex;
using System;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Presentation-only sizing for Orrery's native Tesla beam family.
///
/// Beam.Init caches its LineRenderer widths into protected max-width fields and
/// all later beam-state animation derives from those cached values. Scaling the
/// cache after native Init therefore preserves native activation/fade/chain logic
/// while giving Orrery one explicit beam-thickness knob.
/// </summary>
[HarmonyPatch]
public static class OrreryTeslaBeamWidthPatch
{
    private static readonly FieldInfo MaxWidthField =
        AccessTools.Field(typeof(Beam), "maxWidth");
    private static readonly FieldInfo MaxEndWidthField =
        AccessTools.Field(typeof(Beam), "maxEndWidth");
    private static readonly FieldInfo AdditionalMaxWidthField =
        AccessTools.Field(typeof(Beam), "additionalMaxWidth");

    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Beam),
            "Init",
            new Type[]
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
        if (__instance == null || parentBeamWeapon == null ||
            parentBeamWeapon.damageType != Damageable.DamageType.Electric ||
            !OrreryWeaponSuppression.IsRuntimeAdapter(parentBeamWeapon))
        {
            return;
        }

        float multiplier = Mathf.Max(
            0.01f,
            OrrerySpellRuntime.Tuning.TeslaBeamWidthMultiplier);
        if (Mathf.Approximately(multiplier, 1f))
            return;

        ScaleField(MaxWidthField, __instance, multiplier);
        ScaleField(MaxEndWidthField, __instance, multiplier);
        ScaleField(AdditionalMaxWidthField, __instance, multiplier);
    }

    private static void ScaleField(
        FieldInfo field,
        Beam beam,
        float multiplier)
    {
        if (field == null || beam == null)
            return;

        object value = field.GetValue(beam);
        if (!(value is float))
            return;

        field.SetValue(beam, (float)value * multiplier);
    }
}
