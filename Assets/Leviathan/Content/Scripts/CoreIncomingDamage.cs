using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

/// <summary>
/// Whole-application veto at the target-authority boundary: after GameShip's
/// native incoming scaling/caps, immediately before its base damage call. A veto
/// returns from the enclosing override, so status/reflect/leech/counters do not
/// run. The inactive path leaves the original packet and native code untouched.
/// </summary>
public static class CoreIncomingDamage
{
    public delegate bool ActiveFilter(GameShip target);
    public delegate bool PacketFilter(GameShip target, Damageable.DamageType type,
        Damageable.DamageData[] packet);
    public delegate bool DirectFilter(GameShip target, Damageable.DamageType type,
        float value, bool destroy);
    private sealed class Entry
    {
        public string Name;
        public ActiveFilter Active;
        public PacketFilter Packet;
        public DirectFilter Direct;
    }
    private static readonly List<Entry> entries = new List<Entry>(4);

    // Native packets currently contain only a handful of modifier components.
    // Exact-size scratch arrays avoid changing the packet Length seen by native
    // code. Reentrant packets get distinct slots. Oversize/deep exceptional input
    // is cloned rather than risking shared-target mutation; the hot path is pooled.
    private const int ScratchDepth = 16;
    private const int ScratchLength = 64;
    private static readonly Damageable.DamageData[][][] scratch = CreateScratch();
    private static int depth;

    private static Damageable.DamageData[][][] CreateScratch()
    {
        var result = new Damageable.DamageData[ScratchDepth][][];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new Damageable.DamageData[ScratchLength + 1][];
            for (int n = 0; n <= ScratchLength; n++)
                result[i][n] = new Damageable.DamageData[n];
        }
        return result;
    }

    public static void Register(string name, ActiveFilter active,
        PacketFilter packet, DirectFilter direct)
    {
        if (string.IsNullOrEmpty(name) || active == null || packet == null || direct == null)
            throw new ArgumentException("Invalid incoming-damage registration.");
        for (int i = 0; i < entries.Count; i++)
        {
            Entry e = entries[i];
            if (e.Name != name) continue;
            if (e.Active == active && e.Packet == packet && e.Direct == direct) return;
            throw new InvalidOperationException("Duplicate incoming-damage registration: " + name);
        }
        entries.Add(new Entry { Name = name, Active = active, Packet = packet, Direct = direct });
    }

    private static bool IsLocal(GameShip target)
    {
        return target != null && target.IsPlayer();
    }
    public static void EnterPacket(GameShip target, ref Damageable.DamageData[] packet,
        out bool leased)
    {
        leased = false;
        if (!IsLocal(target) || packet == null) return;
        bool relevant = false;
        for (int i = 0; i < entries.Count && !relevant; i++)
            relevant = entries[i].Active(target);
        if (!relevant) return;
        Damageable.DamageData[] copy = depth < ScratchDepth && packet.Length <= ScratchLength
            ? scratch[depth][packet.Length]
            : new Damageable.DamageData[packet.Length];
        depth++;
        leased = true;
        Array.Copy(packet, copy, packet.Length);
        packet = copy;
    }
    public static void LeavePacket(bool leased)
    {
        if (leased) depth = Math.Max(0, depth - 1);
    }

    public static bool TryBlockPacket(GameShip target, Damageable.DamageType type,
        Damageable.DamageData[] packet)
    {
        CoreDamageApplicationObservation.NativeBoundary(false);
        if (!IsLocal(target)) return false;
        for (int i = 0; i < entries.Count; i++)
        {
            if (!entries[i].Packet(target, type, packet)) continue;
            ClearOutcome(target, false);
            CoreDamageApplicationObservation.NativeBoundary(true);
            return true;
        }
        return false;
    }
    public static bool TryBlockDirect(GameShip target, Damageable.DamageType type,
        float value, bool destroy)
    {
        CoreDamageApplicationObservation.NativeBoundary(false);
        if (!IsLocal(target)) return false;
        for (int i = 0; i < entries.Count; i++)
        {
            if (!entries[i].Direct(target, type, value, destroy)) continue;
            ClearOutcome(target, true);
            CoreDamageApplicationObservation.NativeBoundary(true);
            return true;
        }
        return false;
    }
    private static void ClearOutcome(GameShip target, bool direct)
    {
        // Native Heal and nested callbacks can overwrite these shared scratch
        // fields. A blocked application must report zero at its return boundary.
        target.lastHealthDamage = 0f;
        target.lastShieldDamage = 0f;
        if (direct) target.lastDirectDamageSource = null;
    }

    /// <summary>Installed v0.8.21 has exactly one direct base call in each
    /// GameShip override. Validate both the call and its argument-load shape;
    /// refuse to patch changed native control flow rather than partly suppressing
    /// a hit or silently binding an obsolete boolean-critical overload.</summary>
    internal static IEnumerable<CodeInstruction> InsertVeto(
        IEnumerable<CodeInstruction> source, ILGenerator generator, bool direct)
    {
        var code = new List<CodeInstruction>(source);
        Type[] signature = direct
            ? new[] { typeof(Damageable.DamageType), typeof(float), typeof(bool) }
            : new[] { typeof(Damageable.DamageType), typeof(Damageable.DamageData[]),
                typeof(float), typeof(int), typeof(Vector2), typeof(GameShip), typeof(bool) };
        MethodInfo native = AccessTools.DeclaredMethod(typeof(Damageable),
            direct ? "DirectDamage" : "Damage", signature);
        if (native == null) throw new MissingMethodException("Installed base damage ABI not found.");
        int found = -1;
        for (int i = 0; i < code.Count; i++)
        {
            if (code[i].opcode != OpCodes.Call || !Equals(code[i].operand, native)) continue;
            if (found >= 0) throw new InvalidOperationException("Ambiguous base damage call.");
            found = i;
        }
        int count = direct ? 4 : 8;
        int start = found - count;
        if (start < 0) throw new InvalidOperationException("Missing native damage boundary.");
        for (int i = 0; i < count; i++)
        {
            bool expected = (!direct && i == 7)
                ? code[start + i].opcode == OpCodes.Ldc_I4_0
                : LoadsArgument(code[start + i], i);
            if (!expected || code[start + i].blocks.Count != 0)
                throw new InvalidOperationException("Native damage argument/exception boundary changed.");
        }
        Label proceed = generator.DefineLabel();
        var first = new CodeInstruction(OpCodes.Ldarg_0);
        first.labels.AddRange(code[start].labels);
        code[start].labels.Clear();
        code[start].labels.Add(proceed);
        var injected = new List<CodeInstruction> {
            first, new CodeInstruction(OpCodes.Ldarg_1), new CodeInstruction(OpCodes.Ldarg_2)
        };
        if (direct) injected.Add(new CodeInstruction(OpCodes.Ldarg_3));
        injected.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CoreIncomingDamage),
            direct ? nameof(TryBlockDirect) : nameof(TryBlockPacket))));
        injected.Add(new CodeInstruction(OpCodes.Brfalse, proceed));
        injected.Add(new CodeInstruction(OpCodes.Ldc_I4_0));
        injected.Add(new CodeInstruction(OpCodes.Ret));
        code.InsertRange(start, injected);
        return code;
    }
    private static bool LoadsArgument(CodeInstruction instruction, int index)
    {
        if (index < 4)
            return instruction.opcode == new[] { OpCodes.Ldarg_0, OpCodes.Ldarg_1,
                OpCodes.Ldarg_2, OpCodes.Ldarg_3 }[index];
        return (instruction.opcode == OpCodes.Ldarg_S || instruction.opcode == OpCodes.Ldarg) &&
            Convert.ToInt32(instruction.operand) == index;
    }
}

[HarmonyPatch]
public static class CoreIncomingDamagePacketPatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.DeclaredMethod(typeof(GameShip), "Damage", new[] {
            typeof(Damageable.DamageType), typeof(Damageable.DamageData[]), typeof(float),
            typeof(int), typeof(Vector2), typeof(GameShip), typeof(bool) });
    }
    public struct State { public bool Entered, Leased; public int Observation; }
    [HarmonyPriority(Priority.First)]
    public static void Prefix(GameShip __instance, ref Damageable.DamageData[] __1,
        out State __state)
    {
        __state = new State { Entered = true, Observation = CoreDamageApplicationObservation.BeginApplication(__instance) };
        CoreIncomingDamage.EnterPacket(__instance, ref __1, out __state.Leased);
    }
    public static Exception Finalizer(Exception __exception, State __state)
    {
        if (!__state.Entered) return __exception;
        CoreIncomingDamage.LeavePacket(__state.Leased);
        CoreDamageApplicationObservation.EndApplication(__state.Observation);
        return __exception;
    }
    public static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        return CoreIncomingDamage.InsertVeto(instructions, generator, false);
    }
}

[HarmonyPatch]
public static class CoreIncomingDirectDamagePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.DeclaredMethod(typeof(GameShip), "DirectDamage", new[] {
            typeof(Damageable.DamageType), typeof(float), typeof(bool) });
    }
    [HarmonyPriority(Priority.First)]
    public static void Prefix(GameShip __instance, out int __state)
    { __state = CoreDamageApplicationObservation.BeginApplication(__instance) + 1; }
    public static Exception Finalizer(Exception __exception, int __state)
    {
        CoreDamageApplicationObservation.EndApplication(__state - 1);
        return __exception;
    }
    public static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        return CoreIncomingDamage.InsertVeto(instructions, generator, true);
    }
}
