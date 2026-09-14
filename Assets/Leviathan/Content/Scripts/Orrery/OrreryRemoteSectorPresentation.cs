using HarmonyLib;
using StarVortex;
using UnityEngine;

/// <summary>
/// Remote-only driver for Orrery sector wheels.
///
/// CoreNetwork's exact-replica specialization gate proves that the observed ship
/// currently belongs to an Orrery player. The current Orrery progression exposes
/// only the baseline root, so the wheel geometry is fully derivable locally and
/// no additional dynamic bytes are needed.
/// </summary>
public static class OrreryRemoteSectorPresentation
{
    private static readonly OrreryRuntime.ResolvedState BaselineResolved =
        OrreryRuntime.CreateBaselineResolvedState();

    public static void Tick(GameShip remoteOwner)
    {
        if (remoteOwner == null || !remoteOwner.IsRemotePlayer())
            return;

        if (!CoreNetwork.HasSynchronizedSpecialization(
                remoteOwner,
                CoreClassId.Orrery))
        {
            OrrerySectorPresentation.Hide(remoteOwner);
            return;
        }

        // The wheel only needs proof that the common Orrery dynamic stream is
        // live. Avoid TryReadRemote here because that also scans/decodes the
        // Shatterbolt multipart bank even though sector geometry consumes none of
        // it. Presence plus the base payload version is sufficient after the
        // exact specialization gate above.
        CoreNetwork.SlotReader common;
        if (!CoreNetwork.TryReadSlot(
                remoteOwner,
                OrreryNetwork.SharedSlotId,
                out common) ||
            common.Byte() != OrreryNetwork.PayloadVersion)
        {
            OrrerySectorPresentation.Hide(remoteOwner);
            return;
        }

        OrrerySectorPresentation.TickRemote(
            remoteOwner,
            BaselineResolved);
    }

    public static void Forget(GameShip remoteOwner)
    {
        if (remoteOwner != null)
            OrrerySectorPresentation.Hide(remoteOwner);
    }
}

[HarmonyPatch(typeof(RemoteShipDriver), "Render")]
public static class OrreryRemoteSectorPresentationRenderPatch
{
    public static void Postfix(RemoteShipDriver __instance)
    {
        GameShip remoteOwner = __instance == null
            ? null
            : __instance.gameShip;

        if (remoteOwner != null && remoteOwner.IsRemotePlayer())
            OrreryRemoteSectorPresentation.Tick(remoteOwner);
    }
}

[HarmonyPatch(typeof(GameShip), "OnDestroy")]
public static class OrreryRemoteSectorPresentationShipDestroyedPatch
{
    public static void Prefix(GameShip __instance)
    {
        if (__instance != null && __instance.IsRemotePlayer())
            OrreryRemoteSectorPresentation.Forget(__instance);
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class OrreryRemoteSectorPresentationWorldDestroyedPatch
{
    public static void Prefix()
    {
        OrrerySectorPresentation.Hide();
    }
}
