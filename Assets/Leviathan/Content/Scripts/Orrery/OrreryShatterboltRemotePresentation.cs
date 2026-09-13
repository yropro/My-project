using HarmonyLib;
using StarVortex;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Remote-only Shatterbolt presentation.
///
/// Gameplay stays entirely owner-authoritative. The Orrery presentation bank carries a
/// world-space orb position plus the complete bounded impact history for the current
/// cast, so packet coalescing cannot erase a fast intermediate hop.
/// </summary>
public static class OrreryShatterboltRemotePresentation
{
    private const string LightningOrbPath =
        "Base/Items/SecondaryWeapon/Lightning Orb Launcher";
    private const string FrostNovaPath =
        "Base/Items/Special/Frost Nova Pulse";

    private sealed class OrbVisualState
    {
        public Projectile Projectile;
        public bool ProjectileWasEnabled;
        public Rigidbody2D Body;
        public bool BodyWasSimulated;
        public Collider2D[] Colliders;
        public bool[] ColliderEnabled;
        public Vector3 BaseScale;
    }

    private sealed class BurstVisualState
    {
        public bool Active;
        public float AgeSeconds;
        public float RadiusMeters;
        public Wave Wave;
        public bool WaveWasEnabled;
        public CircleCollider2D Collider;
        public bool ColliderWasEnabled;
        public float ColliderRadius;
        public Vector3 BaseScale;
    }

    private sealed class RemoteState
    {
        public bool GenerationInitialized;
        public uint Generation;
        public int SeenImpactCount;
        public OrbVisualState Orb;
        public readonly BurstVisualState[] Bursts =
            new BurstVisualState[OrrerySpellCompendium.Shatterbolt.MaximumImpacts];

        public RemoteState()
        {
            for (int i = 0; i < Bursts.Length; i++)
                Bursts[i] = new BurstVisualState();
        }
    }

    private static readonly Dictionary<GameShip, RemoteState> states =
        new Dictionary<GameShip, RemoteState>(4);

    private static LauncherItemBase lightningOrbBase;
    private static PulseItemBase frostNovaBase;

    public static void Tick(GameShip remoteOwner, float deltaTime)
    {
        if (remoteOwner == null || !remoteOwner.IsRemotePlayer())
            return;

        OrreryNetwork.PresentationState network;
        if (!OrreryNetwork.TryReadRemote(remoteOwner, out network))
        {
            Forget(remoteOwner);
            return;
        }

        RemoteState state;
        bool hasState = states.TryGetValue(remoteOwner, out state) && state != null;
        if (!hasState && !network.ShatterboltPresent)
            return;

        if (!hasState)
        {
            state = new RemoteState();
            states[remoteOwner] = state;
        }

        if (network.ShatterboltPresent)
        {
            if (!state.GenerationInitialized ||
                state.Generation != network.ShatterboltGeneration)
            {
                ClearState(state);
                state.GenerationInitialized = true;
                state.Generation = network.ShatterboltGeneration;
            }

            int impactCount = Mathf.Clamp(
                network.ShatterboltImpactCount,
                0,
                OrrerySpellCompendium.Shatterbolt.MaximumImpacts);

            // A decreasing count within one generation means we received a reset
            // transition; never reinterpret stale impact positions as new bursts.
            if (impactCount < state.SeenImpactCount)
            {
                ClearBursts(state);
                state.SeenImpactCount = 0;
            }

            for (int i = state.SeenImpactCount; i < impactCount; i++)
                SpawnBurst(remoteOwner, state, network.GetShatterboltImpact(i),
                    network.ShatterboltExplosionRadiusMeters);
            state.SeenImpactCount = impactCount;

            if (network.ShatterboltOrbActive)
            {
                if (state.Orb == null)
                    state.Orb = SpawnOrb(remoteOwner, network.ShatterboltOrbPosition);
                UpdateOrb(state.Orb, network.ShatterboltOrbPosition, deltaTime);
            }
            else
            {
                CleanupOrb(state);
            }
        }
        else
        {
            CleanupOrb(state);
        }

        TickBursts(state, deltaTime);

        if (!network.ShatterboltPresent &&
            state.Orb == null &&
            !HasActiveBursts(state))
        {
            states.Remove(remoteOwner);
        }
    }

    public static void Forget(GameShip remoteOwner)
    {
        if (remoteOwner == null)
            return;

        RemoteState state;
        if (states.TryGetValue(remoteOwner, out state) && state != null)
            ClearState(state);
        states.Remove(remoteOwner);
    }

    public static void Reset()
    {
        foreach (KeyValuePair<GameShip, RemoteState> pair in states)
        {
            if (pair.Value != null)
                ClearState(pair.Value);
        }

        states.Clear();
        lightningOrbBase = null;
        frostNovaBase = null;
    }

    private static OrbVisualState SpawnOrb(GameShip owner, Vector2 position)
    {
        if (PoolController.instance == null)
            return null;

        if (lightningOrbBase == null)
            lightningOrbBase = Resources.Load<LauncherItemBase>(LightningOrbPath);
        if (lightningOrbBase == null)
            return null;

        GameObject prefab = lightningOrbBase.GetProjectileObject(owner.faction);
        if (prefab == null)
            return null;

        GameObject visualObject = PoolController.instance.GetObject(
            prefab,
            position,
            Quaternion.identity,
            false);
        if (visualObject == null)
            return null;

        Projectile projectile;
        if (!visualObject.TryGetComponent<Projectile>(out projectile) ||
            projectile == null)
        {
            ReturnUnexpectedVisual(visualObject);
            return null;
        }

        OrbVisualState visual = new OrbVisualState();
        visual.Projectile = projectile;
        visual.ProjectileWasEnabled = projectile.enabled;
        visual.Body = projectile.rigidBody;
        visual.BodyWasSimulated = visual.Body != null && visual.Body.simulated;
        visual.Colliders = projectile.GetComponentsInChildren<Collider2D>(true);
        visual.ColliderEnabled = new bool[visual.Colliders.Length];
        visual.BaseScale = projectile.transform.localScale;

        projectile.enabled = false;
        if (visual.Body != null)
        {
            visual.Body.velocity = Vector2.zero;
            visual.Body.angularVelocity = 0f;
            visual.Body.simulated = false;
        }

        for (int i = 0; i < visual.Colliders.Length; i++)
        {
            Collider2D collider = visual.Colliders[i];
            if (collider == null)
                continue;
            visual.ColliderEnabled[i] = collider.enabled;
            collider.enabled = false;
        }

        projectile.transform.localScale = visual.BaseScale * Mathf.Max(
            0.01f,
            OrrerySpellCompendium.Shatterbolt.ProjectileVisualScale);
        return visual;
    }

    private static void UpdateOrb(
        OrbVisualState visual,
        Vector2 position,
        float deltaTime)
    {
        if (visual == null || visual.Projectile == null)
            return;

        Transform transform = visual.Projectile.transform;
        Vector3 current = transform.position;
        transform.position = new Vector3(position.x, position.y, current.z);
        transform.Rotate(
            0f,
            0f,
            OrrerySpellCompendium.Shatterbolt.ProjectileSpinDegreesPerSecond *
                Mathf.Max(0f, deltaTime));
    }

    private static void CleanupOrb(RemoteState state)
    {
        if (state == null || state.Orb == null)
            return;

        OrbVisualState visual = state.Orb;
        Projectile projectile = visual.Projectile;
        if (projectile != null)
        {
            projectile.transform.localScale = visual.BaseScale;
            if (visual.Body != null)
            {
                visual.Body.velocity = Vector2.zero;
                visual.Body.angularVelocity = 0f;
                visual.Body.simulated = visual.BodyWasSimulated;
            }

            if (visual.Colliders != null && visual.ColliderEnabled != null)
            {
                int count = Mathf.Min(
                    visual.Colliders.Length,
                    visual.ColliderEnabled.Length);
                for (int i = 0; i < count; i++)
                {
                    Collider2D collider = visual.Colliders[i];
                    if (collider != null)
                        collider.enabled = visual.ColliderEnabled[i];
                }
            }

            projectile.enabled = visual.ProjectileWasEnabled;
            projectile.PoolDestroy();
        }

        state.Orb = null;
    }

    private static void SpawnBurst(
        GameShip owner,
        RemoteState state,
        Vector2 position,
        float radiusMeters)
    {
        if (state == null || PoolController.instance == null)
            return;

        BurstVisualState burst = null;
        for (int i = 0; i < state.Bursts.Length; i++)
        {
            if (!state.Bursts[i].Active)
            {
                burst = state.Bursts[i];
                break;
            }
        }

        if (burst == null)
            return;

        if (frostNovaBase == null)
            frostNovaBase = Resources.Load<PulseItemBase>(FrostNovaPath);
        GameObject prefab = frostNovaBase == null ? null : frostNovaBase.wave;
        if (prefab == null)
            return;

        GameObject visualObject = PoolController.instance.GetObject(
            prefab,
            position,
            Utils.RandomRotation(),
            false);
        if (visualObject == null)
            return;

        Wave wave;
        CircleCollider2D circle;
        if (!visualObject.TryGetComponent<Wave>(out wave) ||
            wave == null ||
            !visualObject.TryGetComponent<CircleCollider2D>(out circle) ||
            circle == null ||
            circle.radius <= 0f)
        {
            ReturnUnexpectedVisual(visualObject);
            return;
        }

        burst.Active = true;
        burst.AgeSeconds = 0f;
        burst.RadiusMeters = radiusMeters;
        burst.Wave = wave;
        burst.WaveWasEnabled = wave.enabled;
        burst.Collider = circle;
        burst.ColliderWasEnabled = circle.enabled;
        burst.ColliderRadius = circle.radius;
        burst.BaseScale = wave.transform.localScale;

        wave.enabled = false;
        circle.enabled = false;
        OrreryShatterboltWavePresentation.ResetMask(wave);
        wave.transform.localScale = Vector3.zero;
    }

    private static void TickBursts(RemoteState state, float deltaTime)
    {
        if (state == null)
            return;

        float expansionMetersPerSecond = Mathf.Max(
            0.001f,
            OrrerySpellCompendium.Shatterbolt.ExplosionExpansionMetersPerSecond);
        float dt = Mathf.Max(0f, deltaTime);

        for (int i = 0; i < state.Bursts.Length; i++)
        {
            BurstVisualState burst = state.Bursts[i];
            if (!burst.Active)
                continue;

            burst.AgeSeconds += dt;
            float duration = Mathf.Max(0.01f,
                burst.RadiusMeters / expansionMetersPerSecond);
            float radiusMeters = Mathf.Min(
                burst.RadiusMeters,
                burst.AgeSeconds * expansionMetersPerSecond);
            float radiusWorld = radiusMeters * OrreryUnits.WorldUnitsPerMeter;

            if (burst.Wave != null && burst.ColliderRadius > 0f)
            {
                float scale = Mathf.Max(
                    0.001f,
                    radiusWorld /
                        burst.ColliderRadius *
                        Mathf.Max(
                            0.01f,
                            OrrerySpellCompendium.Shatterbolt.ExplosionVisualScale));
                burst.Wave.transform.localScale =
                    new Vector3(scale, scale, scale);
            }

            if (burst.AgeSeconds >= duration)
                CleanupBurst(burst);
        }
    }

    private static bool HasActiveBursts(RemoteState state)
    {
        if (state == null)
            return false;

        for (int i = 0; i < state.Bursts.Length; i++)
        {
            if (state.Bursts[i].Active)
                return true;
        }

        return false;
    }

    private static void CleanupBurst(BurstVisualState burst)
    {
        if (burst == null)
            return;

        if (burst.Wave != null)
        {
            burst.Wave.transform.localScale = burst.BaseScale;
            if (burst.Collider != null)
                burst.Collider.enabled = burst.ColliderWasEnabled;
            burst.Wave.enabled = burst.WaveWasEnabled;
            burst.Wave.PoolDestroy();
        }

        burst.Active = false;
        burst.AgeSeconds = 0f;
        burst.Wave = null;
        burst.Collider = null;
        burst.ColliderRadius = 0f;
    }

    private static void ClearBursts(RemoteState state)
    {
        if (state == null)
            return;

        for (int i = 0; i < state.Bursts.Length; i++)
            CleanupBurst(state.Bursts[i]);
    }

    private static void ClearState(RemoteState state)
    {
        if (state == null)
            return;

        CleanupOrb(state);
        ClearBursts(state);
        state.GenerationInitialized = false;
        state.Generation = 0u;
        state.SeenImpactCount = 0;
    }

    private static void ReturnUnexpectedVisual(GameObject visualObject)
    {
        if (visualObject == null)
            return;

        PoolableObject poolable;
        if (visualObject.TryGetComponent<PoolableObject>(out poolable) &&
            poolable != null)
        {
            poolable.PoolDestroy();
            return;
        }

        Object.Destroy(visualObject);
    }
}

// Native Wave.ResetObject restores color/scale, but Wave.Init is what normally
// clears its obstruction shader mask. Both Shatterbolt presenters skip Init to
// avoid native gameplay. Reset only that visual property, preserving other
// material properties. Native reuse initializes its own gameplay mask normally.
internal static class OrreryShatterboltWavePresentation
{
    private static readonly int blockRadiusId = Shader.PropertyToID("_BlockRadius");
    private static readonly float[] unblockedRadii = CreateUnblockedRadii();
    private static readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();

    private static float[] CreateUnblockedRadii()
    {
        float[] radii = new float[128];
        for (int i = 0; i < radii.Length; i++)
            radii[i] = 1000000f;
        return radii;
    }

    public static void ResetMask(Wave wave)
    {
        SpriteRenderer renderer;
        if (wave == null || !wave.TryGetComponent<SpriteRenderer>(out renderer))
            return;
        renderer.GetPropertyBlock(properties);
        properties.SetFloatArray(blockRadiusId, unblockedRadii);
        renderer.SetPropertyBlock(properties);
    }
}

// RemoteShipDriver.Render is the point at which Star Vortex has already applied
// the interpolated remote ship transform. Driving Shatterbolt presentation here
// keeps its reconstructed positions aligned with what the observer actually sees
// without patching FixedUpdate on every ship in the world.
[HarmonyPatch(typeof(RemoteShipDriver), "Render")]
public static class OrreryShatterboltRemoteRenderPatch
{
    public static void Postfix(RemoteShipDriver __instance)
    {
        GameShip remoteOwner = __instance == null ? null : __instance.gameShip;
        if (remoteOwner != null && remoteOwner.IsRemotePlayer())
        {
            OrreryShatterboltRemotePresentation.Tick(
                remoteOwner,
                Time.deltaTime);
        }
    }
}

[HarmonyPatch(typeof(GameShip), "OnDestroy")]
public static class OrreryShatterboltRemoteShipDestroyedPatch
{
    public static void Prefix(GameShip __instance)
    {
        if (__instance != null && __instance.IsRemotePlayer())
            OrreryShatterboltRemotePresentation.Forget(__instance);
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class OrreryShatterboltRemoteWorldDestroyedPatch
{
    public static void Prefix()
    {
        OrreryShatterboltRemotePresentation.Reset();
    }
}
