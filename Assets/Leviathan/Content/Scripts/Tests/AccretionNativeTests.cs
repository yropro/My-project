#if ACCRETION_NATIVE_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using StarVortex;
using Console = System.Console;

/// <summary>Standalone installed-assembly checks. Reads actual native IL and
/// executes production codecs/state only; does not run Unity physics or sockets.</summary>
public static class AccretionNativeTests
{
    private static int checks;
    public static int Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) => {
            foreach (string dir in Environment.GetEnvironmentVariable("CORE_NETWORK_TEST_ASSEMBLIES").Split(';'))
            {
                string path = Path.Combine(dir, new AssemblyName(args.Name).Name + ".dll");
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        try { Run(); Console.WriteLine("Accretion installed-assembly checks passed: " + checks); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Check(bool value, string why)
    { checks++; if (!value) throw new Exception(why); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        Check(typeof(GameShip).Assembly.ManifestModule.ModuleVersionId.ToString() ==
            "dacecd9e-6ef3-4df5-8118-67cde157a00b", "Unexpected installed assembly: re-audit native evidence.");
        CheckDamage(false);
        CheckDamage(true);
        foreach (MethodBase target in CoreProjectileSweepPatch.TargetMethods())
        {
            ILGenerator generator = PatchProcessor.CreateILGenerator(target);
            var before = PatchProcessor.GetOriginalInstructions(target, generator);
            int queries = before.Count(i => i.operand is MethodInfo &&
                ((MethodInfo)i.operand).DeclaringType == typeof(PhysicsController) &&
                (((MethodInfo)i.operand).Name == "Raycast" || ((MethodInfo)i.operand).Name == "CircleCast"));
            var after = new List<CodeInstruction>(CoreProjectileSweep.Adapt(before));
            Check(after.Count == before.Count + queries, "Sweep must add only projectile arguments");
            Check(after.Count(i => Called(i, typeof(CoreProjectileSweep), "BarrierOrReflect")) == 1,
                "Missing exact pre-hit barrier dispatch");
            Check(after.Count(i => Called(i, typeof(CoreProjectileSweep), "NativeHit")) == 1,
                "Missing original virtual hit continuation");
            Check(after.Count(i => Called(i, typeof(CoreProjectileSweep), "Raycast") ||
                Called(i, typeof(CoreProjectileSweep), "CircleCast")) == queries, "Query adapter count");
            Check(!after.Any(i => i.operand is MethodInfo &&
                ((MethodInfo)i.operand).DeclaringType == typeof(PhysicsController)), "Unadapted query");
            MustThrow(() => CoreProjectileSweep.Adapt(new CodeInstruction[0]).ToList(), "Changed sweep accepted");
        }
        var overrides = typeof(Projectile).Assembly.GetTypes().Where(t => typeof(Projectile).IsAssignableFrom(t))
            .Where(t => AccessTools.DeclaredMethod(t, "UpdateCollision") != null).Select(t => t.Name).OrderBy(n => n).ToArray();
        Check(string.Join(",", overrides) == "CapturedProjectile,FuzzyProjectile,Mine,Projectile",
            "A new native virtual collision override needs review: " + string.Join(",", overrides));
        CheckTail(typeof(NetCombat), "ApplyDamageEvent", CoreIncomingNetworkDamageTailPatch.Transpiler, false);
        CheckTail(typeof(ImpaleMissile), "HitObject", CoreIncomingImpaleTailPatch.Transpiler, true);
        MethodInfo capture = AccessTools.Method(typeof(Projectile), "CaptureDestroy");
        var captureCode = PatchProcessor.GetOriginalInstructions(capture, PatchProcessor.CreateILGenerator(capture));
        Check(captureCode.Any(i => Called(i, typeof(AutoDestroy), "ScheduleDestroy") && i.opcode == OpCodes.Call),
            "Capture no longer bypasses projectile explosion override");
        Check(!captureCode.Any(i => Called(i, typeof(Projectile), "ScheduleDestroy")), "Capture calls ordinary explosion path");
        Check(captureCode.Any(i => Called(i, typeof(Projectile), "NotifyNetDespawn")), "Capture lacks native despawn");
        Check(AccessTools.Field(typeof(CapturedProjectile), "capturedDamagePercentage")?.FieldType == typeof(float),
            "Captured percentage ABI changed");
        Check(AccessTools.Field(typeof(GravityCannon), "currentShotBonusDamage")?.FieldType == typeof(float),
            "Captured cannon budget ABI changed");
        PacketValuation();
        Codec();
        MethodInfo init = AccessTools.DeclaredMethod(typeof(Projectile), "Init");
        Check(init != null && init.GetParameters().Length == 7, "Native spawn Init boundary changed");
        MethodInfo shoot = AccessTools.DeclaredMethod(typeof(Launcher), "ShootProjectile");
        Check(shoot != null, "Native launcher completion boundary missing");
        var shootCode = PatchProcessor.GetOriginalInstructions(shoot, PatchProcessor.CreateILGenerator(shoot));
        int initAt = shootCode.FindIndex(i => Called(i, typeof(Projectile), "Init"));
        int addAt = shootCode.FindIndex(i => Called(i, typeof(Launcher), "AddProjectile"));
        int netAt = shootCode.FindIndex(i => Called(i, typeof(NetSession), "RegisterNetProjectile"));
        Check(initAt >= 0 && addAt > initAt && netAt > addAt,
            "Native spawn ordering changed: re-audit deferred capture guard");
    }
    private static bool Called(CodeInstruction i, Type type, string name)
    {
        MethodInfo method = i.operand as MethodInfo;
        return method != null && method.DeclaringType == type && method.Name == name;
    }
    private static void CheckDamage(bool direct)
    {
        MethodBase target = direct ? CoreIncomingDirectDamagePatch.TargetMethod() : CoreIncomingDamagePacketPatch.TargetMethod();
        Check(target != null && target.DeclaringType == typeof(GameShip), "Wrong native application boundary");
        if (!direct) Check(target.GetParameters()[3].ParameterType == typeof(int), "Obsolete boolean crit ABI");
        ILGenerator generator = PatchProcessor.CreateILGenerator(target);
        var before = PatchProcessor.GetOriginalInstructions(target, generator);
        var after = new List<CodeInstruction>(CoreIncomingDamage.InsertVeto(before, generator, direct));
        int gate = after.FindIndex(i => Called(i, typeof(CoreIncomingDamage), direct ? "TryBlockDirect" : "TryBlockPacket"));
        Check(gate >= 0 && after[gate + 1].opcode == OpCodes.Brfalse &&
            after[gate + 2].opcode == OpCodes.Ldc_I4_0 && after[gate + 3].opcode == OpCodes.Ret,
            "Veto must return false from the WHOLE GameShip application");
        int native = after.FindIndex(i => Called(i, typeof(Damageable), direct ? "DirectDamage" : "Damage"));
        Check(native > gate && after[native].opcode == OpCodes.Call, "Lost native base continuation");
        int regionDepth = 0;
        for (int i = 0; i < gate; i++) foreach (ExceptionBlock block in after[i].blocks)
        {
            if (block.blockType == ExceptionBlockType.BeginExceptionBlock) regionDepth++;
            if (block.blockType == ExceptionBlockType.EndExceptionBlock) regionDepth--;
        }
        Check(regionDepth == 0, "Early return would bypass an active native exception region");
        MustThrow(() => CoreIncomingDamage.InsertVeto(new CodeInstruction[0], generator, direct).ToList(),
            "Missing native boundary accepted");
    }
    private static void CheckTail(Type type, string name,
        Func<IEnumerable<CodeInstruction>, ILGenerator, IEnumerable<CodeInstruction>> adapt, bool returnsValue)
    {
        MethodInfo target = AccessTools.DeclaredMethod(type, name);
        ILGenerator generator = PatchProcessor.CreateILGenerator(target);
        var before = PatchProcessor.GetOriginalInstructions(target, generator);
        var after = adapt(before, generator).ToList();
        int gate = after.FindIndex(i => Called(i, typeof(CoreDamageApplicationObservation), "WatchedApplicationBlocked"));
        Check(gate > 0 && after[gate + 1].opcode == OpCodes.Brfalse && after[gate + 2].opcode == OpCodes.Ret,
            "Missing exact native received-hit-tail gate");
        var previous = after[gate - 1].operand as MethodInfo;
        Check(previous != null && previous.ReturnType == (returnsValue ? typeof(bool) : typeof(void)),
            "Tail gate return-stack contract changed");
        Check(after.Count == before.Count + 3, "Unexpected tail transformation");
    }
    private static void PacketValuation()
    {
        float value, spent;
        var packet = new[] { new Damageable.DamageData { damage = 40 },
            new Damageable.DamageData { damage = 100 } };
        Check(OrreryAccretionDisk.TryGetPacketValue(packet, out value) && value == 140f,
            "Mixed components must be totaled, not first damage only");
        var pool = new OrreryAccretionReservoir(); pool.Begin(1, 12, 10);
        Check(pool.TryAbsorb(value, 0, out spent) && spent == 12f,
            "Whole mixed final application blocked, heal from 12 only");
        Check(packet[0].damage == 40 && packet[1].damage == 100,
            "Valuation cannot mutate a packet shared with other targets");
        packet[1].damage = -10;
        Check(OrreryAccretionDisk.TryGetPacketValue(packet, out value) && value == 40,
            "Negative adjustment cannot mint free capacity");
        packet[1].damage = float.NaN;
        Check(!OrreryAccretionDisk.TryGetPacketValue(packet, out value), "NaN component rejected");
        packet[0].damage = packet[1].damage = float.MaxValue;
        Check(OrreryAccretionDisk.TryGetPacketValue(packet, out value) && value == float.MaxValue,
            "Large finite packet saturates rather than bypassing final-hit protection");
        Check(!OrreryAccretionDisk.TryGetPacketValue(null, out value), "Null packet rejected");
    }
    private static void Codec()
    {
        var s = new OrreryAccretionDiskPresentationLease.Snapshot { Active = true, Sample = 1u,
            RemainingSeconds = 4f, DurationSeconds = 8f, RadiusWorld = 5f, Capacity = 128 };
        byte[] expected = { 1, 1,0,0,0, 0,0,128,64, 0,0,0,65, 0,0,160,64, 128 };
        byte[] bytes = new byte[32]; int length;
        CoreWireFormat<OrreryAccretionDiskPresentationLease.Snapshot> format = OrreryAccretionDiskPresentationLease.Wire;
        Check(CoreWire.TryEncode(bytes, 0, bytes.Length, format, ref s, out length), "Snapshot encode");
        Check(length == 18, "Active payload must be 18 bytes");
        for (int i = 0; i < expected.Length; i++) Check(bytes[i] == expected[i], "Independent golden byte " + i);
        var decoded = default(OrreryAccretionDiskPresentationLease.Snapshot);
        Check(CoreWire.TryDecode(bytes, 0, length, format, ref decoded) && decoded.Capacity == 128, "Snapshot roundtrip");
        for (int n = 0; n < length; n++)
        {
            decoded.Sample = 77;
            Check(!CoreWire.TryDecode(bytes, 0, n, format, ref decoded) && decoded.Sample == 77, "Atomic rejection of truncation " + n);
        }
        Check(!CoreWire.TryDecode(bytes, 0, length + 1, format, ref decoded), "Trailing payload accepted");
        bytes[0] = 3;
        Check(!CoreWire.TryDecode(bytes, 0, length, format, ref decoded), "Unknown flags accepted");
        s.RadiusWorld = float.NaN;
        Check(!CoreWire.TryEncode(bytes, 0, bytes.Length, format, ref s, out length), "NaN radius accepted");
        s.RadiusWorld = 5; s.RemainingSeconds = 9;
        Check(!CoreWire.TryEncode(bytes, 0, bytes.Length, format, ref s, out length), "Remaining > duration accepted");
        s.Active = false;
        Check(CoreWire.TryEncode(bytes, 0, bytes.Length, format, ref s, out length) && length == 5, "Terminal size");
        Check(CoreWire.TryDecode(bytes, 0, length, format, ref decoded) && !decoded.Active, "Terminal decode");
        s.Sample = 0;
        Check(!CoreWire.TryEncode(bytes, 0, bytes.Length, format, ref s, out length), "Zero sample accepted");
    }
    private static void MustThrow(Action operation, string why)
    {
        bool threw = false;
        try { operation(); } catch (InvalidOperationException) { threw = true; }
        Check(threw, why);
    }
}
#endif
