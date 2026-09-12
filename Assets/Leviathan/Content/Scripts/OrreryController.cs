using HarmonyLib;
using StarVortex;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Local physical runtime for Orrery satellites.
///
/// The controller owns construction and physical motion only. Canonical satellite
/// identity remains in OrrerySatellites, formula state remains in OrreryCasting,
/// and stable build truth remains in OrreryRuntime.ResolvedState.
/// </summary>
public sealed class OrreryController : MonoBehaviour
{
    private const string SpawnCarrierName = "LeviathanTest";
    private const float FailedBuildRetrySeconds = 2f;

    private static OrreryController instance;

    private readonly List<OrrerySatellites.IntentEntry> intentEntries =
        new List<OrrerySatellites.IntentEntry>(OrreryOrbit.MaxSatellites);
    private readonly List<OrrerySatellites.PublishEntry> publishEntries =
        new List<OrrerySatellites.PublishEntry>(OrreryOrbit.MaxSatellites);

    private GameShip currentOwner;
    private Squadron activeSquadron;
    private float nextBuildAttemptTime;

    public static OrreryController Instance { get { return instance; } }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void FixedUpdate()
    {
        SyncOwner();
        if (currentOwner == null)
            return;

        OrrerySpellRuntime.FixedTick(currentOwner, Time.fixedDeltaTime);

        if (activeSquadron == null)
            return;

        OrreryOrbit.Tick(currentOwner, Time.fixedDeltaTime);
        ApplyDesiredOrbit(false);
    }

    private void LateUpdate()
    {
        if (currentOwner != null)
            OrrerySpellRuntime.LateTick(currentOwner);
    }

    private void OnDestroy()
    {
        if (instance == this)
            instance = null;
        TearDownCurrentBuild();
    }

    public void ResetWorld()
    {
        TearDownCurrentBuild();
        currentOwner = null;
        nextBuildAttemptTime = 0f;
    }

    private void SyncOwner()
    {
        CoreOwnerContext context = CoreClassRuntime.CurrentContext;
        GameShip desiredOwner = context != null && context.IsValid &&
            context.ClassId == CoreClassId.Orrery
                ? context.Ship
                : null;

        if (!ReferenceEquals(desiredOwner, currentOwner))
        {
            TearDownCurrentBuild();
            currentOwner = desiredOwner;
            nextBuildAttemptTime = 0f;
        }

        if (currentOwner == null || activeSquadron != null ||
            !OrreryRuntime.IsActive(currentOwner) ||
            Time.time < nextBuildAttemptTime)
        {
            return;
        }

        if (!TryBuild(currentOwner))
            nextBuildAttemptTime = Time.time + FailedBuildRetrySeconds;
    }

    private bool TryBuild(GameShip owner)
    {
        OrreryRuntime.ResolvedState resolved = OrreryRuntime.GetResolvedState(owner);
        if (resolved == null || !resolved.Active || resolved.SatelliteCount <= 0)
            return false;

        int satelliteCount = Mathf.Clamp(
            resolved.SatelliteCount,
            0,
            OrreryOrbit.MaxSatellites);

        intentEntries.Clear();
        for (int i = 0; i < satelliteCount; i++)
        {
            byte satelliteId = (byte)(i + 1);
            float radius = resolved.BaseOrbitRadiusMeters +
                resolved.OrbitLaneSpacingMeters * i;
            intentEntries.Add(new OrrerySatellites.IntentEntry(
                satelliteId,
                OrrerySatellites.SatelliteKind.Formula,
                radius,
                resolved.BaseOrbitAngularSpeedDegreesPerSecond));
        }

        if (!OrrerySatellites.PublishIntent(owner, intentEntries))
        {
            Debug.LogError("[Orrery] Canonical satellite intent rejected baseline build.");
            return false;
        }

        SquadronBase carrier = FindSpawnCarrier();
        if (carrier == null)
        {
            Debug.LogError("[Orrery] Could not find temporary satellite spawn carrier '" +
                SpawnCarrierName + "'.");
            return false;
        }

        Squadron squadron = carrier.GetSquadron(1);
        if (squadron == null || squadron.ships == null ||
            squadron.ships.Count < satelliteCount + 1)
        {
            Debug.LogError("[Orrery] Satellite spawn carrier does not contain enough follower slots for " +
                satelliteCount + " satellites.");
            return false;
        }

        // The carrier is only a source of independent NPC/Ship definitions. Trim
        // its private runtime copy and convert the requested followers to native
        // orbit AI before spawning; the shared ScriptableObject is never mutated.
        if (squadron.ships.Count > satelliteCount + 1)
            squadron.ships.RemoveRange(
                satelliteCount + 1,
                squadron.ships.Count - satelliteCount - 1);

        Squadron.SquadronShip leader = squadron.ships[0];
        leader.ship = owner;
        leader.spawned = true;
        leader.temporary = false;

        for (int i = 1; i <= satelliteCount; i++)
        {
            Squadron.SquadronShip slot = squadron.ships[i];
            Ship definition = slot == null || slot.npc == null
                ? null
                : slot.npc.GetShip();
            if (definition == null)
            {
                Debug.LogError("[Orrery] Spawn carrier follower slot " + i +
                    " has no mutable Ship definition.");
                return false;
            }

            definition.aiBehaviour = Ship.AiBehaviour.OrbitInner;
            definition.faction = owner.faction;
            slot.spawned = false;
            slot.ship = null;
            slot.temporary = false;
        }

        activeSquadron = squadron;
        owner.SetSquadron(squadron);
        squadron.InvalidateShipCaches();

        try
        {
            squadron.Build(true);
        }
        catch (System.Exception ex)
        {
            Debug.LogError("[Orrery] Satellite squadron build failed: " + ex);
            TearDownCurrentBuild();
            return false;
        }

        publishEntries.Clear();
        for (int i = 1; i <= satelliteCount; i++)
        {
            GameShip satellite = squadron.ships[i].ship;
            if (satellite == null)
            {
                Debug.LogError("[Orrery] Satellite failed to spawn at slot " + i + ".");
                TearDownCurrentBuild();
                return false;
            }

            ConfigureSatellite(satellite, owner);
            OrrerySatellites.IntentEntry intent = intentEntries[i - 1];
            publishEntries.Add(new OrrerySatellites.PublishEntry(
                satellite,
                intent.SatelliteId,
                intent.Kind,
                intent.OrbitRadiusMeters,
                intent.AngularSpeedDegreesPerSecond));
        }

        if (!OrrerySatellites.PublishLiveSatellites(owner, publishEntries))
        {
            Debug.LogError("[Orrery] Canonical satellite publication rejected completed physical build.");
            TearDownCurrentBuild();
            return false;
        }

        OrreryOrbit.GetOrCreate(owner);
        ApplyDesiredOrbit(true);

        Debug.Log("[Orrery] Built " + satelliteCount +
            " baseline formula satellites for local owner.");
        return true;
    }

    private void ApplyDesiredOrbit(bool snap)
    {
        OrrerySatellites.SatelliteSnapshot snapshot =
            OrrerySatellites.GetSnapshot(currentOwner);
        if (snapshot == null || currentOwner == null)
            return;

        Rigidbody2D ownerBody = currentOwner.GetRigidBody();
        Vector2 ownerVelocity = ownerBody == null
            ? Vector2.zero
            : ownerBody.velocity;

        for (int i = 0; i < snapshot.Count; i++)
        {
            OrrerySatellites.SatelliteContext context = snapshot.Get(i);
            if (context == null || !context.IsValid || context.Ship == null)
                continue;

            Vector3 desiredPosition;
            float angle;
            float radius;
            if (!OrreryOrbit.TryGetDesiredPose(
                    currentOwner,
                    context.SatelliteId,
                    out desiredPosition,
                    out angle,
                    out radius))
            {
                continue;
            }

            GameShip satellite = context.Ship;
            satellite.transform.rotation = Quaternion.identity;

            if (snap)
            {
                Vector3 position = satellite.transform.position;
                satellite.transform.position = new Vector3(
                    desiredPosition.x,
                    desiredPosition.y,
                    position.z);
                Rigidbody2D snapBody = satellite.GetRigidBody();
                if (snapBody != null)
                    snapBody.velocity = ownerVelocity;
                continue;
            }

            Rigidbody2D body = satellite.GetRigidBody();
            if (body == null)
                continue;

            Vector2 error = (Vector2)desiredPosition -
                (Vector2)satellite.transform.position;

            // This is the allocation-free fixed-step form of Star Vortex's
            // verified AIShip.FollowParentVelocity correction when deltaTime is
            // Time.fixedDeltaTime.
            body.velocity = ownerVelocity +
                error * AIController.followerCorrectionStrength;
        }
    }

    private void TearDownCurrentBuild()
    {
        GameShip owner = currentOwner;
        if (owner != null)
        {
            OrrerySpellRuntime.Forget(owner);
            OrreryCasting.Cancel(owner);
            OrrerySatellites.InvalidateLiveSatellites(owner);
            OrrerySatellites.InvalidateIntent(owner);
            OrreryOrbit.Forget(owner);
        }

        Squadron oldSquadron = activeSquadron;
        if (oldSquadron != null)
        {
            for (int i = oldSquadron.ships.Count - 1; i >= 1; i--)
            {
                GameShip satellite = oldSquadron.ships[i].ship;
                if (satellite == null)
                    continue;

                satellite.grantXp = false;
                satellite.lootTables = new LootTable[0];

                if (ReferenceEquals(satellite.squadron, oldSquadron))
                    satellite.SetSquadron(null);

                if (satellite.gameObject == null)
                    continue;

                satellite.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(satellite.gameObject);
            }

            if (owner != null && ReferenceEquals(owner.squadron, oldSquadron))
                owner.SetSquadron(null);
        }

        activeSquadron = null;
        intentEntries.Clear();
        publishEntries.Clear();
    }

    private static void ConfigureSatellite(GameShip satellite, GameShip owner)
    {
        satellite.faction = owner.faction;
        satellite.disableMinibars = true;
        satellite.grantXp = false;
        satellite.lootTables = new LootTable[0];
        satellite.SetTurnMode(GameShip.TurnMode.Independent);
        satellite.SetTarget(null);
        satellite.StopActivating(null);

        if (satellite.originalShip != null)
        {
            satellite.originalShip.faction = owner.faction;
            satellite.originalShip.disableMinibars = true;
            satellite.originalShip.grantXp = false;
        }

        satellite.CheckAttachMinibars();

        AIShip ai = AIController.instance == null
            ? null
            : AIController.instance.GetAIShip(satellite);
        if (ai != null)
            ai.SetTargetShip(null, false);
    }

    private static SquadronBase FindSpawnCarrier()
    {
        SquadronBase[] bases = Resources.FindObjectsOfTypeAll<SquadronBase>();
        for (int i = 0; i < bases.Length; i++)
        {
            SquadronBase candidate = bases[i];
            if (candidate != null && candidate.name == SpawnCarrierName)
                return candidate;
        }
        return null;
    }
}

/// <summary>
/// Bootstrap the class-specific physical controller independently from the
/// Leviathan controller. Core class activation remains the authority for whether
/// it does any work.
/// </summary>
[HarmonyPatch(typeof(WorldController), "PostInit")]
public static class OrreryControllerBootstrapPatch
{
    public static void Postfix()
    {
        if (OrreryController.Instance != null)
            return;

        GameObject runtime = new GameObject("Orrery Runtime");
        runtime.AddComponent<OrreryController>();
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class OrreryControllerWorldDestroyedPatch
{
    public static void Prefix()
    {
        if (OrreryController.Instance != null)
            OrreryController.Instance.ResetWorld();
    }
}

/// <summary>
/// Formula satellites are physical and hittable, but excluded from ordinary
/// target-selection APIs. Direct/projectile collision damage remains native.
/// </summary>
[HarmonyPatch(typeof(GameShip), "CanBeTargetedBy")]
public static class OrrerySatelliteTargetingPatch
{
    public static bool Prefix(GameShip __instance, ref bool __result)
    {
        GameShip owner;
        OrrerySatellites.SatelliteContext context;
        if (!OrrerySatellites.TryGetSatelliteContext(__instance, out owner, out context))
            return true;

        __result = false;
        return false;
    }
}

/// <summary>
/// Native OrbitAIShip is retained as the lifecycle/formation AI type, but Orrery
/// owns its movement and satellites have no autonomous weapon behavior.
/// </summary>
[HarmonyPatch(typeof(OrbitAIShip), "RunGambits")]
public static class OrrerySatelliteOrbitAiPatch
{
    public static bool Prefix(OrbitAIShip __instance)
    {
        GameShip ship = __instance == null ? null : __instance.GetGameShip();
        GameShip owner;
        OrrerySatellites.SatelliteContext context;
        return ship == null ||
            !OrrerySatellites.TryGetSatelliteContext(ship, out owner, out context);
    }
}
