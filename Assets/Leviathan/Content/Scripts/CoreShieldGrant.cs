using StarVortex;
using UnityEngine;

/// <summary>
/// Shared boundary for granting shield points to a player ship.
///
/// Today this fills the target's native shield without ever spilling into hull.
/// Callers intentionally do not inspect shield equipment themselves: a future
/// auxiliary/internal shield pool can be added behind this boundary without
/// changing Cold Fusion or any other effect that grants shield points.
/// </summary>
public static class CoreShieldGrant
{
    public static float Grant(GameShip target, float amount, Vector2 fromPosition)
    {
        if (target == null || amount <= 0f || target.health <= 0f)
            return 0f;

        Shield shield = target.shield;
        if (shield == null)
            return 0f;

        float before = shield.shield;
        if (before >= shield.ShieldMax)
            return 0f;

        target.Heal(
            amount,
            false,
            fromPosition,
            null,
            true,
            false);

        return Mathf.Max(0f, shield.shield - before);
    }
}
