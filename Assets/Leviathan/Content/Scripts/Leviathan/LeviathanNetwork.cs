using StarVortex;

/// <summary>Leviathan's presentation wire contracts. Abilities supply semantic
/// state here; byte order, validation and transport identity stay in this file.
/// Native replication already carries movement. These methods author no combat.</summary>
public static class LeviathanNetwork
{
    public struct ConverterState
    {
        public byte Phase;
        public float PhaseProgress;
        public int Sequence;
        public bool ProjectileActive;
        public bool Detonating;
        public float ProjectileProgress;
        public float DetonationScale;
        public float ExplosionProgress;
    }

    public static void PublishPredator(bool lunging)
    {
        CoreNetwork.SlotWriter writer = CoreNetwork.BeginSlot(CoreNetwork.SlotPredator);
        writer.Bool(lunging);
        CoreNetwork.EndSlot(writer);
    }

    public static bool IsPredatorLunging(GameShip owner)
    {
        CoreNetwork.SlotReader reader;
        return CoreNetwork.HasSynchronizedSpecialization(owner, CoreClassId.Leviathan) &&
            CoreNetwork.TryReadSlot(owner, CoreNetwork.SlotPredator, out reader) &&
            reader.Length == 1 && reader.Byte() == 1;
    }

    public static void PublishConverter(ConverterState state)
    {
        CoreNetwork.SlotWriter writer = CoreNetwork.BeginSlot(CoreNetwork.SlotStellarConverter);
        writer.Byte(state.Phase);
        writer.Percent(state.PhaseProgress);
        writer.Sequence(state.Sequence);
        writer.Flags(state.ProjectileActive, state.Detonating);
        writer.Percent(state.ProjectileProgress);
        writer.Percent(state.DetonationScale);
        writer.Percent(state.ExplosionProgress);
        CoreNetwork.EndSlot(writer);
    }

    public static bool TryReadConverter(GameShip owner, out ConverterState state)
    {
        state = default(ConverterState);
        CoreNetwork.SlotReader reader;
        if (!CoreNetwork.TryReadSlot(owner, CoreNetwork.SlotStellarConverter, out reader) ||
            reader.Length != 7) return false;
        ConverterState decoded = default(ConverterState);
        decoded.Phase = reader.Byte();
        decoded.PhaseProgress = reader.Percent();
        decoded.Sequence = reader.Sequence();
        byte flags = reader.FlagsByte();
        if ((flags & ~3) != 0) return false;
        decoded.ProjectileActive = (flags & 1) != 0;
        decoded.Detonating = (flags & 2) != 0;
        decoded.ProjectileProgress = reader.Percent();
        decoded.DetonationScale = reader.Percent();
        decoded.ExplosionProgress = reader.Percent();
        state = decoded;
        return true;
    }
}
