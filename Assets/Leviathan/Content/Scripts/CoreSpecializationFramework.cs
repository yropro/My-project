using StarVortex;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

// =============================================================================
// CORE SPECIALIZATION FRAMEWORK
// =============================================================================
// Universal specialization engine. Skill files expose knobs/flags and implement
// mechanics; *Tree.cs files only declare nodes, prerequisites and values.
// This file intentionally contains all non-UI/non-currency framework machinery:
// data model, requirements, modifiers, graph layout, runtime state/persistence,
// registry/catalog, and the small tree-authoring DSL.

public enum CoreSpecializationNodeType
{
    Root,
    Passive,
    Major,
    Keystone
}

public enum CoreSpecializationEffectType
{
    Flat,
    Percent,
    Multiplier,
    Flag,
    UnlockTree
}

public enum CoreRequirementKind
{
    Always,
    Rank,
    All,
    Any
}

public enum CoreTreeUnlockKind
{
    Always,
    NativeUpgrade,
    SpecializationEffect
}

public enum CoreKnobKind
{
    Flat,
    Percent,
    PercentagePoints,
    Multiplier
}

public interface ICoreSpecializationRankSource
{
    int GetRank(string nodeId);
}

public abstract class CoreRequirement
{
    public abstract CoreRequirementKind Kind { get; }
    public abstract bool IsSatisfied(ICoreSpecializationRankSource ranks);
    public abstract void CollectNodeIds(HashSet<string> output);
    public abstract string Describe(Func<string, string> nameResolver);

    public virtual bool TryGetSimpleParents(out List<string> nodeIds)
    {
        nodeIds = null;
        return false;
    }
}

public sealed class CoreAlwaysRequirement : CoreRequirement
{
    public override CoreRequirementKind Kind
    {
        get { return CoreRequirementKind.Always; }
    }

    public override bool IsSatisfied(ICoreSpecializationRankSource ranks)
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

public sealed class CoreRankRequirement : CoreRequirement
{
    public readonly string NodeId;
    public readonly int Rank;

    public CoreRankRequirement(string nodeId, int rank)
    {
        NodeId = nodeId;
        Rank = Math.Max(1, rank);
    }

    public override CoreRequirementKind Kind
    {
        get { return CoreRequirementKind.Rank; }
    }

    public override bool IsSatisfied(ICoreSpecializationRankSource ranks)
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

public abstract class CoreCompositeRequirement : CoreRequirement
{
    public readonly CoreRequirement[] Children;

    protected CoreCompositeRequirement(params CoreRequirement[] children)
    {
        Children = children == null
            ? new CoreRequirement[0]
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
            CoreRankRequirement rank = Children[i] as CoreRankRequirement;
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

public sealed class CoreAllRequirement : CoreCompositeRequirement
{
    public CoreAllRequirement(params CoreRequirement[] children)
        : base(children)
    {
    }

    public override CoreRequirementKind Kind
    {
        get { return CoreRequirementKind.All; }
    }

    public override bool IsSatisfied(ICoreSpecializationRankSource ranks)
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

public sealed class CoreAnyRequirement : CoreCompositeRequirement
{
    public CoreAnyRequirement(params CoreRequirement[] children)
        : base(children)
    {
    }

    public override CoreRequirementKind Kind
    {
        get { return CoreRequirementKind.Any; }
    }

    public override bool IsSatisfied(ICoreSpecializationRankSource ranks)
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

public static class CoreReq
{
    public static readonly CoreRequirement None =
        new CoreAlwaysRequirement();

    public static CoreRequirement Rank(string nodeId, int rank)
    {
        return new CoreRankRequirement(nodeId, rank);
    }

    public static CoreRequirement Rank(string nodeId)
    {
        return Rank(nodeId, 1);
    }

    public static CoreRequirement All(params CoreRequirement[] children)
    {
        return new CoreAllRequirement(children);
    }

    public static CoreRequirement Any(params CoreRequirement[] children)
    {
        return new CoreAnyRequirement(children);
    }
}

public sealed class CoreSpecializationFlag
{
    public readonly string Id;
    public readonly string Name;

    private CoreSpecializationFlag(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Flag id is required.", "id");

        Id = id;
        Name = string.IsNullOrWhiteSpace(name) ? id : name;
    }

    public static CoreSpecializationFlag Create(
        string id,
        string name)
    {
        return new CoreSpecializationFlag(id, name);
    }

    public static CoreSpecializationFlag Create(string id)
    {
        return Create(id, id);
    }
}

public sealed class CoreSpecializationKnob
{
    public readonly string Id;
    public readonly string Name;
    public readonly CoreKnobKind Kind;
    public readonly string UnitSuffix;

    private CoreSpecializationKnob(
        string id,
        string name,
        CoreKnobKind kind,
        string unitSuffix)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Knob id is required.", "id");

        Id = id;
        Name = string.IsNullOrWhiteSpace(name) ? id : name;
        Kind = kind;
        UnitSuffix = unitSuffix ?? string.Empty;
    }

    public static CoreSpecializationKnob Percent(
        string id,
        string name)
    {
        return new CoreSpecializationKnob(
            id,
            name,
            CoreKnobKind.Percent,
            "%"
        );
    }

    public static CoreSpecializationKnob Flat(
        string id,
        string name,
        string unitSuffix)
    {
        return new CoreSpecializationKnob(
            id,
            name,
            CoreKnobKind.Flat,
            unitSuffix
        );
    }

    public static CoreSpecializationKnob Flat(
        string id,
        string name)
    {
        return Flat(id, name, string.Empty);
    }

    // Human-readable percentage points that aggregate additively.
    // Example: +5 on a 10% crit chance becomes 15%, not 10.5%.
    // Tree definitions still write 5f; runtime storage is 0.05f.
    public static CoreSpecializationKnob PercentagePoints(
        string id,
        string name)
    {
        return new CoreSpecializationKnob(
            id,
            name,
            CoreKnobKind.PercentagePoints,
            "%"
        );
    }

    // Multipliers are authored as actual factors. Example: 1.25 means x1.25.
    public static CoreSpecializationKnob Multiplier(
        string id,
        string name)
    {
        return new CoreSpecializationKnob(
            id,
            name,
            CoreKnobKind.Multiplier,
            "x"
        );
    }

    internal bool UsesPercentDefinition
    {
        get
        {
            return Kind == CoreKnobKind.Percent ||
                Kind == CoreKnobKind.PercentagePoints;
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

        if (Kind == CoreKnobKind.Multiplier)
            return "x" + value.ToString("0.###") + " " + Name;

        return sign + value.ToString("0.###") +
            (string.IsNullOrEmpty(UnitSuffix) ? " " : UnitSuffix + " ") +
            Name;
    }
}

public sealed class CoreSpecializationEffect
{
    public readonly CoreSpecializationEffectType Type;
    public readonly string Key;
    public readonly CoreSpecializationKnob Knob;
    public CoreSpecializationFlag FlagDefinition { get; private set; }

    private readonly bool hasConstantIncrement;
    private readonly float constantIncrement;
    private readonly float[] perRankIncrements;
    private bool multiplierRanksAreTotals;

    private CoreSpecializationEffect(
        CoreSpecializationEffectType type,
        string key,
        CoreSpecializationKnob knob,
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

    public static CoreSpecializationEffect KnobIncrement(
        CoreSpecializationKnob knob,
        float valuePerRank)
    {
        if (knob == null)
            throw new ArgumentNullException("knob");

        return new CoreSpecializationEffect(
            knob.Kind == CoreKnobKind.Multiplier
                ? CoreSpecializationEffectType.Multiplier
                : knob.Kind == CoreKnobKind.Percent
                    ? CoreSpecializationEffectType.Percent
                    : CoreSpecializationEffectType.Flat,
            knob.Id,
            knob,
            true,
            knob.ConvertDefinitionValue(valuePerRank),
            null
        );
    }

    public static CoreSpecializationEffect KnobRanks(
        CoreSpecializationKnob knob,
        params float[] valuesByRank)
    {
        if (knob == null)
            throw new ArgumentNullException("knob");

        if (valuesByRank == null || valuesByRank.Length == 0)
            throw new ArgumentException("At least one per-rank value is required.", "valuesByRank");

        float[] converted = new float[valuesByRank.Length];
        for (int i = 0; i < valuesByRank.Length; i++)
            converted[i] = knob.ConvertDefinitionValue(valuesByRank[i]);

        return new CoreSpecializationEffect(
            knob.Kind == CoreKnobKind.Multiplier
                ? CoreSpecializationEffectType.Multiplier
                : knob.Kind == CoreKnobKind.Percent
                    ? CoreSpecializationEffectType.Percent
                    : CoreSpecializationEffectType.Flat,
            knob.Id,
            knob,
            false,
            0f,
            converted
        );
    }

    // Explicit multiplier contribution against any named knob. The knob's Kind
    // controls the default Increment/Ranks authoring semantics, not the complete
    // set of operations that can target it. This lets one node add +damage while
    // another multiplies the already-resolved damage through the same knob.
    public static CoreSpecializationEffect KnobMultiplier(
        CoreSpecializationKnob knob,
        float factorPerRank)
    {
        if (knob == null)
            throw new ArgumentNullException("knob");

        return new CoreSpecializationEffect(
            CoreSpecializationEffectType.Multiplier,
            knob.Id,
            knob,
            true,
            factorPerRank,
            null
        );
    }

    public static CoreSpecializationEffect KnobMultiplierRanks(
        CoreSpecializationKnob knob,
        params float[] factorsByRank)
    {
        if (knob == null)
            throw new ArgumentNullException("knob");
        if (factorsByRank == null || factorsByRank.Length == 0)
            throw new ArgumentException("At least one per-rank factor is required.", "factorsByRank");

        float[] factors = new float[factorsByRank.Length];
        Array.Copy(factorsByRank, factors, factorsByRank.Length);

        return new CoreSpecializationEffect(
            CoreSpecializationEffectType.Multiplier,
            knob.Id,
            knob,
            false,
            0f,
            factors
        );
    }

    // Per-rank TOTAL multipliers rather than incremental factors. Example:
    // 1.45, 1.90, 2.35 means purchased rank 1/2/3 resolves to exactly those
    // multipliers. This is useful for effects described as +45% per rank without
    // accidentally compounding to 1.45^rank.
    public static CoreSpecializationEffect KnobMultiplierTotals(
        CoreSpecializationKnob knob,
        params float[] totalFactorsByRank)
    {
        CoreSpecializationEffect effect = KnobMultiplierRanks(
            knob,
            totalFactorsByRank
        );
        effect.multiplierRanksAreTotals = true;
        return effect;
    }

    // Compatibility helpers for one-off raw effects. Prefer CoreFx with a
    // named knob for ordinary specialization stats.
    public static CoreSpecializationEffect Flat(
        string statId,
        float valuePerRank)
    {
        return new CoreSpecializationEffect(
            CoreSpecializationEffectType.Flat,
            statId,
            null,
            true,
            valuePerRank,
            null
        );
    }

    // Raw decimal: 0.05 = +5% per rank.
    public static CoreSpecializationEffect Percent(
        string statId,
        float valuePerRank)
    {
        return new CoreSpecializationEffect(
            CoreSpecializationEffectType.Percent,
            statId,
            null,
            true,
            valuePerRank,
            null
        );
    }

    // Value is a factor for one rank: 1.10 = x1.10 per rank.
    public static CoreSpecializationEffect Multiplier(
        string statId,
        float factorPerRank)
    {
        return new CoreSpecializationEffect(
            CoreSpecializationEffectType.Multiplier,
            statId,
            null,
            true,
            factorPerRank,
            null
        );
    }

    public static CoreSpecializationEffect Flag(string flagId)
    {
        return new CoreSpecializationEffect(
            CoreSpecializationEffectType.Flag,
            flagId,
            null,
            true,
            1f,
            null
        );
    }

    public static CoreSpecializationEffect Flag(
        CoreSpecializationFlag flag)
    {
        if (flag == null)
            throw new ArgumentNullException("flag");

        CoreSpecializationEffect effect = Flag(flag.Id);
        effect.FlagDefinition = flag;
        return effect;
    }

    public static CoreSpecializationEffect UnlockTree(string treeId)
    {
        return new CoreSpecializationEffect(
            CoreSpecializationEffectType.UnlockTree,
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
            return Type == CoreSpecializationEffectType.Multiplier ? 1f : 0f;

        if (Type == CoreSpecializationEffectType.Multiplier)
        {
            if (hasConstantIncrement)
                return (float)Math.Pow(constantIncrement, rank);

            int count = Math.Min(rank, perRankIncrements == null ? 0 : perRankIncrements.Length);
            if (count <= 0)
                return 1f;

            if (multiplierRanksAreTotals)
                return perRankIncrements[count - 1];

            float factor = 1f;
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

        if (Type == CoreSpecializationEffectType.Flag)
        {
            string flagName = FlagDefinition != null
                ? FlagDefinition.Name
                : fallbackResolver == null ? Key : fallbackResolver(Key);
            return "Enables " + flagName;
        }

        if (Type == CoreSpecializationEffectType.UnlockTree)
        {
            CoreSpecializationTree tree = CoreSpecializationRegistry.Get(Key);
            string treeName = tree == null ? Key : tree.Name;
            return "Unlocks the " + treeName + " specialization tree";
        }

        if (Knob != null)
        {
            if (Type == CoreSpecializationEffectType.Multiplier)
            {
                float knobFactor = hasConstantIncrement
                    ? constantIncrement
                    : perRankIncrements[Math.Min(rankIndex - 1, perRankIncrements.Length - 1)];
                return "x" + knobFactor.ToString("0.###") + " " + Knob.Name;
            }

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

        if (Type == CoreSpecializationEffectType.Flat)
        {
            float value = hasConstantIncrement
                ? constantIncrement
                : perRankIncrements[Math.Min(rankIndex - 1, perRankIncrements.Length - 1)];
            return (value >= 0f ? "+" : string.Empty) +
                value.ToString("0.###") + " " + name;
        }

        if (Type == CoreSpecializationEffectType.Percent)
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

public static class CoreFx
{
    // Example: Increment(StarfireKnobs.Width, 5f) => +5% every rank when Width
    // is a percent knob. Negative values work identically.
    public static CoreSpecializationEffect Increment(
        CoreSpecializationKnob knob,
        float valuePerRank)
    {
        return CoreSpecializationEffect.KnobIncrement(knob, valuePerRank);
    }

    // Example: Ranks(StarfireKnobs.Width, 5f, 5f, 10f) => rank investments add
    // +5%, then +5%, then +10%, for +20% total at rank 3.
    public static CoreSpecializationEffect Ranks(
        CoreSpecializationKnob knob,
        params float[] valuesByRank)
    {
        return CoreSpecializationEffect.KnobRanks(knob, valuesByRank);
    }

    // Explicit multiplier helper against any named knob. Example:
    // Multiply(StarfireKnobs.Damage, 1.45f) multiplies the damage value after
    // additive/percent contributions on that same knob have been resolved.
    public static CoreSpecializationEffect Multiply(
        CoreSpecializationKnob knob,
        float factorPerRank)
    {
        return CoreSpecializationEffect.KnobMultiplier(
            knob,
            factorPerRank
        );
    }

    public static CoreSpecializationEffect MultiplyRanks(
        CoreSpecializationKnob knob,
        params float[] factorsByRank)
    {
        return CoreSpecializationEffect.KnobMultiplierRanks(
            knob,
            factorsByRank
        );
    }

    public static CoreSpecializationEffect MultiplyTotals(
        CoreSpecializationKnob knob,
        params float[] totalFactorsByRank)
    {
        return CoreSpecializationEffect.KnobMultiplierTotals(
            knob,
            totalFactorsByRank
        );
    }

    public static CoreSpecializationEffect Flag(string flagId)
    {
        return CoreSpecializationEffect.Flag(flagId);
    }

    public static CoreSpecializationEffect Flag(
        CoreSpecializationFlag flag)
    {
        return CoreSpecializationEffect.Flag(flag);
    }

    public static CoreSpecializationEffect UnlockTree(string treeId)
    {
        return CoreSpecializationEffect.UnlockTree(treeId);
    }
}

public sealed class CoreSpecializationNode
{
    public readonly string Id;
    public readonly string Name;
    public readonly int MaxRank;
    public readonly CoreSpecializationNodeType Type;
    public readonly CoreRequirement Requirement;
    public readonly string ExclusiveGroup;
    public readonly string Description;
    public readonly int PointCostPerRank;
    public readonly bool AutoGranted;
    public readonly CoreSpecializationEffect[] Effects;

    public CoreSpecializationNode(
        string id,
        string name,
        int maxRank,
        CoreSpecializationNodeType type,
        CoreRequirement requirement,
        string exclusiveGroup,
        string description,
        params CoreSpecializationEffect[] effects)
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

    public CoreSpecializationNode(
        string id,
        string name,
        int maxRank,
        CoreSpecializationNodeType type,
        CoreRequirement requirement,
        string exclusiveGroup,
        string description,
        int pointCostPerRank,
        bool autoGranted,
        params CoreSpecializationEffect[] effects)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Node id is required.", "id");

        Id = id;
        Name = string.IsNullOrWhiteSpace(name) ? id : name;
        MaxRank = Math.Max(1, maxRank);
        Type = type;
        Requirement = requirement ?? CoreReq.None;
        ExclusiveGroup = exclusiveGroup ?? string.Empty;
        Description = description ?? string.Empty;
        PointCostPerRank = Math.Max(0, pointCostPerRank);
        AutoGranted = autoGranted;
        Effects = effects == null
            ? new CoreSpecializationEffect[0]
            : effects.Where(e => e != null).ToArray();

        for (int i = 0; i < Effects.Length; i++)
            Effects[i].ValidateForNode(MaxRank);
    }
}

public static class CoreNode
{
    public static CoreSpecializationNode GrantedRoot(
        string id,
        string name,
        string description)
    {
        return new CoreSpecializationNode(
            id,
            name,
            1,
            CoreSpecializationNodeType.Root,
            CoreReq.None,
            null,
            description,
            0,
            true
        );
    }

    public static CoreSpecializationNode Passive(
        string id,
        string name,
        int maxRank,
        CoreRequirement requirement,
        string description,
        params CoreSpecializationEffect[] effects)
    {
        return new CoreSpecializationNode(
            id,
            name,
            maxRank,
            CoreSpecializationNodeType.Passive,
            requirement,
            null,
            description,
            effects
        );
    }

    public static CoreSpecializationNode PassiveExclusive(
        string id,
        string name,
        int maxRank,
        CoreRequirement requirement,
        string exclusiveGroup,
        string description,
        params CoreSpecializationEffect[] effects)
    {
        return new CoreSpecializationNode(
            id,
            name,
            maxRank,
            CoreSpecializationNodeType.Passive,
            requirement,
            exclusiveGroup,
            description,
            effects
        );
    }

    public static CoreSpecializationNode Major(
        string id,
        string name,
        int maxRank,
        CoreRequirement requirement,
        string description,
        params CoreSpecializationEffect[] effects)
    {
        return new CoreSpecializationNode(
            id,
            name,
            maxRank,
            CoreSpecializationNodeType.Major,
            requirement,
            null,
            description,
            effects
        );
    }

    public static CoreSpecializationNode MajorExclusive(
        string id,
        string name,
        int maxRank,
        CoreRequirement requirement,
        string exclusiveGroup,
        string description,
        params CoreSpecializationEffect[] effects)
    {
        return new CoreSpecializationNode(
            id,
            name,
            maxRank,
            CoreSpecializationNodeType.Major,
            requirement,
            exclusiveGroup,
            description,
            effects
        );
    }

    public static CoreSpecializationNode Keystone(
        string id,
        string name,
        CoreRequirement requirement,
        string exclusiveGroup,
        string description,
        params CoreSpecializationEffect[] effects)
    {
        return new CoreSpecializationNode(
            id,
            name,
            1,
            CoreSpecializationNodeType.Keystone,
            requirement,
            exclusiveGroup,
            description,
            effects
        );
    }
}

public sealed class CoreSpecializationTree
{
    public readonly string Id;
    public readonly string Name;
    public readonly string RootNodeId;
    public readonly int DisplayOrder;
    public readonly CoreTreeUnlockKind UnlockKind;
    public readonly int NativeUnlockUpgradeKey;

    private readonly List<CoreSpecializationNode> nodes =
        new List<CoreSpecializationNode>();

    private readonly IList<CoreSpecializationNode> readOnlyNodes;

    private readonly Dictionary<string, CoreSpecializationNode> byId =
        new Dictionary<string, CoreSpecializationNode>(StringComparer.Ordinal);

    public CoreSpecializationTree(
        string id,
        string name,
        string rootNodeId,
        int displayOrder,
        CoreTreeUnlockKind unlockKind,
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
        readOnlyNodes = nodes.AsReadOnly();
    }

    public IList<CoreSpecializationNode> Nodes
    {
        get { return readOnlyNodes; }
    }

    public CoreSpecializationTree Add(CoreSpecializationNode node)
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

    public CoreSpecializationNode GetNode(string nodeId)
    {
        CoreSpecializationNode node;
        return nodeId != null && byId.TryGetValue(nodeId, out node)
            ? node
            : null;
    }

    public string GetNodeName(string nodeId)
    {
        CoreSpecializationNode node = GetNode(nodeId);
        return node == null ? nodeId : node.Name;
    }

    public IList<CoreSpecializationNode> GetExclusiveGroupMembers(string group)
    {
        if (string.IsNullOrEmpty(group))
            return new List<CoreSpecializationNode>().AsReadOnly();

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
            CoreSpecializationNode root = GetNode(RootNodeId);
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

public sealed class CoreSpecializationState : ICoreSpecializationRankSource
{
    private readonly Dictionary<string, int> ranks =
        new Dictionary<string, int>(StringComparer.Ordinal);

    private int revision;

    public int Revision
    {
        get { return revision; }
    }

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

        int desired = Math.Max(0, rank);
        int current = GetRank(nodeId);
        if (current == desired)
            return;

        if (desired <= 0)
            ranks.Remove(nodeId);
        else
            ranks[nodeId] = desired;

        unchecked
        {
            revision++;
        }
    }

    public IDictionary<string, int> Snapshot()
    {
        return new Dictionary<string, int>(ranks, StringComparer.Ordinal);
    }

    public bool CanInvest(
        CoreSpecializationTree tree,
        string nodeId,
        out string reason)
    {
        reason = string.Empty;

        if (tree == null)
        {
            reason = "No tree.";
            return false;
        }

        CoreSpecializationNode node = tree.GetNode(nodeId);
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
            IList<CoreSpecializationNode> group =
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
        CoreSpecializationTree tree,
        string nodeId,
        out string reason)
    {
        if (!CanInvest(tree, nodeId, out reason))
            return false;

        CoreSpecializationNode node = tree.GetNode(nodeId);
        SetRank(node.Id, GetRank(node.Id) + 1);
        return true;
    }

    public bool CanRefund(
        CoreSpecializationTree tree,
        string nodeId,
        out string reason)
    {
        reason = string.Empty;

        if (tree == null)
        {
            reason = "No tree.";
            return false;
        }

        CoreSpecializationNode node = tree.GetNode(nodeId);
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
        CoreSpecializationTree tree,
        string nodeId,
        out string reason)
    {
        if (!CanRefund(tree, nodeId, out reason))
            return false;

        SetRank(nodeId, GetRank(nodeId) - 1);
        return true;
    }

    public bool ValidateInvestedState(
        CoreSpecializationTree tree,
        out string invalidNodeName)
    {
        invalidNodeName = string.Empty;

        if (tree == null)
            return false;

        IList<CoreSpecializationNode> nodes = tree.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            CoreSpecializationNode node = nodes[i];
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

    public int GetSpentPointCost(CoreSpecializationTree tree)
    {
        if (tree == null)
            return 0;

        int total = 0;
        IList<CoreSpecializationNode> nodes = tree.Nodes;
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
        CoreSpecializationTree tree,
        string targetTreeId)
    {
        if (tree == null || string.IsNullOrEmpty(targetTreeId))
            return false;

        IList<CoreSpecializationNode> nodes = tree.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (GetRank(nodes[i].Id) <= 0)
                continue;

            for (int e = 0; e < nodes[i].Effects.Length; e++)
            {
                CoreSpecializationEffect effect = nodes[i].Effects[e];
                if (effect.Type == CoreSpecializationEffectType.UnlockTree &&
                    string.Equals(effect.Key, targetTreeId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool HasFlag(
        CoreSpecializationTree tree,
        string flagId)
    {
        if (tree == null || string.IsNullOrEmpty(flagId))
            return false;

        IList<CoreSpecializationNode> nodes = tree.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            int rank = GetRank(nodes[i].Id);
            if (rank <= 0)
                continue;

            for (int e = 0; e < nodes[i].Effects.Length; e++)
            {
                CoreSpecializationEffect effect = nodes[i].Effects[e];
                if (effect.Type == CoreSpecializationEffectType.Flag &&
                    string.Equals(effect.Key, flagId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void Aggregate(
        CoreSpecializationTree tree,
        string statId,
        ref float flat,
        ref float percent,
        ref float multiplier)
    {
        if (tree == null || string.IsNullOrEmpty(statId))
            return;

        IList<CoreSpecializationNode> nodes = tree.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            int rank = GetRank(nodes[i].Id);
            if (rank <= 0)
                continue;

            for (int e = 0; e < nodes[i].Effects.Length; e++)
            {
                CoreSpecializationEffect effect = nodes[i].Effects[e];
                if (!string.Equals(effect.Key, statId, StringComparison.Ordinal))
                    continue;

                if (effect.Type == CoreSpecializationEffectType.Flat)
                {
                    flat += effect.GetAccumulatedValue(rank);
                }
                else if (effect.Type == CoreSpecializationEffectType.Percent)
                {
                    percent += effect.GetAccumulatedValue(rank);
                }
                else if (effect.Type == CoreSpecializationEffectType.Multiplier)
                {
                    multiplier *= effect.GetAccumulatedValue(rank);
                }
            }
        }
    }
}

public struct CoreLayoutPoint
{
    public float X;
    public float Y;

    public CoreLayoutPoint(float x, float y)
    {
        X = x;
        Y = y;
    }
}

public sealed class CoreSpecializationLayoutNode
{
    public string NodeId;
    public int Layer;
    public int Order;
    public CoreLayoutPoint Position;
}

public sealed class CoreSpecializationLayoutEdge
{
    public string FromNodeId;
    public string ToNodeId;
    public CoreRequirementKind TargetRequirementKind;
}

public sealed class CoreSpecializationLayout
{
    public readonly Dictionary<string, CoreSpecializationLayoutNode> Nodes =
        new Dictionary<string, CoreSpecializationLayoutNode>(StringComparer.Ordinal);

    public readonly List<CoreSpecializationLayoutEdge> Edges =
        new List<CoreSpecializationLayoutEdge>();

    public float Width;
    public float Height;
}

public static class CoreSpecializationAutoLayout
{
    private const float HorizontalSpacing = 250f;
    private const float VerticalSpacing = 140f;

    public static CoreSpecializationLayout Build(
        CoreSpecializationTree tree)
    {
        if (tree == null)
            throw new ArgumentNullException("tree");

        tree.Validate();

        CoreSpecializationLayout result =
            new CoreSpecializationLayout();

        Dictionary<string, int> layers =
            new Dictionary<string, int>(StringComparer.Ordinal);

        IList<CoreSpecializationNode> nodes = tree.Nodes;
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
                result.Nodes[ids[i]] = new CoreSpecializationLayoutNode
                {
                    NodeId = ids[i],
                    Layer = pair.Key,
                    Order = i,
                    Position = new CoreLayoutPoint(
                        pair.Key * HorizontalSpacing,
                        (i - center) * VerticalSpacing
                    )
                };
            }
        }

        // Treat ordinary shared-parent children as a local fork centered on
        // their parent rather than only as members of a global depth column.
        // This is intentionally generic: three capstones requiring the same
        // parent fan exactly like any other three sibling branches.
        FanSharedParentChildren(tree, result);

        result.Width = Math.Max(900f, maxLayer * HorizontalSpacing + 420f);
        result.Height = Math.Max(620f, maxCount * VerticalSpacing + 260f);
        return result;
    }

    private static void FanSharedParentChildren(
        CoreSpecializationTree tree,
        CoreSpecializationLayout layout)
    {
        Dictionary<string, List<CoreSpecializationLayoutNode>> childrenByParent =
            new Dictionary<string, List<CoreSpecializationLayoutNode>>(
                StringComparer.Ordinal
            );

        IList<CoreSpecializationNode> nodes = tree.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            CoreRankRequirement direct =
                nodes[i].Requirement as CoreRankRequirement;

            if (direct == null)
                continue;

            CoreSpecializationLayoutNode childLayout;
            if (!layout.Nodes.TryGetValue(nodes[i].Id, out childLayout))
                continue;

            List<CoreSpecializationLayoutNode> siblings;
            if (!childrenByParent.TryGetValue(direct.NodeId, out siblings))
            {
                siblings = new List<CoreSpecializationLayoutNode>();
                childrenByParent.Add(direct.NodeId, siblings);
            }

            siblings.Add(childLayout);
        }

        foreach (KeyValuePair<string, List<CoreSpecializationLayoutNode>> pair
            in childrenByParent)
        {
            List<CoreSpecializationLayoutNode> siblings = pair.Value;
            if (siblings.Count <= 1)
                continue;

            CoreSpecializationLayoutNode parentLayout;
            if (!layout.Nodes.TryGetValue(pair.Key, out parentLayout))
                continue;

            // Only fan siblings that actually occupy the same progression layer.
            int layer = siblings[0].Layer;
            bool sameLayer = true;
            for (int i = 1; i < siblings.Count; i++)
            {
                if (siblings[i].Layer != layer)
                {
                    sameLayer = false;
                    break;
                }
            }

            if (!sameLayer)
                continue;

            siblings.Sort(delegate (
                CoreSpecializationLayoutNode a,
                CoreSpecializationLayoutNode b)
            {
                int orderCompare = a.Order.CompareTo(b.Order);
                return orderCompare != 0
                    ? orderCompare
                    : string.CompareOrdinal(a.NodeId, b.NodeId);
            });

            float center = (siblings.Count - 1) * 0.5f;
            for (int i = 0; i < siblings.Count; i++)
            {
                siblings[i].Position = new CoreLayoutPoint(
                    siblings[i].Position.X,
                    parentLayout.Position.Y +
                        (i - center) * VerticalSpacing
                );
            }
        }
    }

    private static int ComputeLayer(
        CoreSpecializationTree tree,
        string nodeId,
        Dictionary<string, int> layers)
    {
        int existing;
        if (layers.TryGetValue(nodeId, out existing))
            return existing;

        CoreSpecializationNode node = tree.GetNode(nodeId);
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
        CoreSpecializationTree tree,
        CoreSpecializationLayout layout)
    {
        IList<CoreSpecializationNode> nodes = tree.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            HashSet<string> parents = new HashSet<string>(StringComparer.Ordinal);
            nodes[i].Requirement.CollectNodeIds(parents);

            foreach (string parent in parents)
            {
                layout.Edges.Add(new CoreSpecializationLayoutEdge
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
        List<CoreSpecializationLayoutEdge> edges,
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

        current.Sort(delegate (string a, string b)
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
        List<CoreSpecializationLayoutEdge> edges,
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

public static class CoreSpecializationRegistry
{
    private static readonly Dictionary<string, CoreSpecializationTree> trees =
        new Dictionary<string, CoreSpecializationTree>(StringComparer.Ordinal);

    private static IList<CoreSpecializationTree> cachedAll =
        new List<CoreSpecializationTree>().AsReadOnly();

    private static bool cachedAllDirty = true;
    private static int revision;

    public static int Revision
    {
        get { return revision; }
    }

    public static void Register(CoreSpecializationTree tree)
    {
        if (tree == null)
            throw new ArgumentNullException("tree");

        tree.Validate();
        trees[tree.Id] = tree;
        MarkStructureChanged();
    }

    public static CoreSpecializationTree Get(string treeId)
    {
        CoreSpecializationTree tree;
        return treeId != null && trees.TryGetValue(treeId, out tree)
            ? tree
            : null;
    }

    // Tree definitions change only during registration/rebuild. Cache the sorted
    // read-only view so combat/runtime lookups never OrderBy/ToList the registry.
    public static IList<CoreSpecializationTree> All()
    {
        if (!cachedAllDirty)
            return cachedAll;

        List<CoreSpecializationTree> sorted =
            new List<CoreSpecializationTree>(trees.Values);

        sorted.Sort(delegate (
            CoreSpecializationTree a,
            CoreSpecializationTree b)
        {
            int order = a.DisplayOrder.CompareTo(b.DisplayOrder);
            return order != 0
                ? order
                : string.CompareOrdinal(a.Id, b.Id);
        });

        cachedAll = sorted.AsReadOnly();
        cachedAllDirty = false;
        return cachedAll;
    }

    public static void Clear()
    {
        trees.Clear();
        MarkStructureChanged();
    }

    private static void MarkStructureChanged()
    {
        cachedAllDirty = true;
        unchecked
        {
            revision++;
        }
    }

    public static string ResolveEffectName(string key)
    {
        CoreSpecializationTree tree = Get(key);
        return tree == null ? key : tree.Name;
    }
}

internal struct CoreSpecializationAggregateCacheValue
{
    public float Flat;
    public float Percent;
    public float Multiplier;

    public CoreSpecializationAggregateCacheValue(
        float flat,
        float percent,
        float multiplier)
    {
        Flat = flat;
        Percent = percent;
        Multiplier = multiplier;
    }
}

public sealed class CorePilotSpecializationData
{
    public readonly Dictionary<string, CoreSpecializationState> Trees =
        new Dictionary<string, CoreSpecializationState>(StringComparer.Ordinal);

    public bool PersistenceReady;
    public string PersistenceReason;

    // Network replicas use transient specialization state supplied by
    // CoreNetwork. It must never load from or write to local persistence.
    internal bool IsRemoteTransient;
    internal bool RemoteUpdateInProgress;

    internal readonly Dictionary<string, CoreSpecializationState>
        RemotePendingTrees =
            new Dictionary<string, CoreSpecializationState>(
                StringComparer.Ordinal);

    // Runtime resolution caches. Validity is keyed to all inputs that can alter
    // specialization results, including direct SetRank changes used by refund
    // simulation and native-upgrade tree unlocks.
    internal int CacheConfigurationRevision = int.MinValue;
    internal int CacheRegistryRevision = int.MinValue;
    internal int CacheNativeUnlockStamp = int.MinValue;
    internal int CacheStateRevisionStamp = int.MinValue;

    internal readonly Dictionary<string, bool> TreeUnlockCache =
        new Dictionary<string, bool>(StringComparer.Ordinal);

    internal readonly Dictionary<string, CoreSpecializationAggregateCacheValue>
        KnobAggregateCache =
            new Dictionary<string, CoreSpecializationAggregateCacheValue>(
                StringComparer.Ordinal);

    internal readonly Dictionary<string, bool> FlagCache =
        new Dictionary<string, bool>(StringComparer.Ordinal);

    internal readonly HashSet<string> UnlockPathScratch =
        new HashSet<string>(StringComparer.Ordinal);

    internal void ClearResolutionCaches()
    {
        TreeUnlockCache.Clear();
        KnobAggregateCache.Clear();
        FlagCache.Clear();
        UnlockPathScratch.Clear();
    }
}

public static class CoreSpecializationRuntime
{
    public const string DiagnosticBuildMarker = "SPEC-DIAG-20260907-B";
    private static readonly Dictionary<Pilot, CorePilotSpecializationData> data =
        new Dictionary<Pilot, CorePilotSpecializationData>();
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
        if (registeredDefaults)
            return;

        CoreSpecializationPolicies.EnsurePrerequisitesRegistered();
        registeredDefaults = true;
    }

    /// <summary>
    /// Begins an atomic replacement of specialization state for a network
    /// replica Pilot. Remote state is transient: it never participates in local
    /// persistence and is replaced wholesale by each received spec block.
    /// </summary>
    public static void BeginRemoteSpecialization(Pilot pilot)
    {
        RegisterDefaults();

        if (pilot == null)
            throw new ArgumentNullException("pilot");

        Pilot localPilot = GetCurrentPilot();
        if (localPilot != null && ReferenceEquals(localPilot, pilot))
        {
            throw new InvalidOperationException(
                "Refusing to install remote specialization on the local Pilot."
            );
        }

        CorePilotSpecializationData playerData;
        if (!data.TryGetValue(pilot, out playerData))
        {
            playerData = new CorePilotSpecializationData();
            data.Add(pilot, playerData);
        }

        playerData.IsRemoteTransient = true;
        playerData.PersistenceReady = true;
        playerData.PersistenceReason = string.Empty;
        playerData.RemoteUpdateInProgress = true;
        playerData.RemotePendingTrees.Clear();
    }

    /// <summary>
    /// Stages one player-chosen rank from the network specialization schema.
    /// Auto-granted roots are intentionally not accepted here; they are derived
    /// in EndRemoteSpecialization after all transmitted choices are installed.
    /// </summary>
    public static void SetRemoteRank(
        Pilot pilot,
        string treeId,
        string nodeId,
        int rank)
    {
        if (pilot == null)
            throw new ArgumentNullException("pilot");

        CorePilotSpecializationData playerData;
        if (!data.TryGetValue(pilot, out playerData) ||
            !playerData.IsRemoteTransient ||
            !playerData.RemoteUpdateInProgress)
        {
            throw new InvalidOperationException(
                "BeginRemoteSpecialization must be called before SetRemoteRank."
            );
        }

        CoreSpecializationTree tree =
            CoreSpecializationRegistry.Get(treeId);
        if (tree == null)
        {
            throw new InvalidOperationException(
                "Remote specialization referenced unknown tree '" +
                (treeId ?? string.Empty) + "'."
            );
        }

        CoreSpecializationNode node = tree.GetNode(nodeId);
        if (node == null)
        {
            throw new InvalidOperationException(
                "Remote specialization referenced unknown node '" +
                (nodeId ?? string.Empty) + "' in tree '" + tree.Id + "'."
            );
        }

        if (node.AutoGranted)
            return;

        CoreSpecializationState state;
        if (!playerData.RemotePendingTrees.TryGetValue(tree.Id, out state))
        {
            state = new CoreSpecializationState();
            playerData.RemotePendingTrees.Add(tree.Id, state);
        }

        state.SetRank(
            node.Id,
            Math.Max(0, Math.Min(rank, node.MaxRank))
        );
    }

    /// <summary>
    /// Atomically publishes the staged remote rank set, then rebuilds derived
    /// auto-granted roots from the replica Pilot's native unlock state.
    /// </summary>
    public static void EndRemoteSpecialization(Pilot pilot)
    {
        if (pilot == null)
            throw new ArgumentNullException("pilot");

        CorePilotSpecializationData playerData;
        if (!data.TryGetValue(pilot, out playerData) ||
            !playerData.IsRemoteTransient ||
            !playerData.RemoteUpdateInProgress)
        {
            throw new InvalidOperationException(
                "BeginRemoteSpecialization must be called before EndRemoteSpecialization."
            );
        }

        playerData.Trees.Clear();

        foreach (KeyValuePair<string, CoreSpecializationState> pair
            in playerData.RemotePendingTrees)
        {
            playerData.Trees.Add(pair.Key, pair.Value);
        }

        playerData.RemotePendingTrees.Clear();
        playerData.RemoteUpdateInProgress = false;

        playerData.CacheConfigurationRevision = int.MinValue;
        playerData.CacheRegistryRevision = int.MinValue;
        playerData.CacheNativeUnlockStamp = int.MinValue;
        playerData.CacheStateRevisionStamp = int.MinValue;
        playerData.ClearResolutionCaches();

        SynchronizeAllAutoGrantedNodes(pilot);
        InvalidateConfiguration();
    }

    /// <summary>
    /// Releases all transient specialization state owned by a destroyed or
    /// replaced network replica. Local Pilot state is never removed here.
    /// </summary>
    public static void ClearRemoteSpecialization(Pilot pilot)
    {
        if (pilot == null)
            return;

        CorePilotSpecializationData playerData;
        if (!data.TryGetValue(pilot, out playerData) ||
            !playerData.IsRemoteTransient)
        {
            return;
        }

        data.Remove(pilot);
        InvalidateConfiguration();
    }

    // Rebuilds tree definitions from the currently compiled *Tree.cs files.
    // Specialization state is keyed by stable tree/node IDs and is preserved.
    // This is intentionally used by the F10 authoring UI so Unity hot reload
    // cannot leave the registry holding stale tree objects after a tree edit.
    public static void RefreshTreeDefinitions()
    {
        CoreSpecializationPolicies.RebuildAllTrees();
        registeredDefaults = true;
        InvalidateConfiguration();

        Pilot pilot = GetCurrentPilot();
        if (pilot != null && data.ContainsKey(pilot))
            SynchronizeAllAutoGrantedNodes(pilot);
    }

    private static CorePilotSpecializationData GetPilotData(Pilot pilot)
    {
        RegisterDefaults();

        if (pilot == null)
            return null;

        CorePilotSpecializationData playerData;
        if (!data.TryGetValue(pilot, out playerData))
        {
            playerData = new CorePilotSpecializationData();
            data.Add(pilot, playerData);

            string persistenceReason;
            playerData.PersistenceReady = CoreSpecializationPersistence.Load(
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
        else if (playerData.IsRemoteTransient)
        {
            // Remote replica state is supplied by CoreNetwork and is
            // deliberately disconnected from local save persistence.
            return playerData;
        }
        else if (!playerData.PersistenceReady)
        {
            string retryReason;
            bool ready = CoreSpecializationPersistence.Load(
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

    private static CoreSpecializationState GetRawState(
        Pilot pilot,
        string treeId)
    {
        CorePilotSpecializationData playerData = GetPilotData(pilot);
        if (playerData == null)
            return null;

        CoreSpecializationState state;
        if (!playerData.Trees.TryGetValue(treeId, out state))
        {
            state = new CoreSpecializationState();
            playerData.Trees.Add(treeId, state);
        }

        return state;
    }

    private static CoreSpecializationState GetRawState(
        CorePilotSpecializationData playerData,
        string treeId)
    {
        if (playerData == null || string.IsNullOrEmpty(treeId))
            return null;

        CoreSpecializationState state;
        if (!playerData.Trees.TryGetValue(treeId, out state))
        {
            state = new CoreSpecializationState();
            playerData.Trees.Add(treeId, state);
        }

        return state;
    }

    private static int ComputeNativeUnlockStamp(Pilot pilot)
    {
        unchecked
        {
            int hash = 17;
            IList<CoreSpecializationTree> trees =
                CoreSpecializationRegistry.All();

            for (int i = 0; i < trees.Count; i++)
            {
                CoreSpecializationTree tree = trees[i];
                if (tree.UnlockKind != CoreTreeUnlockKind.NativeUpgrade)
                    continue;

                int rank = pilot == null
                    ? 0
                    : pilot.GetUpgradeLevel(
                        (Upgrade.Key)tree.NativeUnlockUpgradeKey);

                hash = hash * 31 + tree.NativeUnlockUpgradeKey;
                hash = hash * 31 + rank;
            }

            return hash;
        }
    }

    private static int ComputeStateRevisionStamp(
        CorePilotSpecializationData playerData)
    {
        unchecked
        {
            int hash = 17;
            IList<CoreSpecializationTree> trees =
                CoreSpecializationRegistry.All();

            for (int i = 0; i < trees.Count; i++)
            {
                CoreSpecializationState state;
                int revision = playerData != null &&
                    playerData.Trees.TryGetValue(trees[i].Id, out state) &&
                    state != null
                        ? state.Revision
                        : 0;

                hash = hash * 31 + revision;
            }

            return hash;
        }
    }

    private static void EnsureResolutionCacheValid(
        Pilot pilot,
        CorePilotSpecializationData playerData)
    {
        if (playerData == null)
            return;

        int registryRevision = CoreSpecializationRegistry.Revision;
        int nativeUnlockStamp = ComputeNativeUnlockStamp(pilot);
        int stateRevisionStamp = ComputeStateRevisionStamp(playerData);

        if (playerData.CacheConfigurationRevision == configurationRevision &&
            playerData.CacheRegistryRevision == registryRevision &&
            playerData.CacheNativeUnlockStamp == nativeUnlockStamp &&
            playerData.CacheStateRevisionStamp == stateRevisionStamp)
        {
            return;
        }

        playerData.CacheConfigurationRevision = configurationRevision;
        playerData.CacheRegistryRevision = registryRevision;
        playerData.CacheNativeUnlockStamp = nativeUnlockStamp;
        playerData.CacheStateRevisionStamp = stateRevisionStamp;
        playerData.ClearResolutionCaches();
    }

    public static CoreSpecializationState GetState(
        Pilot pilot,
        string treeId)
    {
        RegisterDefaults();
        CoreSpecializationTree tree = CoreSpecializationRegistry.Get(treeId);
        CoreSpecializationState state = GetRawState(pilot, treeId);

        if (tree != null && state != null)
            SynchronizeAutoGrantedNodes(pilot, tree, state);

        return state;
    }

    private static void SynchronizeAllAutoGrantedNodes(Pilot pilot)
    {
        IList<CoreSpecializationTree> trees =
            CoreSpecializationRegistry.All();

        for (int i = 0; i < trees.Count; i++)
        {
            CoreSpecializationState state = GetRawState(pilot, trees[i].Id);
            SynchronizeAutoGrantedNodes(pilot, trees[i], state);
        }
    }

    private static void SynchronizeAutoGrantedNodes(
        Pilot pilot,
        CoreSpecializationTree tree,
        CoreSpecializationState state)
    {
        if (tree == null || state == null)
            return;

        bool unlocked = IsTreeUnlockedRaw(pilot, tree);
        IList<CoreSpecializationNode> nodes = tree.Nodes;

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
        CoreSpecializationTree tree = CoreSpecializationRegistry.Get(treeId);
        return IsTreeUnlockedRaw(pilot, tree);
    }

    public static bool IsTreeUnlocked(Pilot pilot, CoreSpecializationTree tree)
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
        CoreSpecializationTree tree)
    {
        if (pilot == null || tree == null)
            return false;

        CorePilotSpecializationData playerData = GetPilotData(pilot);
        if (playerData == null)
            return false;

        EnsureResolutionCacheValid(pilot, playerData);
        return IsTreeUnlockedCached(pilot, tree, playerData);
    }

    private static bool IsTreeUnlockedCached(
        Pilot pilot,
        CoreSpecializationTree tree,
        CorePilotSpecializationData playerData)
    {
        if (pilot == null || tree == null || playerData == null)
            return false;

        bool cached;
        if (playerData.TreeUnlockCache.TryGetValue(tree.Id, out cached))
            return cached;

        playerData.UnlockPathScratch.Clear();
        return EvaluateTreeUnlocked(
            pilot,
            tree,
            playerData,
            playerData.UnlockPathScratch);
    }

    private static bool EvaluateTreeUnlocked(
        Pilot pilot,
        CoreSpecializationTree tree,
        CorePilotSpecializationData playerData,
        HashSet<string> path)
    {
        bool cached;
        if (playerData.TreeUnlockCache.TryGetValue(tree.Id, out cached))
            return cached;

        if (!path.Add(tree.Id))
            return false;

        bool result = false;

        try
        {
            if (tree.UnlockKind == CoreTreeUnlockKind.Always)
            {
                result = true;
            }
            else if (tree.UnlockKind == CoreTreeUnlockKind.NativeUpgrade)
            {
                result = pilot.GetUpgradeLevel(
                    (Upgrade.Key)tree.NativeUnlockUpgradeKey
                ) >= 1;
            }
            else
            {
                IList<CoreSpecializationTree> all =
                    CoreSpecializationRegistry.All();

                for (int i = 0; i < all.Count; i++)
                {
                    CoreSpecializationTree sourceTree = all[i];
                    if (sourceTree.Id == tree.Id ||
                        !EvaluateTreeUnlocked(
                            pilot,
                            sourceTree,
                            playerData,
                            path))
                    {
                        continue;
                    }

                    CoreSpecializationState state =
                        GetRawState(playerData, sourceTree.Id);

                    if (state != null &&
                        state.HasUnlockTreeEffect(sourceTree, tree.Id))
                    {
                        result = true;
                        break;
                    }
                }
            }
        }
        finally
        {
            path.Remove(tree.Id);
        }

        playerData.TreeUnlockCache[tree.Id] = result;
        return result;
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

        CorePilotSpecializationData playerData = GetPilotData(pilot);
        if (playerData == null)
        {
            reason = "No specialization state.";
            return false;
        }

        if (playerData.IsRemoteTransient)
        {
            reason = "Remote specialization is transient and cannot spend points.";
            return false;
        }

        if (!playerData.PersistenceReady)
        {
            reason = playerData.PersistenceReason;
            return false;
        }

        return CoreSpecializationPoints.IsAvailable(pilot, out reason);
    }

    public static bool CanInvest(
        Pilot pilot,
        string treeId,
        string nodeId,
        out string reason)
    {
        reason = string.Empty;
        RegisterDefaults();

        CoreSpecializationTree tree = CoreSpecializationRegistry.Get(treeId);
        if (tree == null || pilot == null)
        {
            reason = "Tree or Pilot unavailable.";
            return false;
        }

        if (!IsTreeUnlockedRaw(pilot, tree))
        {
            reason = tree.Name + " is locked.";
            return false;
        }

        if (!CanSafelySpend(pilot, out reason))
            return false;

        CoreSpecializationState state = GetState(pilot, treeId);
        if (!state.CanInvest(tree, nodeId, out reason))
            return false;

        CoreSpecializationNode node = tree.GetNode(nodeId);
        int cost = node == null ? 1 : node.PointCostPerRank;

        if (GetAvailablePoints(pilot) < cost)
        {
            reason = "Not enough " + CoreSpecializationPoints.GetCurrencyName(pilot) + ".";
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

        CoreSpecializationTree tree = CoreSpecializationRegistry.Get(treeId);
        CoreSpecializationState state = GetState(pilot, treeId);
        CoreSpecializationNode node = tree.GetNode(nodeId);
        int cost = node.PointCostPerRank;

        if (!CoreSpecializationPoints.TrySpend(pilot, cost, out reason))
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

        CorePilotSpecializationData playerData = GetPilotData(pilot);
        if (!CoreSpecializationPersistence.Save(
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

        CoreSpecializationTree tree = CoreSpecializationRegistry.Get(treeId);
        CoreSpecializationState state = GetState(pilot, treeId);

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

        CoreSpecializationTree tree = CoreSpecializationRegistry.Get(treeId);
        CoreSpecializationState state = GetState(pilot, treeId);
        CoreSpecializationNode node = tree.GetNode(nodeId);
        int oldRank = state.GetRank(nodeId);

        state.SetRank(nodeId, oldRank - 1);
        SynchronizeAllAutoGrantedNodes(pilot);

        CorePilotSpecializationData playerData = GetPilotData(pilot);
        if (!CoreSpecializationPersistence.Save(
                pilot,
                playerData.Trees,
                out reason))
        {
            state.SetRank(nodeId, oldRank);
            SynchronizeAllAutoGrantedNodes(pilot);
            return false;
        }

        string ignoredRefundReason;
        CoreSpecializationPoints.TryRefund(
            pilot, node.PointCostPerRank, out ignoredRefundReason);

        InvalidateConfiguration();
        return true;
    }

    private static bool ValidateAllInvestedState(Pilot pilot, out string reason)
    {
        reason = string.Empty;
        IList<CoreSpecializationTree> trees =
            CoreSpecializationRegistry.All();

        for (int i = 0; i < trees.Count; i++)
        {
            CoreSpecializationTree tree = trees[i];
            CoreSpecializationState state = GetRawState(pilot, tree.Id);
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

    public static int GetSpentPointsForClass(
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
    }

    public static int GetTreeSpentPoints(Pilot pilot, string treeId)
    {
        CoreSpecializationTree tree = CoreSpecializationRegistry.Get(treeId);
        CoreSpecializationState state = GetState(pilot, treeId);
        return tree == null || state == null ? 0 : state.GetSpentPointCost(tree);
    }

    public static int GetGrantedPoints(Pilot pilot)
    {
        RegisterDefaults();
        return CoreSpecializationPoints.GetGrantedPoints(pilot);
    }

    public static int GetAvailablePoints(Pilot pilot)
    {
        RegisterDefaults();
        return CoreSpecializationPoints.GetAvailablePoints(pilot);
    }

    public static int GetProgressionRank(Pilot pilot)
    {
        RegisterDefaults();
        return CoreSpecializationPoints.GetProgressionRank(pilot);
    }

    public static bool ResetAll(Pilot pilot, out string reason)
    {
        reason = string.Empty;
        if (pilot == null)
            return false;

        RegisterDefaults();
        CorePilotSpecializationData playerData = GetPilotData(pilot);
        if (playerData == null)
            return false;

        playerData.Trees.Clear();

        IList<CoreSpecializationTree> trees =
            CoreSpecializationRegistry.All();
        for (int i = 0; i < trees.Count; i++)
            playerData.Trees[trees[i].Id] = new CoreSpecializationState();

        SynchronizeAllAutoGrantedNodes(pilot);

        bool saved = CoreSpecializationPersistence.Save(
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
        CoreSpecializationTree tree = CoreSpecializationRegistry.Get(treeId);
        CoreSpecializationState state = GetState(pilot, treeId);

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
        CoreSpecializationTree tree = CoreSpecializationRegistry.Get(treeId);
        CoreSpecializationState state = GetState(pilot, treeId);
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
        CoreSpecializationKnob knob)
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
        CoreSpecializationKnob knob)
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
        CoreSpecializationKnob knob,
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
        CoreSpecializationKnob knob,
        out float flat,
        out float percent,
        out float multiplier)
    {
        flat = 0f;
        percent = 0f;
        multiplier = 1f;

        RegisterDefaults();
        CorePilotSpecializationData playerData = GetPilotData(pilot);
        if (playerData == null)
            return;

        EnsureResolutionCacheValid(pilot, playerData);

        CoreSpecializationAggregateCacheValue cached;
        if (playerData.KnobAggregateCache.TryGetValue(knob.Id, out cached))
        {
            flat = cached.Flat;
            percent = cached.Percent;
            multiplier = cached.Multiplier;
            return;
        }

        IList<CoreSpecializationTree> trees =
            CoreSpecializationRegistry.All();

        for (int i = 0; i < trees.Count; i++)
        {
            if (!IsTreeUnlockedCached(pilot, trees[i], playerData))
                continue;

            CoreSpecializationState state =
                GetRawState(playerData, trees[i].Id);
            if (state == null)
                continue;

            state.Aggregate(
                trees[i],
                knob.Id,
                ref flat,
                ref percent,
                ref multiplier
            );
        }

        playerData.KnobAggregateCache[knob.Id] =
            new CoreSpecializationAggregateCacheValue(
                flat,
                percent,
                multiplier);
    }

    public static bool HasFlag(
        Pilot pilot,
        string treeId,
        string flagId)
    {
        RegisterDefaults();
        CoreSpecializationTree tree = CoreSpecializationRegistry.Get(treeId);
        CoreSpecializationState state = GetState(pilot, treeId);
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
        CoreSpecializationFlag flag)
    {
        if (pilot == null || flag == null)
            return false;

        RegisterDefaults();
        CorePilotSpecializationData playerData = GetPilotData(pilot);
        if (playerData == null)
            return false;

        EnsureResolutionCacheValid(pilot, playerData);

        bool cached;
        if (playerData.FlagCache.TryGetValue(flag.Id, out cached))
            return cached;

        bool enabled = false;
        IList<CoreSpecializationTree> trees =
            CoreSpecializationRegistry.All();

        for (int i = 0; i < trees.Count; i++)
        {
            CoreSpecializationTree tree = trees[i];
            if (!IsTreeUnlockedCached(pilot, tree, playerData))
                continue;

            CoreSpecializationState state =
                GetRawState(playerData, tree.Id);
            if (state != null && state.HasFlag(tree, flag.Id))
            {
                enabled = true;
                break;
            }
        }

        playerData.FlagCache[flag.Id] = enabled;
        return enabled;
    }

    public static bool HasFlag(CoreSpecializationFlag flag)
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

/// <summary>
/// Small authoring DSL for specialization tree content files.
/// Gameplay code should expose named knobs/flags; tree files should mostly read
/// like balance data and prerequisites.
/// </summary>
public static class CoreTreeDsl
{
    // ---------------------------------------------------------------------
    // Trees
    // ---------------------------------------------------------------------

    // Normal specialization tree unlocked by another specialization effect.
    public static CoreSpecializationTree Tree(
        string id,
        string name,
        string rootNodeId,
        int displayOrder)
    {
        return new CoreSpecializationTree(
            id,
            name,
            rootNodeId,
            displayOrder,
            CoreTreeUnlockKind.SpecializationEffect,
            -1
        );
    }

    // Root tree backed directly by a native Star Vortex upgrade.
    public static CoreSpecializationTree NativeTree(
        string id,
        string name,
        string rootNodeId,
        int displayOrder,
        int nativeUpgradeKey)
    {
        return new CoreSpecializationTree(
            id,
            name,
            rootNodeId,
            displayOrder,
            CoreTreeUnlockKind.NativeUpgrade,
            nativeUpgradeKey
        );
    }

    public sealed class Effect
    {
        internal readonly CoreSpecializationEffect Inner;
        internal readonly int RankCountHint;

        internal Effect(CoreSpecializationEffect inner, int rankCountHint)
        {
            Inner = inner;
            RankCountHint = Math.Max(0, rankCountHint);
        }
    }

    // ---------------------------------------------------------------------
    // IDs / requirements
    // ---------------------------------------------------------------------

    public static string Id(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Node name/id is required.", "name");

        StringBuilder builder = new StringBuilder(name.Length);
        bool pendingSeparator = false;

        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];

            if (char.IsLetterOrDigit(c))
            {
                if (pendingSeparator && builder.Length > 0)
                    builder.Append('_');

                builder.Append(char.ToLowerInvariant(c));
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = builder.Length > 0;
            }
        }

        string id = builder.ToString().Trim('_');
        if (id.Length == 0)
            throw new ArgumentException("Node name/id produced an empty stable id.", "name");

        return id;
    }

    public static CoreRequirement Requires(string nodeName)
    {
        return CoreReq.Rank(Id(nodeName), 1);
    }

    public static CoreRequirement Requires(string nodeName, int rank)
    {
        return CoreReq.Rank(Id(nodeName), rank);
    }

    public static CoreRequirement Rank(string nodeName)
    {
        return Requires(nodeName, 1);
    }

    public static CoreRequirement Rank(string nodeName, int rank)
    {
        return Requires(nodeName, rank);
    }

    public static CoreRequirement RequiresAll(params CoreRequirement[] requirements)
    {
        return CoreReq.All(requirements);
    }

    public static CoreRequirement RequiresAny(params CoreRequirement[] requirements)
    {
        return CoreReq.Any(requirements);
    }

    public static CoreRequirement RequiresAll(params string[] nodeNames)
    {
        return CombineNames(true, nodeNames);
    }

    public static CoreRequirement RequiresAny(params string[] nodeNames)
    {
        return CombineNames(false, nodeNames);
    }

    private static CoreRequirement CombineNames(bool all, string[] nodeNames)
    {
        if (nodeNames == null || nodeNames.Length == 0)
            return CoreReq.None;

        CoreRequirement[] requirements = new CoreRequirement[nodeNames.Length];
        for (int i = 0; i < nodeNames.Length; i++)
            requirements[i] = Requires(nodeNames[i]);

        return all
            ? CoreReq.All(requirements)
            : CoreReq.Any(requirements);
    }

    // ---------------------------------------------------------------------
    // Effects
    // ---------------------------------------------------------------------

    // +value each rank. Negative values work identically.
    public static Effect Increment(CoreSpecializationKnob knob, float valuePerRank)
    {
        return new Effect(CoreFx.Increment(knob, valuePerRank), 0);
    }

    // Explicit contribution for each purchased rank. The node rank count is
    // inferred from this list when no explicit max rank is supplied.
    public static Effect Ranks(CoreSpecializationKnob knob, params float[] valuesByRank)
    {
        int count = valuesByRank == null ? 0 : valuesByRank.Length;
        return new Effect(CoreFx.Ranks(knob, valuesByRank), count);
    }

    public static Effect Multiply(CoreSpecializationKnob knob, float factorPerRank)
    {
        return new Effect(CoreFx.Multiply(knob, factorPerRank), 0);
    }

    public static Effect MultiplyRanks(CoreSpecializationKnob knob, params float[] factorsByRank)
    {
        int count = factorsByRank == null ? 0 : factorsByRank.Length;
        return new Effect(CoreFx.MultiplyRanks(knob, factorsByRank), count);
    }

    // Purchased rank 1/2/3 resolves to exactly the supplied total multipliers.
    public static Effect MultiplyTotals(CoreSpecializationKnob knob, params float[] totalFactorsByRank)
    {
        int count = totalFactorsByRank == null ? 0 : totalFactorsByRank.Length;
        return new Effect(CoreFx.MultiplyTotals(knob, totalFactorsByRank), count);
    }

    public static Effect Enable(CoreSpecializationFlag flag)
    {
        return new Effect(CoreFx.Flag(flag), 1);
    }

    public static Effect UnlockTree(string treeId)
    {
        return new Effect(CoreFx.UnlockTree(treeId), 1);
    }

    // ---------------------------------------------------------------------
    // Nodes
    // ---------------------------------------------------------------------

    public static CoreSpecializationNode Root(
        string name,
        string description)
    {
        return CoreNode.GrantedRoot(Id(name), name, description);
    }

    public static CoreSpecializationNode Root(
        string stableId,
        string name,
        string description)
    {
        return CoreNode.GrantedRoot(stableId, name, description);
    }

    public static CoreSpecializationNode Node(
        string name,
        CoreRequirement requirement,
        params Effect[] effects)
    {
        return Node(name, InferRanks(effects), requirement, string.Empty, effects);
    }

    public static CoreSpecializationNode Node(
        string name,
        int ranks,
        CoreRequirement requirement,
        params Effect[] effects)
    {
        return Node(name, ranks, requirement, string.Empty, effects);
    }

    public static CoreSpecializationNode Node(
        string name,
        int ranks,
        CoreRequirement requirement,
        string description,
        params Effect[] effects)
    {
        return CoreNode.Passive(
            Id(name),
            name,
            Math.Max(1, ranks),
            requirement,
            description,
            Unwrap(effects)
        );
    }

    public static CoreSpecializationNode Major(
        string name,
        CoreRequirement requirement,
        params Effect[] effects)
    {
        return Major(name, InferRanks(effects), requirement, string.Empty, effects);
    }

    public static CoreSpecializationNode Major(
        string name,
        int ranks,
        CoreRequirement requirement,
        params Effect[] effects)
    {
        return Major(name, ranks, requirement, string.Empty, effects);
    }

    public static CoreSpecializationNode Major(
        string name,
        int ranks,
        CoreRequirement requirement,
        string description,
        params Effect[] effects)
    {
        return CoreNode.Major(
            Id(name),
            name,
            Math.Max(1, ranks),
            requirement,
            description,
            Unwrap(effects)
        );
    }

    public static CoreSpecializationNode Keystone(
        string name,
        CoreRequirement requirement,
        params Effect[] effects)
    {
        return Keystone(name, requirement, string.Empty, string.Empty, effects);
    }

    public static CoreSpecializationNode Keystone(
        string name,
        CoreRequirement requirement,
        string description,
        params Effect[] effects)
    {
        return Keystone(name, requirement, string.Empty, description, effects);
    }

    public static CoreSpecializationNode Keystone(
        string name,
        CoreRequirement requirement,
        string exclusiveGroup,
        string description,
        params Effect[] effects)
    {
        return CoreNode.Keystone(
            Id(name),
            name,
            requirement,
            exclusiveGroup,
            description,
            Unwrap(effects)
        );
    }

    public static CoreSpecializationNode ExclusiveNode(
        string name,
        CoreRequirement requirement,
        string exclusiveGroup,
        params Effect[] effects)
    {
        return ExclusiveNode(name, InferRanks(effects), requirement, exclusiveGroup, effects);
    }

    public static CoreSpecializationNode ExclusiveNode(
        string name,
        int ranks,
        CoreRequirement requirement,
        string exclusiveGroup,
        params Effect[] effects)
    {
        return CoreNode.PassiveExclusive(
            Id(name),
            name,
            Math.Max(1, ranks),
            requirement,
            exclusiveGroup,
            string.Empty,
            Unwrap(effects)
        );
    }

    public static CoreSpecializationNode ExclusiveMajor(
        string name,
        CoreRequirement requirement,
        string exclusiveGroup,
        params Effect[] effects)
    {
        return ExclusiveMajor(name, InferRanks(effects), requirement, exclusiveGroup, effects);
    }

    public static CoreSpecializationNode ExclusiveMajor(
        string name,
        int ranks,
        CoreRequirement requirement,
        string exclusiveGroup,
        params Effect[] effects)
    {
        return CoreNode.MajorExclusive(
            Id(name),
            name,
            Math.Max(1, ranks),
            requirement,
            exclusiveGroup,
            string.Empty,
            Unwrap(effects)
        );
    }

    // Explicit stable-id escape hatch for renamed display names / save compatibility.
    public static CoreSpecializationNode NodeId(
        string stableId,
        string name,
        CoreRequirement requirement,
        params Effect[] effects)
    {
        return NodeId(stableId, name, InferRanks(effects), requirement, string.Empty, effects);
    }

    public static CoreSpecializationNode NodeId(
        string stableId,
        string name,
        int ranks,
        CoreRequirement requirement,
        params Effect[] effects)
    {
        return NodeId(stableId, name, ranks, requirement, string.Empty, effects);
    }

    public static CoreSpecializationNode NodeId(
        string stableId,
        string name,
        int ranks,
        CoreRequirement requirement,
        string description,
        params Effect[] effects)
    {
        return CoreNode.Passive(
            stableId,
            name,
            Math.Max(1, ranks),
            requirement,
            description ?? string.Empty,
            Unwrap(effects)
        );
    }

    private static int InferRanks(Effect[] effects)
    {
        int inferred = 0;

        if (effects != null)
        {
            for (int i = 0; i < effects.Length; i++)
            {
                if (effects[i] == null || effects[i].RankCountHint <= 0)
                    continue;

                if (inferred == 0)
                {
                    inferred = effects[i].RankCountHint;
                }
                else if (inferred != effects[i].RankCountHint)
                {
                    throw new InvalidOperationException(
                        "Per-rank effect lists on the same node have different lengths (" +
                        inferred.ToString() + " vs " + effects[i].RankCountHint.ToString() + ")."
                    );
                }
            }
        }

        return inferred > 0 ? inferred : 1;
    }

    private static CoreSpecializationEffect[] Unwrap(Effect[] effects)
    {
        if (effects == null || effects.Length == 0)
            return new CoreSpecializationEffect[0];

        List<CoreSpecializationEffect> output =
            new List<CoreSpecializationEffect>(effects.Length);

        for (int i = 0; i < effects.Length; i++)
        {
            if (effects[i] != null && effects[i].Inner != null)
                output.Add(effects[i].Inner);
        }

        return output.ToArray();
    }
}
