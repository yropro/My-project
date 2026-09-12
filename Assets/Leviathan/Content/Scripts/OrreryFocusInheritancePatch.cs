using HarmonyLib;
using StarVortex;

/// <summary>
/// Applies Orrery donor inheritance at the one stable seam shared by all hidden
/// native spell adapters: their virtual equip against the real donor's slot.
/// </summary>
[HarmonyPatch(typeof(Equippable), "Equip")]
public static class OrreryFocusInheritanceEquipPatch
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

        OrreryFocusInheritance.Apply(donor, adapter);
    }
}
