/// <summary>
/// Predator specialization surface.
///
/// Predator is intentionally mechanics-free for now. The tree resolves into
/// these knobs, but no Harmony/runtime behavior consumes them yet. This keeps
/// Predator compiling cleanly while its gameplay is redesigned.
///
/// The small runtime methods at the bottom are compatibility hooks retained for
/// existing LeviathanController call sites. They intentionally perform no
/// Predator behavior.
/// </summary>
public static class LeviathanPredatorRuntime
{
    public static class Knobs
    {
        // Additive percentage change to Predator's eventual damage baseline.
        public static readonly LeviathanSpecializationKnob Damage =
            LeviathanSpecializationKnob.Percent(
                "predator.damage",
                "Damage"
            );

        // Additive percentage change to the eventual Predator lunge distance.
        public static readonly LeviathanSpecializationKnob LungeDistance =
            LeviathanSpecializationKnob.Percent(
                "predator.lunge_distance",
                "Lunge Distance"
            );

        // Additive percentage change to the eventual Predator lunge duration.
        public static readonly LeviathanSpecializationKnob LungeDuration =
            LeviathanSpecializationKnob.Percent(
                "predator.lunge_duration",
                "Lunge Duration"
            );

        // Additive percentage change to Predator's eventual cooldown baseline.
        public static readonly LeviathanSpecializationKnob Cooldown =
            LeviathanSpecializationKnob.Percent(
                "predator.cooldown",
                "Cooldown"
            );

        // Additive percentage points of critical-hit chance.
        public static readonly LeviathanSpecializationKnob CritChance =
            LeviathanSpecializationKnob.PercentagePoints(
                "predator.crit_chance",
                "Critical Chance"
            );
    }

    // -------------------------------------------------------------------------
    // Temporary controller compatibility surface
    // -------------------------------------------------------------------------
    //
    // Predator currently has no runtime mechanics. These methods remain only so
    // existing LeviathanController lifecycle/update code does not need to know
    // that Predator is temporarily inert.

    public static void Cancel()
    {
    }

    public static void CancelForPlayer(StarVortex.GameShip player)
    {
    }

    public static void FixedTick()
    {
    }

    public static bool IsPredatorLunging(StarVortex.GameShip player)
    {
        return false;
    }
}
