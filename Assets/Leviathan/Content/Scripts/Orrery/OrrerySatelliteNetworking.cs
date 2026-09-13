using HarmonyLib;
using StarVortex;
using System.Collections.Generic;

/// <summary>
/// Admits locally-built Orrery satellites into Star Vortex's native player-owned
/// entity replication path. Squadron.Build spawns followers through Star.SpawnShip,
/// which initially classifies them as star-owned entities; Orrery ownership is
/// player-local instead, matching native drones, ship spawners and mirror images.
///
/// The native NetWorldBridge then owns spawn, transform streaming and teardown.
/// Core networking remains presentation-only and never duplicates satellite poses.
/// </summary>
[HarmonyPatch(typeof(OrrerySatellites), nameof(OrrerySatellites.PublishLiveSatellites))]
public static class OrrerySatelliteNetworkingPatch
{
    public static void Prefix(
        GameShip owner,
        IList<OrrerySatellites.PublishEntry> entries)
    {
        if (owner == null || entries == null)
            return;

        for (int i = 0; i < entries.Count; i++)
        {
            GameShip satellite = entries[i].Ship;
            if (satellite == null)
                continue;

            // Star.SpawnShip marks Squadron followers as netStarEntity. Player
            // summons use the mutually-exclusive player-entity family instead;
            // clearing the star flag prevents authority/routing ambiguity.
            satellite.netStarEntity = false;
            satellite.netPlayerEntity = true;
        }
    }
}
