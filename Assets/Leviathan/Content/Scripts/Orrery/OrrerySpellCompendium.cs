/// <summary>
/// Human-facing Orrery spell identity and tuning sheet.
///
/// Spell behavior belongs in each spell's logic file. This compendium deliberately
/// keeps only stable identity, recipe and exposed balancing/presentation knobs so
/// the spellbook can be tuned without hunting through runtime mechanics.
/// </summary>
public static class OrrerySpellCompendium
{
    public static class MagmaCannon
    {
        public const float IntegratedReferenceSeconds = 1f;
        public const float DamageMultiplier = 3f;
        public const float VelocityMultiplier = 0.65f;
        public const float TurnDegreesPerSecond = 155f;
        public const float ProjectileVisualScale = 4f;
        public const float ExplosionRadiusMeters = 40f;
        public const float LifetimeSeconds = 5f;
        public const float SpawnGraceSeconds = 0.15f;
        public const float ContactRearmSeconds = 0.10f;
        public const int MaxTrackedContactTargets = 64;
    }

    public static class TeslaCoil
    {
        public const float InitialDpsMultiplier = 3f;
        public const float MinimumDpsMultiplier = 1f;
        public const float FadeSeconds = 1f;
        public const float RangeMultiplier = 1f;
        public const float BeamWidthMultiplier = 1f;
        public const float ChainRangeMultiplier = 1f;
        public const int ChainCountAdjustment = 0;
        public const float RangeMeters = 195f;
        public const int ChainCount = 2;
        public const float ChainRangeMeters = 97.5f;
        public const float ChainDamageMultiplier = 0.50f;
    }

    public static class ConeOfCold
    {
        public const float IntegratedReferenceSeconds = 1f;
        public const float DamageMultiplier = 3f;
        public const float ConeRangeMeters = 60f;
        public const float ConeAngleDegrees = 30f;
        public const float FreezeChanceAdditive = 0.50f;
        public const int VisualProjectileCount = 9;
        public const int VisualSpreadDegrees = 30;
        public const float VisualVelocityMultiplier = 1.30f;
        public const float VisualProjectileScale = 1f;
        public const float BaseVisualRangeMultiplier = 1f;
        public const float BaseVisualAngleMultiplier = 1f;
        public const int VisualWaveCount = 5;
        public const float VisualWaveIntervalSeconds = 0.10f;
        public const float VisualRangeMeters = 180f;
        public const float WaveProjectileScaleMultiplier = 1.50f;
        public const float OuterAimOffsetDegrees = 5f;
        public const float InnerAimOffsetDegrees = 2.5f;
    }

    public static class ColdFusion
    {
        public const ushort Id = 4;
        public const string Name = "Cold Fusion";
        public static readonly OrreryRecipeKey Recipe =
            default(OrreryRecipeKey)
                .Add(OrreryElement.Fire)
                .Add(OrreryElement.Ice);

        public const float BaseDurationSeconds = 8f;
        public const float MovementBonusFraction = 0.20f;
        public const float HeatGenerationReductionFraction = 0.30f;
        public const float WeaponCadenceBonusFraction = 0.20f;
        public const float ShieldBatteryMultiplier = 1.10f;
        public const float MixedFocusIceWeight = 0.50f;
        public const float DurationModifierScale = 1f;
        public const float CursorTargetRadiusMeters = 120f;
        public const float MaximumTargetRangeMeters = 300f;
        public const int MaxCandidateShips = 16;
        public const float ShieldCurveLevel1Amount = 1700f;
        public const float ShieldCurveBreakpointLevel = 15f;
        public const float ShieldCurveBreakpointAmount = 3400f;
        public const float ShieldCurveSecondReferenceLevel = 30f;
        public const float ShieldCurveSecondReferenceAmount = 6800f;
        public const int HaloCount = 3;
        public const float HaloRadiusMultiplier = 1.08f;
        public const float MinimumHaloRadiusMeters = 12f;
        public const float HaloLayerSpacingFraction = 0.10f;
        public const float HaloOpacity = 0.42f;
        public const float HaloRotationDegreesPerSecond = 22f;
        public const float HaloOuterRotationMultiplier = 0.35f;
        public const float HaloRadiusFollowSpeed = 8f;
    }

    public static class AccretionDisk
    {
        public const ushort Id = 7;
        public const string Name = "Accretion Disk";

        // Placeholder Stellar + Void + Void recipe. Fire/Ice are still the
        // current internal enum names for Stellar/Void. Baseline Orrery currently
        // assembles two-rune formulas, so this definition becomes normally
        // castable when the planned third formula satellite is available.
        public static readonly OrreryRecipeKey Recipe =
            default(OrreryRecipeKey)
                .Add(OrreryElement.Fire)
                .Add(OrreryElement.Ice)
                .Add(OrreryElement.Ice);

        // Core barrier tuning.
        public const float BaseDurationSeconds = 8f;
        public const float BaseRadiusMeters = 80f;
        public const float CapacityReferenceSeconds = 4f;
        public const float CapacityMultiplier = 1f;
        public const float HealFraction = 0.50f;

        // One Stellar + two Void runes: use the same weighting for reference
        // output, compatible duration rolls and compatible range rolls.
        public const float MixedFocusVoidWeight = 2f / 3f;
        public const float DurationModifierScale = 1f;
        public const float RadiusModifierScale = 1f;

        // Ally nearest cursor wins; fallback is self.
        public const float CursorTargetRadiusMeters = 120f;
        public const float MaximumTargetRangeMeters = 300f;
        public const int MaxCandidateShips = 16;

        // Projectile-field tuning. 0 scans every target-authority fixed step.
        public const float ProjectileScanIntervalSeconds = 0f;
        public const int MaxProjectileCandidatesPerScan = 64;
        public const float RemoteValidationRadiusPaddingFraction = 0.20f;

        // Placeholder presentation. Gameplay radius remains authoritative and the
        // disabled Frost Nova visual is scaled to this radius.
        public const float VisualRadiusMultiplier = 1f;
        public const float VisualOpacity = 0.38f;
        public const float MinimumCapacityOpacityFraction = 0.25f;
        public const float VisualRotationDegreesPerSecond = 18f;
    }

    public static class PlasmaBolt
    {
        public const ushort Id = 5;
        public const string Name = "Plasma Bolt";
        public static readonly OrreryRecipeKey Recipe = default(OrreryRecipeKey)
            .Add(OrreryElement.Fire).Add(OrreryElement.Lightning);
        public const float IntegratedReferenceSeconds = 1f;
        public const float LightningDamageMultiplier = 4f;
        public const float StrikeDamageMultiplier = 0.6f;
        public const float BoltLengthMeters = 220f;
        public const float BoltWidthMeters = 25f;
        public const float SpreadRadiusMeters = 42f;
        public const float BurnBudgetMultiplier = 1f;
        public const float BurnDurationSeconds = 5f;
        public const float ReinfectionLockoutSeconds = 10f;
        public const float BurnTickIntervalSeconds = 0.5f;
        public const int BurnTickCount = 10;
        public const float SpreadScanIntervalSeconds = 0.25f;
        public const float ImmolationBudgetMultiplier = 1f / StrikeDamageMultiplier;
        public const float ImmolationDurationSeconds = 8f;
        public const float ImmolationReapplyLockoutSeconds = 8f;
        public const float ImmolationTickIntervalSeconds = 1f;
        public const int ImmolationTickCount = 8;
        public const int MaxActiveInfections = 96;
        public const int MaxPendingImpacts = 16;
        public const float PendingOutcomeTimeoutSeconds = 5f;
        public const int BoltVisualPointCount = 12;
        public const float BoltVisualJitterMeters = 3.5f;
        public const float BoltCoreWidthFraction = 0.22f;
        public const float BoltVisualLifetimeSeconds = 0.12f;
        public const string ZapPrefabPath =
            "Assets/Leviathan/Content/Scripts/Orrery/PlasmaBoltVFX/OrreryPlasmaBoltZap.prefab";
        public const float ZapWidthMultiplier = 2f;
        public const int ZapVisualStrikeCount = 3;
        public const float ZapVisualLifetimeSeconds = 0.75f;
        public const float ZapVisualStrikeLifetimeSeconds = 0.2f;
        public const int ZapSortingOrder = 20;
        public const float ThunderFadeOutStartSeconds = 2.235f;
        public static readonly string ThunderClipName = "thunder";
        public const float ThunderVolume = 1.00f;

        static PlasmaBolt()
        {
            CoreAudioRuntime.SetFadeOutStartSeconds(
                ThunderClipName,
                ThunderFadeOutStartSeconds);
        }
    }

    public static class Shatterbolt
    {
        public const ushort Id = 6;
        public const string Name = "Shatterbolt";
        public static readonly OrreryRecipeKey Recipe =
            default(OrreryRecipeKey)
                .Add(OrreryElement.Ice)
                .Add(OrreryElement.Lightning);
        public const float IntegratedReferenceSeconds = 1f;
        public const float LightningDamageMultiplier = 1.50f;
        public const float IceExplosionDamageMultiplier = 1.50f;
        public const float InitialAcquisitionRangeMeters = 240f;
        public const float ChainRangeMeters = 160f;
        public const int AdditionalChains = 3;
        public const int BaseMaximumImpacts = AdditionalChains + 1;
        public const int MaxInheritedAdditionalChains = 6;
        public const int MaximumImpacts =
            BaseMaximumImpacts + MaxInheritedAdditionalChains;
        public const int MaximumInheritedImpacts = MaximumImpacts;
        public const int MaxCandidateShipsPerQuery = 64;
        public const int MaxTargetsPerExplosion = 64;
        public const float ProjectileSpeedMetersPerSecond = 155f;
        public const float MaximumLegSeconds = 3.5f;
        public const float ExplosionRadiusMeters = 50f;
        public const float ExplosionExpansionMetersPerSecond = 50f;
        public const float ProjectileVisualScale = 1.5f;
        public const float ProjectileSpinDegreesPerSecond = 360f;
        public const float ExplosionVisualScale = 1f;
        public const float PresentationTailSeconds = 1f;
    }
}
