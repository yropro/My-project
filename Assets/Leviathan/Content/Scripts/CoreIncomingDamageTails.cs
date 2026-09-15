using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

/// <summary>Native network reception and ImpaleMissile have received-hit tails
/// OUTSIDE GameShip.Damage. Suppress those only for the exact application vetoed
/// inside the watch. Do not suppress a whole halo/beam lifetime or another hit.</summary>
[HarmonyPatch(typeof(NetCombat), "ApplyDamageEvent")]
public static class CoreIncomingNetworkDamageTailPatch
{
    [HarmonyPriority(Priority.First)]
    public static void Prefix(object __1, out int __state)
    { __state = CoreDamageApplicationObservation.BeginWatch(__1) + 1; }
    public static Exception Finalizer(Exception __exception, int __state)
    {
        CoreDamageApplicationObservation.EndWatch(__state - 1);
        return __exception;
    }
    public static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var code = new List<CodeInstruction>(instructions);
        MethodInfo clear = AccessTools.Method(typeof(GameShip), "ClearPendingKillContext");
        int found = -1;
        for (int i = 0; i < code.Count; i++)
            if (Equals(code[i].operand, clear) && (code[i].opcode == OpCodes.Call || code[i].opcode == OpCodes.Callvirt))
            {
                if (found >= 0) throw new InvalidOperationException("Ambiguous received-damage cleanup boundary.");
                found = i;
            }
        if (found < 0 || found + 1 >= code.Count)
            throw new InvalidOperationException("Missing received-damage cleanup boundary.");
        Label resume = generator.DefineLabel();
        code[found + 1].labels.Add(resume);
        code.InsertRange(found + 1, new[] {
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CoreDamageApplicationObservation),
                nameof(CoreDamageApplicationObservation.WatchedApplicationBlocked))),
            new CodeInstruction(OpCodes.Brfalse, resume), new CodeInstruction(OpCodes.Ret) });
        return code;
    }
}

[HarmonyPatch(typeof(ImpaleMissile), "HitObject")]
public static class CoreIncomingImpaleTailPatch
{
    [HarmonyPriority(Priority.First)]
    public static void Prefix(GameObject __0, out int __state)
    {
        GameShip target;
        GameShip.TryGetShip(__0, out target);
        __state = CoreDamageApplicationObservation.BeginWatch(target) + 1;
    }
    public static Exception Finalizer(Exception __exception, int __state)
    {
        CoreDamageApplicationObservation.EndWatch(__state - 1);
        return __exception;
    }
    public static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var code = new List<CodeInstruction>(instructions);
        int found = -1;
        for (int i = 0; i < code.Count; i++)
        {
            MethodInfo method = code[i].operand as MethodInfo;
            if (code[i].opcode != OpCodes.Call || method == null || method.Name != "HitObject" ||
                method.DeclaringType == typeof(ImpaleMissile) ||
                !typeof(Projectile).IsAssignableFrom(method.DeclaringType)) continue;
            if (found >= 0) throw new InvalidOperationException("Ambiguous impale base-hit boundary.");
            found = i;
        }
        if (found < 0 || found + 1 >= code.Count)
            throw new InvalidOperationException("Missing impale base-hit boundary.");
        Label resume = generator.DefineLabel();
        code[found + 1].labels.Add(resume);
        // The native base bool is still on the stack. Preserve it for either
        // normal continuation or immediate return (the projectile did impact).
        code.InsertRange(found + 1, new[] {
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CoreDamageApplicationObservation),
                nameof(CoreDamageApplicationObservation.WatchedApplicationBlocked))),
            new CodeInstruction(OpCodes.Brfalse, resume), new CodeInstruction(OpCodes.Ret) });
        return code;
    }
}
