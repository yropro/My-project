using HarmonyLib;
using StarVortex;
using System;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Copies shared writable Base* combat/stat properties from the real elemental
/// donor onto Orrery's hidden native spell adapter. This carries the donor's
/// intrinsic item-level/customizer-adjusted baseline (crit, status, range, etc.)
/// without copying raw base damage, which remains owned by OrrerySpellPower.
///
/// Reflection runs only when a hidden adapter is constructed/reconstructed, never
/// per frame or per hit.
/// </summary>
public static class OrreryFocusBaselineInheritance
{
    public static void Apply(Activatable donor, Activatable adapter)
    {
        if (donor == null || adapter == null)
            return;

        PropertyInfo[] donorProperties = donor.GetType().GetProperties(
            BindingFlags.Instance | BindingFlags.Public);
        Type adapterType = adapter.GetType();

        for (int i = 0; i < donorProperties.Length; i++)
        {
            PropertyInfo donorProperty = donorProperties[i];
            if (!ShouldCopy(donorProperty))
                continue;

            PropertyInfo adapterProperty = adapterType.GetProperty(
                donorProperty.Name,
                BindingFlags.Instance | BindingFlags.Public);
            if (adapterProperty == null || !adapterProperty.CanWrite ||
                adapterProperty.PropertyType != donorProperty.PropertyType ||
                adapterProperty.GetIndexParameters().Length != 0)
            {
                continue;
            }

            try
            {
                adapterProperty.SetValue(
                    adapter,
                    donorProperty.GetValue(donor, null),
                    null);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[Orrery] Could not inherit focus baseline property " +
                    donorProperty.Name + ": " + ex.Message);
            }
        }
    }

    private static bool ShouldCopy(PropertyInfo property)
    {
        if (property == null || !property.CanRead ||
            property.GetIndexParameters().Length != 0 ||
            !property.Name.StartsWith("Base", StringComparison.Ordinal))
        {
            return false;
        }

        // Raw/base damage is the one intentionally replaced value family.
        if (property.Name == "BaseDamage")
            return false;

        Type type = property.PropertyType;
        return type.IsValueType || type == typeof(string);
    }
}

/// <summary>
/// Runs after the modifier/customizer inheritance postfix on Equippable.Equip so
/// the donor's realized Base* values are the final inherited baseline. Orrery's
/// spell-specific ConfigureOutput/normalization still runs after Equip returns.
/// </summary>
[HarmonyPatch(typeof(Equippable), "Equip")]
[HarmonyPriority(Priority.Last)]
public static class OrreryFocusBaselineInheritanceEquipPatch
{
    public static void Postfix(Equippable __instance)
    {
        Activatable adapter = __instance as Activatable;
        if (adapter == null || !OrreryWeaponSuppression.IsRuntimeAdapter(adapter))
            return;

        GameShip owner = adapter.parentShip;
        int? slotIndex = adapter.equippedSlot;
        if (owner == null || owner.slots == null || slotIndex == null)
            return;

        int index = slotIndex.Value;
        if (index < 0 || index >= owner.slots.Length)
            return;

        Slot slot = owner.slots[index];
        Activatable donor = slot == null ? null : slot.equippable as Activatable;
        if (donor == null || object.ReferenceEquals(donor, adapter))
            return;

        OrreryFocusBaselineInheritance.Apply(donor, adapter);
        adapter.ModifiersChanged();
    }
}
