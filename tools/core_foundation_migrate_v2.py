from pathlib import Path
import re, subprocess

ROOT = Path.cwd()
S = ROOT / 'Assets/Leviathan/Content/Scripts'


def fail(msg):
    raise RuntimeError('[CoreMigrationV2] ' + msg)


def read(path):
    return path.read_text(encoding='utf-8-sig')


def write(path, text):
    bom = path.exists() and path.read_bytes().startswith(b'\xef\xbb\xbf')
    path.write_text(text, encoding='utf-8-sig' if bom else 'utf-8', newline='')


def replace_once(text, old, new, label):
    if old not in text:
        fail('missing anchor: ' + label)
    return text.replace(old, new, 1)


def git(*args):
    subprocess.run(['git', *args], cwd=ROOT, check=True)


def extract_block(text, marker, next_marker=None):
    start = text.find(marker)
    if start < 0:
        fail('missing block ' + marker)
    if next_marker:
        end = text.find(next_marker, start)
        if end < 0:
            fail('missing end marker ' + next_marker)
    else:
        end = len(text)
    return text[:start] + text[end:], text[start:end].rstrip() + '\n'


def replace_method(text, signature, replacement):
    start = text.find(signature)
    if start < 0:
        fail('missing method signature: ' + signature)
    brace = text.find('{', start)
    if brace < 0:
        fail('missing method body: ' + signature)
    depth = 0
    end = None
    for i in range(brace, len(text)):
        c = text[i]
        if c == '{':
            depth += 1
        elif c == '}':
            depth -= 1
            if depth == 0:
                end = i + 1
                break
    if end is None:
        fail('unbalanced method body: ' + signature)
    return text[:start] + replacement.rstrip() + text[end:]


promoted = {
    'LeviathanCombat.cs': 'CoreCombat.cs',
    'LeviathanCombatHistory.cs': 'CoreCombatHistory.cs',
    'LeviathanCombatState.cs': 'CoreCombatState.cs',
    'LeviathanNetwork.cs': 'CoreNetwork.cs',
    'LeviathanSpecializationFramework.cs': 'CoreSpecializationFramework.cs',
}

for old, new in promoted.items():
    if not (S / old).exists():
        fail('missing ' + old)
    if (S / new).exists():
        fail('target already exists ' + new)

if not (S / 'CoreClassRuntime.cs').exists():
    fail('CoreClassRuntime.cs must exist before migration')

# Remove the three class-owned policy implementations from the framework before
# generic declaration discovery. They are replaced below by a generic policy
# contract + generic persistence + a thin Leviathan policy implementation.
framework_path = S / 'LeviathanSpecializationFramework.cs'
fw = read(framework_path)
fw, _legacy_point_bank = extract_block(
    fw,
    'public sealed class LeviathanEvolutionPointBank',
    'public static class LeviathanSpecializationPersistence')
fw, _legacy_persistence = extract_block(
    fw,
    'public static class LeviathanSpecializationPersistence',
    'internal struct LeviathanSpecializationAggregateCacheValue')
fw, _legacy_catalog = extract_block(
    fw,
    'public static class LeviathanSpecializationCatalog')
write(framework_path, fw.rstrip() + '\n')

# Discover declarations that are genuinely shared and rename only those symbols.
type_re = re.compile(
    r'^\s*(?:(?:public|internal|private|protected)\s+)?'
    r'(?:(?:sealed|abstract|static|partial|readonly)\s+)*'
    r'(?:class|struct|interface|enum)\s+(I?Leviathan[A-Za-z0-9_]+)\b',
    re.M)

rename = {}
for filename in promoted:
    for match in type_re.finditer(read(S / filename)):
        old = match.group(1)
        if old.startswith('ILeviathan'):
            new = 'ICore' + old[len('ILeviathan'):]
        else:
            new = 'Core' + old[len('Leviathan'):]
        rename.setdefault(old, new)

required = {
    'LeviathanCombat': 'CoreCombat',
    'LeviathanCombatHistory': 'CoreCombatHistory',
    'LeviathanCombatState': 'CoreCombatState',
    'LeviathanNetwork': 'CoreNetwork',
    'LeviathanSpecializationRuntime': 'CoreSpecializationRuntime',
    'LeviathanSpecializationRegistry': 'CoreSpecializationRegistry',
}
for old, new in required.items():
    if rename.get(old) != new:
        fail('required mapping absent: ' + old)

ordered = sorted(rename.items(), key=lambda p: len(p[0]), reverse=True)
excluded_dirs = {'Library', 'Temp', 'obj', 'bin', 'Logs', 'PackagesCache'}

for path in ROOT.rglob('*.cs'):
    if any(part in excluded_dirs for part in path.parts):
        continue
    before = read(path)
    after = before
    for old, new in ordered:
        after = re.sub(
            r'(?<![A-Za-z0-9_])' + re.escape(old) + r'(?![A-Za-z0-9_])',
            new,
            after)
    if after != before:
        write(path, after)

# -----------------------------------------------------------------------------
# Shared combat/network ids and truthful Core naming.
# -----------------------------------------------------------------------------
combat_path = S / 'LeviathanCombat.cs'
combat = read(combat_path)
combat = combat.replace(
    'Shared Leviathan combat provenance/correlation foundation.',
    'Shared project combat provenance/correlation foundation.')
combat = replace_once(
    combat,
    '        public const byte DronesTurretsBeacons = 7;',
    '        public const byte DronesTurretsBeacons = 7;\n'
    '        public const byte Orrery = 8;',
    'Orrery shared skill id')
combat = replace_once(
    combat,
    '        WeaponSlot = 8,',
    '        WeaponSlot = 8,\n'
    '        Satellite = 9,',
    'Satellite contributor kind')
write(combat_path, combat)

state_path = S / 'LeviathanCombatState.cs'
write(
    state_path,
    read(state_path).replace(
        'Bounded local registry for temporary Leviathan combat truth.',
        'Bounded local registry for temporary semantic combat truth.'))

spec_path = S / 'LeviathanSpecializationFramework.cs'
spec = read(spec_path).replace(
    '// LEVIATHAN SPECIALIZATION FRAMEWORK',
    '// CORE SPECIALIZATION FRAMEWORK')
write(spec_path, spec)

# -----------------------------------------------------------------------------
# Generic class registration and transport gating.
# -----------------------------------------------------------------------------
mod_path = S / 'LeviathanMod.cs'
mod = read(mod_path)
mod = replace_once(
    mod,
    '        LeviathanSkillSystem.Register();',
    '''        LeviathanSkillSystem.Register();

        CoreClassRuntime.RegisterLocalClass(
            CoreClassId.Leviathan,
            delegate(Pilot pilot)
            {
                return pilot != null &&
                    pilot.GetUpgradeLevel(
                        LeviathanSpecializationCurrency.UpgradeKey) >= 1;
            });

        CoreSpecializationPolicies.Register(
            new LeviathanSpecializationPolicy());''',
    'Leviathan Core registrations')
write(mod_path, mod)

network_path = S / 'LeviathanNetwork.cs'
network = read(network_path)
network = replace_once(
    network,
    '    public const byte SlotBehemoth = 5;',
    '    public const byte SlotBehemoth = 5;\n'
    '    public const byte SlotOrrery = 6;',
    'Orrery slot')
network = replace_once(
    network,
    '    private static int specBurstRemaining;',
    '    private static int specBurstRemaining;\n'
    '    private static int classClearBurstRemaining;\n'
    '    private static int lastSeenClassTransitionRevision = int.MinValue;',
    'class-clear fields')
network = replace_once(
    network,
    '''            // Nothing to say if this player is not a Leviathan.
            if (!LeviathanMod.PlayerHasLeviathan())
                return;

            bool wantSpec = ShouldSendSpecBlock();''',
    '''            bool hasActiveClass = CoreClassRuntime.HasActiveLocalClass;
            int classTransitionRevision = CoreClassRuntime.TransitionRevision;

            if (lastSeenClassTransitionRevision == int.MinValue)
            {
                // Establish the initial baseline without manufacturing a clear
                // burst before a real custom-class transition occurs.
                lastSeenClassTransitionRevision = classTransitionRevision;
            }
            else if (classTransitionRevision != lastSeenClassTransitionRevision)
            {
                lastSeenClassTransitionRevision = classTransitionRevision;
                classClearBurstRemaining = hasActiveClass ? 0 : SpecBurstPackets;
            }

            bool publishingClassClear =
                !hasActiveClass && classClearBurstRemaining > 0;

            if (!hasActiveClass && !publishingClassClear)
                return;

            if (publishingClassClear)
                ClearLocalSlots();

            bool wantSpec = hasActiveClass && ShouldSendSpecBlock();''',
    'generic active-class gate')
network = replace_once(
    network,
    '''            if ((blockFlags & BlockFlagSpec) != 0 && specBurstRemaining > 0)
                specBurstRemaining--;''',
    '''            if ((blockFlags & BlockFlagSpec) != 0 && specBurstRemaining > 0)
                specBurstRemaining--;

            if (publishingClassClear &&
                (blockFlags & BlockFlagDynamic) != 0 &&
                classClearBurstRemaining > 0)
            {
                classClearBurstRemaining--;
            }''',
    'class-clear decrement')
network = replace_once(
    network,
    '        ResetCombatTransport();',
    '        CoreClassRuntime.Reset();\n'
    '        ResetCombatTransport();',
    'Core class reset')
write(network_path, network)

# Canonical Orrery reservations now live in the shared registries.
orrery_combat_path = S / 'OrreryCombat.cs'
orrery_combat = read(orrery_combat_path)
orrery_combat = replace_once(
    orrery_combat,
    '    public const byte SkillId = 8;',
    '    public const byte SkillId = CoreCombat.SkillIds.Orrery;',
    'Orrery combat id')
orrery_combat = replace_once(
    orrery_combat,
    '    public const byte SatelliteContributorKindId = 9;',
    '    public const byte SatelliteContributorKindId =\n'
    '        (byte)CoreCombat.ContributorKind.Satellite;',
    'Orrery contributor id')
orrery_combat = orrery_combat.replace(
    '(CoreCombat.ContributorKind)SatelliteContributorKindId,',
    'CoreCombat.ContributorKind.Satellite,')
write(orrery_combat_path, orrery_combat)

orrery_network_path = S / 'OrreryNetwork.cs'
orrery_network = read(orrery_network_path)
orrery_network = replace_once(
    orrery_network,
    '    public const byte SharedSlotId = 6;',
    '    public const byte SharedSlotId = CoreNetwork.SlotOrrery;',
    'Orrery network id')
write(orrery_network_path, orrery_network)

# -----------------------------------------------------------------------------
# Generic specialization policy + point accounting + persistence.
# -----------------------------------------------------------------------------
core_policy = r'''using StarVortex;
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
'''

(S / 'CoreSpecializationPolicy.cs').write_text(
    core_policy,
    encoding='utf-8',
    newline='')
(S / 'CoreSpecializationPolicy.cs.meta').write_text(
    '''fileFormatVersion: 2\nguid: 2747a41d42384ab490dd12908902e26f\nMonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n''',
    encoding='utf-8')

leviathan_policy = r'''using StarVortex;
using System;

/// <summary>
/// Leviathan-specific progression and presentation plugged into the shared Core
/// specialization engine. Backend accounting remains generic Specialization
/// Points; the player-facing currency is Evolution Points.
/// </summary>
public sealed class LeviathanSpecializationPolicy : ICoreSpecializationPolicy
{
    public CoreClassId ClassId
    {
        get { return CoreClassId.Leviathan; }
    }

    public string ProgressionName
    {
        get { return LeviathanSpecializationCurrency.SkillName; }
    }

    public string PointCurrencyName
    {
        get { return LeviathanSpecializationCurrency.CurrencyName; }
    }

    public void EnsurePrerequisitesRegistered()
    {
        LeviathanSpecializationCurrency.EnsureRegistered();
    }

    public int GetProgressionRank(Pilot pilot)
    {
        if (pilot == null)
            return 0;

        EnsurePrerequisitesRegistered();
        return Math.Max(
            0,
            pilot.GetUpgradeLevel(LeviathanSpecializationCurrency.UpgradeKey));
    }

    public int GetGrantedPoints(Pilot pilot)
    {
        return GetProgressionRank(pilot) *
            LeviathanSpecializationCurrency.PointsPerRank;
    }

    public void RegisterTrees()
    {
        CoreSpecializationPolicies.RegisterTree(
            ClassId,
            LeviathanEvolutionTree.Create());
        CoreSpecializationPolicies.RegisterTree(
            ClassId,
            LeviathanGrowthTree.Create());
        CoreSpecializationPolicies.RegisterTree(
            ClassId,
            LeviathanStarfireTree.Create());
        CoreSpecializationPolicies.RegisterTree(
            ClassId,
            LeviathanConstrictorTree.Create());
        CoreSpecializationPolicies.RegisterTree(
            ClassId,
            LeviathanPredatorTree.Create());
        CoreSpecializationPolicies.RegisterTree(
            ClassId,
            LeviathanBehemothTree.Create());
        CoreSpecializationPolicies.RegisterTree(
            ClassId,
            LeviathanStellarConverterTree.Create());
    }
}
'''

(S / 'LeviathanSpecializationPolicy.cs').write_text(
    leviathan_policy,
    encoding='utf-8',
    newline='')
(S / 'LeviathanSpecializationPolicy.cs.meta').write_text(
    '''fileFormatVersion: 2\nguid: 4860d43b26ea42a293bcd0621136692c\nMonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n''',
    encoding='utf-8')

# -----------------------------------------------------------------------------
# Remove point-bank abstraction and connect Core runtime to generic services.
# -----------------------------------------------------------------------------
spec = read(spec_path)
spec, _old_point_bank_interface = extract_block(
    spec,
    'public interface ICoreSpecializationPointBank',
    'internal struct CoreSpecializationAggregateCacheValue')

spec = re.sub(
    r'\n\s*public static ICoreSpecializationPointBank PointBank\s*=\s*new LeviathanEvolutionPointBank\(\);\s*\n',
    '\n',
    spec,
    count=1)

spec = spec.replace(
    'LeviathanSpecializationPersistence.Load(',
    'CoreSpecializationPersistence.Load(')
spec = spec.replace(
    'LeviathanSpecializationPersistence.Save(',
    'CoreSpecializationPersistence.Save(')
spec = spec.replace(
    'LeviathanSpecializationCurrency.EnsureRegistered();',
    'CoreSpecializationPolicies.EnsurePrerequisitesRegistered();')
spec = spec.replace(
    'LeviathanSpecializationCatalog.RegisterAll();',
    'CoreSpecializationPolicies.EnsurePrerequisitesRegistered();')
spec = spec.replace(
    'LeviathanSpecializationCatalog.RebuildAll();',
    'CoreSpecializationPolicies.RebuildAllTrees();')
spec = spec.replace(
    'return PointBank != null && PointBank.IsAvailable(pilot, out reason);',
    'return CoreSpecializationPoints.IsAvailable(pilot, out reason);')
spec = spec.replace(
    'if (!PointBank.TrySpend(pilot, cost, out reason))',
    'if (!CoreSpecializationPoints.TrySpend(pilot, cost, out reason))')
spec = re.sub(
    r'\n\s*if \(PointBank != null\)\s*\{\s*string ignored;\s*PointBank\.TryRefund\(pilot, node\.PointCostPerRank, out ignored\);\s*\}',
    '\n\n        string ignoredRefundReason;\n'
    '        CoreSpecializationPoints.TryRefund(\n'
    '            pilot, node.PointCostPerRank, out ignoredRefundReason);',
    spec,
    count=1,
    flags=re.S)
spec = spec.replace(
    'return PointBank == null ? 0 : PointBank.GetGrantedPoints(pilot);',
    'return CoreSpecializationPoints.GetGrantedPoints(pilot);')
spec = spec.replace(
    'return PointBank == null ? 0 : PointBank.GetAvailablePoints(pilot);',
    'return CoreSpecializationPoints.GetAvailablePoints(pilot);')
spec = spec.replace(
    '"Not enough Evolution Points."',
    '"Not enough " + CoreSpecializationPoints.GetCurrencyName(pilot) + "."')
spec = spec.replace(
    'tree.Name + " is locked. Unlock it from Evolution first."',
    'tree.Name + " is locked."')
spec = spec.replace('GetEvolutionRank(', 'GetProgressionRank(')

# Replace the remaining progression-rank method body with policy-backed logic.
spec = replace_method(
    spec,
    '    public static int GetProgressionRank(Pilot pilot)',
    '''    public static int GetProgressionRank(Pilot pilot)
    {
        RegisterDefaults();
        return CoreSpecializationPoints.GetProgressionRank(pilot);
    }''')

# Replace total-spent accounting with class-partitioned accounting and preserve
# the old public name as "total for the Pilot's active specialization domain".
spec = replace_method(
    spec,
    '    public static int GetTotalSpentPoints(Pilot pilot)',
    '''    public static int GetSpentPointsForClass(
        Pilot pilot,
        CoreClassId classId)
    {
        if (pilot == null || classId == CoreClassId.None)
            return 0;

        RegisterDefaults();
        SynchronizeAllAutoGrantedNodes(pilot);

        int spent = 0;
        IList<CoreSpecializationTree> trees =
            CoreSpecializationRegistry.All();

        for (int i = 0; i < trees.Count; i++)
        {
            if (CoreSpecializationPolicies.GetOwnerClass(trees[i].Id) != classId)
                continue;

            CoreSpecializationState state = GetRawState(pilot, trees[i].Id);
            if (state != null)
                spent += state.GetSpentPointCost(trees[i]);
        }

        return spent;
    }

    public static int GetTotalSpentPoints(Pilot pilot)
    {
        ICoreSpecializationPolicy policy =
            CoreSpecializationPolicies.GetForPilotOrSingle(pilot);
        return policy == null
            ? 0
            : GetSpentPointsForClass(pilot, policy.ClassId);
    }''')

# RegisterDefaults must never know a concrete class.
spec = replace_method(
    spec,
    '    public static void RegisterDefaults()',
    '''    public static void RegisterDefaults()
    {
        if (registeredDefaults)
            return;

        CoreSpecializationPolicies.EnsurePrerequisitesRegistered();
        registeredDefaults = true;
    }''')

# Tree refresh is now a registry-wide Core operation.
spec = replace_method(
    spec,
    '    public static void RefreshTreeDefinitions()',
    '''    public static void RefreshTreeDefinitions()
    {
        CoreSpecializationPolicies.RebuildAllTrees();
        registeredDefaults = true;
        InvalidateConfiguration();

        Pilot pilot = GetCurrentPilot();
        if (pilot != null && data.ContainsKey(pilot))
            SynchronizeAllAutoGrantedNodes(pilot);
    }''')

write(spec_path, spec)

# Rename source+meta while preserving existing Unity GUID identities.
for old, new in promoted.items():
    git('mv', str((S / old).relative_to(ROOT)), str((S / new).relative_to(ROOT)))
    old_meta = S / (old + '.meta')
    if old_meta.exists():
        git(
            'mv',
            str(old_meta.relative_to(ROOT)),
            str((S / (new + '.meta')).relative_to(ROOT)))

# -----------------------------------------------------------------------------
# Audits.
# -----------------------------------------------------------------------------
net = read(S / 'CoreNetwork.cs')
combat = read(S / 'CoreCombat.cs')
framework = read(S / 'CoreSpecializationFramework.cs')

for fragment in [
    'private const byte Magic0 = 0x4C',
    'private const byte Magic1 = 0x56',
    'public const byte ProtocolVersion = 2',
    'public const byte SlotStellarConverter = 1',
    'public const byte SlotStarfire = 2',
    'public const byte SlotPredator = 3',
    'public const byte SlotConstrictor = 4',
    'public const byte SlotBehemoth = 5',
    'public const byte SlotOrrery = 6',
    'CoreClassRuntime.HasActiveLocalClass',
    'classClearBurstRemaining',
]:
    if fragment not in net:
        fail('network invariant missing: ' + fragment)

for fragment in [
    'public const byte Growth = 1',
    'public const byte Predator = 2',
    'public const byte Constrictor = 3',
    'public const byte Behemoth = 4',
    'public const byte Starfire = 5',
    'public const byte StellarConverter = 6',
    'public const byte DronesTurretsBeacons = 7',
    'public const byte Orrery = 8',
    'Satellite = 9',
]:
    if fragment not in combat:
        fail('combat invariant missing: ' + fragment)

if 'if (!LeviathanMod.PlayerHasLeviathan())' in net:
    fail('old Leviathan-only network gate remains')
if 'LeviathanEvolutionPointBank' in framework:
    fail('Leviathan point bank leaked into Core framework')
if 'LeviathanSpecializationPersistence' in framework:
    fail('Leviathan persistence leaked into Core framework')
if 'LeviathanSpecializationCatalog' in framework:
    fail('Leviathan catalog leaked into Core framework')
if 'ICoreSpecializationPointBank' in framework:
    fail('obsolete point-bank abstraction remains')
if 'Evolution Points' in framework:
    fail('Leviathan currency display leaked into Core framework')
if 'GetEvolutionRank' in framework:
    fail('Leviathan progression naming leaked into Core framework')
if 'CoreSpecializationPersistence.Load' not in framework:
    fail('Core persistence is not wired')
if 'CoreSpecializationPoints.GetAvailablePoints' not in framework:
    fail('Core points are not wired')
if 'CoreSpecializationPolicies.RegisterTree' not in read(S / 'LeviathanSpecializationPolicy.cs'):
    fail('Leviathan policy tree registration missing')
if 'CoreCombat.SkillIds.Orrery' not in read(S / 'OrreryCombat.cs'):
    fail('Orrery canonical skill id missing')
if 'CoreCombat.ContributorKind.Satellite' not in read(S / 'OrreryCombat.cs'):
    fail('Orrery canonical contributor kind missing')
if 'CoreNetwork.SlotOrrery' not in read(S / 'OrreryNetwork.cs'):
    fail('Orrery canonical slot missing')

# All declarations promoted from the old shared files must be gone everywhere.
stale = []
for path in ROOT.rglob('*.cs'):
    if any(part in excluded_dirs for part in path.parts):
        continue
    text = read(path)
    for old, _new in ordered:
        if re.search(
            r'(?<![A-Za-z0-9_])' + re.escape(old) + r'(?![A-Za-z0-9_])',
            text):
            stale.append(str(path.relative_to(ROOT)) + ': ' + old)
if stale:
    fail('stale promoted identifiers:\n' + '\n'.join(sorted(set(stale))))

# Core files may mention Leviathan only where compatibility migration explicitly
# needs to identify the old persistence namespace.
for core_name in [
    'CoreCombat.cs',
    'CoreCombatHistory.cs',
    'CoreCombatState.cs',
    'CoreNetwork.cs',
]:
    if 'Leviathan' in read(S / core_name):
        fail(core_name + ' still contains Leviathan semantic coupling')

subprocess.run(['git', 'diff', '--check'], cwd=ROOT, check=True)
print(subprocess.run(
    ['git', 'diff', '--stat'],
    cwd=ROOT,
    text=True,
    capture_output=True,
    check=True).stdout)
