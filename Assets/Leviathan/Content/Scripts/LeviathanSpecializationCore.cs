using System;
using System.Collections.Generic;
using System.Linq;

public enum LeviathanSpecializationNodeType
{
    Root,
    Passive,
    Major,
    Keystone
}

public enum LeviathanSpecializationEffectType
{
    Flat,
    Percent,
    Multiplier,
    Flag,
    UnlockTree
}

public enum LeviathanRequirementKind
{
    Always,
    Rank,
    All,
    Any
}

public enum LeviathanTreeUnlockKind
{
    Always,
    NativeUpgrade,
    SpecializationEffect
}

public enum LeviathanKnobKind
{
    Flat,
    Percent,
    PercentagePoints,
    Multiplier
}

public interface ILeviathanSpecializationRankSource
{
    int GetRank(string nodeId);
}

public abstract class LeviathanRequirement
{
    public abstract LeviathanRequirementKind Kind { get; }
    public abstract bool IsSatisfied(ILeviathanSpecializationRankSource ranks);
    public abstract void CollectNodeIds(HashSet<string> output);
    public abstract string Describe(Func<string, string> nameResolver);

    public virtual bool TryGetSimpleParents(out List<string> nodeIds)
    {
        nodeIds = null;
        return false;
    }
}

public sealed class LeviathanAlwaysRequirement : LeviathanRequirement
{
    public override LeviathanRequirementKind Kind
    {
        get { return LeviathanRequirementKind.Always; }
    }

    public override bool IsSatisfied(ILeviathanSpecializationRankSource ranks)
    {
        return true;
    }

    public override void CollectNodeIds(HashSet<string> output)
    {
    }

    public override string Describe(Func<string, string> nameResolver)
    {
        return "None";
    }
}

public sealed class LeviathanRankRequirement : LeviathanRequirement
{
    public readonly string NodeId;
    public readonly int Rank;

    public LeviathanRankRequirement(string nodeId, int rank)
    {
        NodeId = nodeId;
        Rank = Math.Max(1, rank);
    }

    public override LeviathanRequirementKind Kind
    {
        get { return LeviathanRequirementKind.Rank; }
    }

    public override bool IsSatisfied(ILeviathanSpecializationRankSource ranks)
    {
        return ranks != null && ranks.GetRank(NodeId) >= Rank;
    }

    public override void CollectNodeIds(HashSet<string> output)
    {
        if (output != null && !string.IsNullOrEmpty(NodeId))
            output.Add(NodeId);
    }

    public override string Describe(Func<string, string> nameResolver)
    {
        string name = nameResolver == null ? NodeId : nameResolver(NodeId);
        return name + " " + Rank.ToString();
    }

    public override bool TryGetSimpleParents(out List<string> nodeIds)
    {
        nodeIds = new List<string> { NodeId };
        return true;
    }
}

public abstract class LeviathanCompositeRequirement : LeviathanRequirement
{
    public readonly LeviathanRequirement[] Children;

    protected LeviathanCompositeRequirement(params LeviathanRequirement[] children)
    {
        Children = children == null
            ? new LeviathanRequirement[0]
            : children.Where(c => c != null).ToArray();
    }

    public override void CollectNodeIds(HashSet<string> output)
    {
        if (output == null)
            return;

        for (int i = 0; i < Children.Length; i++)
            Children[i].CollectNodeIds(output);
    }

    public override bool TryGetSimpleParents(out List<string> nodeIds)
    {
        nodeIds = new List<string>();

        for (int i = 0; i < Children.Length; i++)
        {
            LeviathanRankRequirement rank = Children[i] as LeviathanRankRequirement;
            if (rank == null)
            {
                nodeIds = null;
                return false;
            }

            nodeIds.Add(rank.NodeId);
        }

        return nodeIds.Count > 0;
    }
}

public sealed class LeviathanAllRequirement : LeviathanCompositeRequirement
{
    public LeviathanAllRequirement(params LeviathanRequirement[] children)
        : base(children)
    {
    }

    public override LeviathanRequirementKind Kind
    {
        get { return LeviathanRequirementKind.All; }
    }

    public override bool IsSatisfied(ILeviathanSpecializationRankSource ranks)
    {
        for (int i = 0; i < Children.Length; i++)
        {
            if (!Children[i].IsSatisfied(ranks))
                return false;
        }

        return true;
    }

    public override string Describe(Func<string, string> nameResolver)
    {
        if (Children.Length == 0)
            return "None";

        return "ALL: " + string.Join(
            ", ",
            Children.Select(c => c.Describe(nameResolver)).ToArray()
        );
    }
}

public sealed class LeviathanAnyRequirement : LeviathanCompositeRequirement
{
    public LeviathanAnyRequirement(params LeviathanRequirement[] children)
        : base(children)
    {
    }

    public override LeviathanRequirementKind Kind
    {
        get { return LeviathanRequirementKind.Any; }
    }

    public override bool IsSatisfied(ILeviathanSpecializationRankSource ranks)
    {
        if (Children.Length == 0)
            return true;

        for (int i = 0; i < Children.Length; i++)
        {
            if (Children[i].IsSatisfied(ranks))
                return true;
        }

        return false;
    }

    public override string Describe(Func<string, string> nameResolver)
    {
        if (Children.Length == 0)
            return "None";

        return "ANY: " + string.Join(
            ", ",
            Children.Select(c => c.Describe(nameResolver)).ToArray()
        );
    }
}

public static class LeviathanReq
{
    public static readonly LeviathanRequirement None =
        new LeviathanAlwaysRequirement();

    public static LeviathanRequirement Rank(string nodeId, int rank)
    {
        return new LeviathanRankRequirement(nodeId, rank);
    }

    public static LeviathanRequirement Rank(string nodeId)
    {
        return Rank(nodeId, 1);
    }

    public static LeviathanRequirement All(params LeviathanRequirement[] children)
    {
        return new LeviathanAllRequirement(children);
    }

    public static LeviathanRequirement Any(params LeviathanRequirement[] children)
    {
        return new LeviathanAnyRequirement(children);
    }
}

public sealed class LeviathanSpecializationFlag
{
    public readonly string Id;
    public readonly string Name;

    private LeviathanSpecializationFlag(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Flag id is required.", "id");

        Id = id;
        Name = string.IsNullOrWhiteSpace(name) ? id : name;
    }

    public static LeviathanSpecializationFlag Create(
        string id,
        string name)
    {
        return new LeviathanSpecializationFlag(id, name);
    }

    public static LeviathanSpecializationFlag Create(string id)
    {
        return Create(id, id);
    }
}

public sealed class LeviathanSpecializationKnob
{
    public readonly string Id;
    public readonly string Name;
    public readonly LeviathanKnobKind Kind;
    public readonly string UnitSuffix;

    private LeviathanSpecializationKnob(
        string id,
        string name,
        LeviathanKnobKind kind,
        string unitSuffix)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Knob id is required.", "id");

        Id = id;
        Name = string.IsNullOrWhiteSpace(name) ? id : name;
        Kind = kind;
        UnitSuffix = unitSuffix ?? string.Empty;
    }

    public static LeviathanSpecializationKnob Percent(
        string id,
        string name)
    {
        return new LeviathanSpecializationKnob(
            id,
            name,
            LeviathanKnobKind.Percent,
            "%"
        );
    }

    public static LeviathanSpecializationKnob Flat(
        string id,
        string name,
        string unitSuffix)
    {
        return new LeviathanSpecializationKnob(
            id,
            name,
            LeviathanKnobKind.Flat,
            unitSuffix
        );
    }

    public static LeviathanSpecializationKnob Flat(
        string id,
        string name)
    {
        return Flat(id, name, string.Empty);
    }

    // Human-readable percentage points that aggregate additively.
    // Example: +5 on a 10% crit chance becomes 15%, not 10.5%.
    // Tree definitions still write 5f; runtime storage is 0.05f.
    public static LeviathanSpecializationKnob PercentagePoints(
        string id,
        string name)
    {
        return new LeviathanSpecializationKnob(
            id,
            name,
            LeviathanKnobKind.PercentagePoints,
            "%"
        );
    }

    // Multipliers are authored as actual factors. Example: 1.25 means x1.25.
    public static LeviathanSpecializationKnob Multiplier(
        string id,
        string name)
    {
        return new LeviathanSpecializationKnob(
            id,
            name,
            LeviathanKnobKind.Multiplier,
            "x"
        );
    }

    internal bool UsesPercentDefinition
    {
        get
        {
            return Kind == LeviathanKnobKind.Percent ||
                Kind == LeviathanKnobKind.PercentagePoints;
        }
    }

    internal float ConvertDefinitionValue(float value)
    {
        return UsesPercentDefinition
            ? value / 100f
            : value;
    }

    internal string DescribeDefinitionValue(float value)
    {
        string sign = value >= 0f ? "+" : string.Empty;

        if (UsesPercentDefinition)
            return sign + value.ToString("0.###") + "% " + Name;

        if (Kind == LeviathanKnobKind.Multiplier)
            return "x" + value.ToString("0.###") + " " + Name;

        return sign + value.ToString("0.###") +
            (string.IsNullOrEmpty(UnitSuffix) ? " " : UnitSuffix + " ") +
            Name;
    }
}

public sealed class LeviathanSpecializationEffect
{
    public readonly LeviathanSpecializationEffectType Type;
    public readonly string Key;
    public readonly LeviathanSpecializationKnob Knob;
    public LeviathanSpecializationFlag FlagDefinition { get; private set; }

    private readonly bool hasConstantIncrement;
    private readonly float constantIncrement;
    private readonly float[] perRankIncrements;

    private LeviathanSpecializationEffect(
        LeviathanSpecializationEffectType type,
        string key,
        LeviathanSpecializationKnob knob,
        bool hasConstant,
        float constant,
        float[] increments)
    {
        Type = type;
        Key = key ?? string.Empty;
        Knob = knob;
        hasConstantIncrement = hasConstant;
        constantIncrement = constant;
        perRankIncrements = increments == null
            ? null
            : (float[])increments.Clone();
    }

    public static LeviathanSpecializationEffect KnobIncrement(
        LeviathanSpecializationKnob knob,
        float valuePerRank)
    {
        if (knob == null)
            throw new ArgumentNullException("knob");

        return new LeviathanSpecializationEffect(
            knob.Kind == LeviathanKnobKind.Multiplier
                ? LeviathanSpecializationEffectType.Multiplier
                : knob.Kind == LeviathanKnobKind.Percent
                    ? LeviathanSpecializationEffectType.Percent
                    : LeviathanSpecializationEffectType.Flat,
            knob.Id,
            knob,
            true,
            knob.ConvertDefinitionValue(valuePerRank),
            null
        );
    }

    public static LeviathanSpecializationEffect KnobRanks(
        LeviathanSpecializationKnob knob,
        params float[] valuesByRank)
    {
        if (knob == null)
            throw new ArgumentNullException("knob");

        if (valuesByRank == null || valuesByRank.Length == 0)
            throw new ArgumentException("At least one per-rank value is required.", "valuesByRank");

        float[] converted = new float[valuesByRank.Length];
        for (int i = 0; i < valuesByRank.Length; i++)
            converted[i] = knob.ConvertDefinitionValue(valuesByRank[i]);

        return new LeviathanSpecializationEffect(
            knob.Kind == LeviathanKnobKind.Multiplier
                ? LeviathanSpecializationEffectType.Multiplier
                : knob.Kind == LeviathanKnobKind.Percent
                    ? LeviathanSpecializationEffectType.Percent
                    : LeviathanSpecializationEffectType.Flat,
            knob.Id,
            knob,
            false,
            0f,
            converted
        );
    }

    // Compatibility helpers for one-off raw effects. Prefer LeviathanFx with a
    // named knob for ordinary specialization stats.
    public static LeviathanSpecializationEffect Flat(
        string statId,
        float valuePerRank)
    {
        return new LeviathanSpecializationEffect(
            LeviathanSpecializationEffectType.Flat,
            statId,
            null,
            true,
            valuePerRank,
            null
        );
    }

    // Raw decimal: 0.05 = +5% per rank.
    public static LeviathanSpecializationEffect Percent(
        string statId,
        float valuePerRank)
    {
        return new LeviathanSpecializationEffect(
            LeviathanSpecializationEffectType.Percent,
            statId,
            null,
            true,
            valuePerRank,
            null
        );
    }

    // Value is a factor for one rank: 1.10 = x1.10 per rank.
    public static LeviathanSpecializationEffect Multiplier(
        string statId,
        float factorPerRank)
    {
        return new LeviathanSpecializationEffect(
            LeviathanSpecializationEffectType.Multiplier,
            statId,
            null,
            true,
            factorPerRank,
            null
        );
    }

    public static LeviathanSpecializationEffect Flag(string flagId)
    {
        return new LeviathanSpecializationEffect(
            LeviathanSpecializationEffectType.Flag,
            flagId,
            null,
            true,
            1f,
            null
        );
    }

    public static LeviathanSpecializationEffect Flag(
        LeviathanSpecializationFlag flag)
    {
        if (flag == null)
            throw new ArgumentNullException("flag");

        LeviathanSpecializationEffect effect = Flag(flag.Id);
        effect.FlagDefinition = flag;
        return effect;
    }

    public static LeviathanSpecializationEffect UnlockTree(string treeId)
    {
        return new LeviathanSpecializationEffect(
            LeviathanSpecializationEffectType.UnlockTree,
            treeId,
            null,
            true,
            1f,
            null
        );
    }

    public void ValidateForNode(int maxRank)
    {
        if (perRankIncrements != null && perRankIncrements.Length != maxRank)
        {
            throw new InvalidOperationException(
                "Effect '" + Key + "' provides " +
                perRankIncrements.Length.ToString() +
                " per-rank values, but its node has " +
                maxRank.ToString() + " ranks."
            );
        }
    }

    public float GetAccumulatedValue(int rank)
    {
        rank = Math.Max(0, rank);

        if (rank == 0)
            return Type == LeviathanSpecializationEffectType.Multiplier ? 1f : 0f;

        if (Type == LeviathanSpecializationEffectType.Multiplier)
        {
            if (hasConstantIncrement)
                return (float)Math.Pow(constantIncrement, rank);

            float factor = 1f;
            int count = Math.Min(rank, perRankIncrements == null ? 0 : perRankIncrements.Length);
            for (int i = 0; i < count; i++)
                factor *= perRankIncrements[i];
            return factor;
        }

        if (hasConstantIncrement)
            return constantIncrement * rank;

        float total = 0f;
        int limit = Math.Min(rank, perRankIncrements == null ? 0 : perRankIncrements.Length);
        for (int i = 0; i < limit; i++)
            total += perRankIncrements[i];
        return total;
    }

    public string DescribeRankContribution(
        int rankIndex,
        Func<string, string> fallbackResolver)
    {
        rankIndex = Math.Max(1, rankIndex);

        if (Type == LeviathanSpecializationEffectType.Flag)
        {
            string flagName = FlagDefinition != null
                ? FlagDefinition.Name
                : fallbackResolver == null ? Key : fallbackResolver(Key);
            return "Enables " + flagName;
        }

        if (Type == LeviathanSpecializationEffectType.UnlockTree)
        {
            LeviathanSpecializationTree tree = LeviathanSpecializationRegistry.Get(Key);
            string treeName = tree == null ? Key : tree.Name;
            return "Unlocks the " + treeName + " specialization tree";
        }

        if (Knob != null)
        {
            float rawDefinitionValue;
            if (hasConstantIncrement)
            {
                rawDefinitionValue = Knob.UsesPercentDefinition
                    ? constantIncrement * 100f
                    : constantIncrement;
            }
            else
            {
                int index = Math.Min(rankIndex - 1, perRankIncrements.Length - 1);
                rawDefinitionValue = Knob.UsesPercentDefinition
                    ? perRankIncrements[index] * 100f
                    : perRankIncrements[index];
            }

            return Knob.DescribeDefinitionValue(rawDefinitionValue);
        }

        string name = fallbackResolver == null ? Key : fallbackResolver(Key);

        if (Type == LeviathanSpecializationEffectType.Flat)
        {
            float value = hasConstantIncrement
                ? constantIncrement
                : perRankIncrements[Math.Min(rankIndex - 1, perRankIncrements.Length - 1)];
            return (value >= 0f ? "+" : string.Empty) +
                value.ToString("0.###") + " " + name;
        }

        if (Type == LeviathanSpecializationEffectType.Percent)
        {
            float value = hasConstantIncrement
                ? constantIncrement
                : perRankIncrements[Math.Min(rankIndex - 1, perRankIncrements.Length - 1)];
            return (value >= 0f ? "+" : string.Empty) +
                (value * 100f).ToString("0.###") + "% " + name;
        }

        float factor = hasConstantIncrement
            ? constantIncrement
            : perRankIncrements[Math.Min(rankIndex - 1, perRankIncrements.Length - 1)];
        return "x" + factor.ToString("0.###") + " " + name;
    }
}

public static class LeviathanFx
{
    // Example: Increment(StarfireKnobs.Width, 5f) => +5% every rank when Width
    // is a percent knob. Negative values work identically.
    public static LeviathanSpecializationEffect Increment(
        LeviathanSpecializationKnob knob,
        float valuePerRank)
    {
        return LeviathanSpecializationEffect.KnobIncrement(knob, valuePerRank);
    }

    // Example: Ranks(StarfireKnobs.Width, 5f, 5f, 10f) => rank investments add
    // +5%, then +5%, then +10%, for +20% total at rank 3.
    public static LeviathanSpecializationEffect Ranks(
        LeviathanSpecializationKnob knob,
        params float[] valuesByRank)
    {
        return LeviathanSpecializationEffect.KnobRanks(knob, valuesByRank);
    }

    // Explicit multiplier helper for multiplier knobs. Example:
    // Multiply(knob, 1.25f) => x1.25 for each purchased rank.
    public static LeviathanSpecializationEffect Multiply(
        LeviathanSpecializationKnob knob,
        float factorPerRank)
    {
        if (knob == null)
            throw new ArgumentNullException("knob");
        if (knob.Kind != LeviathanKnobKind.Multiplier)
            throw new ArgumentException(
                "Multiply requires a Multiplier knob.",
                "knob"
            );

        return LeviathanSpecializationEffect.KnobIncrement(
            knob,
            factorPerRank
        );
    }

    public static LeviathanSpecializationEffect MultiplyRanks(
        LeviathanSpecializationKnob knob,
        params float[] factorsByRank)
    {
        if (knob == null)
            throw new ArgumentNullException("knob");
        if (knob.Kind != LeviathanKnobKind.Multiplier)
            throw new ArgumentException(
                "MultiplyRanks requires a Multiplier knob.",
                "knob"
            );

        return LeviathanSpecializationEffect.KnobRanks(
            knob,
            factorsByRank
        );
    }

    public static LeviathanSpecializationEffect Flag(string flagId)
    {
        return LeviathanSpecializationEffect.Flag(flagId);
    }

    public static LeviathanSpecializationEffect Flag(
        LeviathanSpecializationFlag flag)
    {
        return LeviathanSpecializationEffect.Flag(flag);
    }

    public static LeviathanSpecializationEffect UnlockTree(string treeId)
    {
        return LeviathanSpecializationEffect.UnlockTree(treeId);
    }
}

public sealed class LeviathanSpecializationNode
{
    public readonly string Id;
    public readonly string Name;
    public readonly int MaxRank;
    public readonly LeviathanSpecializationNodeType Type;
    public readonly LeviathanRequirement Requirement;
    public readonly string ExclusiveGroup;
    public readonly string Description;
    public readonly int PointCostPerRank;
    public readonly bool AutoGranted;
    public readonly LeviathanSpecializationEffect[] Effects;

    public LeviathanSpecializationNode(
        string id,
        string name,
        int maxRank,
        LeviathanSpecializationNodeType type,
        LeviathanRequirement requirement,
        string exclusiveGroup,
        string description,
        params LeviathanSpecializationEffect[] effects)
        : this(
            id,
            name,
            maxRank,
            type,
            requirement,
            exclusiveGroup,
            description,
            1,
            false,
            effects)
    {
    }

    public LeviathanSpecializationNode(
        string id,
        string name,
        int maxRank,
        LeviathanSpecializationNodeType type,
        LeviathanRequirement requirement,
        string exclusiveGroup,
        string description,
        int pointCostPerRank,
        bool autoGranted,
        params LeviathanSpecializationEffect[] effects)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Node id is required.", "id");

        Id = id;
        Name = string.IsNullOrWhiteSpace(name) ? id : name;
        MaxRank = Math.Max(1, maxRank);
        Type = type;
        Requirement = requirement ?? LeviathanReq.None;
        ExclusiveGroup = exclusiveGroup ?? string.Empty;
        Description = description ?? string.Empty;
        PointCostPerRank = Math.Max(0, pointCostPerRank);
        AutoGranted = autoGranted;
        Effects = effects == null
            ? new LeviathanSpecializationEffect[0]
            : effects.Where(e => e != null).ToArray();

        for (int i = 0; i < Effects.Length; i++)
            Effects[i].ValidateForNode(MaxRank);
    }
}

public static class LeviathanNode
{
    public static LeviathanSpecializationNode GrantedRoot(
        string id,
        string name,
        string description)
    {
        return new LeviathanSpecializationNode(
            id,
            name,
            1,
            LeviathanSpecializationNodeType.Root,
            LeviathanReq.None,
            null,
            description,
            0,
            true
        );
    }

    public static LeviathanSpecializationNode Passive(
        string id,
        string name,
        int maxRank,
        LeviathanRequirement requirement,
        string description,
        params LeviathanSpecializationEffect[] effects)
    {
        return new LeviathanSpecializationNode(
            id,
            name,
            maxRank,
            LeviathanSpecializationNodeType.Passive,
            requirement,
            null,
            description,
            effects
        );
    }

    public static LeviathanSpecializationNode Major(
        string id,
        string name,
        int maxRank,
        LeviathanRequirement requirement,
        string description,
        params LeviathanSpecializationEffect[] effects)
    {
        return new LeviathanSpecializationNode(
            id,
            name,
            maxRank,
            LeviathanSpecializationNodeType.Major,
            requirement,
            null,
            description,
            effects
        );
    }

    public static LeviathanSpecializationNode Keystone(
        string id,
        string name,
        LeviathanRequirement requirement,
        string exclusiveGroup,
        string description,
        params LeviathanSpecializationEffect[] effects)
    {
        return new LeviathanSpecializationNode(
            id,
            name,
            1,
            LeviathanSpecializationNodeType.Keystone,
            requirement,
            exclusiveGroup,
            description,
            effects
        );
    }
}

public sealed class LeviathanSpecializationTree
{
    public readonly string Id;
    public readonly string Name;
    public readonly string RootNodeId;
    public readonly int DisplayOrder;
    public readonly LeviathanTreeUnlockKind UnlockKind;
    public readonly int NativeUnlockUpgradeKey;

    private readonly List<LeviathanSpecializationNode> nodes =
        new List<LeviathanSpecializationNode>();

    private readonly Dictionary<string, LeviathanSpecializationNode> byId =
        new Dictionary<string, LeviathanSpecializationNode>(StringComparer.Ordinal);

    public LeviathanSpecializationTree(
        string id,
        string name,
        string rootNodeId,
        int displayOrder,
        LeviathanTreeUnlockKind unlockKind,
        int nativeUnlockUpgradeKey)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Tree id is required.", "id");

        Id = id;
        Name = string.IsNullOrWhiteSpace(name) ? id : name;
        RootNodeId = rootNodeId ?? string.Empty;
        DisplayOrder = displayOrder;
        UnlockKind = unlockKind;
        NativeUnlockUpgradeKey = nativeUnlockUpgradeKey;
    }

    public IList<LeviathanSpecializationNode> Nodes
    {
        get { return nodes.AsReadOnly(); }
    }

    public LeviathanSpecializationTree Add(LeviathanSpecializationNode node)
    {
        if (node == null)
            throw new ArgumentNullException("node");

        if (byId.ContainsKey(node.Id))
            throw new InvalidOperationException(
                "Duplicate specialization node id '" + node.Id + "'."
            );

        nodes.Add(node);
        byId.Add(node.Id, node);
        return this;
    }

    public LeviathanSpecializationNode GetNode(string nodeId)
    {
        LeviathanSpecializationNode node;
        return nodeId != null && byId.TryGetValue(nodeId, out node)
            ? node
            : null;
    }

    public string GetNodeName(string nodeId)
    {
        LeviathanSpecializationNode node = GetNode(nodeId);
        return node == null ? nodeId : node.Name;
    }

    public IList<LeviathanSpecializationNode> GetExclusiveGroupMembers(string group)
    {
        if (string.IsNullOrEmpty(group))
            return new List<LeviathanSpecializationNode>().AsReadOnly();

        return nodes
            .Where(n => string.Equals(
                n.ExclusiveGroup,
                group,
                StringComparison.Ordinal))
            .ToList()
            .AsReadOnly();
    }

    public void Validate()
    {
        if (!string.IsNullOrEmpty(RootNodeId))
        {
            LeviathanSpecializationNode root = GetNode(RootNodeId);
            if (root == null)
            {
                throw new InvalidOperationException(
                    "Tree '" + Id + "' is missing root node '" + RootNodeId + "'."
                );
            }

            if (!root.AutoGranted)
            {
                throw new InvalidOperationException(
                    "Tree '" + Id + "' root node must be auto-granted."
                );
            }
        }

        for (int i = 0; i < nodes.Count; i++)
        {
            HashSet<string> dependencies = new HashSet<string>(StringComparer.Ordinal);
            nodes[i].Requirement.CollectNodeIds(dependencies);

            foreach (string dependency in dependencies)
            {
                if (!byId.ContainsKey(dependency))
                {
                    throw new InvalidOperationException(
                        "Tree '" + Id + "': node '" + nodes[i].Id +
                        "' requires missing node '" + dependency + "'."
                    );
                }
            }
        }

        Dictionary<string, int> marks =
            new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < nodes.Count; i++)
            VisitForCycle(nodes[i].Id, marks, new Stack<string>());
    }

    private void VisitForCycle(
        string nodeId,
        Dictionary<string, int> marks,
        Stack<string> path)
    {
        int mark;
        if (marks.TryGetValue(nodeId, out mark))
        {
            if (mark == 2)
                return;

            if (mark == 1)
            {
                string cycle = string.Join(" -> ", path.Reverse().ToArray());
                throw new InvalidOperationException(
                    "Tree '" + Id + "' contains a prerequisite cycle near '" +
                    nodeId + "': " + cycle + " -> " + nodeId
                );
            }
        }

        marks[nodeId] = 1;
        path.Push(nodeId);

        HashSet<string> dependencies = new HashSet<string>(StringComparer.Ordinal);
        byId[nodeId].Requirement.CollectNodeIds(dependencies);

        foreach (string dependency in dependencies)
            VisitForCycle(dependency, marks, path);

        path.Pop();
        marks[nodeId] = 2;
    }
}

public sealed class LeviathanSpecializationState : ILeviathanSpecializationRankSource
{
    private readonly Dictionary<string, int> ranks =
        new Dictionary<string, int>(StringComparer.Ordinal);

    public int GetRank(string nodeId)
    {
        int rank;
        return nodeId != null && ranks.TryGetValue(nodeId, out rank)
            ? rank
            : 0;
    }

    public void SetRank(string nodeId, int rank)
    {
        if (string.IsNullOrEmpty(nodeId))
            return;

        if (rank <= 0)
            ranks.Remove(nodeId);
        else
            ranks[nodeId] = rank;
    }

    public IDictionary<string, int> Snapshot()
    {
        return new Dictionary<string, int>(ranks, StringComparer.Ordinal);
    }

    public bool CanInvest(
        LeviathanSpecializationTree tree,
        string nodeId,
        out string reason)
    {
        reason = string.Empty;

        if (tree == null)
        {
            reason = "No tree.";
            return false;
        }

        LeviathanSpecializationNode node = tree.GetNode(nodeId);
        if (node == null)
        {
            reason = "Unknown node.";
            return false;
        }

        if (node.AutoGranted)
        {
            reason = "Granted automatically when this tree is unlocked.";
            return false;
        }

        int current = GetRank(node.Id);
        if (current >= node.MaxRank)
        {
            reason = "Already at maximum rank.";
            return false;
        }

        if (!node.Requirement.IsSatisfied(this))
        {
            reason = "Prerequisites are not satisfied.";
            return false;
        }

        if (!string.IsNullOrEmpty(node.ExclusiveGroup))
        {
            IList<LeviathanSpecializationNode> group =
                tree.GetExclusiveGroupMembers(node.ExclusiveGroup);

            for (int i = 0; i < group.Count; i++)
            {
                if (group[i].Id != node.Id && GetRank(group[i].Id) > 0)
                {
                    reason = "Mutually exclusive with " + group[i].Name + ".";
                    return false;
                }
            }
        }

        return true;
    }

    public bool TryInvest(
        LeviathanSpecializationTree tree,
        string nodeId,
        out string reason)
    {
        if (!CanInvest(tree, nodeId, out reason))
            return false;

        LeviathanSpecializationNode node = tree.GetNode(nodeId);
        SetRank(node.Id, GetRank(node.Id) + 1);
        return true;
    }

    public bool CanRefund(
        LeviathanSpecializationTree tree,
        string nodeId,
        out string reason)
    {
        reason = string.Empty;

        if (tree == null)
        {
            reason = "No tree.";
            return false;
        }

        LeviathanSpecializationNode node = tree.GetNode(nodeId);
        if (node == null || GetRank(nodeId) <= 0)
        {
            reason = "No rank to refund.";
            return false;
        }

        if (node.AutoGranted)
        {
            reason = "Granted root nodes cannot be refunded directly.";
            return false;
        }

        int oldRank = GetRank(nodeId);
        SetRank(nodeId, oldRank - 1);

        string invalid;
        bool valid = ValidateInvestedState(tree, out invalid);

        SetRank(nodeId, oldRank);

        if (!valid)
        {
            reason = "Required by " + invalid + ".";
            return false;
        }

        return true;
    }

    public bool TryRefund(
        LeviathanSpecializationTree tree,
        string nodeId,
        out string reason)
    {
        if (!CanRefund(tree, nodeId, out reason))
            return false;

        SetRank(nodeId, GetRank(nodeId) - 1);
        return true;
    }

    public bool ValidateInvestedState(
        LeviathanSpecializationTree tree,
        out string invalidNodeName)
    {
        invalidNodeName = string.Empty;

        if (tree == null)
            return false;

        IList<LeviathanSpecializationNode> nodes = tree.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            LeviathanSpecializationNode node = nodes[i];
            int rank = GetRank(node.Id);

            if (rank <= 0)
                continue;

            if (rank > node.MaxRank || !node.Requirement.IsSatisfied(this))
            {
                invalidNodeName = node.Name;
                return false;
            }

            if (!string.IsNullOrEmpty(node.ExclusiveGroup))
            {
                int investedInGroup = tree
                    .GetExclusiveGroupMembers(node.ExclusiveGroup)
                    .Count(n => GetRank(n.Id) > 0);

                if (investedInGroup > 1)
                {
                    invalidNodeName = node.Name;
                    return false;
                }
            }
        }

        return true;
    }

    public int GetSpentPointCost(LeviathanSpecializationTree tree)
    {
        if (tree == null)
            return 0;

        int total = 0;
        IList<LeviathanSpecializationNode> nodes = tree.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].AutoGranted)
                continue;

            total += Math.Max(0, GetRank(nodes[i].Id)) *
                Math.Max(0, nodes[i].PointCostPerRank);
        }

        return total;
    }

    public bool HasUnlockTreeEffect(
        LeviathanSpecializationTree tree,
        string targetTreeId)
    {
        if (tree == null || string.IsNullOrEmpty(targetTreeId))
            return false;

        IList<LeviathanSpecializationNode> nodes = tree.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (GetRank(nodes[i].Id) <= 0)
                continue;

            for (int e = 0; e < nodes[i].Effects.Length; e++)
            {
                LeviathanSpecializationEffect effect = nodes[i].Effects[e];
                if (effect.Type == LeviathanSpecializationEffectType.UnlockTree &&
                    string.Equals(effect.Key, targetTreeId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool HasFlag(
        LeviathanSpecializationTree tree,
        string flagId)
    {
        if (tree == null || string.IsNullOrEmpty(flagId))
            return false;

        IList<LeviathanSpecializationNode> nodes = tree.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            int rank = GetRank(nodes[i].Id);
            if (rank <= 0)
                continue;

            for (int e = 0; e < nodes[i].Effects.Length; e++)
            {
                LeviathanSpecializationEffect effect = nodes[i].Effects[e];
                if (effect.Type == LeviathanSpecializationEffectType.Flag &&
                    string.Equals(effect.Key, flagId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void Aggregate(
        LeviathanSpecializationTree tree,
        string statId,
        ref float flat,
        ref float percent,
        ref float multiplier)
    {
        if (tree == null || string.IsNullOrEmpty(statId))
            return;

        IList<LeviathanSpecializationNode> nodes = tree.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            int rank = GetRank(nodes[i].Id);
            if (rank <= 0)
                continue;

            for (int e = 0; e < nodes[i].Effects.Length; e++)
            {
                LeviathanSpecializationEffect effect = nodes[i].Effects[e];
                if (!string.Equals(effect.Key, statId, StringComparison.Ordinal))
                    continue;

                if (effect.Type == LeviathanSpecializationEffectType.Flat)
                {
                    flat += effect.GetAccumulatedValue(rank);
                }
                else if (effect.Type == LeviathanSpecializationEffectType.Percent)
                {
                    percent += effect.GetAccumulatedValue(rank);
                }
                else if (effect.Type == LeviathanSpecializationEffectType.Multiplier)
                {
                    multiplier *= effect.GetAccumulatedValue(rank);
                }
            }
        }
    }
}

public struct LeviathanLayoutPoint
{
    public float X;
    public float Y;

    public LeviathanLayoutPoint(float x, float y)
    {
        X = x;
        Y = y;
    }
}

public sealed class LeviathanSpecializationLayoutNode
{
    public string NodeId;
    public int Layer;
    public int Order;
    public LeviathanLayoutPoint Position;
}

public sealed class LeviathanSpecializationLayoutEdge
{
    public string FromNodeId;
    public string ToNodeId;
    public LeviathanRequirementKind TargetRequirementKind;
}

public sealed class LeviathanSpecializationLayout
{
    public readonly Dictionary<string, LeviathanSpecializationLayoutNode> Nodes =
        new Dictionary<string, LeviathanSpecializationLayoutNode>(StringComparer.Ordinal);

    public readonly List<LeviathanSpecializationLayoutEdge> Edges =
        new List<LeviathanSpecializationLayoutEdge>();

    public float Width;
    public float Height;
}

public static class LeviathanSpecializationAutoLayout
{
    private const float HorizontalSpacing = 250f;
    private const float VerticalSpacing = 140f;

    public static LeviathanSpecializationLayout Build(
        LeviathanSpecializationTree tree)
    {
        if (tree == null)
            throw new ArgumentNullException("tree");

        tree.Validate();

        LeviathanSpecializationLayout result =
            new LeviathanSpecializationLayout();

        Dictionary<string, int> layers =
            new Dictionary<string, int>(StringComparer.Ordinal);

        IList<LeviathanSpecializationNode> nodes = tree.Nodes;
        for (int i = 0; i < nodes.Count; i++)
            ComputeLayer(tree, nodes[i].Id, layers);

        Dictionary<int, List<string>> byLayer =
            new Dictionary<int, List<string>>();

        for (int i = 0; i < nodes.Count; i++)
            AddToLayer(byLayer, layers[nodes[i].Id], nodes[i].Id);

        foreach (KeyValuePair<int, List<string>> pair in byLayer)
            pair.Value.Sort(StringComparer.Ordinal);

        BuildEdges(tree, result);

        for (int pass = 0; pass < 8; pass++)
        {
            bool forward = pass % 2 == 0;
            List<int> layerIds = byLayer.Keys.OrderBy(x => x).ToList();
            if (!forward)
                layerIds.Reverse();

            for (int l = 0; l < layerIds.Count; l++)
            {
                int layer = layerIds[l];
                if (layer == 0)
                    continue;

                SortLayerByBarycenter(
                    byLayer,
                    result.Edges,
                    layer,
                    forward
                );
            }
        }

        if (byLayer.Count == 0)
        {
            result.Width = 900f;
            result.Height = 620f;
            return result;
        }

        int maxLayer = byLayer.Keys.Max();
        int maxCount = byLayer.Values.Max(v => v.Count);

        foreach (KeyValuePair<int, List<string>> pair in byLayer)
        {
            List<string> ids = pair.Value;
            float center = (ids.Count - 1) * 0.5f;

            for (int i = 0; i < ids.Count; i++)
            {
                result.Nodes[ids[i]] = new LeviathanSpecializationLayoutNode
                {
                    NodeId = ids[i],
                    Layer = pair.Key,
                    Order = i,
                    Position = new LeviathanLayoutPoint(
                        pair.Key * HorizontalSpacing,
                        (i - center) * VerticalSpacing
                    )
                };
            }
        }

        result.Width = Math.Max(900f, maxLayer * HorizontalSpacing + 420f);
        result.Height = Math.Max(620f, maxCount * VerticalSpacing + 260f);
        return result;
    }

    private static int ComputeLayer(
        LeviathanSpecializationTree tree,
        string nodeId,
        Dictionary<string, int> layers)
    {
        int existing;
        if (layers.TryGetValue(nodeId, out existing))
            return existing;

        LeviathanSpecializationNode node = tree.GetNode(nodeId);
        HashSet<string> parents = new HashSet<string>(StringComparer.Ordinal);
        node.Requirement.CollectNodeIds(parents);

        int layer = 0;
        foreach (string parent in parents)
            layer = Math.Max(layer, ComputeLayer(tree, parent, layers) + 1);

        layers[nodeId] = layer;
        return layer;
    }

    private static void AddToLayer(
        Dictionary<int, List<string>> byLayer,
        int layer,
        string nodeId)
    {
        List<string> list;
        if (!byLayer.TryGetValue(layer, out list))
        {
            list = new List<string>();
            byLayer.Add(layer, list);
        }

        list.Add(nodeId);
    }

    private static void BuildEdges(
        LeviathanSpecializationTree tree,
        LeviathanSpecializationLayout layout)
    {
        IList<LeviathanSpecializationNode> nodes = tree.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            HashSet<string> parents = new HashSet<string>(StringComparer.Ordinal);
            nodes[i].Requirement.CollectNodeIds(parents);

            foreach (string parent in parents)
            {
                layout.Edges.Add(new LeviathanSpecializationLayoutEdge
                {
                    FromNodeId = parent,
                    ToNodeId = nodes[i].Id,
                    TargetRequirementKind = nodes[i].Requirement.Kind
                });
            }
        }
    }

    private static void SortLayerByBarycenter(
        Dictionary<int, List<string>> byLayer,
        List<LeviathanSpecializationLayoutEdge> edges,
        int layer,
        bool useParents)
    {
        List<string> current;
        if (!byLayer.TryGetValue(layer, out current) || current.Count <= 1)
            return;

        Dictionary<string, float> previousOrder =
            new Dictionary<string, float>(StringComparer.Ordinal);

        foreach (KeyValuePair<int, List<string>> pair in byLayer)
        {
            for (int i = 0; i < pair.Value.Count; i++)
                previousOrder[pair.Value[i]] = i;
        }

        current.Sort(delegate(string a, string b)
        {
            float ba = GetBarycenter(a, edges, previousOrder, useParents);
            float bb = GetBarycenter(b, edges, previousOrder, useParents);

            int compare = ba.CompareTo(bb);
            return compare != 0
                ? compare
                : string.CompareOrdinal(a, b);
        });
    }

    private static float GetBarycenter(
        string nodeId,
        List<LeviathanSpecializationLayoutEdge> edges,
        Dictionary<string, float> order,
        bool useParents)
    {
        float total = 0f;
        int count = 0;

        for (int i = 0; i < edges.Count; i++)
        {
            string neighbor = null;

            if (useParents && edges[i].ToNodeId == nodeId)
                neighbor = edges[i].FromNodeId;
            else if (!useParents && edges[i].FromNodeId == nodeId)
                neighbor = edges[i].ToNodeId;

            float value;
            if (neighbor != null && order.TryGetValue(neighbor, out value))
            {
                total += value;
                count++;
            }
        }

        return count == 0 ? float.MaxValue : total / count;
    }
}

public static class LeviathanSpecializationRegistry
{
    private static readonly Dictionary<string, LeviathanSpecializationTree> trees =
        new Dictionary<string, LeviathanSpecializationTree>(StringComparer.Ordinal);

    public static void Register(LeviathanSpecializationTree tree)
    {
        if (tree == null)
            throw new ArgumentNullException("tree");

        tree.Validate();
        trees[tree.Id] = tree;
    }

    public static LeviathanSpecializationTree Get(string treeId)
    {
        LeviathanSpecializationTree tree;
        return treeId != null && trees.TryGetValue(treeId, out tree)
            ? tree
            : null;
    }

    public static IList<LeviathanSpecializationTree> All()
    {
        return trees.Values
            .OrderBy(t => t.DisplayOrder)
            .ThenBy(t => t.Id)
            .ToList()
            .AsReadOnly();
    }

    public static string ResolveEffectName(string key)
    {
        LeviathanSpecializationTree tree = Get(key);
        return tree == null ? key : tree.Name;
    }
}
