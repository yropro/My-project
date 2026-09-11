using StarVortex;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Class-owned presentation/progression metadata consumed by the generic
/// specialization engine. Core owns points math, persistence and tree state.
/// </summary>
public interface ICoreSpecializationPolicy
{
    CoreClassId ClassId { get; }
    string ProgressionName { get; }
    string PointCurrencyName { get; }

    void EnsurePrerequisitesRegistered();
    void RegisterTrees();
    int GetProgressionRank(Pilot pilot);
    int GetGrantedPoints(Pilot pilot);
}

/// <summary>
/// Process-lifetime registry of specialization domains. Tree ownership is
/// explicit and append-only at runtime so multiple custom classes can coexist
/// in one multiplayer session without sharing currency or persistence state.
/// </summary>
public static class CoreSpecializationPolicies
{
    private static readonly Dictionary<CoreClassId, ICoreSpecializationPolicy> policies =
        new Dictionary<CoreClassId, ICoreSpecializationPolicy>();

    private static readonly Dictionary<string, CoreClassId> treeOwners =
        new Dictionary<string, CoreClassId>(StringComparer.Ordinal);

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

    public static ICoreSpecializationPolicy GetForPilotOrSingle(Pilot pilot)
    {
        ICoreSpecializationPolicy policy = Get(CoreClassRuntime.ResolveClass(pilot));
        if (policy != null)
            return policy;

        // Needed by class-selection / authoring UI before the class root has
        // actually been purchased. Ambiguous once more than one policy exists.
        return ordered.Count == 1 ? ordered[0] : null;
    }

    public static CoreClassId GetOwnerClass(string treeId)
    {
        CoreClassId classId;
        return treeId != null && treeOwners.TryGetValue(treeId, out classId)
            ? classId
            : CoreClassId.None;
    }

    public static ICoreSpecializationPolicy GetForTree(string treeId)
    {
        return Get(GetOwnerClass(treeId));
    }

    public static void RegisterTree(
        CoreClassId ownerClass,
        CoreSpecializationTree tree)
    {
        if (ownerClass == CoreClassId.None)
            throw new InvalidOperationException("Tree owner class is required.");
        if (Get(ownerClass) == null)
            throw new InvalidOperationException(
                "Cannot register a tree for an unregistered class policy.");
        if (tree == null)
            throw new ArgumentNullException("tree");

        CoreClassId existing;
        if (treeOwners.TryGetValue(tree.Id, out existing) && existing != ownerClass)
        {
            throw new InvalidOperationException(
                "Specialization tree id '" + tree.Id +
                "' is already owned by class " + existing + ".");
        }

        treeOwners[tree.Id] = ownerClass;
        CoreSpecializationRegistry.Register(tree);
    }

    public static void EnsurePrerequisitesRegistered()
    {
        for (int i = 0; i < ordered.Count; i++)
            ordered[i].EnsurePrerequisitesRegistered();
    }

    public static void RebuildAllTrees()
    {
        CoreSpecializationRegistry.Clear();
        treeOwners.Clear();

        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].EnsurePrerequisitesRegistered();
            ordered[i].RegisterTrees();
        }
    }

    public static IList<ICoreSpecializationPolicy> All()
    {
        return ordered.AsReadOnly();
    }
}

/// <summary>
/// Generic point accounting. "Specialization Points" is the backend concept;
/// each policy supplies only progression math and player-facing terminology.
/// </summary>
public static class CoreSpecializationPoints
{
    public static bool IsAvailable(Pilot pilot, out string reason)
    {
        reason = string.Empty;
        if (pilot == null)
        {
            reason = "No Pilot.";
            return false;
        }

        ICoreSpecializationPolicy policy =
            CoreSpecializationPolicies.GetForPilotOrSingle(pilot);
        if (policy == null)
        {
            reason = "No specialization policy is available for this Pilot.";
            return false;
        }

        policy.EnsurePrerequisitesRegistered();
        return true;
    }

    public static int GetGrantedPoints(Pilot pilot)
    {
        ICoreSpecializationPolicy policy =
            CoreSpecializationPolicies.GetForPilotOrSingle(pilot);
        return policy == null ? 0 : Math.Max(0, policy.GetGrantedPoints(pilot));
    }

    public static int GetSpentPoints(Pilot pilot)
    {
        ICoreSpecializationPolicy policy =
            CoreSpecializationPolicies.GetForPilotOrSingle(pilot);
        return policy == null
            ? 0
            : CoreSpecializationRuntime.GetSpentPointsForClass(
                pilot,
                policy.ClassId);
    }

    public static int GetAvailablePoints(Pilot pilot)
    {
        return Math.Max(0, GetGrantedPoints(pilot) - GetSpentPoints(pilot));
    }

    public static int GetProgressionRank(Pilot pilot)
    {
        ICoreSpecializationPolicy policy =
            CoreSpecializationPolicies.GetForPilotOrSingle(pilot);
        return policy == null ? 0 : Math.Max(0, policy.GetProgressionRank(pilot));
    }

    public static string GetCurrencyName(Pilot pilot)
    {
        ICoreSpecializationPolicy policy =
            CoreSpecializationPolicies.GetForPilotOrSingle(pilot);
        return policy == null || string.IsNullOrEmpty(policy.PointCurrencyName)
            ? "Specialization Points"
            : policy.PointCurrencyName;
    }

    public static bool TrySpend(Pilot pilot, int amount, out string reason)
    {
        reason = string.Empty;
        if (amount <= 0)
            return true;
        if (!IsAvailable(pilot, out reason))
            return false;
        if (GetAvailablePoints(pilot) < amount)
        {
            reason = "Not enough " + GetCurrencyName(pilot) + ".";
            return false;
        }
        return true;
    }

    public static bool TryRefund(Pilot pilot, int amount, out string reason)
    {
        reason = string.Empty;
        return amount <= 0 || IsAvailable(pilot, out reason);
    }
}

/// <summary>
/// Versioned Core-owned specialization persistence. One file stores all class
/// domains for a Pilot, with every persisted row explicitly partitioned by
/// CoreClassId. Existing Leviathan v3 files are imported once when no Core file
/// exists and are left untouched as a rollback safety net.
/// </summary>
public static class CoreSpecializationPersistence
{
    private const int FormatVersion = 1;

    public static bool Load(
        Pilot pilot,
        Dictionary<string, CoreSpecializationState> states,
        out string reason)
    {
        reason = string.Empty;
        string path;
        if (!TryGetPath(pilot, out path, out reason))
            return false;

        if (File.Exists(path))
            return LoadCoreFile(path, states, out reason);

        bool imported;
        if (!TryImportLegacyLeviathan(pilot, states, out imported, out reason))
            return false;

        if (imported)
        {
            string saveReason;
            if (!Save(pilot, states, out saveReason))
            {
                reason = "Legacy specialization state loaded but Core migration " +
                    "could not be saved: " + saveReason;
                return false;
            }
        }

        return true;
    }

    public static bool Save(
        Pilot pilot,
        Dictionary<string, CoreSpecializationState> states,
        out string reason)
    {
        reason = string.Empty;
        string path;
        if (!TryGetPath(pilot, out path, out reason))
            return false;

        try
        {
            string folder = Path.GetDirectoryName(path);
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            List<string> lines = new List<string>();
            lines.Add("# Core specialization state v" + FormatVersion.ToString());
            lines.Add("# classId|treeId|nodeId|rank");

            foreach (KeyValuePair<string, CoreSpecializationState> pair in states)
            {
                CoreClassId owner =
                    CoreSpecializationPolicies.GetOwnerClass(pair.Key);
                if (owner == CoreClassId.None || pair.Value == null)
                    continue;

                CoreSpecializationTree tree = CoreSpecializationRegistry.Get(pair.Key);
                if (tree == null)
                    continue;

                IList<CoreSpecializationNode> nodes = tree.Nodes;
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (nodes[i].AutoGranted)
                        continue;

                    int rank = pair.Value.GetRank(nodes[i].Id);
                    if (rank <= 0)
                        continue;

                    lines.Add(
                        ((byte)owner).ToString() + "|" +
                        tree.Id + "|" + nodes[i].Id + "|" +
                        rank.ToString());
                }
            }

            File.WriteAllLines(path, lines.ToArray());
            return true;
        }
        catch (Exception ex)
        {
            reason = "Failed saving specialization state: " + ex.Message;
            Debug.LogError("[CoreSpecializations] " + reason);
            return false;
        }
    }

    private static bool LoadCoreFile(
        string path,
        Dictionary<string, CoreSpecializationState> states,
        out string reason)
    {
        reason = string.Empty;
        try
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                string[] parts = line.Split('|');
                if (parts.Length != 4)
                    continue;

                byte classByte;
                int rank;
                if (!byte.TryParse(parts[0], out classByte) ||
                    !int.TryParse(parts[3], out rank) || rank <= 0)
                {
                    continue;
                }

                CoreClassId owner = (CoreClassId)classByte;
                if (owner == CoreClassId.None ||
                    CoreSpecializationPolicies.GetOwnerClass(parts[1]) != owner)
                {
                    continue;
                }

                InstallRank(states, parts[1], parts[2], rank);
            }

            return true;
        }
        catch (Exception ex)
        {
            reason = "Failed loading specialization state: " + ex.Message;
            Debug.LogError("[CoreSpecializations] " + reason);
            return false;
        }
    }

    private static bool TryImportLegacyLeviathan(
        Pilot pilot,
        Dictionary<string, CoreSpecializationState> states,
        out bool imported,
        out string reason)
    {
        imported = false;
        reason = string.Empty;

        string legacyPath;
        if (!TryGetLegacyLeviathanPath(pilot, out legacyPath))
            return true;
        if (!File.Exists(legacyPath))
            return true;

        try
        {
            string[] lines = File.ReadAllLines(legacyPath);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                string[] parts = line.Split('|');
                int rank;
                if (parts.Length != 3 ||
                    !int.TryParse(parts[2], out rank) || rank <= 0)
                {
                    continue;
                }

                if (CoreSpecializationPolicies.GetOwnerClass(parts[0]) !=
                    CoreClassId.Leviathan)
                {
                    continue;
                }

                InstallRank(states, parts[0], parts[1], rank);
                imported = true;
            }

            if (imported)
            {
                Debug.Log(
                    "[CoreSpecializations] Imported legacy Leviathan " +
                    "specialization persistence into Core format.");
            }

            return true;
        }
        catch (Exception ex)
        {
            reason = "Failed importing legacy Leviathan specialization state: " +
                ex.Message;
            return false;
        }
    }

    private static void InstallRank(
        Dictionary<string, CoreSpecializationState> states,
        string treeId,
        string nodeId,
        int rank)
    {
        CoreSpecializationTree tree = CoreSpecializationRegistry.Get(treeId);
        if (tree == null)
            return;

        CoreSpecializationNode node = tree.GetNode(nodeId);
        if (node == null || node.AutoGranted)
            return;

        CoreSpecializationState state;
        if (!states.TryGetValue(tree.Id, out state))
        {
            state = new CoreSpecializationState();
            states.Add(tree.Id, state);
        }

        state.SetRank(node.Id, Math.Min(rank, node.MaxRank));
    }

    private static bool TryGetPath(
        Pilot pilot,
        out string path,
        out string reason)
    {
        path = null;
        reason = string.Empty;

        string stableId = ResolveStablePilotId(pilot);
        if (string.IsNullOrEmpty(stableId))
        {
            reason = "No stable current-player save UID/file was available; " +
                "specialization persistence is disabled.";
            return false;
        }

        string folder = Path.Combine(
            Application.persistentDataPath,
            "CoreSpecializations");
        path = Path.Combine(folder, Sanitize(stableId) + ".txt");
        return true;
    }

    private static bool TryGetLegacyLeviathanPath(
        Pilot pilot,
        out string path)
    {
        path = null;
        string stableId = ResolveStablePilotId(pilot);
        if (string.IsNullOrEmpty(stableId))
            return false;

        string folder = Path.Combine(
            Application.persistentDataPath,
            "LeviathanSpecializations");
        path = Path.Combine(folder, Sanitize(stableId) + ".txt");
        return true;
    }

    private static string ResolveStablePilotId(Pilot pilot)
    {
        if (pilot == null ||
            Core.instance == null ||
            Core.instance.player == null ||
            Core.instance.player.metaData == null)
        {
            return null;
        }

        Pilot current = CoreSpecializationRuntime.GetCurrentPilot();
        if (current == null || !ReferenceEquals(current, pilot))
            return null;

        Savable.MetaData meta = Core.instance.player.metaData;
        if (!string.IsNullOrWhiteSpace(meta.uid))
            return "uid_" + meta.uid.Trim();
        if (!string.IsNullOrWhiteSpace(meta.file))
            return "file_" + meta.file.Trim();
        return null;
    }

    private static string Sanitize(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        char[] chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0 ||
                chars[i] == Path.DirectorySeparatorChar ||
                chars[i] == Path.AltDirectorySeparatorChar)
            {
                chars[i] = '_';
            }
        }
        return new string(chars);
    }
}
