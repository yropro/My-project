using System.Collections.Generic;
using StarVortex;
using UnityEngine;

/// <summary>
/// Distance-scaled time dilation ("Temporal Dive").
///
/// ---------------------------------------------------------------------------
/// WHAT THIS IS
/// ---------------------------------------------------------------------------
/// Vanilla TemporalDrive is not a field. It sets a global Time.timeScale via
/// WorldController.SetTimeScale and then exempts one ship by giving it a
/// speedScale of 1/factor. Time.timeScale is a single number, so it cannot
/// express falloff.
///
/// This class builds the per-entity version. Time.timeScale stays at 1 and each
/// affected object is slowed individually by its distance from a dive owner.
///
/// There is exactly one source of truth: the `active` list below. Ships,
/// launchers, AI and projectiles all ask GetFactor(...) rather than holding
/// their own copy of the field. That is why this is a registry and not a
/// StatusEffect: GameShip.AddStatusEffect returns early for drones
/// (GameShip.cs:2169) and UpdateStatusEffects is only called for non-drones
/// (GameShip.cs:846), so a status-based field silently misses every drone
/// enemy. It also would not have covered projectiles or launchers at all.
///
/// ---------------------------------------------------------------------------
/// WHAT GETS SLOWED
/// ---------------------------------------------------------------------------
///   movement, turning, drag, speed cap   GameShip.speedScale, applied here
///   AI aim / dodge / decision cadence    AIShip.deltaTime, patched
///   weapon reload                        Launcher.UpdateReload, patched
///   projectile speed and collision       Projectile rigidbody velocity, patched
///   projectile lifetime and range        AutoDestroy.timeRemaining, patched
///
/// Not covered, and honestly so: missile self-acceleration and turn rate,
/// homing delay, mine arming, beam tick rate. Those live in subclass update
/// methods that recompute velocity themselves and will overwrite the ratio
/// applied here. See LeviathanTemporalPatches for where to extend.
///
/// ---------------------------------------------------------------------------
/// AUTHORITY
/// ---------------------------------------------------------------------------
/// One rule covers co-op: only touch objects this machine actually simulates.
///
///   GameShip.IsNetRemote()      true  -> skip, someone else owns it
///   Projectile.netRendered      true  -> skip, remote render of a real one
///
/// NPCs are simulated by the star authority (StarAuthorityController streams
/// them via SendEntityStates and clients drive reps with RemoteShipDriver), so
/// on the host the slowdown replicates through the normal entity stream with no
/// protocol work. Player ships are simulated by their owning client.
///
/// The dive OWNER may be remote. We only read its transform position, which is
/// replicated at 20 Hz, so the host can evaluate your field from your ship
/// even though it does not simulate you.
///
/// ---------------------------------------------------------------------------
/// THE COMPOUNDING TRAP
/// ---------------------------------------------------------------------------
/// GameShip.SetTimeScale (GameShip.cs:4702) writes absolute state:
/// `rigidBody.velocity *= speedScale`, using the NEW speedScale, not the
/// change. Calling it every frame drives velocity to zero. ApplyToShip below
/// calls it (for the drag and squared-value bookkeeping it does for free) and
/// then immediately corrects velocity to a ratio change instead.
/// </summary>
public static class LeviathanTemporalField
{
    // =========================================================================
    // TUNING DEFAULTS
    // =========================================================================

    /// <summary>
    /// Full slow inside this radius, in world units. Star.playAreaSize defaults
    /// to 100 and sandbox spans 50-300, so radii are small numbers here. An
    /// outer radius of 180 would blanket an entire default star and you would
    /// never see falloff.
    /// </summary>
    public const float DefaultInnerRadius = 10f;

    /// <summary>Field reaches 1.0x here. Keep it near the visible screen.</summary>
    public const float DefaultOuterRadius = 160f;

    /// <summary>Strongest slowdown at the centre. 0.25 = 75% slower.</summary>
    public const float DefaultEnemyFloor = 0.01f;

    /// <summary>1.0 leaves teammates untouched. Lower it to slow them too.</summary>
    public const float DefaultAllyFloor = 1f;

    /// <summary>
    /// Curve shaping applied on top of SmoothStep. Above 1 holds the deep slow
    /// further out; below 1 recovers faster. 1.0 is plain SmoothStep.
    /// </summary>
    public const float DefaultExponent = 1f;

    /// <summary>
    /// How fast a ship's applied factor chases its target, per second. Without
    /// this a ship hovering on the boundary stutters, and the velocity ratio
    /// math thrashes.
    /// </summary>
    public const float SmoothingRatePerSecond = 3f;

    /// <summary>Never let the engine see a factor at or below zero.</summary>
    public const float MinimumFactor = 0.05f;

    /// <summary>Treat anything above this as "not slowed" and release the ship.</summary>
    private const float RestoreThreshold = 0.999f;

    // =========================================================================
    // STATE
    // =========================================================================

    public sealed class Dive
    {
        public GameShip Owner;
        public float Remaining;
        public float InnerRadius;
        public float OuterRadius;
        public float EnemyFloor;
        public float AllyFloor;
        public float Exponent;

        // Cached so the common "too far away" case costs one subtraction and
        // one dot product, with no sqrt.
        public float OuterRadiusSqr;
    }

    private static readonly List<Dive> active = new List<Dive>(4);

    /// <summary>
    /// Smoothed factor currently applied to each ship. A ship is only in here
    /// while it is actually slowed; reaching 1.0 removes it.
    /// </summary>
    private static readonly Dictionary<GameShip, float> shipFactors =
        new Dictionary<GameShip, float>(128);

    private static readonly List<GameShip> releaseScratch = new List<GameShip>(32);

    private static LeviathanTemporalDriver driver;

    // =========================================================================
    // PUBLIC QUERY
    // =========================================================================

    /// <summary>
    /// Fast out for every hot-path patch. When no dive is running, every patch
    /// in LeviathanTemporalPatches costs one static bool read.
    /// </summary>
    public static bool AnyActive
    {
        get { return active.Count > 0; }
    }

    /// <summary>
    /// The smoothed factor currently applied to a ship. This is what the AI and
    /// launcher patches read, so their slowdown matches the ship's visible
    /// movement rather than snapping a frame early.
    /// </summary>
    public static float GetShipFactor(GameShip ship)
    {
        float factor;
        if (ship && shipFactors.TryGetValue(ship, out factor))
        {
            return factor;
        }

        return 1f;
    }

    /// <summary>
    /// Raw, unsmoothed field strength at a point for an object belonging to
    /// `subjectOwner`. Used for projectiles, which have no inertia in factor
    /// space and should respond immediately when they cross the boundary.
    ///
    /// Returns the STRONGEST slowdown across all dives (minimum factor) rather
    /// than the product, matching how vanilla combines overlapping temporal
    /// windows in NetWorldBridge.RecomputeTemporalWindow (line 992).
    /// </summary>
    public static float GetFactor(Vector2 position, GameShip subjectOwner)
    {
        if (active.Count == 0)
        {
            return 1f;
        }

        float result = 1f;

        for (int i = 0; i < active.Count; i++)
        {
            Dive dive = active[i];
            if (dive.Owner == null || !dive.Owner)
            {
                continue;
            }

            float factor = EvaluateDive(dive, position, subjectOwner);
            if (factor < result)
            {
                result = factor;
            }
        }

        return Mathf.Max(MinimumFactor, result);
    }

    // =========================================================================
    // ACTIVATION
    // =========================================================================

    /// <summary>
    /// Start or refresh a dive. Call this from whatever replaces the vanilla
    /// TemporalDrive activation. One dive per owner; re-activating replaces it.
    ///
    /// Do NOT also call NetSession.RequestTemporalDilation, or the vanilla
    /// global window will fight this.
    /// </summary>
    public static void Activate(
        GameShip owner,
        float duration,
        float innerRadius = DefaultInnerRadius,
        float outerRadius = DefaultOuterRadius,
        float enemyFloor = DefaultEnemyFloor,
        float allyFloor = DefaultAllyFloor,
        float exponent = DefaultExponent)
    {
        if (!owner || duration <= 0f)
        {
            return;
        }

        EnsureDriver();

        Dive dive = Find(owner);
        if (dive == null)
        {
            dive = new Dive();
            dive.Owner = owner;
            active.Add(dive);
        }

        dive.Remaining = duration;
        dive.InnerRadius = Mathf.Max(0f, innerRadius);
        dive.OuterRadius = Mathf.Max(dive.InnerRadius + 0.01f, outerRadius);
        dive.EnemyFloor = Mathf.Clamp(enemyFloor, MinimumFactor, 1f);
        dive.AllyFloor = Mathf.Clamp(allyFloor, MinimumFactor, 1f);
        dive.Exponent = Mathf.Max(0.01f, exponent);
        dive.OuterRadiusSqr = dive.OuterRadius * dive.OuterRadius;
    }

    /// <summary>End a dive early. Ships ease back to 1.0 rather than snapping.</summary>
    public static void Cancel(GameShip owner)
    {
        for (int i = active.Count - 1; i >= 0; i--)
        {
            if (active[i].Owner == owner)
            {
                active.RemoveAt(i);
            }
        }
    }

    public static bool IsActive(GameShip owner)
    {
        return Find(owner) != null;
    }

    /// <summary>
    /// Drop everything and restore every ship immediately. Call on star change,
    /// session teardown, and mod unload. Leaving a ship in the dictionary with
    /// a speedScale below 1 would leave it permanently slow.
    /// </summary>
    public static void Reset()
    {
        active.Clear();

        releaseScratch.Clear();
        foreach (KeyValuePair<GameShip, float> pair in shipFactors)
        {
            releaseScratch.Add(pair.Key);
        }

        for (int i = 0; i < releaseScratch.Count; i++)
        {
            GameShip ship = releaseScratch[i];
            if (ship && !ship.isBeingDestroyed)
            {
                RestoreShip(ship, GetShipFactor(ship));
            }
        }

        releaseScratch.Clear();
        shipFactors.Clear();
    }

    // =========================================================================
    // SIMULATION STEP
    // =========================================================================

    /// <summary>
    /// Driven from LeviathanTemporalDriver.FixedUpdate. Ages the dives, then
    /// walks every ship once. The walk is unconditional while a ship is still
    /// slowed so it can ease back to 1.0 after the dive ends.
    /// </summary>
    internal static void Step(float deltaTime)
    {
        for (int i = active.Count - 1; i >= 0; i--)
        {
            Dive dive = active[i];
            dive.Remaining -= deltaTime;

            if (dive.Remaining <= 0f || dive.Owner == null || !dive.Owner)
            {
                active.RemoveAt(i);
            }
        }

        if (active.Count == 0 && shipFactors.Count == 0)
        {
            return;
        }

        List<GameShip> ships = WorldController.instance
            ? WorldController.instance.GetGameShips()
            : null;

        if (ships != null)
        {
            for (int i = 0; i < ships.Count; i++)
            {
                GameShip ship = ships[i];
                if (!ship || ship.isBeingDestroyed || ship.IsNetRemote())
                {
                    continue;
                }

                float target = GetFactor(ship.transform.position, ship);
                ApplyToShip(ship, target, deltaTime);
            }
        }

        PruneDeadShips();
    }

    // =========================================================================
    // INTERNALS
    // =========================================================================

    private static Dive Find(GameShip owner)
    {
        for (int i = 0; i < active.Count; i++)
        {
            if (active[i].Owner == owner)
            {
                return active[i];
            }
        }

        return null;
    }

    /// <summary>
    /// The falloff curve. SmoothStep rather than a raw exponential because an
    /// exponential never actually reaches 1.0, so it needs a hard cutoff and
    /// pops at the boundary. SmoothStep is flat at both ends by construction.
    /// </summary>
    private static float EvaluateDive(Dive dive, Vector2 position, GameShip subjectOwner)
    {
        // The activator is never slowed by their own dive.
        if (subjectOwner == dive.Owner)
        {
            return 1f;
        }

        bool ally = subjectOwner
            && dive.Owner
            && !string.IsNullOrEmpty(subjectOwner.faction)
            && subjectOwner.faction == dive.Owner.faction;

        float floor = ally ? dive.AllyFloor : dive.EnemyFloor;
        if (floor >= 1f)
        {
            return 1f;
        }

        Vector2 delta = (Vector2)dive.Owner.transform.position - position;
        float distanceSqr = delta.sqrMagnitude;

        if (distanceSqr >= dive.OuterRadiusSqr)
        {
            return 1f;
        }

        float distance = Mathf.Sqrt(distanceSqr);
        if (distance <= dive.InnerRadius)
        {
            return floor;
        }

        float t = (distance - dive.InnerRadius) / (dive.OuterRadius - dive.InnerRadius);
        t = Mathf.SmoothStep(0f, 1f, t);

        if (dive.Exponent != 1f)
        {
            t = Mathf.Pow(t, dive.Exponent);
        }

        return Mathf.Lerp(floor, 1f, t);
    }

    private static void ApplyToShip(GameShip ship, float target, float deltaTime)
    {
        float current;
        if (!shipFactors.TryGetValue(ship, out current))
        {
            current = 1f;

            // Nothing to do and nothing stored: stay out of the dictionary.
            if (target >= RestoreThreshold)
            {
                return;
            }
        }

        float next = Mathf.MoveTowards(current, target, SmoothingRatePerSecond * deltaTime);
        if (next == current)
        {
            return;
        }

        WriteShipTimeScale(ship, current, next);

        if (next >= RestoreThreshold)
        {
            shipFactors.Remove(ship);
        }
        else
        {
            shipFactors[ship] = next;
        }
    }

    private static void RestoreShip(GameShip ship, float current)
    {
        if (current >= RestoreThreshold)
        {
            return;
        }

        WriteShipTimeScale(ship, current, 1f);
    }

    /// <summary>
    /// The one place that touches native ship time.
    ///
    /// SetTimeScale is called for its side effects: it sets speedScale, rebuilds
    /// drag from initialDrag (which is private, so this avoids reflection) and
    /// calls GenerateSquaredValues to refresh speedScaleSquared. Its velocity
    /// write is absolute, so we capture velocity first and put back the RATIO.
    ///
    /// Note SetTimeScale takes the reciprocal: passing 1/next yields
    /// speedScale == next. speedScale below 1 slows the ship, which is the
    /// opposite of every vanilla caller, all of which pass values above 1 to
    /// exempt a ship from a global slowdown.
    /// </summary>
    private static void WriteShipTimeScale(GameShip ship, float current, float next)
    {
        Rigidbody2D body = ship.GetRigidBody();
        Vector2 velocityBefore = body ? body.velocity : Vector2.zero;

        ship.SetTimeScale(1f / Mathf.Max(MinimumFactor, next));

        if (body)
        {
            body.velocity = velocityBefore * (next / Mathf.Max(MinimumFactor, current));
        }
    }

    private static void PruneDeadShips()
    {
        if (shipFactors.Count == 0)
        {
            return;
        }

        releaseScratch.Clear();

        foreach (KeyValuePair<GameShip, float> pair in shipFactors)
        {
            GameShip ship = pair.Key;
            if (!ship || ship.isBeingDestroyed || ship.health <= 0f)
            {
                releaseScratch.Add(ship);
            }
        }

        for (int i = 0; i < releaseScratch.Count; i++)
        {
            shipFactors.Remove(releaseScratch[i]);
        }

        releaseScratch.Clear();
    }

    private static void EnsureDriver()
    {
        if (driver)
        {
            return;
        }

        GameObject host = new GameObject("LeviathanTemporalDriver");
        Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        driver = host.AddComponent<LeviathanTemporalDriver>();
    }
}

/// <summary>
/// Pumps the field from FixedUpdate so physics writes land on the physics step.
/// Nothing else reads ordering: every consumer queries the registry directly,
/// so Unity's undefined execution order between this and AIController does not
/// matter.
/// </summary>
public class LeviathanTemporalDriver : MonoBehaviour
{
    private void FixedUpdate()
    {
        // Inside FixedUpdate, Time.deltaTime returns fixedDeltaTime.
        LeviathanTemporalField.Step(Time.deltaTime);
    }
}
