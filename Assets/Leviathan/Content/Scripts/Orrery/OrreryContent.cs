using StarVortex;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Canonical Orrery access to borrowed Star Vortex content.
///
/// This resolver owns only stable source-asset references. Assets are resolved
/// lazily from legitimate gameplay/presentation paths after mod loading, successful
/// lookups are cached for the process lifetime, and failed lookups are retried on
/// later calls. Runtime items, faction-selected projectile prefabs, pooled objects,
/// ships, casts, networking and presentation lifetime remain owned by consumers.
/// </summary>
public static class OrreryContent
{
    internal const string InfernoCannonPath =
        "Base/Items/PrimaryWeapon/Inferno Cannon";
    internal const string CryoGunPath =
        "Base/Items/PrimaryWeapon/Cryo Gun";
    internal const string LightningOrbLauncherPath =
        "Base/Items/SecondaryWeapon/Lightning Orb Launcher";
    internal const string FrozenOrbLauncherPath =
        "Base/Items/SecondaryWeapon/Frozen Orb Launcher";
    internal const string FrostNovaPulsePath =
        "Base/Items/Special/Frost Nova Pulse";
    internal const string ThermalHaloPath =
        "Base/Items/AutoSpecial/Thermal Halo";
    internal const string ColdHaloPath =
        "Base/Items/AutoSpecial/Cold Halo";
    internal const string ElectricHaloPath =
        "Base/Items/AutoSpecial/Electric Halo";

    private static readonly HashSet<string> warnedMissing =
        new HashSet<string>();

    private static LauncherItemBase infernoCannon;
    private static LauncherItemBase cryoGun;
    private static LauncherItemBase lightningOrbLauncher;
    private static LauncherItemBase frozenOrbLauncher;
    private static PulseItemBase frostNovaPulse;
    private static HaloItemBase thermalHalo;
    private static HaloItemBase coldHalo;
    private static HaloItemBase electricHalo;

    public static LauncherItemBase InfernoCannon
    {
        get
        {
            return Resolve(
                ref infernoCannon,
                InfernoCannonPath,
                "Inferno Cannon");
        }
    }

    public static LauncherItemBase CryoGun
    {
        get
        {
            return Resolve(
                ref cryoGun,
                CryoGunPath,
                "Cryo Gun");
        }
    }

    public static LauncherItemBase LightningOrbLauncher
    {
        get
        {
            return Resolve(
                ref lightningOrbLauncher,
                LightningOrbLauncherPath,
                "Lightning Orb Launcher");
        }
    }

    public static LauncherItemBase FrozenOrbLauncher
    {
        get
        {
            return Resolve(
                ref frozenOrbLauncher,
                FrozenOrbLauncherPath,
                "Frozen Orb Launcher");
        }
    }

    public static PulseItemBase FrostNovaPulse
    {
        get
        {
            return Resolve(
                ref frostNovaPulse,
                FrostNovaPulsePath,
                "Frost Nova Pulse");
        }
    }

    public static HaloItemBase ThermalHalo
    {
        get
        {
            return Resolve(
                ref thermalHalo,
                ThermalHaloPath,
                "Thermal Halo");
        }
    }

    public static HaloItemBase ColdHalo
    {
        get
        {
            return Resolve(
                ref coldHalo,
                ColdHaloPath,
                "Cold Halo");
        }
    }

    public static HaloItemBase ElectricHalo
    {
        get
        {
            return Resolve(
                ref electricHalo,
                ElectricHaloPath,
                "Electric Halo");
        }
    }

    public static HaloItemBase GetElementalHalo(OrreryElement element)
    {
        if (element == OrreryElement.Fire)
            return ThermalHalo;
        if (element == OrreryElement.Ice)
            return ColdHalo;
        if (element == OrreryElement.Lightning)
            return ElectricHalo;
        return null;
    }

    private static T Resolve<T>(
        ref T cached,
        string path,
        string logicalName)
        where T : Object
    {
        if (cached != null)
            return cached;

        T resolved = ModContent.Load<T>(path);
        if (resolved != null)
        {
            cached = resolved;
            return cached;
        }

        if (warnedMissing.Add(path))
        {
            Debug.LogWarning(
                "[Orrery] Content asset was not found or was incompatible: " +
                logicalName + " (" + path + ", expected " +
                typeof(T).Name + ").");
        }

        // Failure is deliberately not cached. An accidental pre-bundle call must
        // not make the asset permanently unavailable once mod loading completes.
        return null;
    }
}
