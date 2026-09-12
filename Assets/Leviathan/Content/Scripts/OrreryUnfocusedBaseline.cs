using HarmonyLib;
using StarVortex;

/// <summary>
/// Applies the intrinsic no-focus combat baseline to Orrery's hidden native spell
/// adapters. This runs once when the adapter is virtually equipped, not in combat
/// hot paths.
///
/// A borrowed native slot is required because Activatable.CanActivate indexes the
/// parent ship's slot table. The borrowed slot is context only: if its equipped
/// Activatable does not share the spell adapter's native damage type, it is not an
/// elemental focus and none of its item stats are treated as donor state.
/// </summary>
[HarmonyPatch(typeof(Equippable), "Equip")]
public static class OrreryUnfocusedBaselinePatch
{
    public static void Postfix(Equippable __instance)
    {
        Activatable adapter = __instance as Activatable;
        if (adapter == null || !OrreryWeaponSuppression.IsRuntimeAdapter(adapter) ||
            HasMatchingElementalDonor(adapter))
        {
            return;
        }

        Launcher launcher = adapter as Launcher;
        if (launcher != null)
        {
            launcher.BaseCritChance = OrrerySpellPower.UnfocusedCritChance;
            launcher.BaseStatusEffectChance =
                OrrerySpellPower.UnfocusedStatusEffectChance;
            launcher.ModifiersChanged();
            return;
        }

        BeamWeapon beam = adapter as BeamWeapon;
        if (beam != null)
        {
            beam.BaseCritChance = OrrerySpellPower.UnfocusedCritChance;
            beam.BaseStatusEffectChance =
                OrrerySpellPower.UnfocusedStatusEffectChance;
            beam.ModifiersChanged();
        }
    }

    private static bool HasMatchingElementalDonor(Activatable adapter)
    {
        if (adapter == null || adapter.parentShip == null ||
            adapter.equippedSlot == null || adapter.parentShip.slots == null)
        {
            return false;
        }

        int slotIndex = adapter.equippedSlot.Value;
        if (slotIndex < 0 || slotIndex >= adapter.parentShip.slots.Length)
            return false;

        Slot slot = adapter.parentShip.slots[slotIndex];
        Activatable donor = slot == null ? null : slot.equippable as Activatable;
        if (donor == null)
            return false;

        Damageable.DamageType adapterType;
        Damageable.DamageType donorType;
        return OrreryFocusResolver.TryGetDamageType(adapter, out adapterType) &&
            OrreryFocusResolver.TryGetDamageType(donor, out donorType) &&
            adapterType == donorType;
    }
}
