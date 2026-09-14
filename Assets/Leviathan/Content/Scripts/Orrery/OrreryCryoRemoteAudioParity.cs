using HarmonyLib;
using StarVortex;
using UnityEngine;

/// <summary>
/// Local Cone of Cold emits five native Cryo volleys. Launcher.ActivateNow routes
/// each volley through ShootProjectile, so the native one-shot is heard five times.
/// Remote reconstruction creates the same five visual ranks without activating a
/// Launcher; mirror the four follow-up one-shots here. The first/centered volley is
/// already played when OrreryLegacySpellRemotePresentation observes a new cast.
/// </summary>
[HarmonyPatch(typeof(OrreryLegacySpellRemotePresentation), "SpawnCryoWave")]
public static class OrreryCryoRemoteAudioParityPatch
{
    public static void Postfix(GameShip owner, int waveIndex)
    {
        // Wave zero's sound is emitted by the new-generation path. Native local
        // presentation plays again for each of the four ActivateNow follow-ups.
        if (owner == null || waveIndex <= 0)
            return;

        LauncherItemBase cryoBase = OrreryContent.CryoGun;
        if (cryoBase == null || cryoBase.soundEffect == null ||
            cryoBase.soundEffect.audioClip == null)
        {
            return;
        }

        OrreryLegacySpellRemotePresentation.PlayNativeOneShot(
            cryoBase.soundEffect,
            owner.transform.position,
            "Orrery Remote Cone of Cold Wave");
    }
}
