using HarmonyLib;
using StarVortex;

/// <summary>
/// Plasma presentation deliberately survives a temporarily omitted multiplex-bank
/// record, because active burn visuals have their own short refresh timeout. The
/// common Orrery state slot is different: it is published on every valid Orrery
/// ship-state send. Its absence therefore means this replica no longer has a live
/// Orrery stream (class exit/replacement/stale stream), so retaining Plasma timers
/// would only leave presentation after owner-side gameplay has already been torn
/// down.
/// </summary>
[HarmonyPatch(typeof(OrreryPlasmaBoltPresentation),
    nameof(OrreryPlasmaBoltPresentation.Tick))]
public static class OrreryPlasmaPresentationLifecycleGuardPatch
{
    public static bool Prefix(GameShip owner)
    {
        if (owner == null || !owner.IsRemotePlayer())
            return true;

        // This is only a liveness gate. Reading the full Orrery presentation
        // state would also rescan/decode Shatterbolt's multipart history every
        // render even though Plasma does not consume any of those fields.
        CoreNetwork.SlotReader common;
        if (CoreNetwork.TryReadSlot(
                owner,
                OrreryNetwork.SharedSlotId,
                out common) &&
            common.Byte() == OrreryNetwork.PayloadVersion)
        {
            return true;
        }

        OrreryPlasmaBoltPresentation.Forget(owner);
        return false;
    }
}
