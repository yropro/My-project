using System;
using HarmonyLib;
using StarVortex;
using UnityEngine;
using static StarVortex.Damageable;

/// <summary>
/// Current game's integer critical-hit API, bound independently of the editor's
/// older game reference. Custom attacks retain their existing binary crit rules;
/// native beam forks and redirected damage preserve the complete native tier.
/// </summary>
public static class CoreNativeCriticalHits
{
    private static T Bind<T>(Type owner, string name, params Type[] parameters) where T : Delegate
    {
        var method = AccessTools.Method(owner, name, parameters);
        if (method == null)
            throw new MissingMethodException(owner.FullName, name + " (integer critical-hit API)");
        return AccessTools.MethodDelegate<T>(method);
    }

    private static readonly Func<float, GameShip, int> Roll =
        Bind<Func<float, GameShip, int>>(typeof(Modifier), "CritRoll", typeof(float), typeof(GameShip));
    private static readonly Func<BeamWeapon, int, bool, DamageData[]> BeamPacket =
        Bind<Func<BeamWeapon, int, bool, DamageData[]>>(typeof(BeamWeapon), "GetDamageData", typeof(int), typeof(bool));
    private static readonly Func<Launcher, int, bool, DamageData[]> LauncherPacket =
        Bind<Func<Launcher, int, bool, DamageData[]>>(typeof(Launcher), "GetDamageData", typeof(int), typeof(bool));
    private static readonly Func<Torch, int, DamageData[]> TorchPacket =
        Bind<Func<Torch, int, DamageData[]>>(typeof(Torch), "GetDamageData", typeof(int));

    public static bool CritRoll(float chance, GameShip target) => Roll(chance, target) > 0;
    public static DamageData[] GetDamageData(BeamWeapon source, int critTier, bool subBeam) => BeamPacket(source, critTier, subBeam);
    public static DamageData[] GetDamageData(BeamWeapon source, bool crit, bool subBeam) => BeamPacket(source, crit ? 1 : 0, subBeam);
    public static DamageData[] GetDamageData(Launcher source, bool crit, bool subBeam) => LauncherPacket(source, crit ? 1 : 0, subBeam);
    public static DamageData[] GetDamageData(Torch source, bool crit) => TorchPacket(source, crit ? 1 : 0);

    private delegate bool DamageCall(Damageable target, DamageType type, DamageData[] data,
        float status, int critTier, Vector2 position, GameShip source, bool bypass);
    private static readonly DamageCall NativeDamage = Bind<DamageCall>(typeof(Damageable), "Damage",
        typeof(DamageType), typeof(DamageData[]), typeof(float), typeof(int), typeof(Vector2), typeof(GameShip), typeof(bool));
    private static readonly Action<Damageable, float, int, Vector2, GameObject, bool, bool> NativeHeal =
        Bind<Action<Damageable, float, int, Vector2, GameObject, bool, bool>>(typeof(Damageable), "Heal",
            typeof(float), typeof(int), typeof(Vector2), typeof(GameObject), typeof(bool), typeof(bool));

    public static bool Damage(Damageable target, DamageType type, DamageData[] data,
        float status, int critTier, Vector2 position, GameShip source, bool bypass) =>
        NativeDamage(target, type, data, status, critTier, position, source, bypass);

    public static void Heal(Damageable target, float amount, int critTier, Vector2 position,
        GameObject source, bool shieldOnly, bool hullOnly) =>
        NativeHeal(target, amount, critTier, position, source, shieldOnly, hullOnly);
}
