using HarmonyLib;
using StarVortex;
using UnityEngine;

/// <summary>
/// Applies player-authored numeric Orrery satellite templates at the last safe
/// point before the native carrier squadron instantiates its follower ships.
/// </summary>
[HarmonyPatch(typeof(Squadron), "Build")]
public static class OrrerySatelliteTemplateRuntimePatch
{
    public static void Prefix(Squadron __instance)
    {
        if (__instance == null || __instance.ships == null ||
            __instance.ships.Count <= 1)
        {
            return;
        }

        CoreOwnerContext context = CoreClassRuntime.CurrentContext;
        GameShip owner = context != null && context.IsValid &&
            context.ClassId == CoreClassId.Orrery
                ? context.Ship
                : null;
        if (owner == null || !OrreryRuntime.IsActive(owner))
            return;

        Squadron.SquadronShip leader = __instance.ships[0];
        if (leader == null || !object.ReferenceEquals(leader.ship, owner) ||
            !object.ReferenceEquals(owner.squadron, __instance))
        {
            return;
        }

        OrrerySatelliteTemplates.EnsureDirectory();
        OrreryRuntime.ResolvedState resolved = OrreryRuntime.GetResolvedState(owner);
        int satelliteCount = resolved == null
            ? 0
            : Mathf.Clamp(
                resolved.SatelliteCount,
                0,
                Mathf.Min(
                    OrreryOrbit.MaxSatellites,
                    __instance.ships.Count - 1));

        int loaded = 0;
        for (int ordinal = 1; ordinal <= satelliteCount; ordinal++)
        {
            string serialized;
            if (!OrrerySatelliteTemplates.TryLoadSerializedBody(
                    ordinal,
                    out serialized))
            {
                continue;
            }

            Squadron.SquadronShip slot = __instance.ships[ordinal];
            Ship definition = slot == null || slot.npc == null
                ? null
                : slot.npc.GetShip();
            if (definition == null)
                continue;

            definition.serializedBody = serialized;
            loaded++;
        }

        if (loaded > 0)
        {
            Debug.Log(
                "[Orrery] Applied " + loaded +
                " player-authored satellite template(s) before native build.");
        }
    }
}
