using static LeviathanTreeDsl;
using Predator = LeviathanPredatorRuntime;

/// <summary>
/// Predator test progression while the full 30-40 node topology is designed.
///
/// The three one-rank nodes below are intentionally real Predator mechanics,
/// chosen to make Prey, authoritative DirectLunge outcomes, Prey Kill/Hunt
/// stacking, and cooldown reset behavior obvious during normal co-op play.
/// They can later move/rebalance without changing their runtime semantics.
/// </summary>
public static class LeviathanPredatorTree
{
    public const string TreeId = "predator";
    public const string RootNodeId = "predator";

    public static LeviathanSpecializationTree Create()
    {
        LeviathanSpecializationTree tree = Tree(
            TreeId,
            "Predator",
            RootNodeId,
            30
        );

        tree.Add(Root(
            RootNodeId,
            "Predator",
            "Granted automatically when Predator is unlocked in Evolution."
        ));

        // Temporary progression only. The agreed Predator baseline lives in
        // Predator.Tuning; these increments keep the current scaffolding useful
        // while the final topology is still being designed.
        tree.Add(Node(
            "Predatory Instinct",
            5,
            Requires("Predator"),
            Increment(Predator.Knobs.Damage, 10f),
            Increment(Predator.Knobs.LungeDistance, 5f),
            Increment(Predator.Knobs.LungeSpeed, 5f),
            Increment(Predator.Knobs.Cooldown, -5f),
            Increment(Predator.Knobs.CritChance, 2f)
        ));

        tree.Add(Node(
            "Marked for Death",
            1,
            Requires("Predator"),
            "Predator deals +100% Damage to targets that were already Prey when the lunge was authored. The first lunge that applies Prey does not receive this bonus.",
            Increment(Predator.Knobs.VsPrey.Damage, 100f)
        ));

        tree.Add(Node(
            "Feeding Frenzy",
            1,
            Requires("Predator"),
            "Prey Kills grant Hunt Streak. Hunt lasts 4 seconds and refreshes on each Prey Kill. Each stack grants +25% Predator Damage and +25% Lunge Speed, up to 5 stacks.",
            Increment(Predator.Knobs.HuntMaxStacks, 5f),
            Increment(Predator.Knobs.PerHuntStreakStack.Damage, 25f),
            Increment(Predator.Knobs.PerHuntStreakStack.LungeSpeed, 25f)
        ));

        tree.Add(Node(
            "Relentless Pursuit",
            1,
            Requires("Predator"),
            "A confirmed damaging Predator lunge against a target that was already Prey resets Predator's cooldown. The lunge that first applies Prey does not trigger this.",
            Enable(Predator.Flags.ResetCooldownOnPreyDamage)
        ));

        tree.Validate();
        return tree;
    }
}
