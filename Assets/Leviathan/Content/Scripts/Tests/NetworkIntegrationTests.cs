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
        Cryo();
        Tesla();
        Shatterbolt();
        Plasma();
        Bounds();
        Budget();
        Framing();
        CombatTrailers();
        PublishFailure();
        Registration();
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

    private static void Cryo()
    {
        // Expected bytes derived from the pre-conversion writer, not from the
        // new one: WritePosition(origin) then WriteFloat(aim), little-endian.
        byte[] golden = { 0,0,128,63, 0,0,0,192, 0,0,180,66 };
        CoreWireFormat<OrreryLegacySpellPresentation.CryoWireState> format =
            OrreryLegacySpellPresentation.WireCryo;

        var state = new OrreryLegacySpellPresentation.CryoWireState {
            Origin = new Vector2(1, -2), AimDegrees = 90 };
        byte[] buffer = new byte[32]; int length;
        Assert(CoreWire.TryEncode(buffer, 0, buffer.Length, format, ref state, out length), "Cryo encode");
        Assert(length == golden.Length, "Cryo length");
        for (int i = 0; i < length; i++) Assert(buffer[i] == golden[i], "Cryo golden byte " + i);

        var decoded = default(OrreryLegacySpellPresentation.CryoWireState);
        Assert(CoreWire.TryDecode(buffer, 0, length, format, ref decoded), "Cryo decode");
        Assert(decoded.AimDegrees == 90 && decoded.Origin.y == -2, "Cryo values");
        for (int cut = 0; cut < length; cut++)
        {
            decoded.Generation = 77;
            Assert(!CoreWire.TryDecode(buffer, 0, cut, format, ref decoded), "Cryo truncated accepted");
            Assert(decoded.Generation == 77, "Rejected decode changed live state");
        }
        Assert(!CoreWire.TryDecode(buffer, 0, length + 1, format, ref decoded), "Cryo trailing bytes");

        var nonFinite = new OrreryLegacySpellPresentation.CryoWireState {
            Origin = new Vector2(float.NaN, 0) };
        Assert(!CoreWire.TryEncode(buffer, 0, buffer.Length, format, ref nonFinite, out length), "Cryo NaN published");
    }

    private static void Tesla()
    {
        // Pre-conversion writer: (byte)segmentCount, WriteFloat(width), then
        // WritePosition(start) and WritePosition(end) per segment.
        byte[][] golden = {
            null,
            new byte[]{ 1, 0,0,0,64, 0,0,64,64, 0,0,128,64, 0,0,160,64, 0,0,192,64 },
            new byte[]{ 2, 0,0,0,64, 0,0,64,64, 0,0,128,64, 0,0,160,64, 0,0,192,64,
                           0,0,224,64, 0,0,0,65, 0,0,16,65, 0,0,32,65 },
            new byte[]{ 3, 0,0,0,64, 0,0,64,64, 0,0,128,64, 0,0,160,64, 0,0,192,64,
                           0,0,224,64, 0,0,0,65, 0,0,16,65, 0,0,32,65,
                           0,0,48,65, 0,0,64,65, 0,0,80,65, 0,0,96,65 }
        };
        CoreWireFormat<OrreryLegacySpellPresentation.TeslaWireState> format =
            OrreryLegacySpellPresentation.WireTesla;
        Vector2[] starts = { new Vector2(3, 4), new Vector2(7, 8), new Vector2(11, 12) };
        Vector2[] ends = { new Vector2(5, 6), new Vector2(9, 10), new Vector2(13, 14) };

        for (int segments = 1; segments <= OrreryLegacySpellPresentation.Tuning.MaxTeslaSegments; segments++)
        {
            var state = default(OrreryLegacySpellPresentation.TeslaWireState);
            state.SegmentCount = segments;
            state.Width = 2f;
            for (int i = 0; i < segments; i++) state.SetSegment(i, starts[i], ends[i]);

            byte[] buffer = new byte[128]; int length;
            Assert(CoreWire.TryEncode(buffer, 0, buffer.Length, format, ref state, out length), "Tesla encode");
            Assert(length == golden[segments].Length, "Tesla length");
            for (int i = 0; i < length; i++) Assert(buffer[i] == golden[segments][i], "Tesla golden byte " + i);

            var decoded = default(OrreryLegacySpellPresentation.TeslaWireState);
            Assert(CoreWire.TryDecode(buffer, 0, length, format, ref decoded), "Tesla decode");
            Assert(decoded.SegmentCount == segments && decoded.Width == 2f, "Tesla values");
            for (int i = 0; i < segments; i++)
                Assert(decoded.GetStart(i).x == starts[i].x && decoded.GetEnd(i).y == ends[i].y, "Tesla segment " + i);

            // A partial group must never be applied: the record bank can deliver
            // a leader without its continuation when the budget drops a record.
            for (int cut = 0; cut < length; cut++)
            {
                decoded.Generation = 99;
                Assert(!CoreWire.TryDecode(buffer, 0, cut, format, ref decoded), "Tesla truncated accepted");
                Assert(decoded.Generation == 99, "Rejected decode changed live state");
            }
            Assert(!CoreWire.TryDecode(buffer, 0, length + 1, format, ref decoded), "Tesla trailing bytes");
        }

        byte[] scratch = new byte[128]; int written;
        foreach (int bad in new[] { 0, OrreryLegacySpellPresentation.Tuning.MaxTeslaSegments + 1, 255 })
        {
            var state = default(OrreryLegacySpellPresentation.TeslaWireState);
            state.SegmentCount = bad; state.Width = 1f;
            Assert(!CoreWire.TryEncode(scratch, 0, scratch.Length, format, ref state, out written),
                "Tesla published segment count " + bad);

            var decoded = default(OrreryLegacySpellPresentation.TeslaWireState);
            byte[] hostile = new byte[53];
            hostile[0] = (byte)bad; hostile[4] = 64; // width 2f
            Assert(!CoreWire.TryDecode(hostile, 0, hostile.Length, format, ref decoded),
                "Tesla accepted segment count " + bad);
        }

        var zeroWidth = default(OrreryLegacySpellPresentation.TeslaWireState);
        zeroWidth.SegmentCount = 1;
        zeroWidth.Width = 0f;
        Assert(!CoreWire.TryEncode(scratch, 0, scratch.Length, format, ref zeroWidth, out written),
            "Tesla published zero width");
    }

    private static void Shatterbolt()
    {
        // Expected bytes rebuilt from the pre-conversion writer: flag byte with
        // orb in bit 0, impact count byte, WriteFloat(radius), optional orb
        // position, then one position per impact. Every orb/count shape is
        // checked, so a layout change cannot pass by agreeing with itself.
        CoreWireFormat<OrreryShatterboltPresentationCodec.ShatterboltWireState> format =
            OrreryShatterboltPresentationCodec.WireShatterbolt;

        foreach (bool orb in new[] { false, true })
        for (int impacts = 0; impacts <= OrrerySpellCompendium.Shatterbolt.MaximumImpacts; impacts++)
        {
            var state = default(OrreryShatterboltPresentationCodec.ShatterboltWireState);
            state.OrbActive = orb;
            state.ImpactCount = (byte)impacts;
            state.ExplosionRadiusMeters = 2.5f;
            state.OrbPosition = new Vector2(100f, -50f);
            for (int i = 0; i < impacts; i++) state.SetImpact(i, new Vector2(i + 1, -(i + 1)));

            byte[] expected = LegacyShatterboltBytes(orb, impacts);
            byte[] buffer = new byte[128]; int length;
            Assert(CoreWire.TryEncode(buffer, 0, buffer.Length, format, ref state, out length), "Shatterbolt encode");
            Assert(length == expected.Length, "Shatterbolt length");
            for (int i = 0; i < length; i++) Assert(buffer[i] == expected[i], "Shatterbolt golden byte " + i);

            var decoded = default(OrreryShatterboltPresentationCodec.ShatterboltWireState);
            Assert(CoreWire.TryDecode(buffer, 0, length, format, ref decoded), "Shatterbolt decode");
            Assert(decoded.OrbActive == orb && decoded.ImpactCount == impacts, "Shatterbolt header");
            for (int i = 0; i < impacts; i++)
                Assert(decoded.GetImpact(i).x == i + 1, "Shatterbolt impact " + i);

            // A dropped continuation record must never half-apply.
            for (int cut = 0; cut < length; cut++)
            {
                var live = default(OrreryShatterboltPresentationCodec.ShatterboltWireState);
                live.ExplosionRadiusMeters = 999f;
                Assert(!CoreWire.TryDecode(buffer, 0, cut, format, ref live), "Shatterbolt truncated accepted");
                Assert(live.ExplosionRadiusMeters == 999f, "Rejected decode changed live state");
            }
            Assert(!CoreWire.TryDecode(buffer, 0, length + 1, format, ref decoded), "Shatterbolt trailing bytes");
        }

        byte[] scratch = new byte[128]; int written;
        var overCount = default(OrreryShatterboltPresentationCodec.ShatterboltWireState);
        overCount.ImpactCount = (byte)(OrrerySpellCompendium.Shatterbolt.MaximumImpacts + 1);
        overCount.ExplosionRadiusMeters = 1f;
        Assert(!CoreWire.TryEncode(scratch, 0, scratch.Length, format, ref overCount, out written),
            "Shatterbolt published impact count over the bound");

        var hostile = default(OrreryShatterboltPresentationCodec.ShatterboltWireState);
        byte[] spareFlag = { 2, 0, 0, 0, 32, 64 };
        Assert(!CoreWire.TryDecode(spareFlag, 0, spareFlag.Length, format, ref hostile),
            "Shatterbolt accepted an unknown flag bit");

        var zeroRadius = default(OrreryShatterboltPresentationCodec.ShatterboltWireState);
        zeroRadius.ExplosionRadiusMeters = 0f;
        Assert(!CoreWire.TryEncode(scratch, 0, scratch.Length, format, ref zeroRadius, out written),
            "Shatterbolt published zero radius");
    }

    private static byte[] LegacyShatterboltBytes(bool orb, int impacts)
    {
        var bytes = new System.Collections.Generic.List<byte>();
        bytes.Add((byte)(orb ? 1 : 0));
        bytes.Add((byte)impacts);
        bytes.AddRange(BitConverter.GetBytes(2.5f));
        if (orb)
        {
            bytes.AddRange(BitConverter.GetBytes(100f));
            bytes.AddRange(BitConverter.GetBytes(-50f));
        }
        for (int i = 0; i < impacts; i++)
        {
            bytes.AddRange(BitConverter.GetBytes((float)(i + 1)));
            bytes.AddRange(BitConverter.GetBytes((float)-(i + 1)));
        }
        return bytes.ToArray();
    }

    private static void Plasma()
    {
        var stroke = new OrreryPlasmaBoltPresentation.StrokeState {
            Start = new Vector2(1, -2), End = new Vector2(3, 4), Width = 5 };
        byte[] bytes = new byte[64]; int length;
        var strokeFormat = (CoreWireFormat<OrreryPlasmaBoltPresentation.StrokeState>)OrreryPlasmaBoltPresentation.WireStroke;
        byte[] expectedStroke = { 0,0,128,63, 0,0,0,192, 0,0,64,64, 0,0,128,64, 0,0,160,64 };
        Assert(CoreWire.TryEncode(bytes, 0, bytes.Length, strokeFormat, ref stroke, out length), "Plasma stroke encode");
        Assert(length == 20, "Plasma stroke preserves length");
        for (int i = 0; i < length; i++) Assert(bytes[i] == expectedStroke[i], "Plasma stroke golden");
        for (int cut = 0; cut < length; cut++)
        {
            var live = stroke;
            Assert(!CoreWire.TryDecode(bytes, 0, cut, strokeFormat, ref live) && live.Width == 5, "Plasma stroke cut transactional");
        }
        stroke.Width = float.NaN;
        Assert(!CoreWire.TryEncode(bytes, 0, bytes.Length, strokeFormat, ref stroke, out length), "Plasma rejects NaN width");

        var format = (CoreWireFormat<OrreryPlasmaBoltPresentation.RefreshState>)OrreryPlasmaBoltPresentation.WireRefresh;
        for (int count = 1; count <= 6; count++)
        {
            var state = default(OrreryPlasmaBoltPresentation.RefreshState);
            var expected = new System.Collections.Generic.List<byte> { (byte)count };
            for (int i = 0; i < count; i++)
            {
                uint target = (uint)(0x10203040 + i);
                byte remaining = (byte)(160 + i); // Immolation must exceed the old 5s cap.
                state.Add(target, remaining);
                expected.Add((byte)target); expected.Add((byte)(target >> 8));
                expected.Add((byte)(target >> 16)); expected.Add((byte)(target >> 24));
                expected.Add(remaining);
            }
            Assert(CoreWire.TryEncode(bytes, 0, bytes.Length, format, ref state, out length), "Plasma refresh encode");
            Assert(length == 1 + count * 5, "Plasma refresh length");
            for (int i = 0; i < length; i++) Assert(bytes[i] == expected[i], "Plasma refresh golden");
            var decoded = default(OrreryPlasmaBoltPresentation.RefreshState);
            Assert(CoreWire.TryDecode(bytes, 0, length, format, ref decoded), "Plasma refresh decode");
            Assert(decoded.Count == count && decoded.Get(count - 1).Remaining == 159 + count, "Plasma full duration roundtrip");
            for (int cut = 0; cut < length; cut++)
            {
                var live = state;
                Assert(!CoreWire.TryDecode(bytes, 0, cut, format, ref live) && live.E0.Target == state.E0.Target,
                    "Plasma refresh cut transactional");
            }
            Assert(!CoreWire.TryDecode(bytes, 0, length + 1, format, ref decoded), "Plasma refresh trailing bytes");
            bytes[1] = bytes[2] = bytes[3] = bytes[4] = 0;
            Assert(!CoreWire.TryDecode(bytes, 0, length, format, ref decoded), "Plasma zero target");
        }
        var invalid = new OrreryPlasmaBoltPresentation.RefreshState { Count = 7 };
        Assert(!CoreWire.TryEncode(bytes, 0, bytes.Length, format, ref invalid, out length), "Plasma excessive count");
        invalid.Count = 1; invalid.E0.Target = 1;
        Assert(!CoreWire.TryEncode(bytes, 0, bytes.Length, format, ref invalid, out length), "Plasma zero remaining");
        var fire = new OrreryFocusProfile.Resolved { SpellDamageBonus = 0.25f };
        Assert(OrreryPlasmaBolt.ResolveDotBudget(fire, 100, 2) == 250, "Fire scales confirmed seed once");
        Assert(OrreryPlasmaBolt.ResolveDotBudget(default(OrreryFocusProfile.Resolved), 100, 2) == 200, "No-focus has no donor bonus");
        Assert(OrreryPlasmaBolt.ResolveDotBudget(fire, 100, 0) == 0, "Disabled DOT budget");
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
        // Exercise production slot grouping and packet selection together.
        int[] sizes = { 16, 32, 32, 12 };
        for (int i = 0; i < sizes.Length; i++)
        {
            var slot = CoreNetwork.BeginSlot((byte)(200 + i));
            for (int j = 0; j < sizes[i]; j++) slot.Byte(1);
            CoreNetwork.EndSlot(slot);
        }
        CoreNetwork.GroupLocalSlots(201, 2);
        CoreNetwork.GroupLocalSlots(203, 1);
        object[] build = { false, 34, (byte)0 };
        Assert((int)Call("BuildPayload", build) == 34, "Production budget size");
        byte[] selected = (byte[])Get("scratch");
        Assert(selected[1] == 2 && selected[2] == 200 && selected[20] == 203,
            "Production group selection dropped essential or split group");
        CoreNetwork.ClearLocalSlots();
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

    private static void Registration()
    {
        CoreNetwork.RegisterDefaultSlots();
        OrreryNetwork.Initialize();
        var entries = (IList)typeof(CoreNetworkPresentation).GetField("entries", Private).GetValue(null);
        int entryCount = entries.Count;
        CoreNetwork.RegisterDefaultSlots();
        OrreryNetwork.Initialize();
        Assert(entries.Count == entryCount, "Repeated initialization duplicated callbacks");
        Assert(entryCount == 9, "Unexpected presentation composition (includes Accretion)");
        var slots = (IDictionary)Get("slots");
        for (byte i = 1; i <= 14; i++) Assert(slots.Contains(i), "Missing registered mod slot " + i);
    }
}
#endif
