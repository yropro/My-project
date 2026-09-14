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
        // Channel envelope / donor normalization.
        public const float InitialDpsMultiplier = 3f;
        public const float MinimumDpsMultiplier = 1f;
        public const float FadeSeconds = 1f;
        public const float RangeMultiplier = 1f;
        public const float BeamWidthMultiplier = 1f;
        public const float ChainRangeMultiplier = 1f;
        public const int ChainCountAdjustment = 0;

        // Authoritative Orrery geometry and native chain behavior.
        public const float RangeMeters = 195f;
        public const int ChainCount = 2;
        public const float ChainRangeMeters = 97.5f;
        public const float ChainDamageMultiplier = 0.50f;
    }

    public static class ConeOfCold
    {
        // Mechanical cone and native presentation volley.
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

        // Presentation-only travelling cold-front wave.
        public const int VisualWaveCount = 5;
        public const float VisualWaveIntervalSeconds = 0.10f;
        public const float VisualRangeMeters = 180f;
        public const float WaveProjectileScaleMultiplier = 1.50f;
        public const float OuterAimOffsetDegrees = 5f;
        public const float InnerAimOffsetDegrees = 2.5f;
    }

    public static class ColdFusion
    {
        // Identity
        public const ushort Id = 4;
        public const string Name = "Cold Fusion";
        public static readonly OrreryRecipeKey Recipe =
            default(OrreryRecipeKey)
                .Add(OrreryElement.Fire)
                .Add(OrreryElement.Ice);

        // Buff tuning. Duration bonuses extend the full effect while shield
        // grant/sec remains based on BaseDurationSeconds, so duration is always
        // beneficial rather than stretching the same shield budget thinner.
        public const float BaseDurationSeconds = 8f;
        public const float MovementBonusFraction = 0.20f;
        public const float HeatGenerationReductionFraction = 0.30f;
        public const float WeaponCadenceBonusFraction = 0.20f;
        public const float ShieldBatteryMultiplier = 1.10f;

        // Mixed Fire/Ice focus aggregation. 0 = Fire only, 1 = Ice only, 0.5 =
        // equal average. This weight is used for both effective item level and
        // compatible Effect Duration bonuses.
        public const float MixedFocusIceWeight = 0.50f;
        public const float DurationModifierScale = 1f;

        // Ally nearest the cursor inside this local cursor search wins. A target
        // must also be within MaximumTargetRangeMeters of the caster. If none is
        // found, the spell buffs the caster.
        public const float CursorTargetRadiusMeters = 120f;
        public const float MaximumTargetRangeMeters = 300f;
        public const int MaxCandidateShips = 16;

        // Shield Battery-equivalent level curve. These are exposed separately so
        // the support curve can be retuned without changing spell mechanics.
        public const float ShieldCurveLevel1Amount = 1700f;
        public const float ShieldCurveBreakpointLevel = 15f;
        public const float ShieldCurveBreakpointAmount = 3400f;
        public const float ShieldCurveSecondReferenceLevel = 30f;
        public const float ShieldCurveSecondReferenceAmount = 6800f;

        // Presentation-only layered Frost Nova halos. Radius starts from the
        // target ship's native shield/ship radius, so the effect hugs the target
        // instead of using one fixed world size.
        public const int HaloCount = 3;
        public const float HaloRadiusMultiplier = 1.08f;
        public const float MinimumHaloRadiusMeters = 12f;
        public const float HaloLayerSpacingFraction = 0.10f;
        public const float HaloOpacity = 0.42f;
        public const float HaloRotationDegreesPerSecond = 22f;
        public const float HaloOuterRotationMultiplier = 0.35f;
        public const float HaloRadiusFollowSpeed = 8f;
    }

    public static class PlasmaBolt
    {
        public const ushort Id = 5;
        public const string Name = "Plasma Bolt";
        public static readonly OrreryRecipeKey Recipe = default(OrreryRecipeKey)
            .Add(OrreryElement.Fire).Add(OrreryElement.Lightning);
        public const float IntegratedReferenceSeconds = 1f;

        // ---- Strike -------------------------------------------------------
        // LightningDamageMultiplier is the spell's reference power and is left
        // at its original value so the baseline stays readable. StrikeDamage-
        // Multiplier is the knob that actually tunes the direct hit; it is
        // folded in before the focus bonus, so it behaves exactly as if the
        // reference multiplier had been lowered.
        public const float LightningDamageMultiplier = 4f;
        public const float StrikeDamageMultiplier = 0.6f;
        public const float BoltLengthMeters = 220f;
        public const float BoltWidthMeters = 25f;
        public const float SpreadRadiusMeters = 42f;
        // ---- Plasma Burn: spreading damage-over-time -----------------------
        // Budget is a multiple of the confirmed post-mitigation hit, so a
        // weaker strike spreads a proportionally weaker burn. That is the
        // intended consequence of the strike nerf: contagion damage scales
        // with the hit that seeded it.
        public const float BurnBudgetMultiplier = 1f;
        public const float BurnDurationSeconds = 5f;
        public const float ReinfectionLockoutSeconds = 10f;
        public const float BurnTickIntervalSeconds = 0.5f;
        public const int BurnTickCount = 10;
        public const float SpreadScanIntervalSeconds = 0.25f;

        // ---- Immolation: single-target damage-over-time --------------------
        // Never spreads. The budget restores the pre-nerf strike value: the
        // confirmed hit is StrikeDamageMultiplier of the original, so dividing
        // by it returns the original. Replace with a plain number to decouple
        // Immolation from the strike knob.
        public const float ImmolationBudgetMultiplier = 1f / StrikeDamageMultiplier;
        public const float ImmolationDurationSeconds = 8f;
        public const float ImmolationReapplyLockoutSeconds = 8f;
        public const float ImmolationTickIntervalSeconds = 1f;
        public const int ImmolationTickCount = 8;
        // Concurrent storage bounds only: no generation or total-spread limit.
        // Plasma Burn and Immolation share this pool. Immolation adds at most
        // one slot per directly struck target and never spreads, so the headroom
        // increase is small relative to contagion.
        public const int MaxActiveInfections = 96;
        public const int MaxPendingImpacts = 16;
        public const float PendingOutcomeTimeoutSeconds = 5f;
        public const int BoltVisualPointCount = 12;
        public const float BoltVisualJitterMeters = 3.5f;
        public const float BoltCoreWidthFraction = 0.22f;
        public const float BoltVisualLifetimeSeconds = 0.12f;
        public const string ZapPrefabPath =
            "Assets/Leviathan/Content/Scripts/Orrery/PlasmaBoltVFX/OrreryPlasmaBoltZap.prefab";
        // Compensates for transparent margins around the lightning in its atlas.
        // Visual width tracks the resolved hitbox width, including spell scaling.
        public const float ZapWidthMultiplier = 2f;
        public const int ZapVisualStrikeCount = 3;
        public const float ZapVisualLifetimeSeconds = 0.75f;
        public const float ZapVisualStrikeLifetimeSeconds = 0.2f;
        public const int ZapSortingOrder = 20;

        // Presentation audio. The clip may be absent during development; Core
        // fails closed with one warning and requires no dummy AudioClip. Keep the
        // fade start as a normal tuning knob; the clip's actual length determines
        // the remaining fade duration automatically.
        public const float ThunderFadeOutStartSeconds = 2.235f;
        public static readonly string ThunderClipName = "thunder";
        public const float ThunderVolume = 1.00f;

        static PlasmaBolt()
        {
            // Register lazily on first Plasma Bolt use so local and remote
            // presentation both configure the same clip without a separate
            // bootstrap, Harmony patch, or network event.
            CoreAudioRuntime.SetFadeOutStartSeconds(
                ThunderClipName,
                ThunderFadeOutStartSeconds);
        }
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
        public const float IceExplosionDamageMultiplier = 1.50f;

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
        public const float ProjectileSpeedMetersPerSecond = 155f;
        public const float MaximumLegSeconds = 3.5f;

        // Explosion
        public const float ExplosionRadiusMeters = 50f;
        public const float ExplosionExpansionMetersPerSecond = 50f;

        // Presentation
        public const float ProjectileVisualScale = 1.5f;
        public const float ProjectileSpinDegreesPerSecond = 360f;
        public const float ExplosionVisualScale = 1f;
        public const float PresentationTailSeconds = 1f;
    }
}
