using StarVortex;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

// =============================================================================
// LEVIATHAN SPECIALIZATION FRAMEWORK
// =============================================================================
// Universal specialization engine. Skill files expose knobs/flags and implement
// mechanics; *Tree.cs files only declare nodes, prerequisites and values.
// This file intentionally contains all non-UI/non-currency framework machinery:
// data model, requirements, modifiers, graph layout, runtime state/persistence,
// registry/catalog, and the small tree-authoring DSL.

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
    private bool multiplierRanksAreTotals;

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

    // Explicit multiplier contribution against any named knob. The knob's Kind
    // controls the default Increment/Ranks authoring semantics, not the complete
    // set of operations that can target it. This lets one node add +damage while
    // another multiplies the already-resolved damage through the same knob.
    public static LeviathanSpecializationEffect KnobMultiplier(
        LeviathanSpecializationKnob knob,
        float factorPerRank)
    {
        if (knob == null)
            throw new ArgumentNullException("knob");

        return new LeviathanSpecializationEffect(
            LeviathanSpecializationEffectType.Multiplier,
            knob.Id,
            knob,
            true,
            factorPerRank,
            null
        );
    }

    public static LeviathanSpecializationEffect KnobMultiplierRanks(
        LeviathanSpecializationKnob knob,
        params float[] factorsByRank)
    {
        if (knob == null)
            throw new ArgumentNullException("knob");
        if (factorsByRank == null || factorsByRank.Length == 0)
            throw new ArgumentException("At least one per-rank factor is required.", "factorsByRank");

        float[] factors = new float[factorsByRank.Length];
        Array.Copy(factorsByRank, factors, factorsByRank.Length);

        return new LeviathanSpecializationEffect(
            LeviathanSpecializationEffectType.Multiplier,
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
    public static LeviathanSpecializationEffect KnobMultiplierTotals(
        LeviathanSpecializationKnob knob,
        params float[] totalFactorsByRank)
    {
        LeviathanSpecializationEffect effect = KnobMultiplierRanks(
            knob,
            totalFactorsByRank
        );
        effect.multiplierRanksAreTotals = true;
        return effect;
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
            if (Type == LeviathanSpecializationEffectType.Multiplier)
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

    // Explicit multiplier helper against any named knob. Example:
    // Multiply(StarfireKnobs.Damage, 1.45f) multiplies the damage value after
    // additive/percent contributions on that same knob have been resolved.
    public static LeviathanSpecializationEffect Multiply(
        LeviathanSpecializationKnob knob,
        float factorPerRank)
    {
        return LeviathanSpecializationEffect.KnobMultiplier(
            knob,
            factorPerRank
        );
    }

    public static LeviathanSpecializationEffect MultiplyRanks(
        LeviathanSpecializationKnob knob,
        params float[] factorsByRank)
    {
        return LeviathanSpecializationEffect.KnobMultiplierRanks(
            knob,
            factorsByRank
        );
    }

    public static LeviathanSpecializationEffect MultiplyTotals(
        LeviathanSpecializationKnob knob,
        params float[] totalFactorsByRank)
    {
        return LeviathanSpecializationEffect.KnobMultiplierTotals(
            knob,
            totalFactorsByRank
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

    public static LeviathanSpecializationNode PassiveExclusive(
        string id,
        string name,
        int maxRank,
        LeviathanRequirement requirement,
        string exclusiveGroup,
        string description,
        params LeviathanSpecializationEffect[] effects)
    {
        return new LeviathanSpecializationNode(
            id,
            name,
            maxRank,
            LeviathanSpecializationNodeType.Passive,
            requirement,
            exclusiveGroup,
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

    public static LeviathanSpecializationNode MajorExclusive(
        string id,
        string name,
        int maxRank,
        LeviathanRequirement requirement,
        string exclusiveGroup,
        string description,
        params LeviathanSpecializationEffect[] effects)
    {
        return new LeviathanSpecializationNode(
            id,
            name,
            maxRank,
            LeviathanSpecializationNodeType.Major,
            requirement,
            exclusiveGroup,
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

    private readonly IList<LeviathanSpecializationNode> readOnlyNodes;

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
        readOnlyNodes = nodes.AsReadOnly();
    }

    public IList<LeviathanSpecializationNode> Nodes
    {
        get { return readOnlyNodes; }
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
        LeviathanSpecializationTree tree,
        LeviathanSpecializationLayout layout)
    {
        Dictionary<string, List<LeviathanSpecializationLayoutNode>> childrenByParent =
            new Dictionary<string, List<LeviathanSpecializationLayoutNode>>(
                StringComparer.Ordinal
            );

        IList<LeviathanSpecializationNode> nodes = tree.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            LeviathanRankRequirement direct =
                nodes[i].Requirement as LeviathanRankRequirement;

            if (direct == null)
                continue;

            LeviathanSpecializationLayoutNode childLayout;
            if (!layout.Nodes.TryGetValue(nodes[i].Id, out childLayout))
                continue;

            List<LeviathanSpecializationLayoutNode> siblings;
            if (!childrenByParent.TryGetValue(direct.NodeId, out siblings))
            {
                siblings = new List<LeviathanSpecializationLayoutNode>();
                childrenByParent.Add(direct.NodeId, siblings);
            }

            siblings.Add(childLayout);
        }

        foreach (KeyValuePair<string, List<LeviathanSpecializationLayoutNode>> pair
            in childrenByParent)
        {
            List<LeviathanSpecializationLayoutNode> siblings = pair.Value;
            if (siblings.Count <= 1)
                continue;

            LeviathanSpecializationLayoutNode parentLayout;
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
                LeviathanSpecializationLayoutNode a,
                LeviathanSpecializationLayoutNode b)
            {
                int orderCompare = a.Order.CompareTo(b.Order);
                return orderCompare != 0
                    ? orderCompare
                    : string.CompareOrdinal(a.NodeId, b.NodeId);
            });

            float center = (siblings.Count - 1) * 0.5f;
            for (int i = 0; i < siblings.Count; i++)
            {
                siblings[i].Position = new LeviathanLayoutPoint(
                    siblings[i].Position.X,
                    parentLayout.Position.Y +
                        (i - center) * VerticalSpacing
                );
            }
        }
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

    private static IList<LeviathanSpecializationTree> cachedAll =
        new List<LeviathanSpecializationTree>().AsReadOnly();

    private static bool cachedAllDirty = true;
    private static int revision;

    public static int Revision
    {
        get { return revision; }
    }

    public static void Register(LeviathanSpecializationTree tree)
    {
        if (tree == null)
            throw new ArgumentNullException("tree");

        tree.Validate();
        trees[tree.Id] = tree;
        MarkStructureChanged();
    }

    public static LeviathanSpecializationTree Get(string treeId)
    {
        LeviathanSpecializationTree tree;
        return treeId != null && trees.TryGetValue(treeId, out tree)
            ? tree
            : null;
    }

    // Tree definitions change only during registration/rebuild. Cache the sorted
    // read-only view so combat/runtime lookups never OrderBy/ToList the registry.
    public static IList<LeviathanSpecializationTree> All()
    {
        if (!cachedAllDirty)
            return cachedAll;

        List<LeviathanSpecializationTree> sorted =
            new List<LeviathanSpecializationTree>(trees.Values);

        sorted.Sort(delegate (
            LeviathanSpecializationTree a,
            LeviathanSpecializationTree b)
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
        LeviathanSpecializationTree tree = Get(key);
        return tree == null ? key : tree.Name;
    }
}

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

internal struct LeviathanSpecializationAggregateCacheValue
{
    public float Flat;
    public float Percent;
    public float Multiplier;

    public LeviathanSpecializationAggregateCacheValue(
        float flat,
        float percent,
        float multiplier)
    {
        Flat = flat;
        Percent = percent;
        Multiplier = multiplier;
    }
}

public sealed class LeviathanPilotSpecializationData
{
    public readonly Dictionary<string, LeviathanSpecializationState> Trees =
        new Dictionary<string, LeviathanSpecializationState>(StringComparer.Ordinal);

    public bool PersistenceReady;
    public string PersistenceReason;

    // Runtime resolution caches. Validity is keyed to all inputs that can alter
    // specialization results, including direct SetRank changes used by refund
    // simulation and native-upgrade tree unlocks.
    internal int CacheConfigurationRevision = int.MinValue;
    internal int CacheRegistryRevision = int.MinValue;
    internal int CacheNativeUnlockStamp = int.MinValue;
    internal int CacheStateRevisionStamp = int.MinValue;

    internal readonly Dictionary<string, bool> TreeUnlockCache =
        new Dictionary<string, bool>(StringComparer.Ordinal);

    internal readonly Dictionary<string, LeviathanSpecializationAggregateCacheValue>
        KnobAggregateCache =
            new Dictionary<string, LeviathanSpecializationAggregateCacheValue>(
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

public static class LeviathanSpecializationRuntime
{
    public const string DiagnosticBuildMarker = "SPEC-DIAG-20260907-B";
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

    // Rebuilds tree definitions from the currently compiled *Tree.cs files.
    // Specialization state is keyed by stable tree/node IDs and is preserved.
    // This is intentionally used by the F10 authoring UI so Unity hot reload
    // cannot leave the registry holding stale tree objects after a tree edit.
    public static void RefreshTreeDefinitions()
    {
        LeviathanSpecializationCurrency.EnsureRegistered();
        LeviathanSpecializationCatalog.RebuildAll();
        registeredDefaults = true;
        InvalidateConfiguration();

        Pilot pilot = GetCurrentPilot();
        if (pilot != null && data.ContainsKey(pilot))
            SynchronizeAllAutoGrantedNodes(pilot);
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

    private static LeviathanSpecializationState GetRawState(
        LeviathanPilotSpecializationData playerData,
        string treeId)
    {
        if (playerData == null || string.IsNullOrEmpty(treeId))
            return null;

        LeviathanSpecializationState state;
        if (!playerData.Trees.TryGetValue(treeId, out state))
        {
            state = new LeviathanSpecializationState();
            playerData.Trees.Add(treeId, state);
        }

        return state;
    }

    private static int ComputeNativeUnlockStamp(Pilot pilot)
    {
        unchecked
        {
            int hash = 17;
            IList<LeviathanSpecializationTree> trees =
                LeviathanSpecializationRegistry.All();

            for (int i = 0; i < trees.Count; i++)
            {
                LeviathanSpecializationTree tree = trees[i];
                if (tree.UnlockKind != LeviathanTreeUnlockKind.NativeUpgrade)
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
        LeviathanPilotSpecializationData playerData)
    {
        unchecked
        {
            int hash = 17;
            IList<LeviathanSpecializationTree> trees =
                LeviathanSpecializationRegistry.All();

            for (int i = 0; i < trees.Count; i++)
            {
                LeviathanSpecializationState state;
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
        LeviathanPilotSpecializationData playerData)
    {
        if (playerData == null)
            return;

        int registryRevision = LeviathanSpecializationRegistry.Revision;
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
        if (pilot == null || tree == null)
            return false;

        LeviathanPilotSpecializationData playerData = GetPilotData(pilot);
        if (playerData == null)
            return false;

        EnsureResolutionCacheValid(pilot, playerData);
        return IsTreeUnlockedCached(pilot, tree, playerData);
    }

    private static bool IsTreeUnlockedCached(
        Pilot pilot,
        LeviathanSpecializationTree tree,
        LeviathanPilotSpecializationData playerData)
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
        LeviathanSpecializationTree tree,
        LeviathanPilotSpecializationData playerData,
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
            if (tree.UnlockKind == LeviathanTreeUnlockKind.Always)
            {
                result = true;
            }
            else if (tree.UnlockKind == LeviathanTreeUnlockKind.NativeUpgrade)
            {
                result = pilot.GetUpgradeLevel(
                    (Upgrade.Key)tree.NativeUnlockUpgradeKey
                ) >= 1;
            }
            else
            {
                IList<LeviathanSpecializationTree> all =
                    LeviathanSpecializationRegistry.All();

                for (int i = 0; i < all.Count; i++)
                {
                    LeviathanSpecializationTree sourceTree = all[i];
                    if (sourceTree.Id == tree.Id ||
                        !EvaluateTreeUnlocked(
                            pilot,
                            sourceTree,
                            playerData,
                            path))
                    {
                        continue;
                    }

                    LeviathanSpecializationState state =
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
        LeviathanPilotSpecializationData playerData = GetPilotData(pilot);
        if (playerData == null)
            return;

        EnsureResolutionCacheValid(pilot, playerData);

        LeviathanSpecializationAggregateCacheValue cached;
        if (playerData.KnobAggregateCache.TryGetValue(knob.Id, out cached))
        {
            flat = cached.Flat;
            percent = cached.Percent;
            multiplier = cached.Multiplier;
            return;
        }

        IList<LeviathanSpecializationTree> trees =
            LeviathanSpecializationRegistry.All();

        for (int i = 0; i < trees.Count; i++)
        {
            if (!IsTreeUnlockedCached(pilot, trees[i], playerData))
                continue;

            LeviathanSpecializationState state =
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
            new LeviathanSpecializationAggregateCacheValue(
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
        LeviathanPilotSpecializationData playerData = GetPilotData(pilot);
        if (playerData == null)
            return false;

        EnsureResolutionCacheValid(pilot, playerData);

        bool cached;
        if (playerData.FlagCache.TryGetValue(flag.Id, out cached))
            return cached;

        bool enabled = false;
        IList<LeviathanSpecializationTree> trees =
            LeviathanSpecializationRegistry.All();

        for (int i = 0; i < trees.Count; i++)
        {
            LeviathanSpecializationTree tree = trees[i];
            if (!IsTreeUnlockedCached(pilot, tree, playerData))
                continue;

            LeviathanSpecializationState state =
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

/// <summary>
/// Small authoring DSL for specialization tree content files.
/// Gameplay code should expose named knobs/flags; tree files should mostly read
/// like balance data and prerequisites.
/// </summary>
public static class LeviathanTreeDsl
{
    // ---------------------------------------------------------------------
    // Trees
    // ---------------------------------------------------------------------

    // Normal specialization tree unlocked by another specialization effect.
    public static LeviathanSpecializationTree Tree(
        string id,
        string name,
        string rootNodeId,
        int displayOrder)
    {
        return new LeviathanSpecializationTree(
            id,
            name,
            rootNodeId,
            displayOrder,
            LeviathanTreeUnlockKind.SpecializationEffect,
            -1
        );
    }

    // Root tree backed directly by a native Star Vortex upgrade.
    public static LeviathanSpecializationTree NativeTree(
        string id,
        string name,
        string rootNodeId,
        int displayOrder,
        int nativeUpgradeKey)
    {
        return new LeviathanSpecializationTree(
            id,
            name,
            rootNodeId,
            displayOrder,
            LeviathanTreeUnlockKind.NativeUpgrade,
            nativeUpgradeKey
        );
    }

    public sealed class Effect
    {
        internal readonly LeviathanSpecializationEffect Inner;
        internal readonly int RankCountHint;

        internal Effect(LeviathanSpecializationEffect inner, int rankCountHint)
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

    public static LeviathanRequirement Requires(string nodeName)
    {
        return LeviathanReq.Rank(Id(nodeName), 1);
    }

    public static LeviathanRequirement Requires(string nodeName, int rank)
    {
        return LeviathanReq.Rank(Id(nodeName), rank);
    }

    public static LeviathanRequirement Rank(string nodeName)
    {
        return Requires(nodeName, 1);
    }

    public static LeviathanRequirement Rank(string nodeName, int rank)
    {
        return Requires(nodeName, rank);
    }

    public static LeviathanRequirement RequiresAll(params LeviathanRequirement[] requirements)
    {
        return LeviathanReq.All(requirements);
    }

    public static LeviathanRequirement RequiresAny(params LeviathanRequirement[] requirements)
    {
        return LeviathanReq.Any(requirements);
    }

    public static LeviathanRequirement RequiresAll(params string[] nodeNames)
    {
        return CombineNames(true, nodeNames);
    }

    public static LeviathanRequirement RequiresAny(params string[] nodeNames)
    {
        return CombineNames(false, nodeNames);
    }

    private static LeviathanRequirement CombineNames(bool all, string[] nodeNames)
    {
        if (nodeNames == null || nodeNames.Length == 0)
            return LeviathanReq.None;

        LeviathanRequirement[] requirements = new LeviathanRequirement[nodeNames.Length];
        for (int i = 0; i < nodeNames.Length; i++)
            requirements[i] = Requires(nodeNames[i]);

        return all
            ? LeviathanReq.All(requirements)
            : LeviathanReq.Any(requirements);
    }

    // ---------------------------------------------------------------------
    // Effects
    // ---------------------------------------------------------------------

    // +value each rank. Negative values work identically.
    public static Effect Increment(LeviathanSpecializationKnob knob, float valuePerRank)
    {
        return new Effect(LeviathanFx.Increment(knob, valuePerRank), 0);
    }

    // Explicit contribution for each purchased rank. The node rank count is
    // inferred from this list when no explicit max rank is supplied.
    public static Effect Ranks(LeviathanSpecializationKnob knob, params float[] valuesByRank)
    {
        int count = valuesByRank == null ? 0 : valuesByRank.Length;
        return new Effect(LeviathanFx.Ranks(knob, valuesByRank), count);
    }

    public static Effect Multiply(LeviathanSpecializationKnob knob, float factorPerRank)
    {
        return new Effect(LeviathanFx.Multiply(knob, factorPerRank), 0);
    }

    public static Effect MultiplyRanks(LeviathanSpecializationKnob knob, params float[] factorsByRank)
    {
        int count = factorsByRank == null ? 0 : factorsByRank.Length;
        return new Effect(LeviathanFx.MultiplyRanks(knob, factorsByRank), count);
    }

    // Purchased rank 1/2/3 resolves to exactly the supplied total multipliers.
    public static Effect MultiplyTotals(LeviathanSpecializationKnob knob, params float[] totalFactorsByRank)
    {
        int count = totalFactorsByRank == null ? 0 : totalFactorsByRank.Length;
        return new Effect(LeviathanFx.MultiplyTotals(knob, totalFactorsByRank), count);
    }

    public static Effect Enable(LeviathanSpecializationFlag flag)
    {
        return new Effect(LeviathanFx.Flag(flag), 1);
    }

    public static Effect UnlockTree(string treeId)
    {
        return new Effect(LeviathanFx.UnlockTree(treeId), 1);
    }

    // ---------------------------------------------------------------------
    // Nodes
    // ---------------------------------------------------------------------

    public static LeviathanSpecializationNode Root(
        string name,
        string description)
    {
        return LeviathanNode.GrantedRoot(Id(name), name, description);
    }

    public static LeviathanSpecializationNode Root(
        string stableId,
        string name,
        string description)
    {
        return LeviathanNode.GrantedRoot(stableId, name, description);
    }

    public static LeviathanSpecializationNode Node(
        string name,
        LeviathanRequirement requirement,
        params Effect[] effects)
    {
        return Node(name, InferRanks(effects), requirement, string.Empty, effects);
    }

    public static LeviathanSpecializationNode Node(
        string name,
        int ranks,
        LeviathanRequirement requirement,
        params Effect[] effects)
    {
        return Node(name, ranks, requirement, string.Empty, effects);
    }

    public static LeviathanSpecializationNode Node(
        string name,
        int ranks,
        LeviathanRequirement requirement,
        string description,
        params Effect[] effects)
    {
        return LeviathanNode.Passive(
            Id(name),
            name,
            Math.Max(1, ranks),
            requirement,
            description,
            Unwrap(effects)
        );
    }

    public static LeviathanSpecializationNode Major(
        string name,
        LeviathanRequirement requirement,
        params Effect[] effects)
    {
        return Major(name, InferRanks(effects), requirement, string.Empty, effects);
    }

    public static LeviathanSpecializationNode Major(
        string name,
        int ranks,
        LeviathanRequirement requirement,
        params Effect[] effects)
    {
        return Major(name, ranks, requirement, string.Empty, effects);
    }

    public static LeviathanSpecializationNode Major(
        string name,
        int ranks,
        LeviathanRequirement requirement,
        string description,
        params Effect[] effects)
    {
        return LeviathanNode.Major(
            Id(name),
            name,
            Math.Max(1, ranks),
            requirement,
            description,
            Unwrap(effects)
        );
    }

    public static LeviathanSpecializationNode Keystone(
        string name,
        LeviathanRequirement requirement,
        params Effect[] effects)
    {
        return Keystone(name, requirement, string.Empty, string.Empty, effects);
    }

    public static LeviathanSpecializationNode Keystone(
        string name,
        LeviathanRequirement requirement,
        string description,
        params Effect[] effects)
    {
        return Keystone(name, requirement, string.Empty, description, effects);
    }

    public static LeviathanSpecializationNode Keystone(
        string name,
        LeviathanRequirement requirement,
        string exclusiveGroup,
        string description,
        params Effect[] effects)
    {
        return LeviathanNode.Keystone(
            Id(name),
            name,
            requirement,
            exclusiveGroup,
            description,
            Unwrap(effects)
        );
    }

    public static LeviathanSpecializationNode ExclusiveNode(
        string name,
        LeviathanRequirement requirement,
        string exclusiveGroup,
        params Effect[] effects)
    {
        return ExclusiveNode(name, InferRanks(effects), requirement, exclusiveGroup, effects);
    }

    public static LeviathanSpecializationNode ExclusiveNode(
        string name,
        int ranks,
        LeviathanRequirement requirement,
        string exclusiveGroup,
        params Effect[] effects)
    {
        return LeviathanNode.PassiveExclusive(
            Id(name),
            name,
            Math.Max(1, ranks),
            requirement,
            exclusiveGroup,
            string.Empty,
            Unwrap(effects)
        );
    }

    public static LeviathanSpecializationNode ExclusiveMajor(
        string name,
        LeviathanRequirement requirement,
        string exclusiveGroup,
        params Effect[] effects)
    {
        return ExclusiveMajor(name, InferRanks(effects), requirement, exclusiveGroup, effects);
    }

    public static LeviathanSpecializationNode ExclusiveMajor(
        string name,
        int ranks,
        LeviathanRequirement requirement,
        string exclusiveGroup,
        params Effect[] effects)
    {
        return LeviathanNode.MajorExclusive(
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
    public static LeviathanSpecializationNode NodeId(
        string stableId,
        string name,
        LeviathanRequirement requirement,
        params Effect[] effects)
    {
        return NodeId(stableId, name, InferRanks(effects), requirement, string.Empty, effects);
    }

    public static LeviathanSpecializationNode NodeId(
        string stableId,
        string name,
        int ranks,
        LeviathanRequirement requirement,
        params Effect[] effects)
    {
        return NodeId(stableId, name, ranks, requirement, string.Empty, effects);
    }

    public static LeviathanSpecializationNode NodeId(
        string stableId,
        string name,
        int ranks,
        LeviathanRequirement requirement,
        string description,
        params Effect[] effects)
    {
        return LeviathanNode.Passive(
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

    private static LeviathanSpecializationEffect[] Unwrap(Effect[] effects)
    {
        if (effects == null || effects.Length == 0)
            return new LeviathanSpecializationEffect[0];

        List<LeviathanSpecializationEffect> output =
            new List<LeviathanSpecializationEffect>(effects.Length);

        for (int i = 0; i < effects.Length; i++)
        {
            if (effects[i] != null && effects[i].Inner != null)
                output.Add(effects[i].Inner);
        }

        return output.ToArray();
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
        LeviathanSpecializationRegistry.Clear();
        RegisterCurrentDefinitions();
        registered = true;
    }

    private static void RegisterCurrentDefinitions()
    {
        LeviathanSpecializationRegistry.Register(LeviathanEvolutionTree.Create());
        LeviathanSpecializationRegistry.Register(LeviathanStarfireTree.Create());
        LeviathanSpecializationRegistry.Register(LeviathanConstrictorTree.Create());
        LeviathanSpecializationRegistry.Register(LeviathanPredatorTree.Create());
        LeviathanSpecializationRegistry.Register(LeviathanBehemothTree.Create());
        LeviathanSpecializationRegistry.Register(LeviathanStellarConverterTree.Create());
    }
}
