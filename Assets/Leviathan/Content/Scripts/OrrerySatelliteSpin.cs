using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Presentation state for Orrery satellite axial rotation.
///
/// Orbit position remains authoritative in OrreryOrbit/OrreryController. This
/// layer only owns the satellite's Z rotation after physical orbit/shuffle motion
/// has been applied for the fixed step.
/// </summary>
public static class OrrerySatelliteSpin
{
    public static class Tuning
    {
        // Normal idle spin is intentionally slower than the 45 deg/s baseline
        // orbital motion. Sign is derived automatically as opposite the satellite's
        // actual orbit direction.
        public const float NormalSpinDegreesPerSecond = 15f;

        // Spell completion/shuffle begins with a brief fast spin, then eases back
        // to the normal magnitude over the existing shuffle interval.
        public const float ShuffleBurstSpinDegreesPerSecond = 720f;
        public const float ShuffleSpinDecaySeconds = 0.50f;
    }

    private sealed class OwnerSpinState
    {
        public readonly float[] AnglesDegrees =
            new float[OrreryOrbit.MaxSatellites];
        public readonly bool[] Initialized =
            new bool[OrreryOrbit.MaxSatellites];
        public bool WasShuffling;
        public float ShuffleElapsedSeconds;
    }

    private static readonly Dictionary<GameShip, OwnerSpinState> states =
        new Dictionary<GameShip, OwnerSpinState>(4);

    public static void Tick(GameShip owner, float deltaTime)
    {
        if (owner == null || !OrreryRuntime.IsActive(owner) || deltaTime <= 0f)
            return;

        OrrerySatellites.SatelliteSnapshot snapshot =
            OrrerySatellites.GetSnapshot(owner);
        if (snapshot == null)
            return;

        OwnerSpinState state;
        if (!states.TryGetValue(owner, out state) || state == null)
        {
            state = new OwnerSpinState();
            states[owner] = state;
        }

        bool shuffling = OrreryController.IsShuffling(owner);
        if (shuffling)
        {
            if (!state.WasShuffling)
                state.ShuffleElapsedSeconds = 0f;
            else
                state.ShuffleElapsedSeconds += deltaTime;
        }
        else
        {
            state.ShuffleElapsedSeconds = 0f;
        }

        float normalMagnitude = Mathf.Max(0f, Tuning.NormalSpinDegreesPerSecond);
        float shuffleMagnitude = normalMagnitude;
        if (shuffling)
        {
            float decaySeconds = Mathf.Max(0.001f, Tuning.ShuffleSpinDecaySeconds);
            float t = Mathf.Clamp01(state.ShuffleElapsedSeconds / decaySeconds);
            shuffleMagnitude = Mathf.Lerp(
                Mathf.Max(normalMagnitude, Tuning.ShuffleBurstSpinDegreesPerSecond),
                normalMagnitude,
                t);
        }

        for (int i = 0; i < snapshot.Count; i++)
        {
            OrrerySatellites.SatelliteContext satellite = snapshot.Get(i);
            if (satellite == null || !satellite.IsValid || satellite.Ship == null ||
                satellite.SatelliteId == 0 ||
                satellite.SatelliteId > OrreryOrbit.MaxSatellites)
            {
                continue;
            }

            int index = satellite.SatelliteId - 1;
            GameShip ship = satellite.Ship;
            if (!state.Initialized[index])
            {
                state.AnglesDegrees[index] = ship.transform.eulerAngles.z;
                state.Initialized[index] = true;
            }

            float orbitDirection =
                OrreryOrbit.GetOrbitDirectionSign(satellite.SatelliteId);

            // Normal: rotate opposite the orbital direction.
            // Locked: reverse axial spin while the orbital phase is frozen.
            // Shuffle: fast normal-direction spin, easing back to idle magnitude.
            float spinDirection;
            float magnitude;
            if (shuffling)
            {
                spinDirection = -orbitDirection;
                magnitude = shuffleMagnitude;
            }
            else if (satellite.Locked)
            {
                spinDirection = orbitDirection;
                magnitude = normalMagnitude;
            }
            else
            {
                spinDirection = -orbitDirection;
                magnitude = normalMagnitude;
            }

            state.AnglesDegrees[index] = OrreryRuntime.NormalizeDegrees(
                state.AnglesDegrees[index] +
                spinDirection * magnitude * deltaTime);

            ApplyRotation(ship, state.AnglesDegrees[index]);
        }

        state.WasShuffling = shuffling;
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

    private static void ApplyRotation(GameShip ship, float angleDegrees)
    {
        if (ship == null)
            return;

        Rigidbody2D body = ship.GetRigidBody();
        if (body != null)
        {
            body.angularVelocity = 0f;
            body.rotation = angleDegrees;
        }

        ship.transform.rotation = Quaternion.Euler(0f, 0f, angleDegrees);
    }
}

/// <summary>
/// OrreryController owns movement and currently normalizes satellite rotation
/// while applying orbit/shuffle poses. Run after that fixed step so axial spin is
/// the final presentation pose without changing orbit physics.
/// </summary>
[HarmonyPatch(typeof(OrreryController), "FixedUpdate")]
public static class OrrerySatelliteSpinControllerPatch
{
    public static void Postfix()
    {
        CoreOwnerContext context = CoreClassRuntime.CurrentContext;
        if (context == null || !context.IsValid ||
            context.ClassId != CoreClassId.Orrery || context.Ship == null)
        {
            return;
        }

        OrrerySatelliteSpin.Tick(context.Ship, Time.fixedDeltaTime);
    }
}

[HarmonyPatch(typeof(OrreryRuntime), "Deactivate", new Type[] { typeof(GameShip) })]
public static class OrrerySatelliteSpinDeactivatePatch
{
    public static void Prefix(GameShip owner)
    {
        OrrerySatelliteSpin.Forget(owner);
    }
}

[HarmonyPatch(typeof(OrreryRuntime), "Reset")]
public static class OrrerySatelliteSpinResetPatch
{
    public static void Prefix()
    {
        OrrerySatelliteSpin.Reset();
    }
}
