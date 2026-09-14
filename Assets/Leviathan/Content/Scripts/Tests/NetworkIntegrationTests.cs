#if CORE_NETWORK_TESTS
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using StarVortex;
using UnityEngine;
using Console = System.Console;

// Standalone process only: these tests deliberately seed private transport
// state. Never run them in a live Unity world. See NETWORKING.md for the runner.
public static class NetworkIntegrationTests
{
    private static int checks;
    private static readonly BindingFlags Private = BindingFlags.Static | BindingFlags.NonPublic;
    public static int Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            foreach (string dir in Environment.GetEnvironmentVariable("CORE_NETWORK_TEST_ASSEMBLIES").Split(';'))
            {
                string path = Path.Combine(dir, new AssemblyName(args.Name).Name + ".dll");
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };
        try { Run(); Console.WriteLine("Network integration checks passed: " + checks); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run()
    {
        Magma();
        Bounds();
        Budget();
        Framing();
        CombatTrailers();
        PublishFailure();
    }

    private static void Assert(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }

    private static void Magma()
    {
        // Independent expected bytes for all conditional layouts, matching
        // Sol's original little-endian float format (including the idle shape).
        byte[][] golden = {
            new byte[] { 0 },
            new byte[] { 1, 0,0,128,63, 0,0,0,192, 0,0,180,66 },
            new byte[] { 2, 0,0,64,64, 0,0,128,64, 0,0,160,64 },
            new byte[] { 3, 0,0,128,63, 0,0,0,192, 0,0,180,66,
                0,0,64,64, 0,0,128,64, 0,0,160,64 }
        };
        CoreWireFormat<OrreryLegacySpellPresentation.MagmaWireState> format = OrreryLegacySpellPresentation.WireMagma;
        for (int flags = 0; flags < 4; flags++)
        {
            var state = new OrreryLegacySpellPresentation.MagmaWireState {
                ProjectilePresent = (flags & 1) != 0, ExplosionPresent = (flags & 2) != 0,
                ProjectilePosition = new Vector2(1, -2), ProjectileAngleDegrees = 90,
                ExplosionPosition = new Vector2(3, 4), ExplosionRadiusWorld = 5
            };
            byte[] buffer = new byte[32]; int length;
            Assert(CoreWire.TryEncode(buffer, 0, buffer.Length, format, ref state, out length), "Magma encode");
            Assert(length == golden[flags].Length, "Magma length");
            for (int i = 0; i < length; i++) Assert(buffer[i] == golden[flags][i], "Magma golden byte " + i);
            var decoded = default(OrreryLegacySpellPresentation.MagmaWireState);
            Assert(CoreWire.TryDecode(buffer, 0, length, format, ref decoded), "Magma decode");
            Assert(decoded.ProjectilePresent == state.ProjectilePresent && decoded.ExplosionPresent == state.ExplosionPresent, "Magma flags");
            for (int cut = 0; cut < length; cut++)
            {
                decoded.Generation = 123;
                Assert(!CoreWire.TryDecode(buffer, 0, cut, format, ref decoded), "Magma truncated accepted");
                Assert(decoded.Generation == 123, "Rejected decode changed live state");
            }
            Assert(!CoreWire.TryDecode(buffer, 0, length + 1, format, ref decoded), "Magma trailing bytes");
        }
        var live = default(OrreryLegacySpellPresentation.MagmaWireState);
        Assert(!CoreWire.TryDecode(new byte[] { 4 }, 0, 1, format, ref live), "Unknown flags");
    }

    private static void Bounds()
    {
        byte value = 1;
        var w = CoreWire.Write(new byte[1], int.MaxValue, 1); w.Byte(ref value);
        Assert(!w.Ok, "Write overflow");
        var r = CoreWire.Read(new byte[1], int.MaxValue, 1); r.Byte(ref value);
        Assert(!r.Ok, "Read overflow");
        foreach (int length in new[] { 255, 256, 384 })
        {
            byte[] bytes = new byte[2]; ushort sent = (ushort)length;
            w = CoreWire.Write(bytes, 0, 2); w.UInt16(ref sent);
            ushort got = 0; r = CoreWire.Read(bytes, 0, 2); r.UInt16(ref got);
            Assert(r.Ok && r.AtEnd && got == length, "Wide length");
        }
    }

    private static void Budget()
    {
        int[] lengths = { 16, 32, 32, 12 };
        byte[] groups = { 0, 7, 7, 9 };
        bool[] selected = new bool[4];
        for (int budget = 0; budget <= 105; budget++)
        {
            int size = CoreNetworkBudget.Select(4, lengths, groups, budget, selected);
            Assert(selected[1] == selected[2], "Split multipart group");
            Assert(size <= budget, "Exceeded budget");
            if (size >= 0) Assert(selected[0], "Dropped essential state");
        }
        Assert(CoreNetworkBudget.Select(4, lengths, groups, 34, selected) == 34 &&
            selected[0] && !selected[1] && selected[3], "Smaller group must fit after large group omitted");
    }

    private static object Get(string field) { return typeof(CoreNetwork).GetField(field, Private).GetValue(null); }
    private static void Set(string field, object value) { typeof(CoreNetwork).GetField(field, Private).SetValue(null, value); }
    private static object Call(string method, params object[] args) { return typeof(CoreNetwork).GetMethod(method, Private).Invoke(null, args); }

    private static void Framing()
    {
        typeof(CoreClassRuntime).GetField("initialized", Private).SetValue(null, true);
        // Actual production parser: payloads on both sides of the byte boundary.
        for (int count = 1; count <= 11; count++)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new byte[] { 0x4c, 0x56, 3, 1 });
                writer.Write((ushort)(2 + count * 34));
                writer.Write((byte)0); writer.Write((byte)count);
                for (byte i = 0; i < count; i++)
                {
                    byte id = (byte)(200 + i);
                    if (count == i + 1) CoreNetwork.RegisterSlot(id, CoreClassId.None, "test");
                    writer.Write(id); writer.Write((byte)32); writer.Write(new byte[32]);
                }
                byte[] packet = stream.ToArray();
                stream.Position = 0;
                CoreNetwork.ParseIncomingExtension(new BinaryReader(stream));
                Assert((bool)Get("pendingValid") && (int)Get("pendingSlotCount") == count, "Production header reader");
                for (int cut = 0; cut < packet.Length; cut++)
                {
                    using (var truncated = new MemoryStream(packet, 0, cut))
                        CoreNetwork.ParseIncomingExtension(new BinaryReader(truncated));
                    Assert(!(bool)Get("pendingValid"), "Truncated packet accepted");
                }
            }
        }
        // Seed a legal 512-node schema without accessing a Unity world. This
        // makes the actual production writer emit a 265-byte spec payload.
        object schema = Call("GetSchema", CoreClassId.None);
        FieldInfo entries = schema.GetType().GetField("Entries");
        Array nodes = Array.CreateInstance(entries.FieldType.GetElementType(), 512);
        entries.SetValue(schema, nodes);
        Set("schema", nodes); Set("localSpecClass", CoreClassId.None);
        Set("localSpecPacked", new byte[256]); Set("localSpecDirty", false);
        Set("lastSeenConfigurationRevision", CoreSpecializationRuntime.ConfigurationRevision);
        Set("lastSeenRegistryRevision", CoreSpecializationRegistry.Revision);
        Set("specBurstRemaining", 1);
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((byte)0xab);
            CoreNetwork.AppendLocalExtension(writer);
            byte[] bytes = stream.ToArray();
            Assert(bytes.Length == 274, "Production writer total length: " + bytes.Length);
            Assert(bytes[1] == 0x4c && bytes[2] == 0x56 && bytes[5] == 11 && bytes[6] == 1, "Production writer ushort 267");
            stream.Position = 1;
            CoreNetwork.ParseIncomingExtension(new BinaryReader(stream));
            Assert((bool)Get("pendingValid") && (bool)Get("pendingHasDynamic"), "Production ship-state round trip");
        }
    }

    private static void CombatTrailers()
    {
        foreach (bool result in new[] { false, true })
        {
            Type messageType = result ? typeof(MsgDamageResult) : typeof(MsgDamageEvent);
            object message = FormatterServices.GetUninitializedObject(messageType);
            IDictionary dictionary = (IDictionary)Get(result ? "combatResultMetadata" : "combatEventMetadata");
            Type metadataType = dictionary.GetType().GetGenericArguments()[1];
            object metadata = Activator.CreateInstance(metadataType);
            metadataType.GetField("EventId").SetValue(metadata, (uint)0x12345678);
            dictionary.Add(message, metadata);
            NetSerializer serializer = (NetSerializer)FormatterServices.GetUninitializedObject(typeof(NetSerializer));
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                typeof(NetSerializer).GetField("writer").SetValue(serializer, writer);
                writer.Write((byte)0xab);
                Call(result ? "AppendCombatResultTrailer" : "AppendCombatEventTrailer", serializer, message);
                byte[] bytes = stream.ToArray();
                int payloadLength = result ? 5 : 7;
                Assert(bytes.Length == 1 + payloadLength + 7, "Production combat footer size");
                int footer = 1 + payloadLength;
                byte[] golden = { 0x4c, 0x56, 0x43, 0x54, 1, (byte)(result ? 2 : 1), (byte)payloadLength };
                for (int i = 0; i < golden.Length; i++) Assert(bytes[footer+i] == golden[i], "Combat footer golden");
                object[] args = { bytes, bytes.Length, (byte)(result ? 2 : 1), 0, 0 };
                Assert((bool)Call("TryLocateCombatTrailer", args) && (int)args[3] == 1 && (int)args[4] == payloadLength, "Production combat locator");
            }
        }
    }

    private static void PublishFailure()
    {
        CoreNetworkPresentation.Register("test/failure", publish: () => { throw new InvalidOperationException("injected"); });
        // Suppress Unity logging in the standalone harness; test exception
        // containment, not Unity's native logging implementation.
        var entries = (IList)typeof(CoreNetworkPresentation).GetField("entries", Private).GetValue(null);
        object entry = entries[entries.Count - 1]; entry.GetType().GetField("Warned").SetValue(entry, true);
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((byte)0xab);
            CoreNetwork.AppendLocalExtension(writer);
            Assert(stream.Length == 1 && stream.ToArray()[0] == 0xab, "Publisher corrupted native packet");
            Assert((int)Get("localSlotCount") == 0, "Publisher left stale slots");
        }
    }
}
#endif
