using UnityEngine;

/// <summary>
/// Orrery spatial unit boundary.
///
/// Player-facing/authored spatial tuning is expressed in meters. Star Vortex
/// simulation space uses 20 meters per Unity world unit, so conversion happens
/// only when a value crosses into a Transform/physics/native-radius API.
/// </summary>
public static class OrreryUnits
{
    public const float WorldUnitsPerMeter = 1f / 20f;
    public const float MetersPerWorldUnit = 20f;

    public static float MetersToWorld(float meters)
    {
        return Mathf.Max(0f, meters) * WorldUnitsPerMeter;
    }

    public static float WorldToMeters(float worldUnits)
    {
        return worldUnits * MetersPerWorldUnit;
    }
}
