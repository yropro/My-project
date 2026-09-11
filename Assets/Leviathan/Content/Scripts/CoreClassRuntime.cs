using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Stable ids for standalone custom classes that participate in shared Core
/// infrastructure. Values are append-only once published.
/// </summary>
public enum CoreClassId : byte
{
    None = 0,
    Leviathan = 1,
    Orrery = 2
}

/// <summary>
/// Shared standalone-class identity and local lifecycle boundary.
/// Individual classes own the predicate that defines membership. Core observes
/// native Pilot/ship mutation boundaries and requires at most one active class
/// per Pilot.
/// </summary>
public static class CoreClassRuntime
{
    private static readonly Dictionary<CoreClassId, Func<Pilot, bool>> resolvers =
        new Dictionary<CoreClassId, Func<Pilot, bool>>();

    private static CoreClassId activeLocalClass = CoreClassId.None;
    private static Pilot activeLocalPilot;
    private static bool initialized;
    private static int transitionRevision;
    private static int contextRevision;
    private static bool warnedClassConflict;

    private static readonly Dictionary<CoreClassId, CoreClassLifecycle> lifecycles =
        new Dictionary<CoreClassId, CoreClassLifecycle>();
    private static CoreOwnerContext context;
    private static ulong generation;
    private static bool transitioning;
    private static bool worldExiting;
    public static CoreOwnerContext CurrentContext { get { EnsureInitialized(); return context; } }


    public static CoreClassId ActiveLocalClass
    {
        get
        {
            EnsureInitialized();
            return activeLocalClass;
        }
    }

    public static Pilot ActiveLocalPilot
    {
        get
        {
            EnsureInitialized();
            return activeLocalPilot;
        }
    }

    public static bool HasActiveLocalClass
    {
        get { return ActiveLocalClass != CoreClassId.None; }
    }

    public static int TransitionRevision
    {
        get
        {
            EnsureInitialized();
            return transitionRevision;
        }
    }

    public static int ContextRevision
    {
        get
        {
            EnsureInitialized();
            return contextRevision;
        }
    }

    public static void RegisterLocalClass(
        CoreClassId classId,
        Func<Pilot, bool> resolver, CoreClassLifecycle lifecycle = null)
    {
        if (classId == CoreClassId.None)
            throw new ArgumentException("None cannot own a class resolver.", "classId");
        if (resolver == null)
            throw new ArgumentNullException("resolver");

        if (resolvers.ContainsKey(classId))
            throw new InvalidOperationException("Duplicate class resolver: " + classId);
        if (CoreSpecializationPolicies.Get(classId) == null)
            throw new InvalidOperationException("Register the class catalog before its resolver.");
        resolvers.Add(classId, resolver);
        if (lifecycle != null) lifecycles.Add(classId, lifecycle);
    }

    public static void UnregisterLocalClass(CoreClassId classId)
    {
        if (classId == CoreClassId.None)
            return;

        if (resolvers.Remove(classId))
        {
            Refresh();
            lifecycles.Remove(classId);
        }
    }

    public static bool IsLocalClassActive(CoreClassId classId)
    {
        return classId != CoreClassId.None && ActiveLocalClass == classId;
    }

    public static bool IsCurrentLocalPilot(Pilot pilot)
    {
        return pilot != null && ReferenceEquals(ActiveLocalPilot, pilot);
    }

    /// <summary>
    /// Resolve class membership for any Pilot without local-player side effects.
    /// More than one active standalone class is invalid and resolves to None;
    /// Core never silently chooses a winner.
    /// </summary>
    public static CoreClassId ResolveClass(Pilot pilot)
    {
        if (pilot == null || resolvers.Count == 0)
            return CoreClassId.None;

        CoreClassId selected = CoreClassId.None;
        int matches = 0;

        foreach (KeyValuePair<CoreClassId, Func<Pilot, bool>> pair in resolvers)
        {
            bool active = false;
            try
            {
                active = pair.Value != null && pair.Value(pilot);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[CoreClassRuntime] Class resolver " + pair.Key +
                    " threw: " + ex.Message);
            }

            if (!active)
                continue;

            matches++;
            selected = pair.Key;

            if (matches > 1)
                break;
        }

        if (matches > 1)
        {
            if (!warnedClassConflict)
            {
                warnedClassConflict = true;
                Debug.LogError(
                    "[CoreClassRuntime] Invalid Pilot state: multiple standalone " +
                    "custom classes are active simultaneously. Shared class " +
                    "runtime is disabled until the conflict is resolved.");
            }

            return CoreClassId.None;
        }

        warnedClassConflict = false;
        return selected;
    }

    public static void Refresh()
    {
        Refresh(ResolveCurrentPilot());
    }

    internal static void Refresh(Pilot mutationPilot)
    {
        Pilot current = ResolveCurrentPilot();

        if (current != null && mutationPilot != null &&
            !ReferenceEquals(current, mutationPilot))
        {
            return;
        }

        if (transitioning || worldExiting) return;
        Pilot nextPilot = current;
        GameShip ship = WorldController.instance == null ? null :
            WorldController.instance.GetCurrentPlayerShip();
        Transition(nextPilot, ship, ResolveClass(nextPilot));
    }

    internal static void Transition(Pilot pilot, GameShip ship, CoreClassId classId)
    {
        if (transitioning) return;
        if (initialized && ReferenceEquals(activeLocalPilot, pilot) &&
            activeLocalClass == classId && context != null && ReferenceEquals(context.Ship, ship)) return;
        transitioning = true;
        CoreOwnerContext old = context;
        CoreClassId previous = activeLocalClass;
        try
        {
            if (old != null) old.Invalidate();
            // Lifetime validity changes before cancellation callbacks run.
            CoreAbilityRuntime.CancelOwner(old);
            CoreClassEntities.ReleaseOwner(old);
            CoreClassLifecycle lifecycle;
            if (old != null && lifecycles.TryGetValue(old.ClassId, out lifecycle))
                InvokeSafely(lifecycle.Exit, old);
            CoreNetwork.ClearLocalSlots();
            activeLocalPilot = pilot;
            activeLocalClass = classId;
            context = new CoreOwnerContext(pilot, ship, classId, ++generation);
            initialized = true;
            unchecked { contextRevision++; if (previous != classId) transitionRevision++; }
            CoreSpecializationRuntime.InvalidateConfiguration();
            CoreNetwork.InvalidateLocalSpecialization();
            if (ship != null && classId != CoreClassId.None && lifecycles.TryGetValue(classId, out lifecycle))
                InvokeSafely(lifecycle.Enter, context);
        }
        finally { transitioning = false; }
    }

    private static void InvokeSafely(Action<CoreOwnerContext> callback, CoreOwnerContext value)
    {
        try { if (callback != null) callback(value); }
        catch (Exception ex) { Debug.LogError("[CoreClassRuntime] Lifecycle failed: " + ex); }
    }

    public static void Reset()
    {
        worldExiting = true;
        Transition(null, null, CoreClassId.None);
        warnedClassConflict = false;
    }

    internal static void WorldEntered()
    {
        worldExiting = false;
        Refresh();
    }

    private static void EnsureInitialized()
    {
        if (!initialized && !worldExiting)
            Refresh();
    }

    private static Pilot ResolveCurrentPilot()
    {
        if (WorldController.instance != null)
        {
            GameShip ship = WorldController.instance.GetCurrentPlayerShip();
            if (ship != null)
            {
                Pilot pilot = GameShip.GetPlayerSourcePilot(ship);
                if (pilot != null)
                    return pilot;
            }
        }

        if (Core.instance != null &&
            Core.instance.player != null &&
            Core.instance.player.ship != null)
        {
            return Core.instance.player.ship.pilot;
        }

        return null;
    }
}

[HarmonyPatch(typeof(Pilot), nameof(Pilot.SetUpgrade))]
public static class CoreClassRuntimeSetUpgradePatch
{
    public static void Postfix(Pilot __instance)
    {
        CoreClassRuntime.Refresh(__instance);
    }
}

[HarmonyPatch(typeof(Pilot), nameof(Pilot.ResetUpgrades))]
public static class CoreClassRuntimeResetUpgradesPatch
{
    public static void Postfix(Pilot __instance)
    {
        CoreClassRuntime.Refresh(__instance);
    }
}

[HarmonyPatch(typeof(WorldController), "PostInit")]
public static class CoreClassRuntimeWorldPostInitPatch
{
    public static void Postfix()
    {
        CoreClassRuntime.WorldEntered();
    }
}

[HarmonyPatch(typeof(WorldController), "SetCurrentPlayerShip")]
public static class CoreClassRuntimePlayerShipChangedPatch
{
    public static void Postfix()
    {
        CoreClassRuntime.Refresh();
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class CoreClassRuntimeWorldDestroyedPatch
{
    public static void Prefix()
    {
        CoreClassRuntime.Reset();
    }
}
