using StarVortex;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Resolves the first equipped damage source for each Orrery element.
///
/// A matching elemental weapon is an optional focus, not a cast requirement.
/// When one exists, its real equipped slot/stat level are returned so spell
/// adapters can inherit it. When none exists, Orrery still returns a valid
/// unfocused casting context using a safe borrowed native slot and the player's
/// current level as the reference-power level.
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

        // Positive = focused item stat level. Negative = unfocused player level.
        // OrrerySpellPower owns interpretation of the sign so spell runtimes do
        // not need separate focused/unfocused damage formulas.
        public int EffectiveItemLevel;
        public bool HasFocus;

        public bool IsValid
        {
            get
            {
                return Owner != null && SlotIndex >= 0 &&
                    EffectiveItemLevel != 0 && Element != OrreryElement.None;
            }
        }
    }

    private static readonly Dictionary<Type, FieldInfo> damageTypeFields =
        new Dictionary<Type, FieldInfo>();
    private static readonly HashSet<Type> noDamageTypeField =
        new HashSet<Type>();

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
            focus.HasFocus = true;

            Debug.Log("[Orrery] " + element + " focus resolved from slot " +
                i + " (" + source.GetType().Name + "), stat level " +
                effectiveLevel + ".");
            return true;
        }

        int fallbackSlot;
        if (!TryGetFallbackSlot(owner, out fallbackSlot))
            return false;

        Pilot pilot = GameShip.GetPlayerSourcePilot(owner);
        if (pilot == null)
            pilot = owner.pilot;
        int playerLevel = pilot == null ? 1 : Mathf.Max(1, pilot.GetLevel(0));

        focus.Owner = owner;
        focus.Element = element;
        focus.DamageType = desiredDamageType;
        focus.Source = null;
        focus.SlotIndex = fallbackSlot;
        focus.EffectiveItemLevel = -playerLevel;
        focus.HasFocus = false;
        return true;
    }

    public static float GetReferenceDps(
        Focus focus,
        OrrerySpellPower.ReferenceMode mode = OrrerySpellPower.ReferenceMode.Mean)
    {
        return focus.IsValid
            ? OrrerySpellPower.GetReferenceDps(focus.EffectiveItemLevel, mode)
            : 0f;
    }

    public static bool IsMatchingFocus(
        Activatable source,
        OrreryElement element)
    {
        if (source == null || element == OrreryElement.None)
            return false;

        Damageable.DamageType expected;
        Damageable.DamageType actual;
        return TryGetDamageType(element, out expected) &&
            TryGetDamageType(source, out actual) &&
            actual == expected;
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

    /// <summary>
    /// All damaging Activatable families in Star Vortex expose a native
    /// damageType field, but they do not share a public damage-weapon interface.
    /// Resolve that field generically rather than maintaining a brittle list of
    /// Launcher/Beam/Torch/etc subclasses. Reflection is cached per runtime type
    /// and this path only runs while resolving a cast focus, not per damage tick.
    /// </summary>
    public static bool TryGetDamageType(
        Activatable source,
        out Damageable.DamageType damageType)
    {
        damageType = Damageable.DamageType.Kinetic;
        if (source == null)
            return false;

        Type sourceType = source.GetType();
        FieldInfo field;
        if (!damageTypeFields.TryGetValue(sourceType, out field))
        {
            if (noDamageTypeField.Contains(sourceType))
                return false;

            Type current = sourceType;
            while (current != null && field == null)
            {
                field = current.GetField(
                    "damageType",
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);
                current = current.BaseType;
            }

            if (field == null || field.FieldType != typeof(Damageable.DamageType))
            {
                noDamageTypeField.Add(sourceType);
                return false;
            }

            damageTypeFields[sourceType] = field;
        }

        object value = field.GetValue(source);
        if (!(value is Damageable.DamageType))
            return false;

        damageType = (Damageable.DamageType)value;
        return true;
    }

    private static bool TryGetFallbackSlot(GameShip owner, out int slotIndex)
    {
        slotIndex = -1;
        if (owner == null || owner.slots == null)
            return false;

        // Prefer a real primary slot so native projectile/beam source-slot
        // bookkeeping remains weapon-shaped even when Orrery has no focus.
        for (int i = 0; i < owner.slots.Length; i++)
        {
            Slot slot = owner.slots[i];
            if (slot != null && slot.enabled && slot.type == Item.Type.PrimaryWeapon)
            {
                slotIndex = i;
                return true;
            }
        }

        // Ships without a primary mount can still cast. Activatable.CanActivate
        // only requires a valid enabled native slot index for the hidden adapter.
        for (int i = 0; i < owner.slots.Length; i++)
        {
            Slot slot = owner.slots[i];
            if (slot != null && slot.enabled)
            {
                slotIndex = i;
                return true;
            }
        }

        return false;
    }
}
