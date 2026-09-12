using UnityEngine;

/// <summary>
/// Small, allocation-free 2D spatial math shared by class and skill mechanics.
///
/// This class intentionally answers geometry questions only. It does not decide
/// target eligibility, damage, status effects, authority, networking, or VFX.
/// Callers remain responsible for broad-phase candidate collection and for using
/// real Collider2D overlap/contact tests when a mechanic cares about collider
/// extent rather than a target point.
///
/// Unless a method explicitly converts meters, all positions and distances must
/// be supplied in the same coordinate units.
/// </summary>
public static class CoreSpatial
{
    // Star Vortex displays 20 meters per Unity world unit.
    public const float WorldUnitsPerMeter = 1f / 20f;

    private const float DirectionEpsilonSquared = 0.000001f;

    public static float MetersToWorldUnits(float meters)
    {
        return meters * WorldUnitsPerMeter;
    }

    public static float WorldUnitsToMeters(float worldUnits)
    {
        return worldUnits / WorldUnitsPerMeter;
    }

    /// <summary>
    /// True when point lies inside or on a circle. Radius must be non-negative.
    /// This is a point test; it does not account for Collider2D extent.
    /// </summary>
    public static bool IsPointInCircle(Vector2 point, Vector2 center, float radius)
    {
        if (radius < 0f || float.IsNaN(radius) || float.IsInfinity(radius))
            return false;

        Vector2 offset = point - center;
        return offset.sqrMagnitude <= radius * radius;
    }

    /// <summary>
    /// True when point lies inside or on the radial band [innerRadius, outerRadius].
    /// This is a point test; it does not account for Collider2D extent.
    /// </summary>
    public static bool IsPointInRing(
        Vector2 point,
        Vector2 center,
        float innerRadius,
        float outerRadius)
    {
        if (innerRadius < 0f || outerRadius < innerRadius ||
            float.IsNaN(innerRadius) || float.IsInfinity(innerRadius) ||
            float.IsNaN(outerRadius) || float.IsInfinity(outerRadius))
        {
            return false;
        }

        float distanceSquared = (point - center).sqrMagnitude;
        return distanceSquared >= innerRadius * innerRadius &&
            distanceSquared <= outerRadius * outerRadius;
    }

    /// <summary>
    /// True when point lies inside a finite cone/sector originating at origin.
    /// halfAngleDegrees is measured from forward to either edge, so a 25-degree
    /// total cone uses 12.5 degrees here. Valid half-angles are 0..180 degrees.
    /// This is a point test; it does not account for Collider2D extent.
    /// </summary>
    public static bool IsPointInCone(
        Vector2 point,
        Vector2 origin,
        Vector2 forward,
        float range,
        float halfAngleDegrees)
    {
        if (!IsValidConeInput(forward, range, halfAngleDegrees))
            return false;

        Vector2 offset = point - origin;
        float distanceSquared = offset.sqrMagnitude;
        if (distanceSquared > range * range)
            return false;

        // The origin has no meaningful bearing but is geometrically inside.
        if (distanceSquared <= DirectionEpsilonSquared)
            return true;

        if (halfAngleDegrees >= 180f)
            return true;

        float denominator = Mathf.Sqrt(distanceSquared * forward.sqrMagnitude);
        float cosineThreshold = Mathf.Cos(halfAngleDegrees * Mathf.Deg2Rad);
        float dot = Vector2.Dot(offset, forward);
        return dot >= cosineThreshold * denominator;
    }

    /// <summary>
    /// Squared shortest distance from point to the finite segment [start, end].
    /// A zero-length segment behaves as a point at start.
    /// </summary>
    public static float DistanceSquaredToSegment(
        Vector2 point,
        Vector2 start,
        Vector2 end)
    {
        Vector2 segment = end - start;
        float segmentLengthSquared = segment.sqrMagnitude;
        if (segmentLengthSquared <= DirectionEpsilonSquared)
            return (point - start).sqrMagnitude;

        float t = Vector2.Dot(point - start, segment) / segmentLengthSquared;
        t = Mathf.Clamp01(t);

        Vector2 closest = start + segment * t;
        return (point - closest).sqrMagnitude;
    }

    /// <summary>
    /// Shortest distance from point to the finite segment [start, end].
    /// </summary>
    public static float DistanceToSegment(
        Vector2 point,
        Vector2 start,
        Vector2 end)
    {
        return Mathf.Sqrt(DistanceSquaredToSegment(point, start, end));
    }

    /// <summary>
    /// True when point lies within radius of the finite segment [start, end].
    /// This is the useful geometry for a constant-width beam/corridor. Radius is
    /// the beam half-width. A zero-length segment becomes a circle.
    /// This is a point test; it does not account for Collider2D extent.
    /// </summary>
    public static bool IsPointInCapsule(
        Vector2 point,
        Vector2 start,
        Vector2 end,
        float radius)
    {
        if (radius < 0f || float.IsNaN(radius) || float.IsInfinity(radius))
            return false;

        return DistanceSquaredToSegment(point, start, end) <= radius * radius;
    }

    /// <summary>
    /// True when point lies inside an angular radial band: a ring restricted to
    /// a cone. This is the useful geometry for crescents / blast-wave fronts.
    /// halfAngleDegrees is measured from forward to either edge.
    /// This is a point test; it does not account for Collider2D extent.
    /// </summary>
    public static bool IsPointInAnnularSector(
        Vector2 point,
        Vector2 origin,
        Vector2 forward,
        float innerRadius,
        float outerRadius,
        float halfAngleDegrees)
    {
        if (innerRadius < 0f || outerRadius < innerRadius ||
            float.IsNaN(innerRadius) || float.IsInfinity(innerRadius) ||
            float.IsNaN(outerRadius) || float.IsInfinity(outerRadius) ||
            !IsValidConeInput(forward, outerRadius, halfAngleDegrees))
        {
            return false;
        }

        Vector2 offset = point - origin;
        float distanceSquared = offset.sqrMagnitude;
        if (distanceSquared < innerRadius * innerRadius ||
            distanceSquared > outerRadius * outerRadius)
        {
            return false;
        }

        if (distanceSquared <= DirectionEpsilonSquared)
            return true;

        if (halfAngleDegrees >= 180f)
            return true;

        float denominator = Mathf.Sqrt(distanceSquared * forward.sqrMagnitude);
        float cosineThreshold = Mathf.Cos(halfAngleDegrees * Mathf.Deg2Rad);
        float dot = Vector2.Dot(offset, forward);
        return dot >= cosineThreshold * denominator;
    }

    /// <summary>
    /// Returns normalized radial distance: 0 at center and 1 at/beyond radius.
    /// Negative distance is treated as zero. A non-positive radius contains only
    /// distance zero.
    /// </summary>
    public static float GetRadialFraction(float distance, float radius)
    {
        if (float.IsNaN(distance) || float.IsNaN(radius) ||
            float.IsInfinity(distance) || float.IsInfinity(radius))
        {
            return 1f;
        }

        distance = Mathf.Max(0f, distance);
        if (radius <= 0f)
            return distance <= 0f ? 0f : 1f;

        return Mathf.Clamp01(distance / radius);
    }

    /// <summary>
    /// Returns a center-to-edge falloff in [0,1]. Strength is 1 at center and 0
    /// at/beyond radius. Exponent 1 is linear; values above 1 concentrate strength
    /// toward the center; values between 0 and 1 retain more strength near the edge.
    /// Negative/invalid exponents fail closed to zero.
    /// </summary>
    public static float GetRadialFalloff(float distance, float radius, float exponent)
    {
        if (exponent < 0f || float.IsNaN(exponent) || float.IsInfinity(exponent))
            return 0f;

        float fraction = GetRadialFraction(distance, radius);
        if (fraction >= 1f)
            return 0f;

        if (exponent == 0f)
            return 1f;

        return Mathf.Pow(1f - fraction, exponent);
    }

    private static bool IsValidConeInput(
        Vector2 forward,
        float range,
        float halfAngleDegrees)
    {
        if (range < 0f || halfAngleDegrees < 0f || halfAngleDegrees > 180f ||
            float.IsNaN(range) || float.IsInfinity(range) ||
            float.IsNaN(halfAngleDegrees) || float.IsInfinity(halfAngleDegrees))
        {
            return false;
        }

        float forwardSquared = forward.sqrMagnitude;
        return forwardSquared > DirectionEpsilonSquared &&
            !float.IsNaN(forwardSquared) && !float.IsInfinity(forwardSquared);
    }
}
