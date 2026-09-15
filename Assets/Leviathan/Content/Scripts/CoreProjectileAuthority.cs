using HarmonyLib;
using StarVortex;
using System.Collections;
using System.Reflection;

/// <summary>
/// Sparse authority lookup for mechanics that must act on the real native
/// projectile rather than a rendered replica. Reflection handles are cached once;
/// callers do not scan these tables in steady-state simulation.
/// </summary>
public static class CoreProjectileAuthority
{
    private static readonly FieldInfo ActiveBridgeField =
        AccessTools.Field(typeof(NetSession), "activeBridge");
    private static readonly FieldInfo MyNetProjectilesField =
        AccessTools.Field(typeof(NetWorldBridge), "myNetProjectiles");
    private static readonly FieldInfo RepsField =
        AccessTools.Field(typeof(NetWorldBridge), "reps");

    public static bool TryGetLocalAuthoritative(
        uint projectileNetId,
        out Projectile projectile)
    {
        projectile = null;

        NetSession session = NetSession.instance;
        if (!NetSession.InSession || session == null ||
            projectileNetId == 0u ||
            !NetIds.IsProjectileNetId(projectileNetId) ||
            NetIds.ProjectileOwnerOf(projectileNetId) != session.localPlayerId ||
            ActiveBridgeField == null || MyNetProjectilesField == null)
        {
            return false;
        }

        NetWorldBridge bridge = ActiveBridgeField.GetValue(session) as NetWorldBridge;
        if (bridge == null)
            return false;

        IDictionary projectiles = MyNetProjectilesField.GetValue(bridge) as IDictionary;
        if (projectiles == null || !projectiles.Contains(projectileNetId))
            return false;

        projectile = projectiles[projectileNetId] as Projectile;
        return projectile != null && projectile.netId == projectileNetId &&
            !projectile.netRendered && !projectile.IsDestroying();
    }

    public static bool TryGetPlayerShip(int playerId, out GameShip ship)
    {
        ship = null;

        NetSession session = NetSession.instance;
        if (session == null || playerId < 0)
            return false;

        if (playerId == session.localPlayerId)
        {
            ship = WorldController.instance == null
                ? null
                : WorldController.instance.GetCurrentPlayerShip();
            return ship != null;
        }

        if (ActiveBridgeField == null || RepsField == null)
            return false;

        NetWorldBridge bridge = ActiveBridgeField.GetValue(session) as NetWorldBridge;
        if (bridge == null)
            return false;

        IDictionary reps = RepsField.GetValue(bridge) as IDictionary;
        if (reps == null || !reps.Contains(playerId))
            return false;

        RemoteShipDriver driver = reps[playerId] as RemoteShipDriver;
        ship = driver == null ? null : driver.gameShip;
        return ship != null;
    }
}
