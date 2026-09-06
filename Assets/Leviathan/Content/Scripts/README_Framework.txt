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

Skill-side knob surface / integration (first example):
    LeviathanStarfireKnobs.cs
    LeviathanStarfire.cs
    LeviathanStarfireSpecializationBridge.cs

The framework does not know what Width, Damage, LungeDistance, etc. mean.
A skill exposes named knobs and tree nodes point at those knobs.

Preferred integration:
    Values owned by mod skill code read their knobs directly in that skill.
    A Harmony bridge is only needed for values owned by native Star Vortex code
    or for activation/unlock boundaries that cannot be changed directly.


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

Flat knob:

    public static readonly LeviathanSpecializationKnob ChargeTimeSeconds =
        LeviathanSpecializationKnob.Flat(
            "starfire.charge_seconds",
            "Charge Time",
            "s"
        );

Then the tree can say:

    LeviathanFx.Increment(ChargeTimeSeconds, 0.20f)

and the effect is +0.20 seconds each rank rather than +20%.

Additive percentage-points knob:

    public static readonly LeviathanSpecializationKnob CritChance =
        LeviathanSpecializationKnob.PercentagePoints(
            "starfire.crit_chance",
            "Critical Chance"
        );

Then:

    LeviathanFx.Increment(CritChance, 5f)

means +5 percentage points. A source weapon with 10% crit becomes 15%.
This is intentionally different from Percent(), where +5 means multiply the
existing value by 1.05.

PercentagePoints is also appropriate for status chance, crit-damage bonus,
opacity, and other values stored internally as decimal fractions where the
authored talent should say things like +5%.


READING A KNOB FROM SKILL FUNCTIONALITY
---------------------------------------
For percentage-style scaling:

    value *= LeviathanSpecializationRuntime.GetKnobMultiplier(
        pilot,
        LeviathanStarfireKnobs.Width
    );

For a flat or percentage-points knob:

    value += LeviathanSpecializationRuntime.GetKnobFlat(
        pilot,
        SomeSkillKnobs.SomeFlatValue
    );

PercentagePoints returns decimal flat values here: authored +5% returns 0.05.

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
4. Expose named knobs for the skill.
5. Read mod-owned values directly through those knobs; use a bridge only when
   the underlying value belongs to native Star Vortex code.
6. Add nodes to its tree definition. Renderer/runtime/save logic does not change.


STARFIRE EXPOSED KNOBS
----------------------
Core gameplay:
    HeatGeneration
    Length
    Width
    Damage
    CritChance             additive percentage points
    CritDamage             additive percentage points
    StatusChance           additive percentage points

Breath / charge timing:
    Duration               overall percent multiplier on hold + retreat
    FullSizeHoldSeconds
    RetreatSeconds
    MinimumLength          additive percentage points
    RetreatCurveExponent
    StartupDelaySeconds
    ChargeRampSpeed
    RechargeTime           common native recovery multiplier
    Cooldown               native Activatable cooldown only
    RechargeSeconds        native Activatable recharge only

Cone / hitbox:
    BaseFanHalfAngleDegrees
    MuzzleWidth            additive percentage points
    HitboxWidthPadding     additive percentage points
    CenterLengthBonus      additive percentage points

Cosmetic plume:
    VisualOpacity          additive percentage points
    VisualBeamFill         additive percentage points
    VisualMinimumBeamWidth additive percentage points
    VisualEndFeather       additive percentage points
    VisualBeamCount        flat integer after rounding
    VisualLengthSegments   flat integer after rounding

All Starfire-owned values are read directly through these knobs. Native
Activatable cooldown/recharge values remain in the Starfire specialization
bridge because those properties belong to Star Vortex itself.

FRAMEWORK v4 ADDITIONS
----------------------
Named multiplier knobs:

    public static readonly LeviathanSpecializationKnob DamageFactor =
        LeviathanSpecializationKnob.Multiplier(
            "example.damage_factor",
            "Damage Factor"
        );

    LeviathanFx.Multiply(DamageFactor, 1.25f)

means x1.25 per purchased rank. MultiplyRanks() is also available for explicit
per-rank factors. Generic Ranks() also works with Multiplier knobs.

Typed feature flags:

    public static readonly LeviathanSpecializationFlag SomeMode =
        LeviathanSpecializationFlag.Create(
            "example.some_mode",
            "Some Mode"
        );

    LeviathanFx.Flag(SomeMode)

Runtime feature code can then use:

    LeviathanSpecializationRuntime.HasFlag(pilot, SomeMode)

Named flag lookup scans every unlocked registered tree, just like named knob
aggregation. Runtime functionality therefore does not need a source tree ID.


STARFIRE PERSISTENT BREATH MODEL
--------------------------------
Starfire now owns a persistent breath reservoir instead of resetting its falloff
clock on every trigger release.

Baseline specialization Starfire:
    Full-power capacity:        1.50 seconds
    Falloff capacity:           2.00 seconds
    Total reservoir:            3.50 breath-seconds
    Active drain:               1.00 breath-second / second
    Idle recovery:              0.50 breath-second / second
    Empty-to-full recovery:     about 7 seconds
    Recovery delay:             0 seconds

Windup does not consume breath. Releasing preserves the remaining reservoir and
starts recovery. Damage, length and angular width all read the same reservoir,
but have independent minimum values and falloff curves.

Persistent-breath knobs:
    Duration
    FullSizeHoldSeconds
    RetreatSeconds
    ActiveDrainRate
    RecoveryRate
    RecoverySecondsPerSecond
    RecoveryDelaySeconds
    RecoveryCurveExponent
    RetreatCurveExponent
    DamageFalloffCurveExponent
    LengthFalloffCurveExponent
    WidthFalloffCurveExponent
    MinimumDamage
    MinimumLength
    MinimumWidth

Starfire exposes public state helpers for future conditional talents:
    GetBreathPower01(torch)
    GetBreathRemainingSeconds(torch)
    IsBreathRecovering(torch)

This is the intended hook for effects such as Recharge Pull / Big Succ.

Additional Starfire feature surfaces are already named for future tree content:
    RechargePull flag + radius/strength/falloff/max-speed knobs
    BlastWave flag + arc/damage-seconds/damage-multiplier/speed/range/width/
        duration/falloff/opacity/knockback knobs
