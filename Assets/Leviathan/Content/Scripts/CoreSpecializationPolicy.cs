using StarVortex;
using System;
using System.Collections.Generic;

/// <summary>
/// Persistence adapter owned by a specialization domain. Core stores the shared
/// tree state shape; each class chooses its own persistence namespace/format.
/// </summary>
public interface ICoreSpecializationPersistence
{
    bool Load(
        Pilot pilot,
        Dictionary<string, CoreSpecializationState> states,
        out string reason);

    bool Save(
        Pilot pilot,
        Dictionary<string, CoreSpecializationState> states,
        out string reason);
}

/// <summary>
/// Class-owned policy plugged into the shared specialization engine.
///
/// The graph/runtime/network representation is Core-owned. Currency,
/// persistence, native prerequisite registration and compiled tree catalog are
/// deliberately class-owned so unrelated standalone classes can coexist in one
/// multiplayer session without inheriting Leviathan assumptions.
/// </summary>
public interface ICoreSpecializationPolicy
{
    CoreClassId ClassId { get; }
    string Id { get; }
    string CurrencyName { get; }
    ICoreSpecializationPointBank PointBank { get; }
    ICoreSpecializationPersistence Persistence { get; }

    void EnsurePrerequisitesRegistered();
    void RegisterTrees();
    int GetProgressionRank(Pilot pilot);
}

/// <summary>
/// Process-lifetime registry of class specialization domains.
///
/// Tree ids remain globally unique because the network schema is shared by all
/// peers. Each tree is also tagged with an owning CoreClassId so point banks,
/// persistence and future UI can resolve policy without global "current class"
/// state. This is important when different players use different custom classes.
/// </summary>
public static class CoreSpecializationPolicies
{
    private static readonly Dictionary<CoreClassId, ICoreSpecializationPolicy> policies =
        new Dictionary<CoreClassId, ICoreSpecializationPolicy>();

    private static readonly List<ICoreSpecializationPolicy> ordered =
        new List<ICoreSpecializationPolicy>();

    public static void Register(ICoreSpecializationPolicy policy)
    {
        if (policy == null)
            throw new ArgumentNullException("policy");
        if (policy.ClassId == CoreClassId.None)
            throw new InvalidOperationException(
                "A specialization policy must own a concrete CoreClassId.");

        ICoreSpecializationPolicy previous;
        if (policies.TryGetValue(policy.ClassId, out previous))
            ordered.Remove(previous);

        policies[policy.ClassId] = policy;
        ordered.Add(policy);
        ordered.Sort(delegate (
            ICoreSpecializationPolicy a,
            ICoreSpecializationPolicy b)
        {
            return ((byte)a.ClassId).CompareTo((byte)b.ClassId);
        });

        policy.EnsurePrerequisitesRegistered();
        policy.RegisterTrees();
    }

    public static ICoreSpecializationPolicy Get(CoreClassId classId)
    {
        ICoreSpecializationPolicy policy;
        return classId != CoreClassId.None &&
            policies.TryGetValue(classId, out policy)
                ? policy
                : null;
    }

    public static ICoreSpecializationPolicy GetForTree(string treeId)
    {
        CoreClassId owner = CoreSpecializationRegistry.GetOwnerClass(treeId);
        return Get(owner);
    }

    /// <summary>
    /// Resolves the active Pilot's class. Before a class has been activated,
    /// returning the sole registered policy preserves class-selection UIs. Once
    /// multiple class policies exist, callers with a tree should use GetForTree
    /// rather than relying on this deliberately conservative fallback.
    /// </summary>
    public static ICoreSpecializationPolicy GetForPilotOrSingle(Pilot pilot)
    {
        CoreClassId classId = CoreClassRuntime.ResolveClass(pilot);
        ICoreSpecializationPolicy policy = Get(classId);
        if (policy != null)
            return policy;

        return ordered.Count == 1 ? ordered[0] : null;
    }

    public static IList<ICoreSpecializationPolicy> All()
    {
        return ordered.AsReadOnly();
    }

    public static void EnsureRegistered()
    {
        for (int i = 0; i < ordered.Count; i++)
            ordered[i].EnsurePrerequisitesRegistered();
    }

    public static void RebuildAllTrees()
    {
        CoreSpecializationRegistry.Clear();
        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].EnsurePrerequisitesRegistered();
            ordered[i].RegisterTrees();
        }
    }

    public static void RegisterTree(
        CoreClassId ownerClass,
        CoreSpecializationTree tree)
    {
        if (Get(ownerClass) == null)
        {
            throw new InvalidOperationException(
                "Cannot register specialization tree for unregistered class " +
                ownerClass + ".");
        }

        CoreSpecializationRegistry.Register(ownerClass, tree);
    }

    public static bool LoadAll(
        Pilot pilot,
        Dictionary<string, CoreSpecializationState> states,
        out string reason)
    {
        reason = string.Empty;

        for (int i = 0; i < ordered.Count; i++)
        {
            ICoreSpecializationPersistence persistence = ordered[i].Persistence;
            if (persistence == null)
                continue;

            string localReason;
            if (!persistence.Load(pilot, states, out localReason))
            {
                reason = localReason;
                return false;
            }
        }

        return true;
    }

    public static bool SaveAll(
        Pilot pilot,
        Dictionary<string, CoreSpecializationState> states,
        out string reason)
    {
        reason = string.Empty;

        for (int i = 0; i < ordered.Count; i++)
        {
            ICoreSpecializationPersistence persistence = ordered[i].Persistence;
            if (persistence == null)
                continue;

            string localReason;
            if (!persistence.Save(pilot, states, out localReason))
            {
                reason = localReason;
                return false;
            }
        }

        return true;
    }

    public static bool SaveTreeOwner(
        string treeId,
        Pilot pilot,
        Dictionary<string, CoreSpecializationState> states,
        out string reason)
    {
        reason = string.Empty;
        ICoreSpecializationPolicy policy = GetForTree(treeId);
        if (policy == null)
        {
            reason = "Specialization tree has no registered class policy.";
            return false;
        }

        return policy.Persistence == null ||
            policy.Persistence.Save(pilot, states, out reason);
    }
}
