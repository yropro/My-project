using StarVortex;

/// <summary>
/// Orrery-facing adapter over the shared combat provenance/history/state core.
///
/// The shared implementation is historically named CoreCombat. Orrery
/// owns these numeric reservations here so the standalone class does not spread
/// legacy infrastructure names throughout its mechanics. When the shared core
/// is renamed later, Orrery consumers only need this boundary migrated.
/// </summary>
public static class OrreryCombat
{
    public const byte SkillId = CoreCombat.SkillIds.Orrery;
    public const byte SatelliteContributorKindId =
        (byte)CoreCombat.ContributorKind.Satellite;

    public static class EffectIds
    {
        public const byte MagmaCannon = 1;
        public const byte TeslaCoil = 2;
        public const byte CryoGun = 3;
        public const byte Shatterbolt = 4;
        public const byte ShatterboltFrostBurst = 5;
        public const byte CastInvoked = 128;
    }

    public static readonly CoreCombat.SemanticKey MagmaCannon =
        CoreCombat.SemanticKey.Create(SkillId, EffectIds.MagmaCannon);
    public static readonly CoreCombat.SemanticKey TeslaCoil =
        CoreCombat.SemanticKey.Create(SkillId, EffectIds.TeslaCoil);
    public static readonly CoreCombat.SemanticKey CryoGun =
        CoreCombat.SemanticKey.Create(SkillId, EffectIds.CryoGun);
    public static readonly CoreCombat.SemanticKey Shatterbolt =
        CoreCombat.SemanticKey.Create(SkillId, EffectIds.Shatterbolt);
    public static readonly CoreCombat.SemanticKey ShatterboltFrostBurst =
        CoreCombat.SemanticKey.Create(SkillId, EffectIds.ShatterboltFrostBurst);
    public static readonly CoreCombat.SemanticKey CastInvoked =
        CoreCombat.SemanticKey.Create(SkillId, EffectIds.CastInvoked);

    public static CoreCombat.SemanticKey Spell(byte effectId)
    {
        return effectId == 0
            ? default(CoreCombat.SemanticKey)
            : CoreCombat.SemanticKey.Create(SkillId, effectId);
    }

    public static CoreCombat.ContributorKey Satellite(byte satelliteId)
    {
        if (satelliteId == 0)
            return default(CoreCombat.ContributorKey);

        return CoreCombat.ContributorKey.Create(
            CoreCombat.ContributorKind.Satellite,
            satelliteId);
    }

    public static void ResetOwner(GameShip owner)
    {
        if (owner != null)
            CoreCombat.ResetOwnerSkillRuntime(owner, SkillId);
    }
}
