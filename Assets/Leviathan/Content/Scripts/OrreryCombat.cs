using StarVortex;

/// <summary>
/// Orrery-facing adapter over the shared combat provenance/history/state core.
///
/// The shared implementation is historically named LeviathanCombat. Orrery
/// owns these numeric reservations here so the standalone class does not spread
/// legacy infrastructure names throughout its mechanics. When the shared core
/// is renamed later, Orrery consumers only need this boundary migrated.
/// </summary>
public static class OrreryCombat
{
    public const byte SkillId = 8;
    public const byte SatelliteContributorKindId = 9;

    public static class EffectIds
    {
        public const byte MagmaCannon = 1;
        public const byte TeslaCoil = 2;
        public const byte CryoGun = 3;
        public const byte CastInvoked = 128;
    }

    public static readonly LeviathanCombat.SemanticKey MagmaCannon =
        LeviathanCombat.SemanticKey.Create(SkillId, EffectIds.MagmaCannon);
    public static readonly LeviathanCombat.SemanticKey TeslaCoil =
        LeviathanCombat.SemanticKey.Create(SkillId, EffectIds.TeslaCoil);
    public static readonly LeviathanCombat.SemanticKey CryoGun =
        LeviathanCombat.SemanticKey.Create(SkillId, EffectIds.CryoGun);
    public static readonly LeviathanCombat.SemanticKey CastInvoked =
        LeviathanCombat.SemanticKey.Create(SkillId, EffectIds.CastInvoked);

    public static LeviathanCombat.SemanticKey Spell(byte effectId)
    {
        return effectId == 0
            ? default(LeviathanCombat.SemanticKey)
            : LeviathanCombat.SemanticKey.Create(SkillId, effectId);
    }

    public static LeviathanCombat.ContributorKey Satellite(byte satelliteId)
    {
        if (satelliteId == 0)
            return default(LeviathanCombat.ContributorKey);

        return LeviathanCombat.ContributorKey.Create(
            (LeviathanCombat.ContributorKind)SatelliteContributorKindId,
            satelliteId);
    }

    public static void ResetOwner(GameShip owner)
    {
        if (owner != null)
            LeviathanCombat.ResetOwnerSkillRuntime(owner, SkillId);
    }
}
