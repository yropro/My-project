using UnityEngine;

/// <summary>
/// Shared Orrery spell-output reference curve.
///
/// The curve is intentionally independent from focus selection. Callers resolve
/// an effective item level first, then ask this type for the reference output.
/// That keeps loot interpretation (including RequiredLevel rolls) out of spell
/// implementations and lets every formula normalize against the same baseline.
/// </summary>
public static class OrrerySpellPower
{
    public const float MeanDpsAtLevelOne = 726f;
    public const float MedianDpsAtLevelOne = 546f;
    public const float DpsGrowthPerEffectiveItemLevel = 0.02f;

    public enum ReferenceMode : byte
    {
        Mean = 0,
        Median = 1
    }

    public static float GetLevelScale(float effectiveItemLevel)
    {
        float level = Mathf.Max(1f, effectiveItemLevel);
        return 1f + DpsGrowthPerEffectiveItemLevel * (level - 1f);
    }

    public static float GetReferenceDps(
        float effectiveItemLevel,
        ReferenceMode mode = ReferenceMode.Mean)
    {
        float baseDps = mode == ReferenceMode.Median
            ? MedianDpsAtLevelOne
            : MeanDpsAtLevelOne;
        return baseDps * GetLevelScale(effectiveItemLevel);
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
