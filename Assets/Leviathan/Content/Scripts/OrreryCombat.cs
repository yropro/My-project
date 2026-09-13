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
        public const byte PlasmaBolt = 6;
        public const byte PlasmaBurnTick = 7;
        public const byte PlasmaBurnState = 64;
        public const byte PlasmaBurnRecent = 65;
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

    public static readonly CoreCombat.SemanticKey PlasmaBolt =
        CoreCombat.SemanticKey.Create(SkillId, EffectIds.PlasmaBolt);
    public static readonly CoreCombat.SemanticKey PlasmaBurnTick =
        CoreCombat.SemanticKey.Create(SkillId, EffectIds.PlasmaBurnTick);
    public static readonly CoreCombat.SemanticKey PlasmaBurn =
        CoreCombat.SemanticKey.Create(SkillId, EffectIds.PlasmaBurnState);
    public static readonly CoreCombat.SemanticKey PlasmaBurnRecent =
        CoreCombat.SemanticKey.Create(SkillId, EffectIds.PlasmaBurnRecent);

    public static CoreCombat.ContributorKey SatelliteFor(
        OrreryCastInvocation invocation, OrreryElement element)
    {
        for (byte id = 1; id <= OrreryCasting.MaxFormulaSatellites; id++)
        {
            if ((invocation.LockedMask & (1 << (id - 1))) != 0 &&
                ((invocation.PackedElementsBySatellite >> ((id - 1) * 3)) & 7u) == (uint)element)
                return Satellite(id);
        }
        return default(CoreCombat.ContributorKey);
    }

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
