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

// Specialization currency is derived, not separately mutated:
// Evolution rank * PointsPerRank - specialization ranks currently invested.
// The native Evolution rank itself is persisted by Pilot as a normal Upgrade.
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

        // Spending is represented by the node rank itself. No second counter is
        // mutated, so currency cannot drift out of sync with specialization state.
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
                if (node == null)
                    continue;

                LeviathanSpecializationState state;
                if (!states.TryGetValue(tree.Id, out state))
                {
                    state = new LeviathanSpecializationState();
                    states.Add(tree.Id, state);
                }

                state.SetRank(node.Id, Math.Min(rank, node.MaxRank));
            }

            // Tree definitions will change during development. Remove only ranks
            // that became illegal instead of rejecting the whole save file.
            foreach (KeyValuePair<string, LeviathanSpecializationState> pair in states)
            {
                LeviathanSpecializationTree tree =
                    LeviathanSpecializationRegistry.Get(pair.Key);

                if (tree == null)
                    continue;

                bool changed = true;
                while (changed)
                {
                    changed = false;
                    string invalid;

                    if (pair.Value.ValidateInvestedState(tree, out invalid))
                        break;

                    LeviathanSpecializationNode bad = tree.Nodes
                        .FirstOrDefault(x => x.Name == invalid);

                    if (bad == null)
                        break;

                    pair.Value.SetRank(bad.Id, 0);
                    changed = true;
                }
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
            lines.Add("# Leviathan specialization state v2");

            foreach (KeyValuePair<string, LeviathanSpecializationState> treeState in states)
            {
                LeviathanSpecializationTree tree =
                    LeviathanSpecializationRegistry.Get(treeState.Key);

                if (tree == null)
                    continue;

                IList<LeviathanSpecializationNode> nodes = tree.Nodes;
                for (int i = 0; i < nodes.Count; i++)
                {
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

    public static void RegisterDefaults()
    {
        LeviathanSpecializationCurrency.EnsureRegistered();

        if (registeredDefaults)
            return;

        registeredDefaults = true;

        LeviathanSpecializationRegistry.Register(
            LeviathanStarfireSpecialization.Create(
                (int)LeviathanSpecializationCurrency.UpgradeKey
            )
        );
    }

    private static void RetryPersistenceIfNeeded(
        Pilot pilot,
        LeviathanPilotSpecializationData playerData)
    {
        if (pilot == null ||
            playerData == null ||
            playerData.PersistenceReady)
        {
            return;
        }

        string retryReason;
        bool ready = LeviathanSpecializationPersistence.Load(
            pilot,
            playerData.Trees,
            out retryReason
        );

        playerData.PersistenceReady = ready;
        playerData.PersistenceReason = retryReason;
    }

    public static LeviathanSpecializationState GetState(
        Pilot pilot,
        string treeId)
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
        }
        else
        {
            // F10 can be opened during scene transitions before Player.metaData is
            // populated. Retry later instead of permanently pinning that Pilot to
            // safety mode for the rest of the session. No spending is possible
            // while persistence is unavailable, so re-loading here cannot overwrite
            // legal unsaved investments.
            RetryPersistenceIfNeeded(pilot, playerData);
        }

        LeviathanSpecializationState state;
        if (!playerData.Trees.TryGetValue(treeId, out state))
        {
            state = new LeviathanSpecializationState();
            playerData.Trees.Add(treeId, state);
        }

        return state;
    }

    public static bool IsTreeUnlocked(Pilot pilot, LeviathanSpecializationTree tree)
    {
        if (pilot == null || tree == null)
            return false;

        return pilot.GetUpgradeLevel((Upgrade.Key)tree.OwnerUpgradeKey) >= 1;
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

        GetState(pilot, LeviathanStarfireSpecialization.TreeId);
        LeviathanPilotSpecializationData playerData = data[pilot];

        if (!playerData.PersistenceReady)
        {
            reason = playerData.PersistenceReason;
            return false;
        }

        return PointBank != null && PointBank.IsAvailable(pilot, out reason);
    }

    public static bool TryInvest(
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

        if (!IsTreeUnlocked(pilot, tree))
        {
            reason = "Evolution is not unlocked.";
            return false;
        }

        if (!CanSafelySpend(pilot, out reason))
            return false;

        LeviathanSpecializationState state = GetState(pilot, treeId);
        if (!state.CanInvest(tree, nodeId, out reason))
            return false;

        if (!PointBank.TrySpend(pilot, 1, out reason))
            return false;

        if (!state.TryInvest(tree, nodeId, out reason))
            return false;

        LeviathanPilotSpecializationData playerData = data[pilot];
        if (!LeviathanSpecializationPersistence.Save(
                pilot,
                playerData.Trees,
                out reason))
        {
            string ignored;
            state.TryRefund(tree, nodeId, out ignored);
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

        if (!state.TryRefund(tree, nodeId, out reason))
            return false;

        LeviathanPilotSpecializationData playerData = data[pilot];
        if (!LeviathanSpecializationPersistence.Save(
                pilot,
                playerData.Trees,
                out reason))
        {
            // Save failed; restore the refunded rank. The pre-refund state was
            // valid, so direct SetRank is safer than re-running current prereqs.
            LeviathanSpecializationNode node = tree.GetNode(nodeId);
            if (node != null)
                state.SetRank(node.Id, state.GetRank(node.Id) + 1);
            return false;
        }

        return true;
    }

    public static int GetTotalSpentPoints(Pilot pilot)
    {
        if (pilot == null)
            return 0;

        RegisterDefaults();

        int spent = 0;
        IList<LeviathanSpecializationTree> trees =
            LeviathanSpecializationRegistry.All();

        for (int t = 0; t < trees.Count; t++)
        {
            LeviathanSpecializationState state = GetState(pilot, trees[t].Id);
            if (state == null)
                continue;

            IList<LeviathanSpecializationNode> nodes = trees[t].Nodes;
            for (int n = 0; n < nodes.Count; n++)
                spent += Math.Max(0, state.GetRank(nodes[n].Id));
        }

        return spent;
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
        GetState(pilot, LeviathanStarfireSpecialization.TreeId);

        LeviathanPilotSpecializationData playerData = data[pilot];
        playerData.Trees.Clear();

        // Recreate empty registered states so current UI references remain sane.
        IList<LeviathanSpecializationTree> trees =
            LeviathanSpecializationRegistry.All();
        for (int i = 0; i < trees.Count; i++)
            playerData.Trees[trees[i].Id] = new LeviathanSpecializationState();

        return LeviathanSpecializationPersistence.Save(
            pilot,
            playerData.Trees,
            out reason
        );
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

    public static float GetMultiplier(
        Pilot pilot,
        string treeId,
        string statId)
    {
        RegisterDefaults();
        LeviathanSpecializationTree tree = LeviathanSpecializationRegistry.Get(treeId);
        LeviathanSpecializationState state = GetState(pilot, treeId);
        return tree == null || state == null
            ? 1f
            : state.GetMultiplier(tree, statId);
    }

    public static bool HasFlag(
        Pilot pilot,
        string treeId,
        string flagId)
    {
        RegisterDefaults();
        LeviathanSpecializationTree tree = LeviathanSpecializationRegistry.Get(treeId);
        LeviathanSpecializationState state = GetState(pilot, treeId);
        return tree != null && state != null && state.HasFlag(tree, flagId);
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
