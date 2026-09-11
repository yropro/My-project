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

    public static event Action<CoreClassId, CoreClassId> LocalClassChanged;

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
        Func<Pilot, bool> resolver)
    {
        if (classId == CoreClassId.None)
            throw new ArgumentException("None cannot own a class resolver.", "classId");
        if (resolver == null)
            throw new ArgumentNullException("resolver");

        resolvers[classId] = resolver;
        Refresh();
    }

    public static void UnregisterLocalClass(CoreClassId classId)
    {
        if (classId == CoreClassId.None)
            return;

        if (resolvers.Remove(classId))
            Refresh();
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

        Pilot nextPilot = current ?? mutationPilot;
        CoreClassId nextClass = ResolveClass(nextPilot);
        bool pilotChanged = !ReferenceEquals(activeLocalPilot, nextPilot);
        bool classChanged = nextClass != activeLocalClass;

        if (initialized && !pilotChanged && !classChanged)
            return;

        CoreClassId previousClass = activeLocalClass;

        activeLocalPilot = nextPilot;
        activeLocalClass = nextClass;
        initialized = true;

        unchecked
        {
            contextRevision++;
            if (classChanged)
                transitionRevision++;
        }

        if (classChanged)
        {
            Action<CoreClassId, CoreClassId> handler = LocalClassChanged;
            if (handler != null)
                handler(previousClass, nextClass);
        }
    }

    public static void Reset()
    {
        CoreClassId previousClass = activeLocalClass;
        bool hadContext = initialized || activeLocalPilot != null ||
            previousClass != CoreClassId.None;

        activeLocalPilot = null;
        activeLocalClass = CoreClassId.None;
        initialized = false;
        warnedClassConflict = false;

        if (!hadContext)
            return;

        unchecked
        {
            contextRevision++;
            if (previousClass != CoreClassId.None)
                transitionRevision++;
        }

        if (previousClass != CoreClassId.None)
        {
            Action<CoreClassId, CoreClassId> handler = LocalClassChanged;
            if (handler != null)
                handler(previousClass, CoreClassId.None);
        }
    }

    private static void EnsureInitialized()
    {
        if (!initialized)
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
        CoreClassRuntime.Refresh();
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
