// Named Starfire knobs exposed to specialization tree definitions.
// The tree never needs to know how Starfire implements these values internally.
public static class LeviathanStarfireKnobs
{
    public static readonly LeviathanSpecializationKnob Width =
        LeviathanSpecializationKnob.Percent("starfire.width", "Width");

    public static readonly LeviathanSpecializationKnob Length =
        LeviathanSpecializationKnob.Percent("starfire.length", "Length");

    public static readonly LeviathanSpecializationKnob Duration =
        LeviathanSpecializationKnob.Percent("starfire.duration", "Duration");

    public static readonly LeviathanSpecializationKnob RechargeTime =
        LeviathanSpecializationKnob.Percent("starfire.recharge_time", "Recharge Time");

    public static readonly LeviathanSpecializationKnob Damage =
        LeviathanSpecializationKnob.Percent("starfire.damage", "Damage");

    public static readonly LeviathanSpecializationKnob DebuffChance =
        LeviathanSpecializationKnob.Percent("starfire.debuff_chance", "Debuff Chance");
}
