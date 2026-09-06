LEVIATHAN SPECIALIZATION FRAMEWORK v3
=====================================

CURRENT STRUCTURE
-----------------
Native Star Vortex skill:
    Evolution rank N -> N * 3 Growth Points

Evolution specialization tree:
    Evolution root is granted automatically when native Evolution >= 1.
    Five normal 1-point nodes unlock:
        Starfire
        Constrictor
        Predator
        Behemoth
        Stellar Converter

Child trees:
    Buying the corresponding Evolution unlock grants the child tree's root
    automatically for 0 additional Growth Points.

    The child trees are intentionally blank in this pass. Their root is the
    first node and future nodes simply require that root (or another node).


FRAMEWORK / CONTENT SPLIT
-------------------------
Generic framework:
    LeviathanSpecializationCore.cs
    LeviathanSpecializationRuntime.cs
    LeviathanSpecializationUI.cs
    LeviathanSpecializationCurrency.cs

Registration only:
    LeviathanSpecializationCatalog.cs

Tree definitions:
    LeviathanEvolutionTree.cs
    LeviathanStarfireTree.cs
    LeviathanConstrictorTree.cs
    LeviathanPredatorTree.cs
    LeviathanBehemothTree.cs
    LeviathanStellarConverterTree.cs

Skill-side knob bridge (first example):
    LeviathanStarfireKnobs.cs
    LeviathanStarfireSpecializationBridge.cs

The framework does not know what Width, Damage, LungeDistance, etc. mean.
A skill exposes named knobs. Tree nodes point at those knobs. The skill-side
bridge reads the final knob result and applies it to the skill's actual code.


NODE -> KNOB SYNTAX
-------------------
Percent knobs use human-readable percentages in tree files.

Constant increment:

    tree.Add(LeviathanNode.Passive(
        "focused_destruction",
        "Focused Destruction",
        3,
        LeviathanReq.Rank(RootNodeId),
        "Narrows the Starfire cone.",
        LeviathanFx.Increment(LeviathanStarfireKnobs.Width, -5f)
    ));

This means:
    Rank 1: -5% Width
    Rank 2: another -5% Width  (-10% total)
    Rank 3: another -5% Width  (-15% total)

Manual value per rank:

    tree.Add(LeviathanNode.Passive(
        "broad_breath",
        "Broad Breath",
        3,
        LeviathanReq.Rank(RootNodeId),
        "Broadens the Starfire cone.",
        LeviathanFx.Ranks(LeviathanStarfireKnobs.Width, 5f, 5f, 10f)
    ));

This means:
    Rank 1 adds +5%
    Rank 2 adds +5%
    Rank 3 adds +10%
    Rank 3 total = +20% Width

Negative values work in either form.

A node can modify several knobs:

    tree.Add(LeviathanNode.Passive(
        "compressed_plasma",
        "Compressed Plasma",
        3,
        LeviathanReq.Rank(RootNodeId),
        "Trades area for concentrated output.",
        LeviathanFx.Increment(LeviathanStarfireKnobs.Width, -5f),
        LeviathanFx.Increment(LeviathanStarfireKnobs.Damage, 10f),
        LeviathanFx.Ranks(LeviathanStarfireKnobs.DebuffChance, 2f, 3f, 5f)
    ));


DEFINING A KNOB
---------------
Percent knob:

    public static readonly LeviathanSpecializationKnob Width =
        LeviathanSpecializationKnob.Percent("starfire.width", "Width");

Flat knob (example for future skill code):

    public static readonly LeviathanSpecializationKnob ChargeTimeSeconds =
        LeviathanSpecializationKnob.Flat(
            "starfire.charge_seconds",
            "Charge Time",
            "s"
        );

Then the tree can say:

    LeviathanFx.Increment(ChargeTimeSeconds, 0.20f)

and the effect is +0.20 seconds each rank rather than +20%.


READING A KNOB FROM SKILL FUNCTIONALITY
---------------------------------------
For percentage-style scaling:

    value *= LeviathanSpecializationRuntime.GetKnobMultiplier(
        pilot,
        LeviathanStarfireKnobs.Width
    );

For a flat knob:

    value += LeviathanSpecializationRuntime.GetKnobFlat(
        pilot,
        SomeSkillKnobs.SomeFlatValue
    );

Or let the framework do both:

    value = LeviathanSpecializationRuntime.ApplyKnob(
        pilot,
        SomeSkillKnobs.SomeKnob,
        value
    );

Knob aggregation scans all active registered trees. This means a future
Evolution/global node could modify the same skill knob without changing the
skill bridge.


PREREQUISITES / LAYOUT
----------------------
Nodes still use only prerequisite relationships. Layout is generated.

    LeviathanReq.Rank("node_a")

    LeviathanReq.All(
        LeviathanReq.Rank("node_a", 2),
        LeviathanReq.Rank("node_b", 1)
    )

    LeviathanReq.Any(
        LeviathanReq.Rank("node_a"),
        LeviathanReq.Rank("node_b")
    )

AND/OR can be nested. Exclusive groups remain independent of layout.

No node coordinates are required.


TREE UNLOCKING
--------------
Evolution unlock nodes use the same generic node effect:

    LeviathanFx.UnlockTree("starfire")

A child tree declares:

    UnlockKind = SpecializationEffect

and has a zero-cost auto-granted root. No Starfire-specific unlock code exists
in the tree framework.

Refunding a tree-unlock node is blocked if paid points remain in that child tree.


ADDING A NEW TREE
-----------------
1. Create a tree definition file with a stable tree ID and granted root.
2. Register it in LeviathanSpecializationCatalog.cs.
3. Add an Evolution node with LeviathanFx.UnlockTree(newTreeId).
4. Expose knobs from that skill's functionality/bridge as needed.
5. Add nodes to its tree definition. Renderer/runtime/save logic does not change.
