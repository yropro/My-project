using StarVortex;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Leviathan-owned policy adapters for the shared specialization engine.
/// Currency, persistence namespace and default tree catalog intentionally live
/// outside Core so another standalone class can supply different policy.
/// </summary>
public sealed class LeviathanEvolutionPointBank : ICoreSpecializationPointBank
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
            reason = "Evolution Point source skill is unavailable: " + ex.Message;
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
        int spent = CoreSpecializationRuntime.GetTotalSpentPoints(pilot);
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
            reason = "Not enough Evolution Points.";
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
        Dictionary<string, CoreSpecializationState> states,
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

                CoreSpecializationTree tree =
                    CoreSpecializationRegistry.Get(parts[0]);

                if (tree == null)
                    continue;

                CoreSpecializationNode node = tree.GetNode(parts[1]);
                if (node == null || node.AutoGranted)
                    continue;

                CoreSpecializationState state;
                if (!states.TryGetValue(tree.Id, out state))
                {
                    state = new CoreSpecializationState();
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
            lines.Add("# Leviathan specialization state v3");

            foreach (KeyValuePair<string, CoreSpecializationState> treeState in states)
            {
                CoreSpecializationTree tree =
                    CoreSpecializationRegistry.Get(treeState.Key);

                if (tree == null)
                    continue;

                IList<CoreSpecializationNode> nodes = tree.Nodes;
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

public static class LeviathanSpecializationCatalog
{
    private static bool registered;

    public static void RegisterAll()
    {
        if (registered)
            return;

        RegisterCurrentDefinitions();
        registered = true;
    }

    public static void RebuildAll()
    {
        CoreSpecializationRegistry.Clear();
        RegisterCurrentDefinitions();
        registered = true;
    }

    private static void RegisterCurrentDefinitions()
    {
        CoreSpecializationRegistry.Register(LeviathanEvolutionTree.Create());
        CoreSpecializationRegistry.Register(LeviathanGrowthTree.Create());
        CoreSpecializationRegistry.Register(LeviathanStarfireTree.Create());
        CoreSpecializationRegistry.Register(LeviathanConstrictorTree.Create());
        CoreSpecializationRegistry.Register(LeviathanPredatorTree.Create());
        CoreSpecializationRegistry.Register(LeviathanBehemothTree.Create());
        CoreSpecializationRegistry.Register(LeviathanStellarConverterTree.Create());
    }
}
