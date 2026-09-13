using StarVortex;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Narrow Orrery adapter over Star Vortex's native NetCombat.RouteDamage path.
///
/// The native overload is internal, so the signature is resolved once by
/// reflection and converted to a strongly typed delegate. Steady-state custom
/// spell hits therefore avoid MethodInfo.Invoke argument arrays and value boxing.
/// Legacy V0 spells can migrate onto this helper when they move to per-spell files.
/// </summary>
public static class OrreryDamageRouter
{
    private delegate bool RouteDamageDelegate(
        // Native IDamageable is internal. Delegate argument contravariance
        // accepts public Damageable, which implements that interface.
        Damageable damageable,
        Damageable.DamageType damageType,
        Damageable.DamageData[] damageData,
        float statusEffectChance,
        int crit,
        Vector2 fromPosition,
        GameShip fromShip,
        bool bypassDamageLimit,
        float knockbackPower,
        Activatable slotSource,
        float impaleDps,
        float impaleDuration,
        bool forceAttackerLocal,
        float impaleRotation);

    private static RouteDamageDelegate routeDamage;
    private static bool routeDamageResolved;

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

        try
        {
            return routeDamage(
                damageable,
                damageType,
                damageData,
                statusEffectChance,
                crit ? 1 : 0,
                fromPosition,
                owner,
                bypassDamageLimit,
                knockbackPower,
                slotSource,
                0f,
                0f,
                false,
                0f);
        }
        catch (System.Exception ex)
        {
            Debug.LogError("[Orrery] Native damage routing failed: " + ex);
            return false;
        }
    }

    // Kept as part of the shared spell-runtime lifecycle surface. The delegate is
    // process-wide and owner-agnostic, so there is intentionally no per-owner state.
    public static void Forget(GameShip owner) { }

    public static void Reset()
    {
        routeDamage = null;
        routeDamageResolved = false;
    }

    private static bool EnsureNativeDamageRouter()
    {
        if (routeDamageResolved)
            return routeDamage != null;

        routeDamageResolved = true;
        MethodInfo[] methods = typeof(NetCombat).GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < methods.Length; i++)
        {
            MethodInfo method = methods[i];
            if (method.Name != "RouteDamage")
                continue;

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != 14 ||
                parameters[0].ParameterType.FullName != "StarVortex.IDamageable" ||
                !parameters[0].ParameterType.IsAssignableFrom(typeof(Damageable)) ||
                parameters[1].ParameterType != typeof(Damageable.DamageType) ||
                parameters[2].ParameterType != typeof(Damageable.DamageData[]) ||
                parameters[4].ParameterType != typeof(int))
            {
                continue;
            }

            try
            {
                routeDamage = (RouteDamageDelegate)System.Delegate.CreateDelegate(
                    typeof(RouteDamageDelegate),
                    method);
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[Orrery] Could not bind native NetCombat.RouteDamage: " + ex);
                routeDamage = null;
            }
            break;
        }

        if (routeDamage == null)
            Debug.LogError("[Orrery] Could not resolve native NetCombat.RouteDamage signature.");
        return routeDamage != null;
    }
}
