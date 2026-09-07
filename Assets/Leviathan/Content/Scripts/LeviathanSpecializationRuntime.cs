using StarVortex;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public interface ILeviathanSpecializationPointBank
{
    bool IsAvailable(Pilot pilot, out string reason);
    int GetAvailablePoints(Pilot pilot);
    int GetGrantedPoints(Pilot pilot);
    bool TrySpend(Pilot pilot, int amount, out string reason);
    bool TryRefund(Pilot pilot, int amount, out string reason);
}

public sealed class LeviathanGrowthPointBank : ILeviathanSpecializationPointBank
{
    public bool IsAvailable(Pilot pilot, out string reason)
    {
        reason = string.Empty;

        if (pilot == null)
        {
            reason = "No Pilot.";
            return false;
        }

        try
        {
            LeviathanSpecializationCurrency.EnsureRegistered();
            return true;
        }
        catch (Exception ex)
        {
            reason = "Growth Point source skill is unavailable: " + ex.Message;
            return false;
        }
    }

    public int GetGrantedPoints(Pilot pilot)
    {
        if (pilot == null)
            return 0;

        LeviathanSpecializationCurrency.EnsureRegistered();
        int rank = pilot.GetUpgradeLevel(
            LeviathanSpecializationCurrency.UpgradeKey
        );

        return Math.Max(
            0,
            rank * LeviathanSpecializationCurrency.PointsPerRank
        );
    }

    public int GetAvailablePoints(Pilot pilot)
    {
        int granted = GetGrantedPoints(pilot);
        int spent = LeviathanSpecializationRuntime.GetTotalSpentPoints(pilot);
        return Math.Max(0, granted - spent);
    }

    public bool TrySpend(Pilot pilot, int amount, out string reason)
    {
        reason = string.Empty;
        if (amount <= 0)
            return true;

        if (!IsAvailable(pilot, out reason))
            return false;

        if (GetAvailablePoints(pilot) < amount)
        {
            reason = "Not enough Growth Points.";
            return false;
        }

        return true;
    }

    public bool TryRefund(Pilot pilot, int amount, out string reason)
    {
        reason = string.Empty;
        return pilot != null;
    }
}

public static class LeviathanSpecializationPersistence
{
    public static bool TryGetPath(Pilot pilot, out string path, out string reason)
    {
        path = null;
        reason = string.Empty;

        if (pilot == null)
        {
            reason = "No Pilot.";
            return false;
        }

        string stableId = ResolveStablePilotId(pilot);
        if (string.IsNullOrEmpty(stableId))
        {
            reason = "No stable current-player save UID/file was available; persistence is disabled.";
            return false;
        }

        string safe = Sanitize(stableId);
        string folder = Path.Combine(
            Application.persistentDataPath,
            "LeviathanSpecializations"
        );

        path = Path.Combine(folder, safe + ".txt");
        return true;
    }

    public static bool Load(
        Pilot pilot,
        Dictionary<string, LeviathanSpecializationState> states,
        out string reason)
    {
        reason = string.Empty;
        string path;

        if (!TryGetPath(pilot, out path, out reason))
            return false;

        if (!File.Exists(path))
            return true;

        try
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                string[] parts = line.Split('|');
                if (parts.Length != 3)
                    continue;

                int rank;
                if (!int.TryParse(parts[2], out rank) || rank <= 0)
                    continue;

                LeviathanSpecializationTree tree =
                    LeviathanSpecializationRegistry.Get(parts[0]);

                if (tree == null)
                    continue;

                LeviathanSpecializationNode node = tree.GetNode(parts[1]);
                if (node == null || node.AutoGranted)
                    continue;

                LeviathanSpecializationState state;
                if (!states.TryGetValue(tree.Id, out state))
                {
                    state = new LeviathanSpecializationState();
                    states.Add(tree.Id, state);
                }

                state.SetRank(node.Id, Math.Min(rank, node.MaxRank));
            }

            return true;
        }
        catch (Exception ex)
        {
            reason = "Failed loading specialization state: " + ex.Message;
            Debug.LogError("[Leviathan] " + reason);
            return false;
        }
    }

    public static bool Save(
        Pilot pilot,
        Dictionary<string, LeviathanSpecializationState> states,
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
            lines.Add("# Leviathan specialization state v3");

            foreach (KeyValuePair<string, LeviathanSpecializationState> treeState in states)
            {
                LeviathanSpecializationTree tree =
                    LeviathanSpecializationRegistry.Get(treeState.Key);

                if (tree == null)
                    continue;

                IList<LeviathanSpecializationNode> nodes = tree.Nodes;
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (nodes[i].AutoGranted)
                        continue;

                    int rank = treeState.Value.GetRank(nodes[i].Id);
                    if (rank > 0)
                    {
                        lines.Add(
                            tree.Id + "|" + nodes[i].Id + "|" + rank.ToString()
                        );
                    }
                }
            }

            File.WriteAllLines(path, lines.ToArray());
            return true;
        }
        catch (Exception ex)
        {
            reason = "Failed saving specialization state: " + ex.Message;
            Debug.LogError("[Leviathan] " + reason);
            return false;
        }
    }

    private static string ResolveStablePilotId(Pilot pilot)
    {
        if (Core.instance == null ||
            Core.instance.player == null ||
            Core.instance.player.metaData == null)
        {
            return null;
        }

        Pilot current = LeviathanSpecializationRuntime.GetCurrentPilot();
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

public sealed class LeviathanPilotSpecializationData
{
    public readonly Dictionary<string, LeviathanSpecializationState> Trees =
        new Dictionary<string, LeviathanSpecializationState>(StringComparer.Ordinal);

    public bool PersistenceReady;
    public string PersistenceReason;
}

public static class LeviathanSpecializationRuntime
{
    private static readonly Dictionary<Pilot, LeviathanPilotSpecializationData> data =
        new Dictionary<Pilot, LeviathanPilotSpecializationData>();

    public static ILeviathanSpecializationPointBank PointBank =
        new LeviathanGrowthPointBank();

    private static bool registeredDefaults;
    private static int configurationRevision;

    // Changes only when specialization state actually changes. Runtime consumers
    // can cache fully-resolved configurations against this revision instead of
    // rebuilding every rendered frame.
    public static int ConfigurationRevision
    {
        get { return configurationRevision; }
    }

    public static void InvalidateConfiguration()
    {
        unchecked
        {
            configurationRevision++;
        }
    }

    public static void RegisterDefaults()
    {
        LeviathanSpecializationCurrency.EnsureRegistered();

        if (registeredDefaults)
            return;

        registeredDefaults = true;
        LeviathanSpecializationCatalog.RegisterAll();
    }

    private static LeviathanPilotSpecializationData GetPilotData(Pilot pilot)
    {
        RegisterDefaults();

        if (pilot == null)
            return null;

        LeviathanPilotSpecializationData playerData;
        if (!data.TryGetValue(pilot, out playerData))
        {
            playerData = new LeviathanPilotSpecializationData();
            data.Add(pilot, playerData);

            string persistenceReason;
            playerData.PersistenceReady = LeviathanSpecializationPersistence.Load(
                pilot,
                playerData.Trees,
                out persistenceReason
            );
            playerData.PersistenceReason = persistenceReason;

            // Auto-granted roots are derived state and are intentionally not
            // serialized. Rebuild them immediately after loading so persisted
            // child nodes can satisfy their normal root prerequisites.
            if (playerData.PersistenceReady)
            {
                SynchronizeAllAutoGrantedNodes(pilot);
                InvalidateConfiguration();
            }
        }
        else if (!playerData.PersistenceReady)
        {
            string retryReason;
            bool ready = LeviathanSpecializationPersistence.Load(
                pilot,
                playerData.Trees,
                out retryReason
            );

            playerData.PersistenceReady = ready;
            playerData.PersistenceReason = retryReason;

            if (ready)
            {
                SynchronizeAllAutoGrantedNodes(pilot);
                InvalidateConfiguration();
            }
        }

        return playerData;
    }

    private static LeviathanSpecializationState GetRawState(
        Pilot pilot,
        string treeId)
    {
        LeviathanPilotSpecializationData playerData = GetPilotData(pilot);
        if (playerData == null)
            return null;

        LeviathanSpecializationState state;
        if (!playerData.Trees.TryGetValue(treeId, out state))
        {
            state = new LeviathanSpecializationState();
            playerData.Trees.Add(treeId, state);
        }

        return state;
    }

    public static LeviathanSpecializationState GetState(
        Pilot pilot,
        string treeId)
    {
        RegisterDefaults();
        LeviathanSpecializationTree tree = LeviathanSpecializationRegistry.Get(treeId);
        LeviathanSpecializationState state = GetRawState(pilot, treeId);

        if (tree != null && state != null)
            SynchronizeAutoGrantedNodes(pilot, tree, state);

        return state;
    }

    private static void SynchronizeAllAutoGrantedNodes(Pilot pilot)
    {
        IList<LeviathanSpecializationTree> trees =
            LeviathanSpecializationRegistry.All();

        for (int i = 0; i < trees.Count; i++)
        {
            LeviathanSpecializationState state = GetRawState(pilot, trees[i].Id);
            SynchronizeAutoGrantedNodes(pilot, trees[i], state);
        }
    }

    private static void SynchronizeAutoGrantedNodes(
        Pilot pilot,
        LeviathanSpecializationTree tree,
        LeviathanSpecializationState state)
    {
        if (tree == null || state == null)
            return;

        bool unlocked = IsTreeUnlockedRaw(pilot, tree);
        IList<LeviathanSpecializationNode> nodes = tree.Nodes;

        for (int i = 0; i < nodes.Count; i++)
        {
            if (!nodes[i].AutoGranted)
                continue;

            int desiredRank = unlocked ? nodes[i].MaxRank : 0;
            if (state.GetRank(nodes[i].Id) != desiredRank)
            {
                state.SetRank(nodes[i].Id, desiredRank);
                InvalidateConfiguration();
            }
        }
    }

    public static bool IsTreeUnlocked(Pilot pilot, string treeId)
    {
        RegisterDefaults();
        LeviathanSpecializationTree tree = LeviathanSpecializationRegistry.Get(treeId);
        return IsTreeUnlockedRaw(pilot, tree);
    }

    public static bool IsTreeUnlocked(Pilot pilot, LeviathanSpecializationTree tree)
    {
        RegisterDefaults();
        return IsTreeUnlockedRaw(pilot, tree);
    }

    public static bool IsTreeActive(Pilot pilot, string treeId)
    {
        return IsTreeUnlocked(pilot, treeId);
    }

    private static bool IsTreeUnlockedRaw(
        Pilot pilot,
        LeviathanSpecializationTree tree)
    {
        return IsTreeUnlockedRaw(
            pilot,
            tree,
            new HashSet<string>(StringComparer.Ordinal)
        );
    }

    private static bool IsTreeUnlockedRaw(
        Pilot pilot,
        LeviathanSpecializationTree tree,
        HashSet<string> path)
    {
        if (pilot == null || tree == null)
            return false;

        if (!path.Add(tree.Id))
            return false;

        try
        {
            if (tree.UnlockKind == LeviathanTreeUnlockKind.Always)
                return true;

            if (tree.UnlockKind == LeviathanTreeUnlockKind.NativeUpgrade)
            {
                return pilot.GetUpgradeLevel(
                    (Upgrade.Key)tree.NativeUnlockUpgradeKey
                ) >= 1;
            }

            IList<LeviathanSpecializationTree> all =
                LeviathanSpecializationRegistry.All();

            for (int i = 0; i < all.Count; i++)
            {
                LeviathanSpecializationTree sourceTree = all[i];

                if (sourceTree.Id == tree.Id ||
                    !IsTreeUnlockedRaw(pilot, sourceTree, path))
                {
                    continue;
                }

                LeviathanSpecializationState state =
                    GetRawState(pilot, sourceTree.Id);

                if (state != null &&
                    state.HasUnlockTreeEffect(sourceTree, tree.Id))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            path.Remove(tree.Id);
        }
    }

    public static bool CanSafelySpend(Pilot pilot, out string reason)
    {
        reason = string.Empty;
        RegisterDefaults();

        if (pilot == null)
        {
            reason = "No Pilot.";
            return false;
        }

        LeviathanPilotSpecializationData playerData = GetPilotData(pilot);
        if (playerData == null)
        {
            reason = "No specialization state.";
            return false;
        }

        if (!playerData.PersistenceReady)
        {
            reason = playerData.PersistenceReason;
            return false;
        }

        return PointBank != null && PointBank.IsAvailable(pilot, out reason);
    }

    public static bool CanInvest(
        Pilot pilot,
        string treeId,
        string nodeId,
        out string reason)
    {
        reason = string.Empty;
        RegisterDefaults();

        LeviathanSpecializationTree tree = LeviathanSpecializationRegistry.Get(treeId);
        if (tree == null || pilot == null)
        {
            reason = "Tree or Pilot unavailable.";
            return false;
        }

        if (!IsTreeUnlockedRaw(pilot, tree))
        {
            reason = tree.Name + " is locked. Unlock it from Evolution first.";
            return false;
        }

        if (!CanSafelySpend(pilot, out reason))
            return false;

        LeviathanSpecializationState state = GetState(pilot, treeId);
        if (!state.CanInvest(tree, nodeId, out reason))
            return false;

        LeviathanSpecializationNode node = tree.GetNode(nodeId);
        int cost = node == null ? 1 : node.PointCostPerRank;

        if (GetAvailablePoints(pilot) < cost)
        {
            reason = "Not enough Growth Points.";
            return false;
        }

        return true;
    }

    public static bool TryInvest(
        Pilot pilot,
        string treeId,
        string nodeId,
        out string reason)
    {
        reason = string.Empty;

        if (!CanInvest(pilot, treeId, nodeId, out reason))
            return false;

        LeviathanSpecializationTree tree = LeviathanSpecializationRegistry.Get(treeId);
        LeviathanSpecializationState state = GetState(pilot, treeId);
        LeviathanSpecializationNode node = tree.GetNode(nodeId);
        int cost = node.PointCostPerRank;

        if (!PointBank.TrySpend(pilot, cost, out reason))
            return false;

        if (!state.TryInvest(tree, nodeId, out reason))
            return false;

        SynchronizeAllAutoGrantedNodes(pilot);

        string invalid;
        if (!ValidateAllInvestedState(pilot, out invalid))
        {
            state.SetRank(nodeId, state.GetRank(nodeId) - 1);
            SynchronizeAllAutoGrantedNodes(pilot);
            reason = invalid;
            return false;
        }

        LeviathanPilotSpecializationData playerData = GetPilotData(pilot);
        if (!LeviathanSpecializationPersistence.Save(
                pilot,
                playerData.Trees,
                out reason))
        {
            state.SetRank(nodeId, state.GetRank(nodeId) - 1);
            SynchronizeAllAutoGrantedNodes(pilot);
            return false;
        }

        InvalidateConfiguration();
        return true;
    }

    public static bool CanRefund(
        Pilot pilot,
        string treeId,
        string nodeId,
        out string reason)
    {
        reason = string.Empty;
        RegisterDefaults();

        LeviathanSpecializationTree tree = LeviathanSpecializationRegistry.Get(treeId);
        LeviathanSpecializationState state = GetState(pilot, treeId);

        if (tree == null || state == null)
        {
            reason = "Tree or Pilot unavailable.";
            return false;
        }

        if (!CanSafelySpend(pilot, out reason))
            return false;

        if (!state.CanRefund(tree, nodeId, out reason))
            return false;

        int oldRank = state.GetRank(nodeId);
        state.SetRank(nodeId, oldRank - 1);
        SynchronizeAllAutoGrantedNodes(pilot);

        string invalid;
        bool valid = ValidateAllInvestedState(pilot, out invalid);

        state.SetRank(nodeId, oldRank);
        SynchronizeAllAutoGrantedNodes(pilot);

        if (!valid)
        {
            reason = invalid;
            return false;
        }

        return true;
    }

    public static bool TryRefund(
        Pilot pilot,
        string treeId,
        string nodeId,
        out string reason)
    {
        if (!CanRefund(pilot, treeId, nodeId, out reason))
            return false;

        LeviathanSpecializationTree tree = LeviathanSpecializationRegistry.Get(treeId);
        LeviathanSpecializationState state = GetState(pilot, treeId);
        LeviathanSpecializationNode node = tree.GetNode(nodeId);
        int oldRank = state.GetRank(nodeId);

        state.SetRank(nodeId, oldRank - 1);
        SynchronizeAllAutoGrantedNodes(pilot);

        LeviathanPilotSpecializationData playerData = GetPilotData(pilot);
        if (!LeviathanSpecializationPersistence.Save(
                pilot,
                playerData.Trees,
                out reason))
        {
            state.SetRank(nodeId, oldRank);
            SynchronizeAllAutoGrantedNodes(pilot);
            return false;
        }

        if (PointBank != null)
        {
            string ignored;
            PointBank.TryRefund(pilot, node.PointCostPerRank, out ignored);
        }

        InvalidateConfiguration();
        return true;
    }

    private static bool ValidateAllInvestedState(Pilot pilot, out string reason)
    {
        reason = string.Empty;
        IList<LeviathanSpecializationTree> trees =
            LeviathanSpecializationRegistry.All();

        for (int i = 0; i < trees.Count; i++)
        {
            LeviathanSpecializationTree tree = trees[i];
            LeviathanSpecializationState state = GetRawState(pilot, tree.Id);
            if (state == null)
                continue;

            int paid = state.GetSpentPointCost(tree);
            if (paid > 0 && !IsTreeUnlockedRaw(pilot, tree))
            {
                reason = "Refund points from " + tree.Name +
                    " before removing its Evolution unlock.";
                return false;
            }

            if (IsTreeUnlockedRaw(pilot, tree))
            {
                string invalid;
                if (!state.ValidateInvestedState(tree, out invalid))
                {
                    reason = "Invalid " + tree.Name + " node: " + invalid + ".";
                    return false;
                }
            }
        }

        return true;
    }

    public static int GetTotalSpentPoints(Pilot pilot)
    {
        if (pilot == null)
            return 0;

        RegisterDefaults();
        SynchronizeAllAutoGrantedNodes(pilot);

        int spent = 0;
        IList<LeviathanSpecializationTree> trees =
            LeviathanSpecializationRegistry.All();

        for (int i = 0; i < trees.Count; i++)
        {
            LeviathanSpecializationState state = GetRawState(pilot, trees[i].Id);
            if (state != null)
                spent += state.GetSpentPointCost(trees[i]);
        }

        return spent;
    }

    public static int GetTreeSpentPoints(Pilot pilot, string treeId)
    {
        LeviathanSpecializationTree tree = LeviathanSpecializationRegistry.Get(treeId);
        LeviathanSpecializationState state = GetState(pilot, treeId);
        return tree == null || state == null ? 0 : state.GetSpentPointCost(tree);
    }

    public static int GetGrantedPoints(Pilot pilot)
    {
        RegisterDefaults();
        return PointBank == null ? 0 : PointBank.GetGrantedPoints(pilot);
    }

    public static int GetAvailablePoints(Pilot pilot)
    {
        RegisterDefaults();
        return PointBank == null ? 0 : PointBank.GetAvailablePoints(pilot);
    }

    public static int GetEvolutionRank(Pilot pilot)
    {
        if (pilot == null)
            return 0;

        RegisterDefaults();
        return pilot.GetUpgradeLevel(LeviathanSpecializationCurrency.UpgradeKey);
    }

    public static bool ResetAll(Pilot pilot, out string reason)
    {
        reason = string.Empty;
        if (pilot == null)
            return false;

        RegisterDefaults();
        LeviathanPilotSpecializationData playerData = GetPilotData(pilot);
        if (playerData == null)
            return false;

        playerData.Trees.Clear();

        IList<LeviathanSpecializationTree> trees =
            LeviathanSpecializationRegistry.All();
        for (int i = 0; i < trees.Count; i++)
            playerData.Trees[trees[i].Id] = new LeviathanSpecializationState();

        SynchronizeAllAutoGrantedNodes(pilot);

        bool saved = LeviathanSpecializationPersistence.Save(
            pilot,
            playerData.Trees,
            out reason
        );

        if (saved)
            InvalidateConfiguration();

        return saved;
    }

    public static int GetNodeRank(
        Pilot pilot,
        string treeId,
        string nodeId)
    {
        RegisterDefaults();
        LeviathanSpecializationTree tree = LeviathanSpecializationRegistry.Get(treeId);
        LeviathanSpecializationState state = GetState(pilot, treeId);

        return tree == null ||
            state == null ||
            tree.GetNode(nodeId) == null
                ? 0
                : state.GetRank(nodeId);
    }

    public static bool HasNode(
        Pilot pilot,
        string treeId,
        string nodeId)
    {
        return GetNodeRank(pilot, treeId, nodeId) > 0;
    }

    // Legacy tree-specific lookup retained for current bridges.
    public static float GetMultiplier(
        Pilot pilot,
        string treeId,
        string statId)
    {
        RegisterDefaults();
        LeviathanSpecializationTree tree = LeviathanSpecializationRegistry.Get(treeId);
        LeviathanSpecializationState state = GetState(pilot, treeId);
        if (tree == null || state == null || !IsTreeUnlockedRaw(pilot, tree))
            return 1f;

        float flat = 0f;
        float percent = 0f;
        float multiplier = 1f;
        state.Aggregate(tree, statId, ref flat, ref percent, ref multiplier);
        return (1f + percent) * multiplier;
    }

    public static float GetKnobMultiplier(
        Pilot pilot,
        LeviathanSpecializationKnob knob)
    {
        if (pilot == null || knob == null)
            return 1f;

        float flat;
        float percent;
        float multiplier;
        AggregateKnob(pilot, knob, out flat, out percent, out multiplier);
        return (1f + percent) * multiplier;
    }

    public static float GetKnobFlat(
        Pilot pilot,
        LeviathanSpecializationKnob knob)
    {
        if (pilot == null || knob == null)
            return 0f;

        float flat;
        float percent;
        float multiplier;
        AggregateKnob(pilot, knob, out flat, out percent, out multiplier);
        return flat;
    }

    public static float ApplyKnob(
        Pilot pilot,
        LeviathanSpecializationKnob knob,
        float baseValue)
    {
        if (pilot == null || knob == null)
            return baseValue;

        float flat;
        float percent;
        float multiplier;
        AggregateKnob(pilot, knob, out flat, out percent, out multiplier);
        return (baseValue + flat) * (1f + percent) * multiplier;
    }

    private static void AggregateKnob(
        Pilot pilot,
        LeviathanSpecializationKnob knob,
        out float flat,
        out float percent,
        out float multiplier)
    {
        flat = 0f;
        percent = 0f;
        multiplier = 1f;

        RegisterDefaults();
        IList<LeviathanSpecializationTree> trees =
            LeviathanSpecializationRegistry.All();

        for (int i = 0; i < trees.Count; i++)
        {
            if (!IsTreeUnlockedRaw(pilot, trees[i]))
                continue;

            LeviathanSpecializationState state = GetState(pilot, trees[i].Id);
            if (state != null)
            {
                state.Aggregate(
                    trees[i],
                    knob.Id,
                    ref flat,
                    ref percent,
                    ref multiplier
                );
            }
        }
    }

    public static bool HasFlag(
        Pilot pilot,
        string treeId,
        string flagId)
    {
        RegisterDefaults();
        LeviathanSpecializationTree tree = LeviathanSpecializationRegistry.Get(treeId);
        LeviathanSpecializationState state = GetState(pilot, treeId);
        return tree != null &&
            state != null &&
            IsTreeUnlockedRaw(pilot, tree) &&
            state.HasFlag(tree, flagId);
    }

    // Named flags resolve globally across every unlocked specialization tree,
    // mirroring named knob aggregation. Runtime functionality no longer needs to
    // know which tree granted a feature.
    public static bool HasFlag(
        Pilot pilot,
        LeviathanSpecializationFlag flag)
    {
        if (pilot == null || flag == null)
            return false;

        RegisterDefaults();
        IList<LeviathanSpecializationTree> trees =
            LeviathanSpecializationRegistry.All();

        for (int i = 0; i < trees.Count; i++)
        {
            LeviathanSpecializationTree tree = trees[i];
            if (!IsTreeUnlockedRaw(pilot, tree))
                continue;

            LeviathanSpecializationState state = GetState(pilot, tree.Id);
            if (state != null && state.HasFlag(tree, flag.Id))
                return true;
        }

        return false;
    }

    public static bool HasFlag(LeviathanSpecializationFlag flag)
    {
        return HasFlag(GetCurrentPilot(), flag);
    }

    public static Pilot GetCurrentPilot()
    {
        if (WorldController.instance != null)
        {
            GameShip ship = WorldController.instance.GetCurrentPlayerShip();
            if (ship != null)
            {
                Pilot source = GameShip.GetPlayerSourcePilot(ship);
                if (source != null)
                    return source;
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
