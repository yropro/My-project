using StarVortex;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Orrery-owned orbital simulation state. This computes deterministic desired
/// world-space poses for semantic satellites; it does not spawn ships, own input,
/// or move native GameShip bodies. The physical builder/controller consumes this
/// state after it has published a verified OrrerySatellites snapshot.
/// </summary>
public static class OrreryOrbit
{
    public const int MaxSatellites = OrreryCasting.MaxFormulaSatellites;

    public sealed class ControlState
    {
        public float RadiusOffsetMeters;
        public float AngularSpeedMultiplier = 1f;
    }

    public sealed class OrbitState
    {
        internal readonly float[] AnglesDegrees = new float[MaxSatellites];
        internal readonly bool[] Initialized = new bool[MaxSatellites];
        public readonly ControlState Control = new ControlState();
        public int ActiveSatelliteCount { get; internal set; }
    }

    private static readonly Dictionary<GameShip, OrbitState> states =
        new Dictionary<GameShip, OrbitState>(4);

    public static OrbitState GetOrCreate(GameShip owner)
    {
        if (owner == null || !OrreryRuntime.IsActive(owner))
            return null;

        OrreryRuntime.ResolvedState resolved = OrreryRuntime.GetResolvedState(owner);
        if (resolved == null || !resolved.Active)
            return null;

        OrbitState state;
        if (!states.TryGetValue(owner, out state) || state == null)
        {
            state = new OrbitState();
            states[owner] = state;
        }

        int count = Mathf.Clamp(resolved.SatelliteCount, 0, MaxSatellites);
        if (state.ActiveSatelliteCount != count)
        {
            state.ActiveSatelliteCount = count;
            InitializeMissingPhases(state, count);
        }

        return state;
    }

    /// <summary>
    /// Advances only unlocked satellites. A casting lock freezes angular
    /// progression while radial changes continue to affect the desired pose.
    /// </summary>
    public static void Tick(GameShip owner, float deltaTime)
    {
        OrbitState state = GetOrCreate(owner);
        if (state == null || deltaTime <= 0f)
            return;

        OrreryRuntime.ResolvedState resolved = OrreryRuntime.GetResolvedState(owner);
        float speed = resolved.BaseOrbitAngularSpeedDegreesPerSecond *
            Mathf.Max(0f, state.Control.AngularSpeedMultiplier);

        for (int i = 0; i < state.ActiveSatelliteCount; i++)
        {
            byte satelliteId = (byte)(i + 1);
            OrrerySatellites.SatelliteContext satellite;
            bool locked = OrrerySatellites.TryGetSatellite(owner, satelliteId, out satellite) &&
                satellite != null && satellite.Locked;
            if (locked)
                continue;

            state.AnglesDegrees[i] = OrreryRuntime.NormalizeDegrees(
                state.AnglesDegrees[i] + speed * deltaTime);
        }
    }

    public static bool TryGetDesiredPose(
        GameShip owner,
        byte satelliteId,
        out Vector3 worldPosition,
        out float worldAngleDegrees,
        out float orbitRadiusMeters)
    {
        worldPosition = default(Vector3);
        worldAngleDegrees = 0f;
        orbitRadiusMeters = 0f;

        OrbitState state = GetOrCreate(owner);
        if (state == null || satelliteId == 0 || satelliteId > state.ActiveSatelliteCount)
            return false;

        OrreryRuntime.ResolvedState resolved = OrreryRuntime.GetResolvedState(owner);
        int index = satelliteId - 1;
        orbitRadiusMeters = Mathf.Max(
            0f,
            resolved.BaseOrbitRadiusMeters +
            resolved.OrbitLaneSpacingMeters * index +
            state.Control.RadiusOffsetMeters);
        worldAngleDegrees = state.AnglesDegrees[index];

        Vector3 radial = Quaternion.Euler(0f, 0f, worldAngleDegrees) *
            (Vector3.right * orbitRadiusMeters);
        worldPosition = owner.transform.position + radial;
        return true;
    }

    public static float GetAngleDegrees(GameShip owner, byte satelliteId)
    {
        OrbitState state = GetOrCreate(owner);
        if (state == null || satelliteId == 0 || satelliteId > state.ActiveSatelliteCount)
            return 0f;
        return state.AnglesDegrees[satelliteId - 1];
    }

    public static void SetControl(
        GameShip owner,
        float radiusOffsetMeters,
        float angularSpeedMultiplier)
    {
        OrbitState state = GetOrCreate(owner);
        if (state == null)
            return;

        state.Control.RadiusOffsetMeters = radiusOffsetMeters;
        state.Control.AngularSpeedMultiplier = Mathf.Max(0f, angularSpeedMultiplier);
    }

    public static void Forget(GameShip owner)
    {
        if (owner != null)
            states.Remove(owner);
    }

    public static void Reset()
    {
        states.Clear();
    }

    private static void InitializeMissingPhases(OrbitState state, int count)
    {
        if (count <= 0)
            return;

        float spacing = 360f / count;
        for (int i = 0; i < count; i++)
        {
            if (state.Initialized[i])
                continue;
            state.AnglesDegrees[i] = spacing * i;
            state.Initialized[i] = true;
        }

        for (int i = count; i < MaxSatellites; i++)
        {
            state.Initialized[i] = false;
            state.AnglesDegrees[i] = 0f;
        }
    }
}
