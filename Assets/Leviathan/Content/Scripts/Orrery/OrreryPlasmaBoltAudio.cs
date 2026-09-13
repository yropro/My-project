using HarmonyLib;
using UnityEngine;

/// <summary>
/// Plasma Bolt audio presentation. ShowBoltVisual is the already-deduplicated
/// stroke event for both the local owner and remote reconstruction, so audio is
/// deliberately attached to that event rather than given its own network state.
/// </summary>
[HarmonyPatch(typeof(OrreryPlasmaBolt), "ShowBoltVisual")]
public static class OrreryPlasmaBoltAudioPatch
{
    public static void Prefix(Vector2 __1, float __3)
    {
        if (__3 <= 0f)
            return;

        // Localize the thunder at the caster/start of the stroke. The same start
        // position is reconstructed from the replicated Plasma stroke payload on
        // remote peers, giving one playback per observed cast in either direction.
        CoreAudioRuntime.PlayPositionalOneShot(
            OrrerySpellCompendium.PlasmaBolt.ThunderClipName,
            __1,
            OrrerySpellCompendium.PlasmaBolt.ThunderVolume,
            "Orrery Plasma Bolt Thunder");
    }
}
