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
    private static readonly Dictionary<CoreClassId, ICoreSpecializationPolicy> policies = new Dictionary<CoreClassId, ICoreSpecializationPolicy>();
    private static readonly Dictionary<string, CoreClassId> treeOwners = new Dictionary<string, CoreClassId>(StringComparer.Ordinal);
    private static readonly Dictionary<CoreClassId, IList<CoreSpecializationTree>> views = new Dictionary<CoreClassId, IList<CoreSpecializationTree>>();
    private static readonly IList<CoreSpecializationTree> empty = new List<CoreSpecializationTree>().AsReadOnly();
    private static readonly List<ICoreSpecializationPolicy> ordered = new List<ICoreSpecializationPolicy>();
    private static Dictionary<string, CoreSpecializationTree> staged;
    private static Dictionary<string, CoreClassId> stagedOwners;
    private static CoreClassId registering;

    public static void Register(ICoreSpecializationPolicy policy)
    {
        if (policy == null || policy.ClassId == CoreClassId.None) throw new ArgumentException("Concrete policy required.");
        if (policies.ContainsKey(policy.ClassId) || staged != null) throw new InvalidOperationException("Duplicate/reentrant class registration.");
        staged = new Dictionary<string, CoreSpecializationTree>(StringComparer.Ordinal);
        stagedOwners = new Dictionary<string, CoreClassId>(treeOwners, StringComparer.Ordinal);
        try
        {
            registering = policy.ClassId;
            policy.EnsurePrerequisitesRegistered();
            policy.RegisterTrees();
            ValidateStaged();
            policies.Add(policy.ClassId, policy);
            ordered.Add(policy);
            ordered.Sort((a, b) => ((byte)a.ClassId).CompareTo((byte)b.ClassId));
            PublishStaged(false);
        }
        finally { staged = null; stagedOwners = null; registering = CoreClassId.None; }
    }

    public static ICoreSpecializationPolicy Get(CoreClassId classId)
    { ICoreSpecializationPolicy policy; return policies.TryGetValue(classId, out policy) ? policy : null; }

    public static ICoreSpecializationPolicy GetForPilot(Pilot pilot)
    { return Get(CoreSpecializationRuntime.GetEffectiveClass(pilot)); }

    public static CoreClassId GetOwnerClass(string treeId)
    { CoreClassId owner; return treeId != null && treeOwners.TryGetValue(treeId, out owner) ? owner : CoreClassId.None; }

    public static ICoreSpecializationPolicy GetForTree(string treeId) { return Get(GetOwnerClass(treeId)); }

    public static IList<CoreSpecializationTree> GetTrees(CoreClassId classId)
    {
        IList<CoreSpecializationTree> view;
        if (views.TryGetValue(classId, out view)) return view;
        if (classId == CoreClassId.None) return empty;
        List<CoreSpecializationTree> list = new List<CoreSpecializationTree>();
        foreach (CoreSpecializationTree tree in CoreSpecializationRegistry.All())
            if (GetOwnerClass(tree.Id) == classId) list.Add(tree);
        view = list.AsReadOnly(); views.Add(classId, view); return view;
    }

    public static void RegisterTree(CoreClassId ownerClass, CoreSpecializationTree tree)
    {
        if (staged == null || ownerClass != registering || tree == null)
            throw new InvalidOperationException("Trees must be registered by their policy transaction.");
        if (stagedOwners.ContainsKey(tree.Id)) throw new InvalidOperationException("Duplicate tree: " + tree.Id);
        tree.Validate();
        foreach (CoreSpecializationNode node in tree.Nodes)
            if (node.MaxRank > 15) throw new InvalidOperationException("Node rank exceeds four-bit schema: " + tree.Id + "/" + node.Id);
        staged.Add(tree.Id, tree); stagedOwners.Add(tree.Id, ownerClass);
    }

    private static void ValidateStaged()
    {
        var counts = new Dictionary<CoreClassId, int>();
        foreach (CoreSpecializationTree tree in staged.Values)
        {
            int count; counts.TryGetValue(stagedOwners[tree.Id], out count);
            foreach (CoreSpecializationNode node in tree.Nodes) if (!node.AutoGranted) count++;
            if (count > CoreNetwork.MaxClassNodes) throw new InvalidOperationException("Class schema capacity exceeded.");
            counts[stagedOwners[tree.Id]] = count;
        }
        foreach (CoreSpecializationTree tree in staged.Values)
            foreach (CoreSpecializationNode node in tree.Nodes)
                foreach (CoreSpecializationEffect effect in node.Effects)
                    if (effect.Type == CoreSpecializationEffectType.UnlockTree)
                    {
                        CoreClassId owner;
                        if (!stagedOwners.TryGetValue(effect.Key, out owner) || owner != stagedOwners[tree.Id])
                            throw new InvalidOperationException("Unknown or cross-class tree unlock: " + tree.Id + " -> " + effect.Key);
                    }
        // Node prerequisites are tree-local and tree.Validate rejects missing nodes.
    }

    private static void PublishStaged(bool rebuild)
    {
        if (rebuild) CoreSpecializationRegistry.Clear();
        foreach (CoreSpecializationTree tree in staged.Values) CoreSpecializationRegistry.Register(tree);
        treeOwners.Clear(); foreach (var pair in stagedOwners) treeOwners.Add(pair.Key, pair.Value);
        views.Clear();
    }

    public static void EnsurePrerequisitesRegistered()
    { for (int i = 0; i < ordered.Count; i++) ordered[i].EnsurePrerequisitesRegistered(); }

    public static void RebuildAllTrees()
    {
        if (staged != null) throw new InvalidOperationException("Reentrant catalog rebuild.");
        staged = new Dictionary<string, CoreSpecializationTree>(StringComparer.Ordinal);
        stagedOwners = new Dictionary<string, CoreClassId>(StringComparer.Ordinal);
        try
        {
            foreach (var policy in ordered)
            { registering = policy.ClassId; policy.EnsurePrerequisitesRegistered(); policy.RegisterTrees(); }
            ValidateStaged(); PublishStaged(true);
        }
        finally { staged = null; stagedOwners = null; registering = CoreClassId.None; }
    }
    public static IList<ICoreSpecializationPolicy> All() { return ordered.AsReadOnly(); }

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
            CoreSpecializationPolicies.GetForPilot(pilot);
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
            CoreSpecializationPolicies.GetForPilot(pilot);
        return policy == null ? 0 : Math.Max(0, policy.GetGrantedPoints(pilot));
    }

    public static int GetSpentPoints(Pilot pilot)
    {
        ICoreSpecializationPolicy policy =
            CoreSpecializationPolicies.GetForPilot(pilot);
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
            CoreSpecializationPolicies.GetForPilot(pilot);
        return policy == null ? 0 : Math.Max(0, policy.GetProgressionRank(pilot));
    }

    public static string GetCurrencyName(Pilot pilot)
    {
        ICoreSpecializationPolicy policy =
            CoreSpecializationPolicies.GetForPilot(pilot);
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

        var stagedState = new Dictionary<string, CoreSpecializationState>(StringComparer.Ordinal);
        bool imported;
        if (!TryImportLegacyLeviathan(pilot, stagedState, out imported, out reason)) return false;
        if (imported && !Save(pilot, stagedState, out reason)) return false;
        states.Clear(); foreach (var pair in stagedState) states.Add(pair.Key, pair.Value);
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
            if (File.Exists(path))
            {
                var existing = new Dictionary<string, CoreSpecializationState>(StringComparer.Ordinal);
                if (!LoadCoreFile(path, existing, out reason)) return false;
            }
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

            WriteAtomic(path, lines.ToArray());
            return true;
        }
        catch (Exception ex)
        {
            reason = "Failed saving specialization state: " + ex.Message;
            return false;
        }
    }

    private static void WriteAtomic(string path, string[] lines)
    {
        if (File.Exists(path))
        {
            string[] original = File.ReadAllLines(path);
            if (original.Length == 0 || original[0].Trim() != "# Core specialization state v" + FormatVersion)
                throw new InvalidDataException("Unsupported specialization version; save refused.");
        }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporary, lines);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
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
            if (lines.Length == 0 || lines[0].Trim() != "# Core specialization state v" + FormatVersion)
                throw new InvalidDataException("Unsupported specialization format version; original file is protected.");
            var stagedState = new Dictionary<string, CoreSpecializationState>(StringComparer.Ordinal);
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] parts = line.Split('|');
                byte owner; int rank;
                if (parts.Length != 4 || !byte.TryParse(parts[0], out owner) ||
                    !int.TryParse(parts[3], out rank) || rank <= 0 ||
                    owner == 0 || CoreSpecializationPolicies.GetOwnerClass(parts[1]) != (CoreClassId)owner)
                    throw new InvalidDataException("Invalid or unregistered class/tree at row " + (i + 1));
                CoreSpecializationTree tree = CoreSpecializationRegistry.Get(parts[1]);
                CoreSpecializationNode node = tree == null ? null : tree.GetNode(parts[2]);
                if (node == null || node.AutoGranted || rank > node.MaxRank)
                    throw new InvalidDataException("Invalid node/rank at row " + (i + 1));
                CoreSpecializationState previous;
                if (stagedState.TryGetValue(tree.Id, out previous) && previous.GetRank(node.Id) != 0)
                    throw new InvalidDataException("Duplicate persisted rank.");
                InstallRank(stagedState, tree.Id, node.Id, rank);
            }
            states.Clear(); foreach (var pair in stagedState) states.Add(pair.Key, pair.Value);
            return true;
        }
        catch (Exception ex)
        {
            reason = "Failed loading specialization state: " + ex.Message;
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
