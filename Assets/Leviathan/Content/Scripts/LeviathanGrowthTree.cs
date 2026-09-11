using System;

// Growth specialization configuration only. Gameplay mechanics, native patches,
// resolved-state calculation and tuning live in LeviathanGrowth.cs.
public static class LeviathanGrowthTree
{
    public const string TreeId = "growth";
    public const string RootNodeId = "growth_root";

    public const string CellDivisionId = "cell_division";
    public const string InternalHeatsinksId = "internal_heatsinks";
    public const string EnhancedMusculatureId = "enhanced_musculature";
    public const string ThermoregulationId = "thermoregulation";
    public const string RedundantSystemsId = "redundant_systems";
    public const string EnhancedReactionsId = "enhanced_reactions";
    public const string ColdBloodedId = "cold_blooded";
    public const string SwiftPredatorId = "swift_predator";
    public const string StarDragonId = "star_dragon";
    public const string CalcificationId = "calcification";
    public const string SidewinderId = "sidewinder";
    public const string AblativeScalesId = "ablative_scales";
    public const string ExpansionId = "expansion";
    public const string BifurcationId = "bifurcation";
    public const string GigantismId = "gigantism";
    public const string SpecializedPredatorId = "specialized_predator";
    public const string AncientWyrmId = "ancient_wyrm";
    public const string LaminarScutesId = "laminar_scutes";
    public const string CoolingFinsId = "cooling_fins";
    public const string HiggsDampingFieldId = "higgs_damping_field";
    public const string CoronalMassEjectionId = "coronal_mass_ejection";

    public static CoreSpecializationTree Create()
    {
        CoreSpecializationTree tree = new CoreSpecializationTree(
            TreeId,
            "Growth",
            RootNodeId,
            5,
            CoreTreeUnlockKind.NativeUpgrade,
            LeviathanSpecializationCurrency.UpgradeKeyValue
        );

        // Evolution rank 1 automatically awakens the Leviathan chassis and this
        // granted Growth root. Growth itself is no longer a native skill.
        tree.Add(CoreNode.GrantedRoot(
            RootNodeId,
            "Growth 1",
            "Evolution rank 1 awakens the Leviathan chassis with one body segment and one tail. Ordinary per-segment stat scaling counts every section except the primary head; segment protections apply only to body and tail sections. Segment hits have 20% baseline debuff discard, plus 1 percentage point for every 2 Evolution Points spent in Growth. Maximum Heat Capacity and Heat Dissipation use the Leviathan 90% native baseline before Growth modifiers are applied."
        ));

        tree.Add(Node(
            CellDivisionId,
            "Cell Division",
            2,
            CoreReq.Rank(RootNodeId),
            1,
            "Gain 200 Hull and 2 body segments per rank.",
            CoreFx.Increment(LeviathanGrowth.Knobs.HullFlat, 200f),
            CoreFx.Increment(LeviathanGrowth.Knobs.AdditionalSections, 2f)
        ));

        tree.Add(Node(
            InternalHeatsinksId,
            "Internal Heatsinks",
            2,
            CoreReq.Rank(RootNodeId),
            1,
            "Gain 5% maximum Heat Capacity and 1 body segment per rank.",
            CoreFx.Increment(LeviathanGrowth.Knobs.MaxHeatPercent, 5f),
            CoreFx.Increment(LeviathanGrowth.Knobs.AdditionalSections, 1f)
        ));

        tree.Add(Node(
            EnhancedMusculatureId,
            "Enhanced Musculature",
            2,
            CoreReq.Rank(RootNodeId),
            1,
            "Gain 5% Acceleration and 1 body segment per rank.",
            CoreFx.Increment(LeviathanGrowth.Knobs.AccelerationPercent, 5f),
            CoreFx.Increment(LeviathanGrowth.Knobs.AdditionalSections, 1f)
        ));

        tree.Add(Node(
            ThermoregulationId,
            "Thermoregulation",
            2,
            CoreReq.Any(
                CoreReq.Rank(InternalHeatsinksId),
                CoreReq.Rank(CellDivisionId)
            ),
            1,
            "Gain 5% Heat Dissipation plus 1% Heat Dissipation per segment per rank.",
            CoreFx.Increment(LeviathanGrowth.Knobs.HeatDissipationPercent, 5f),
            CoreFx.Increment(LeviathanGrowth.Knobs.HeatDissipationPerSegmentPercent, 1f)
        ));

        tree.Add(Node(
            RedundantSystemsId,
            "Redundant Systems",
            2,
            CoreReq.Rank(CellDivisionId),
            1,
            "Gain 15 health regeneration per second and 1 body segment per rank.",
            CoreFx.Increment(LeviathanGrowth.Knobs.HealthRegenFlat, 15f),
            CoreFx.Increment(LeviathanGrowth.Knobs.AdditionalSections, 1f)
        ));

        tree.Add(Node(
            EnhancedReactionsId,
            "Enhanced Reactions",
            1,
            CoreReq.Any(
                CoreReq.Rank(CellDivisionId),
                CoreReq.Rank(EnhancedMusculatureId)
            ),
            1,
            "Gain 0.75% Turn Speed per segment and 5% Maneuverability.",
            CoreFx.Increment(LeviathanGrowth.Knobs.TurnSpeedPerSegmentPercent, 0.75f),
            CoreFx.Increment(LeviathanGrowth.Knobs.MobilityPercent, 5f)
        ));

        tree.Add(Node(
            ColdBloodedId,
            "Cold Blooded",
            1,
            CoreReq.Rank(ThermoregulationId),
            2,
            "Lose 50% Heat Dissipation and gain 75% maximum Heat Capacity. While active, gain Acceleration, Maneuverability, Top Speed and Boost equal to 50% of effective Heat percentage (100% Heat = +50%; 200% Heat = +100%).",
            CoreFx.Increment(LeviathanGrowth.Knobs.HeatDissipationPercent, -50f),
            CoreFx.Increment(LeviathanGrowth.Knobs.MaxHeatPercent, 75f),
            CoreFx.Flag(LeviathanGrowth.Flags.ColdBlooded)
        ));

        tree.Add(Node(
            SwiftPredatorId,
            "Swift Predator",
            2,
            CoreReq.Rank(EnhancedMusculatureId),
            1,
            "Gain 1.5% Top Speed and Boost per segment and 1 body segment per rank.",
            CoreFx.Increment(LeviathanGrowth.Knobs.TopSpeedPerSegmentPercent, 1.5f),
            CoreFx.Increment(LeviathanGrowth.Knobs.BoostPerSegmentPercent, 1.5f),
            CoreFx.Increment(LeviathanGrowth.Knobs.AdditionalSections, 1f)
        ));

        tree.Add(Node(
            StarDragonId,
            "Star Dragon",
            1,
            CoreReq.Rank(ThermoregulationId),
            2,
            "Unlocks Supernova behavior. Crossing into Supernova costs 7.5% current HP with a 10-second entry-cost cooldown; while above 100% Heat, Global Damage equals Overheat percentage and maximum-HP drain per second equals Overheat percentage divided by 3. Runtime behavior is reserved for the Supernova pass.",
            CoreFx.Flag(LeviathanGrowth.Flags.StarDragon)
        ));

        tree.Add(Node(
            CalcificationId,
            "Calcification",
            2,
            CoreReq.Rank(RedundantSystemsId),
            1,
            "Gain 5% Armor, 1% Armor per segment, and 1 body segment per rank.",
            CoreFx.Increment(LeviathanGrowth.Knobs.ArmorPoints, 5f),
            CoreFx.Increment(LeviathanGrowth.Knobs.ArmorPerSegmentPoints, 1f),
            CoreFx.Increment(LeviathanGrowth.Knobs.AdditionalSections, 1f)
        ));

        tree.Add(Node(
            SidewinderId,
            "Sidewinder",
            2,
            CoreReq.Rank(EnhancedReactionsId),
            1,
            "Gain 5% Maneuverability and 5% Acceleration per rank.",
            CoreFx.Increment(LeviathanGrowth.Knobs.MobilityPercent, 5f),
            CoreFx.Increment(LeviathanGrowth.Knobs.AccelerationPercent, 5f)
        ));

        tree.Add(Node(
            AblativeScalesId,
            "Ablative Scales",
            2,
            CoreReq.Rank(RedundantSystemsId),
            1,
            "Gain 5% all resistances, 1.5% Shield per segment, and 1 body segment per rank.",
            CoreFx.Increment(LeviathanGrowth.Knobs.AllResistancePoints, 5f),
            CoreFx.Increment(LeviathanGrowth.Knobs.ShieldPerSegmentPercent, 1.5f),
            CoreFx.Increment(LeviathanGrowth.Knobs.AdditionalSections, 1f)
        ));

        tree.Add(Node(
            ExpansionId,
            "Expansion",
            2,
            CoreReq.Rank(RedundantSystemsId),
            1,
            "Gain 2 body segments per rank.",
            CoreFx.Increment(LeviathanGrowth.Knobs.AdditionalSections, 2f)
        ));

        tree.Add(Node(
            BifurcationId,
            "Bifurcation",
            1,
            CoreReq.Rank(ExpansionId),
            1,
            "Split your existing segment budget into two tail branches and gain 7.5% Turn Speed and 7.5% Maneuverability. Save Tail_a / Tail_b for branch-wide designs, Tail_aN / Tail_bN for numbered overrides, and BifurcateN for the final shared-body segment where the split occurs.",
            CoreFx.Increment(LeviathanGrowth.Knobs.TurnSpeedPercent, 7.5f),
            CoreFx.Increment(LeviathanGrowth.Knobs.MobilityPercent, 7.5f),
            CoreFx.Increment(LeviathanGrowth.Knobs.AdditionalTails, 1f),
            CoreFx.Flag(LeviathanGrowth.Flags.Bifurcation)
        ));

        tree.Add(Node(
            GigantismId,
            "Gigantism",
            1,
            CoreReq.Rank(ExpansionId),
            1,
            "Increase the maximum ship size category available in the ship builder by 1, using the same class-override semantics as Warden.",
            CoreFx.Increment(LeviathanGrowth.Knobs.ShipSizeCategoryIncrease, 1f),
            CoreFx.Flag(LeviathanGrowth.Flags.Gigantism)
        ));

        tree.Add(Node(
            SpecializedPredatorId,
            "Specialized Predator",
            2,
            CoreReq.Rank(ExpansionId),
            2,
            "Lose 2 weapon slots and gain 25% Global Damage per rank. Rank 1 requires at least 2 pre-node weapon slots; rank 2 requires at least 4, allowing a zero-weapon-slot utility/Assault build.",
            CoreFx.Increment(LeviathanGrowth.Knobs.WeaponSlotsFlat, -2f),
            CoreFx.Increment(LeviathanGrowth.Knobs.GlobalDamagePercent, 25f),
            CoreFx.Flag(LeviathanGrowth.Flags.SpecializedPredator)
        ));

        tree.Add(Node(
            AncientWyrmId,
            "Ancient Wyrm",
            1,
            CoreReq.Rank(ExpansionId),
            2,
            "Gain 3 body segments, 2% Mass per segment, 2% Critical Damage per segment, and 0.75 percentage points of status chance per segment.",
            CoreFx.Increment(LeviathanGrowth.Knobs.AdditionalSections, 3f),
            CoreFx.Increment(LeviathanGrowth.Knobs.MassPerSegmentPercent, 2f),
            CoreFx.Increment(LeviathanGrowth.Knobs.CritDamagePerSegmentPercent, 2f),
            CoreFx.Increment(LeviathanGrowth.Knobs.StatusChancePerSegmentPoints, 0.75f)
        ));

        tree.Add(Node(
            LaminarScutesId,
            "Laminar Scutes",
            2,
            CoreReq.Rank(SwiftPredatorId),
            1,
            "Gain 5% Top Speed and reduce Leviathan high-speed air resistance by 30% per rank.",
            CoreFx.Increment(LeviathanGrowth.Knobs.TopSpeedPercent, 5f),
            CoreFx.Increment(LeviathanGrowth.Knobs.AirResistancePercent, -30f),
            CoreFx.Flag(LeviathanGrowth.Flags.LaminarScutes)
        ));

        tree.Add(Node(
            CoolingFinsId,
            "Cooling Fins",
            1,
            CoreReq.Rank(LaminarScutesId),
            2,
            "Movement directly vents Heat even while firing or boosting. Each 10 m/s of current speed removes Heat each second equal to 1% of your current Heat Dissipation rate.",
            CoreFx.Flag(LeviathanGrowth.Flags.CoolingFins)
        ));

        tree.Add(Node(
            HiggsDampingFieldId,
            "Higgs Damping Field",
            3,
            CoreReq.Any(
                CoreReq.Rank(LaminarScutesId),
                CoreReq.Rank(SidewinderId)
            ),
            2,
            "Reduce Mass by 6% per rank.",
            CoreFx.Increment(LeviathanGrowth.Knobs.MassPercent, -6f)
        ));

        tree.Add(Node(
            CoronalMassEjectionId,
            "Coronal Mass Ejection",
            3,
            CoreReq.Rank(StarDragonId),
            1,
            "Supernova catastrophically vents at 200% / 185% / 170% effective Heat and disables the ship for 2 / 1 / 0 seconds by rank. The +100% to +0% five-second speed burst begins after the disable. Runtime behavior is reserved for the Supernova pass.",
            // Ranks() stores additive per-rank contributions, so these deltas
            // resolve to exact totals of 200/185/170 and 2/1/0 respectively.
            CoreFx.Ranks(LeviathanGrowth.Knobs.SupernovaCollapseHeatPercent, 200f, -15f, -15f),
            CoreFx.Ranks(LeviathanGrowth.Knobs.SupernovaDisableSeconds, 2f, -1f, -1f),
            CoreFx.Flag(LeviathanGrowth.Flags.CoronalMassEjection)
        ));

        tree.Validate();
        return tree;
    }

    // Point cost is intentionally tree-local configuration. The universal DSL
    // remains generic; Growth's 2-point majors do not require framework changes.
    private static CoreSpecializationNode Node(
        string id,
        string name,
        int maxRank,
        CoreRequirement requirement,
        int pointCostPerRank,
        string description,
        params CoreSpecializationEffect[] effects)
    {
        return new CoreSpecializationNode(
            id,
            name,
            Math.Max(1, maxRank),
            CoreSpecializationNodeType.Passive,
            requirement,
            null,
            description ?? string.Empty,
            Math.Max(1, pointCostPerRank),
            false,
            effects
        );
    }
}
