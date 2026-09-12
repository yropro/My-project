using UnityEngine;

/// <summary>
/// Shared Orrery spell-output reference curve.
///
/// Positive effective item levels are focused casts using the donor item's native
/// stat level. Negative levels are unfocused casts using the player's level and
/// the explicit unfocused output multiplier. Keeping that distinction here lets
/// every spell family share one reference-output contract.
/// </summary>
public static class OrrerySpellPower
{
    public const float MeanDpsAtLevelOne = 726f;
    public const float MedianDpsAtLevelOne = 546f;
    public const float DpsGrowthPerEffectiveItemLevel = 0.02f;

    // No elemental focus is required to cast. Focusless spells deliberately keep
    // most of baseline output so gearing improves/changes spells rather than
    // functioning as a hard class-enablement gate.
    public const float UnfocusedDpsMultiplier = 0.80f;
    public const float UnfocusedCritChance = 0.10f;
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
        return baseDps * GetLevelScale(effectiveItemLevel) * focusMultiplier;
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
