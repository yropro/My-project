using StarVortex;

/// <summary>
/// Resolves the first equipped damage source for each Orrery element.
///
/// This is deliberately a lookup layer, not a stat-copy layer. Spell adapters can
/// consume the returned native Activatable later to inherit whitelisted non-damage
/// behavior without teaching spell identity about inventory layout.
/// </summary>
public static class OrreryFocusResolver
{
    public struct Focus
    {
        public GameShip Owner;
        public OrreryElement Element;
        public Damageable.DamageType DamageType;
        public Activatable Source;
        public int SlotIndex;
        public int EffectiveItemLevel;

        public bool IsValid
        {
            get
            {
                return Owner != null && Source != null && SlotIndex >= 0 &&
                    EffectiveItemLevel >= 1 && Element != OrreryElement.None;
            }
        }
    }

    public static bool TryResolve(
        GameShip owner,
        OrreryElement element,
        out Focus focus)
    {
        focus = default(Focus);
        if (owner == null || owner.slots == null || element == OrreryElement.None)
            return false;

        Damageable.DamageType desiredDamageType;
        if (!TryGetDamageType(element, out desiredDamageType))
            return false;

        for (int i = 0; i < owner.slots.Length; i++)
        {
            Slot slot = owner.slots[i];
            if (slot == null || slot.equippable == null)
                continue;

            Activatable source = slot.equippable as Activatable;
            if (source == null)
                continue;

            Damageable.DamageType sourceDamageType;
            if (!TryGetDamageType(source, out sourceDamageType) ||
                sourceDamageType != desiredDamageType)
            {
                continue;
            }

            // Star Vortex stores the item's stat level in BaseRequiredLevel
            // (Equippable.level). A -N Level Requirement modifier lowers only the
            // RequiredLevel property, so the stat/effective item level is already
            // the higher value we want here.
            int effectiveLevel = source.BaseRequiredLevel;
            if (effectiveLevel < 1)
                effectiveLevel = 1;

            focus.Owner = owner;
            focus.Element = element;
            focus.DamageType = sourceDamageType;
            focus.Source = source;
            focus.SlotIndex = i;
            focus.EffectiveItemLevel = effectiveLevel;
            return true;
        }

        return false;
    }

    public static float GetReferenceDps(
        Focus focus,
        OrrerySpellPower.ReferenceMode mode = OrrerySpellPower.ReferenceMode.Mean)
    {
        return focus.IsValid
            ? OrrerySpellPower.GetReferenceDps(focus.EffectiveItemLevel, mode)
            : 0f;
    }

    public static bool TryGetDamageType(
        OrreryElement element,
        out Damageable.DamageType damageType)
    {
        switch (element)
        {
            case OrreryElement.Fire:
                damageType = Damageable.DamageType.Thermal;
                return true;
            case OrreryElement.Ice:
                damageType = Damageable.DamageType.Cold;
                return true;
            case OrreryElement.Lightning:
                damageType = Damageable.DamageType.Electric;
                return true;
            default:
                damageType = Damageable.DamageType.Kinetic;
                return false;
        }
    }

    private static bool TryGetDamageType(
        Activatable source,
        out Damageable.DamageType damageType)
    {
        Launcher launcher = source as Launcher;
        if (launcher != null)
        {
            damageType = launcher.damageType;
            return true;
        }

        BeamWeapon beam = source as BeamWeapon;
        if (beam != null)
        {
            damageType = beam.damageType;
            return true;
        }

        Torch torch = source as Torch;
        if (torch != null)
        {
            damageType = torch.damageType;
            return true;
        }

        Pulse pulse = source as Pulse;
        if (pulse != null)
        {
            damageType = pulse.damageType;
            return true;
        }

        Assault assault = source as Assault;
        if (assault != null)
        {
            damageType = assault.damageType;
            return true;
        }

        Conduit conduit = source as Conduit;
        if (conduit != null)
        {
            damageType = conduit.damageType;
            return true;
        }

        LaserSpinner spinner = source as LaserSpinner;
        if (spinner != null)
        {
            damageType = spinner.damageType;
            return true;
        }

        Halo halo = source as Halo;
        if (halo != null)
        {
            damageType = halo.damageType;
            return true;
        }

        DroneDropper dropper = source as DroneDropper;
        if (dropper != null)
        {
            damageType = dropper.damageType;
            return true;
        }

        damageType = Damageable.DamageType.Kinetic;
        return false;
    }
}
