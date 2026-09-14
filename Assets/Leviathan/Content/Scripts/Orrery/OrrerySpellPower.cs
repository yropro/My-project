using UnityEngine;

/// <summary>
/// Shared Orrery spell-output reference curve.
///
/// Positive effective item levels are focused casts using the donor item's native
/// stat level. Negative levels are unfocused casts using the player's level and
/// the explicit unfocused output multiplier. Keeping that distinction here lets
/// every spell family share one reference-output contract.
///
/// Orrery suppresses the ship's normal weapons, so its shared spell budget also
/// represents the conventional primary-weapon output being replaced by the class.
/// That budget grows smoothly from two weapon-equivalents at level 1 to four at
/// level 20, then remains capped at four while ordinary item-level DPS scaling
/// continues normally.
/// </summary>
public static class OrrerySpellPower
{
    public const float MeanDpsAtLevelOne = 726f;
    public const float MedianDpsAtLevelOne = 546f;
    public const float DpsGrowthPerEffectiveItemLevel = 0.02f;

    // Whole-loadout output budget. Keep these as explicit tuning knobs rather than
    // burying progression assumptions inside individual spells.
    public const float WeaponEquivalentsAtLevelOne = 2f;
    public const float WeaponEquivalentsAtLevelTwenty = 4f;
    public const float WeaponEquivalentRampEndLevel = 20f;

    // No elemental focus is required to cast. Focusless spells deliberately keep
    // most of baseline output so gearing improves/changes spells rather than
    // functioning as a hard class-enablement gate.
    public const float UnfocusedDpsMultiplier = 0.80f;
    public const float UnfocusedCritChance = 0.10f;
    public const float UnfocusedCritModifier = 1.00f;
    public const float UnfocusedStatusEffectChance = 0.10f;

    public enum ReferenceMode : byte
    {
        Mean = 0,
        Median = 1
    }

    public static float GetLevelScale(float effectiveItemLevel)
    {
        float level = Mathf.Max(1f, Mathf.Abs(effectiveItemLevel));
        return 1f + DpsGrowthPerEffectiveItemLevel * (level - 1f);
    }

    public static float GetWeaponEquivalentBudget(float effectiveItemLevel)
    {
        float level = Mathf.Max(1f, Mathf.Abs(effectiveItemLevel));
        float rampLength = Mathf.Max(1f, WeaponEquivalentRampEndLevel - 1f);
        float t = Mathf.Clamp01((level - 1f) / rampLength);
        return Mathf.SmoothStep(
            WeaponEquivalentsAtLevelOne,
            WeaponEquivalentsAtLevelTwenty,
            t);
    }

    public static float GetReferenceDps(
        float effectiveItemLevel,
        ReferenceMode mode = ReferenceMode.Mean)
    {
        float baseDps = mode == ReferenceMode.Median
            ? MedianDpsAtLevelOne
            : MeanDpsAtLevelOne;
        float focusMultiplier = effectiveItemLevel < 0f
            ? UnfocusedDpsMultiplier
            : 1f;
        return baseDps *
            GetLevelScale(effectiveItemLevel) *
            GetWeaponEquivalentBudget(effectiveItemLevel) *
            focusMultiplier;
    }

    public static float GetIntegratedDamage(
        float effectiveItemLevel,
        float outputSeconds,
        float dpsMultiplier = 1f,
        ReferenceMode mode = ReferenceMode.Mean)
    {
        return GetReferenceDps(effectiveItemLevel, mode) *
            Mathf.Max(0f, outputSeconds) *
            Mathf.Max(0f, dpsMultiplier);
    }
}
