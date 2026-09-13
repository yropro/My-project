/// <summary>
/// Human-facing Orrery spell identity and tuning sheet.
///
/// Spell behavior belongs in each spell's logic file. This compendium deliberately
/// keeps only stable identity, recipe and exposed balancing/presentation knobs so
/// the spellbook can be tuned without hunting through runtime mechanics.
/// </summary>
public static class OrrerySpellCompendium
{
    public static class PlasmaBolt
    {
        public const ushort Id = 5;
        public const string Name = "Plasma Bolt";
        public static readonly OrreryRecipeKey Recipe = default(OrreryRecipeKey)
            .Add(OrreryElement.Fire).Add(OrreryElement.Lightning);
        public const float IntegratedReferenceSeconds = 1f;
        public const float LightningDamageMultiplier = 2f;
        public const float BoltLengthMeters = 120f;
        public const float BoltWidthMeters = 25f;
        public const float SpreadRadiusMeters = 50f;
        public const float BurnDurationSeconds = 5f;
        public const float ReinfectionLockoutSeconds = 10f;
        public const float BurnTickIntervalSeconds = 0.5f;
        public const int BurnTickCount = 10;
        public const float SpreadScanIntervalSeconds = 0.25f;
        // Concurrent storage bounds only: no generation or total-spread limit.
        public const int MaxActiveInfections = 64;
        public const int MaxPendingImpacts = 16;
        public const float PendingOutcomeTimeoutSeconds = 5f;
        public const int BoltVisualPointCount = 12;
        public const float BoltVisualJitterMeters = 3.5f;
        public const float BoltCoreWidthFraction = 0.22f;
        public const float BoltVisualLifetimeSeconds = 0.12f;
    }

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

        // Native Extra Shot / Extra Chain rolls add chain attempts 1:1. This is
        // only the bounded storage/transport ceiling; actual casts still stop as
        // soon as their resolved chain allowance is spent or no target exists.
        public const int MaxInheritedAdditionalChains = 6;
        public const int MaximumImpacts =
            BaseMaximumImpacts + MaxInheritedAdditionalChains;
        public const int MaximumInheritedImpacts = MaximumImpacts;

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
