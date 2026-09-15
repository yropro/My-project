using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Shared target-authority boundary for mechanics that transform incoming player
/// damage before native resistance / shield / hull resolution.
///
/// Skills register allocation-free filters once. Core owns the native Harmony
/// bindings and fails open if an individual filter throws. The packet hook sits
/// on the base Damageable.Damage call reached from GameShip.Damage, so normal
/// GameShip level/difficulty/player-cap processing has already happened.
/// </summary>
public static class CoreIncomingDamage
{
    public delegate void PacketFilter(
        GameShip target,
        Damageable.DamageType damageType,
        Damageable.DamageData[] damageData);

    public delegate void DirectFilter(
        GameShip target,
        Damageable.DamageType damageType,
        ref float damage,
        bool destroy);

    private sealed class Entry
    {
        public string Name;
        public PacketFilter Packet;
        public DirectFilter Direct;
        public bool Warned;
    }

    private static readonly List<Entry> entries = new List<Entry>(4);

    public static void Register(
        string name,
        PacketFilter packetFilter,
        DirectFilter directFilter)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            (packetFilter == null && directFilter == null))
        {
            throw new ArgumentException(
                "Incoming-damage filter requires a name and callback.");
        }

        for (int i = 0; i < entries.Count; i++)
        {
            Entry existing = entries[i];
            if (!string.Equals(existing.Name, name, StringComparison.Ordinal))
                continue;

            if (existing.Packet == packetFilter && existing.Direct == directFilter)
                return;

            throw new InvalidOperationException(
                "Duplicate incoming-damage filter registration: " + name);
        }

        entries.Add(new Entry
        {
            Name = name,
            Packet = packetFilter,
            Direct = directFilter
        });
    }

    internal static void ApplyPacket(
        GameShip target,
        Damageable.DamageType damageType,
        Damageable.DamageData[] damageData)
    {
        if (target == null || damageData == null || damageData.Length == 0)
            return;

        for (int i = 0; i < entries.Count; i++)
        {
            Entry entry = entries[i];
            if (entry == null || entry.Packet == null)
                continue;

            try
            {
                entry.Packet(target, damageType, damageData);
            }
            catch (Exception ex)
            {
                WarnOnce(entry, ex);
            }
        }
    }

    internal static void ApplyDirect(
        GameShip target,
        Damageable.DamageType damageType,
        ref float damage,
        bool destroy)
    {
        if (target == null || damage <= 0f)
            return;

        for (int i = 0; i < entries.Count; i++)
        {
            Entry entry = entries[i];
            if (entry == null || entry.Direct == null)
                continue;

            try
            {
                entry.Direct(target, damageType, ref damage, destroy);
            }
            catch (Exception ex)
            {
                WarnOnce(entry, ex);
            }
        }
    }

    private static void WarnOnce(Entry entry, Exception ex)
    {
        if (entry == null || entry.Warned)
            return;

        entry.Warned = true;
        Debug.LogWarning(
            "[CoreIncomingDamage] Filter '" + entry.Name +
            "' failed open: " + ex);
    }
}

/// <summary>
/// v0.8.21 uses integer critical tiers. Resolve the exact installed ABI rather
/// than naming the editor reference's historical bool-crit overload.
/// </summary>
[HarmonyPatch]
public static class CoreIncomingDamagePacketPatch
{
    public static MethodBase TargetMethod()
    {
        MethodInfo[] methods = typeof(Damageable).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        for (int i = 0; i < methods.Length; i++)
        {
            MethodInfo method = methods[i];
            if (method.Name != "Damage")
                continue;

            ParameterInfo[] p = method.GetParameters();
            if (p.Length == 7 &&
                p[0].ParameterType == typeof(Damageable.DamageType) &&
                p[1].ParameterType == typeof(Damageable.DamageData[]) &&
                p[2].ParameterType == typeof(float) &&
                p[3].ParameterType == typeof(int) &&
                p[4].ParameterType == typeof(Vector2) &&
                p[5].ParameterType == typeof(GameShip) &&
                p[6].ParameterType == typeof(bool))
            {
                return method;
            }
        }

        return null;
    }

    public static void Prefix(
        Damageable __instance,
        Damageable.DamageType __0,
        Damageable.DamageData[] __1)
    {
        GameShip target = __instance as GameShip;
        if (target == null || !target.IsPlayer())
            return;

        CoreIncomingDamage.ApplyPacket(target, __0, __1);
    }
}

/// <summary>
/// Direct status damage has its own native path. Hook the base implementation so
/// GameShip's authority/immunity checks have already run while native resistance,
/// shield and hull resolution have not.
/// </summary>
[HarmonyPatch]
public static class CoreIncomingDirectDamagePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Damageable),
            "DirectDamage",
            new Type[]
            {
                typeof(Damageable.DamageType),
                typeof(float),
                typeof(bool)
            });
    }

    public static void Prefix(
        Damageable __instance,
        Damageable.DamageType __0,
        ref float __1,
        bool __2)
    {
        GameShip target = __instance as GameShip;
        if (target == null || !target.IsPlayer())
            return;

        CoreIncomingDamage.ApplyDirect(target, __0, ref __1, __2);
    }
}
