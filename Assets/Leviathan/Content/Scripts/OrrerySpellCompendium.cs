/// <summary>
/// Human-facing Orrery spell identity and tuning sheet.
///
/// Spell behavior belongs in each spell's logic file. This compendium deliberately
/// keeps only stable identity, recipe and exposed balancing/presentation knobs so
/// the spellbook can be tuned without hunting through runtime mechanics.
/// </summary>
public static class OrrerySpellCompendium
{
    public static class Shatterbolt
    {
        // Identity
        public const ushort Id = 6;
        public const string Name = "Shatterbolt";
        public static readonly OrreryRecipeKey Recipe =
            default(OrreryRecipeKey)
                .Add(OrreryElement.Ice)
                .Add(OrreryElement.Lightning);

        // Damage. Percentages are relative to one second of Orrery reference DPS.
        public const float IntegratedReferenceSeconds = 1f;
        public const float LightningDamageMultiplier = 1.50f;
        public const float IceExplosionDamageMultiplier = 2.00f;

        // Targeting / chaining
        public const float InitialAcquisitionRangeMeters = 240f;
        public const float ChainRangeMeters = 160f;
        public const int AdditionalChains = 3;
        public const int BaseMaximumImpacts = AdditionalChains + 1;

        // Keep the current live spell at four impacts until the runtime patch
        // explicitly opts into inherited Extra Shot / Extra Chain count. The
        // larger bound is the preallocated safety ceiling for that migration.
        public const int MaximumImpacts = BaseMaximumImpacts;
        public const int MaxInheritedAdditionalChains = 6;
        public const int MaximumInheritedImpacts =
            BaseMaximumImpacts + MaxInheritedAdditionalChains;

        public const int MaxCandidateShipsPerQuery = 64;
        public const int MaxTargetsPerExplosion = 64;

        // Motion
        public const float ProjectileSpeedMetersPerSecond = 120f;
        public const float MaximumLegSeconds = 3f;

        // Explosion
        public const float ExplosionRadiusMeters = 50f;
        public const float ExplosionExpansionMetersPerSecond = 85f;

        // Presentation
        public const float ProjectileVisualScale = 1f;
        public const float ProjectileSpinDegreesPerSecond = 360f;
        public const float ExplosionVisualScale = 1f;
    }
}
