using StarVortex;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Narrow Orrery adapter over Star Vortex's native NetCombat.RouteDamage path.
///
/// New explicit spell files can route custom packets without duplicating the
/// reflected native signature or allocating argument arrays per hit. Legacy V0
/// spells still carry their private copy until their later per-spell migration.
/// </summary>
public static class OrreryDamageRouter
{
    private sealed class OwnerScratch
    {
        public readonly object[] Arguments = new object[14];
    }

    private static readonly Dictionary<GameShip, OwnerScratch> scratchByOwner =
        new Dictionary<GameShip, OwnerScratch>(4);

    private static MethodInfo routeDamageMethod;
    private static bool routeDamageMethodResolved;

    public static bool Route(
        GameShip owner,
        Damageable damageable,
        Damageable.DamageType damageType,
        Damageable.DamageData[] damageData,
        float statusEffectChance,
        bool crit,
        Vector2 fromPosition,
        bool bypassDamageLimit,
        float knockbackPower,
        Activatable slotSource)
    {
        if (owner == null || damageable == null || damageData == null ||
            !EnsureNativeDamageRouter())
        {
            return false;
        }

        OwnerScratch scratch;
        if (!scratchByOwner.TryGetValue(owner, out scratch) || scratch == null)
        {
            scratch = new OwnerScratch();
            scratchByOwner[owner] = scratch;
        }

        object[] args = scratch.Arguments;
        args[0] = damageable;
        args[1] = damageType;
        args[2] = damageData;
        args[3] = statusEffectChance;
        args[4] = crit;
        args[5] = fromPosition;
        args[6] = owner;
        args[7] = bypassDamageLimit;
        args[8] = knockbackPower;
        args[9] = slotSource;
        args[10] = 0f;
        args[11] = 0f;
        args[12] = false;
        args[13] = 0f;

        try
        {
            object result = routeDamageMethod.Invoke(null, args);
            return result is bool && (bool)result;
        }
        catch (System.Exception ex)
        {
            Debug.LogError("[Orrery] Native damage routing failed: " + ex);
            return false;
        }
    }

    public static void Forget(GameShip owner)
    {
        if (owner != null)
            scratchByOwner.Remove(owner);
    }

    public static void Reset()
    {
        scratchByOwner.Clear();
        routeDamageMethod = null;
        routeDamageMethodResolved = false;
    }

    private static bool EnsureNativeDamageRouter()
    {
        if (routeDamageMethodResolved)
            return routeDamageMethod != null;

        routeDamageMethodResolved = true;
        MethodInfo[] methods = typeof(NetCombat).GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < methods.Length; i++)
        {
            MethodInfo method = methods[i];
            if (method.Name != "RouteDamage")
                continue;

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length == 14 &&
                parameters[0].ParameterType.IsAssignableFrom(typeof(Damageable)) &&
                parameters[1].ParameterType == typeof(Damageable.DamageType) &&
                parameters[2].ParameterType == typeof(Damageable.DamageData[]))
            {
                routeDamageMethod = method;
                break;
            }
        }

        if (routeDamageMethod == null)
            Debug.LogError("[Orrery] Could not resolve native NetCombat.RouteDamage signature.");
        return routeDamageMethod != null;
    }
}
