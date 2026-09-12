using HarmonyLib;
using StarVortex;

/// <summary>
/// Prevents ordinary equipped weapons from firing while Orrery is active without
/// unequipping them or mutating their loot/stat identity.
///
/// The hidden native spell adapters are intentionally exempt: they are equipped
/// virtually against a focus slot but are not the slot's actual Equippable.
/// </summary>
public static class OrreryWeaponSuppression
{
    public static bool IsSuppressedWeaponType(Item.Type type)
    {
        return type == Item.Type.PrimaryWeapon ||
            type == Item.Type.SecondaryWeapon ||
            type == Item.Type.Special ||
            type == Item.Type.AutoSpecial;
    }

    /// <summary>
    /// True only for an Orrery-owned virtual Activatable that borrows a real focus
    /// slot for native owner/modifier context without actually occupying that slot.
    /// This is also the canonical discriminator for presentation/tuning patches.
    /// </summary>
    public static bool IsRuntimeAdapter(Activatable activatable)
    {
        if (activatable == null || activatable.parentShip == null ||
            !OrreryRuntime.IsActive(activatable.parentShip))
        {
            return false;
        }

        int? slotIndex = activatable.equippedSlot;
        if (slotIndex == null || activatable.parentShip.slots == null)
            return false;

        int index = slotIndex.Value;
        if (index < 0 || index >= activatable.parentShip.slots.Length)
            return false;

        Slot slot = activatable.parentShip.slots[index];
        return slot != null && !object.ReferenceEquals(slot.equippable, activatable);
    }

    public static bool ShouldSuppress(Activatable activatable)
    {
        if (activatable == null || activatable.parentShip == null ||
            !OrreryRuntime.IsActive(activatable.parentShip) ||
            !IsSuppressedWeaponType(activatable.type))
        {
            return false;
        }

        int? slotIndex = activatable.equippedSlot;
        if (slotIndex == null || activatable.parentShip.slots == null)
            return false;

        int index = slotIndex.Value;
        if (index < 0 || index >= activatable.parentShip.slots.Length)
            return false;

        Slot slot = activatable.parentShip.slots[index];

        // Real equipped loot occupies the slot and is suppressed. Orrery's hidden
        // native spell adapter points at the focus slot for native modifier/owner
        // context, but the slot still contains the player's focus item instead.
        return slot != null && object.ReferenceEquals(slot.equippable, activatable);
    }
}

[HarmonyPatch(typeof(Activatable), "CanActivate")]
public static class OrreryActivatableCanActivatePatch
{
    public static bool Prefix(Activatable __instance, ref bool __result)
    {
        if (!OrreryWeaponSuppression.ShouldSuppress(__instance))
            return true;

        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(GameShip), "StartActivating", new System.Type[] { typeof(Item.Type) })]
public static class OrreryStartActivatingTypePatch
{
    public static bool Prefix(GameShip __instance, Item.Type type, ref bool __result)
    {
        if (__instance == null || !OrreryRuntime.IsActive(__instance) ||
            !OrreryWeaponSuppression.IsSuppressedWeaponType(type))
        {
            return true;
        }

        // Native StartActivating returns false on a successful activation and true
        // when input should play its error sound. Suppression is intentional, not
        // an activation failure, so return false without running native firing.
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(GameShip), "StartActivating", new System.Type[] { typeof(int) })]
public static class OrreryStartActivatingIndexPatch
{
    public static bool Prefix(GameShip __instance, int number, ref bool __result)
    {
        if (__instance == null || !OrreryRuntime.IsActive(__instance) ||
            __instance.activatables == null ||
            number < 0 || number >= __instance.activatables.Count)
        {
            return true;
        }

        Activatable activatable = __instance.activatables[number];
        if (activatable == null ||
            !OrreryWeaponSuppression.IsSuppressedWeaponType(activatable.type))
        {
            return true;
        }

        __result = false;
        return false;
    }
}
