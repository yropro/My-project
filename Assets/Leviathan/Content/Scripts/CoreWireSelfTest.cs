#if UNITY_EDITOR
using UnityEngine;

/// <summary>
/// Round-trip tests for <see cref="CoreWire"/>.
///
/// Run from the Unity menu: Leviathan > Net > Run CoreWire Self Test.
///
/// ---------------------------------------------------------------------------
/// WHAT THIS IS FOR
/// ---------------------------------------------------------------------------
/// Two shipped bugs came from a wire format implemented twice and the two
/// copies disagreeing:
///
///   1. CoreNetwork wrote the ship-state extension length with
///      writer.Write((byte)payloadLength) while ParseIncomingExtension read it
///      with reader.ReadUInt16(). Every receiver rejected every packet.
///
///   2. The combat trailer went the other way: written as ushort, located with
///      CombatTrailerFooterBytes = 7 and read as a single byte at
///      buffer[footer + 6]. The trailer magic never matched.
///
/// Neither is detectable by compilation, by Harmony selector resolution, or by
/// any lifecycle harness. Both sides compile and resolve perfectly. The only
/// thing that catches this class of bug is encoding and then decoding.
///
/// Every test below is the same six lines: fill a struct, wire it out, wire it
/// back, compare. Adding a format means adding one case, not one test suite.
/// </summary>
public static class CoreWireSelfTest
{
    private static int checks;
    private static int failures;

    [UnityEditor.MenuItem("Leviathan/Net/Run CoreWire Self Test")]
    public static void Run()
    {
        checks = 0;
        failures = 0;

        RoundTripAllFlagCombinations();
        RejectsTruncatedPayload();
        RejectsTrailingBytes();
        RejectsNonFiniteFloat();
        RejectsNonPositiveRadius();
        RejectsUnknownFlagBit();
        MatchesGoldenBytes();
        SafeEntryPointsDoNotCommitOnFailure();
        ContainsThrowingFormat();
        FailedEncodeLeavesNoPartialPayload();
        LengthSurvivesAboveByteRange();
        WriteRefusesOverrun();

        if (failures == 0)
            Debug.Log("[CoreWire] self test passed (" + checks + " checks).");
        else
            Debug.LogError("[CoreWire] SELF TEST FAILED: " + failures +
                           " of " + checks + " checks.");
    }

    // =========================================================================
    // THE SHAPE UNDER TEST
    // =========================================================================
    // Deliberately modelled on the Magma Cannon payload, because it is the
    // hardest shape: a conditional layout whose branch conditions are
    // themselves wired fields. On the read pass Flags() populates the two
    // booleans before the if-statements evaluate them, which is what makes a
    // single declaration work in both directions.

    private struct SampleState
    {
        public bool ProjectilePresent;
        public bool ExplosionPresent;
        public Vector2 ProjectilePosition;
        public float ProjectileAngleDegrees;
        public Vector2 ExplosionPosition;
        public float ExplosionRadiusWorld;

        public bool Matches(SampleState other)
        {
            if (ProjectilePresent != other.ProjectilePresent) return false;
            if (ExplosionPresent != other.ExplosionPresent) return false;
            if (ProjectilePresent)
            {
                if (ProjectilePosition != other.ProjectilePosition) return false;
                if (ProjectileAngleDegrees != other.ProjectileAngleDegrees) return false;
            }
            if (ExplosionPresent)
            {
                if (ExplosionPosition != other.ExplosionPosition) return false;
                if (ExplosionRadiusWorld != other.ExplosionRadiusWorld) return false;
            }
            return true;
        }
    }

    /// <summary>Stored once; no per-frame allocation.</summary>
    private static readonly CoreWireFormat<SampleState> Format = Wire;

    private static readonly CoreWireFormat<SampleState> ThrowingFormat = WireThrows;

    private static void WireThrows(ref CoreWire w, ref SampleState s)
    {
        w.Flags(ref s.ProjectilePresent, ref s.ExplosionPresent);
        throw new System.InvalidOperationException("deliberate test throw");
    }

    /// <summary>The entire format. One declaration, both directions.</summary>
    private static void Wire(ref CoreWire w, ref SampleState s)
    {
        w.Flags(ref s.ProjectilePresent, ref s.ExplosionPresent);
        if (s.ProjectilePresent)
        {
            w.Position(ref s.ProjectilePosition);
            w.Float(ref s.ProjectileAngleDegrees);
        }
        if (s.ExplosionPresent)
        {
            w.Position(ref s.ExplosionPosition);
            w.Positive(ref s.ExplosionRadiusWorld);
        }
    }

    // =========================================================================
    // CASES
    // =========================================================================


    /// <summary>
    /// A round trip alone proves the two directions agree with EACH OTHER, not
    /// that either agrees with what shipped. Change both sides the same wrong
    /// way and it still passes green while every peer on the old build breaks.
    ///
    /// These vectors pin the actual bytes. If a layout edit is intentional the
    /// vector is updated in the same commit, which makes the wire change
    /// visible in review instead of silent.
    /// </summary>
    private static void MatchesGoldenBytes()
    {
        SampleState both = default(SampleState);
        both.ProjectilePresent = true;
        both.ExplosionPresent = true;
        both.ProjectilePosition = new Vector2(1.5f, -2.25f);
        both.ProjectileAngleDegrees = 90f;
        both.ExplosionPosition = new Vector2(0f, 8f);
        both.ExplosionRadiusWorld = 3f;

        byte[] expectedBoth =
        {
            0x03,
            0x00, 0x00, 0xC0, 0x3F,   // projectile x = 1.5
            0x00, 0x00, 0x10, 0xC0,   // projectile y = -2.25
            0x00, 0x00, 0xB4, 0x42,   // angle = 90
            0x00, 0x00, 0x00, 0x00,   // explosion x = 0
            0x00, 0x00, 0x00, 0x41,   // explosion y = 8
            0x00, 0x00, 0x40, 0x40    // radius = 3
        };
        ExpectBytes(ref both, expectedBoth, "both flags");

        SampleState projectileOnly = default(SampleState);
        projectileOnly.ProjectilePresent = true;
        projectileOnly.ProjectilePosition = new Vector2(1.5f, -2.25f);
        projectileOnly.ProjectileAngleDegrees = 90f;

        byte[] expectedProjectile =
        {
            0x01,
            0x00, 0x00, 0xC0, 0x3F,
            0x00, 0x00, 0x10, 0xC0,
            0x00, 0x00, 0xB4, 0x42
        };
        ExpectBytes(ref projectileOnly, expectedProjectile, "projectile only");

        // And the reverse direction: these exact bytes must still decode to
        // those values on a future build.
        SampleState decoded = default(SampleState);
        Expect(CoreWire.TryDecode(expectedBoth, 0, expectedBoth.Length,
                                  Format, ref decoded),
               "golden bytes decode");
        Expect(decoded.Matches(both), "golden bytes decode to original values");
    }

    private static void ExpectBytes(ref SampleState state, byte[] expected, string what)
    {
        byte[] buffer = new byte[64];
        int length;
        Expect(CoreWire.TryEncode(buffer, 0, buffer.Length, Format,
                                  ref state, out length),
               "encode ok, " + what);

        if (length != expected.Length)
        {
            Expect(false, "byte length " + length + " != " + expected.Length +
                          ", " + what);
            return;
        }
        for (int i = 0; i < expected.Length; i++)
        {
            if (buffer[i] == expected[i]) continue;
            Expect(false, "byte " + i + " is 0x" + buffer[i].ToString("X2") +
                          ", expected 0x" + expected[i].ToString("X2") +
                          ", " + what);
            return;
        }
        Expect(true, "bytes match, " + what);
    }

    /// <summary>
    /// A rejected payload must leave the caller's state untouched. Decoding
    /// straight into live state would half-apply a packet that then failed.
    /// </summary>
    private static void SafeEntryPointsDoNotCommitOnFailure()
    {
        SampleState live = default(SampleState);
        live.ProjectilePresent = true;
        live.ProjectilePosition = new Vector2(99f, 99f);
        live.ProjectileAngleDegrees = 12f;
        SampleState before = live;

        byte[] truncated = { 0x03, 0x00, 0x00, 0xC0, 0x3F };
        Expect(!CoreWire.TryDecode(truncated, 0, truncated.Length, Format, ref live),
               "truncated payload rejected by TryDecode");
        Expect(live.Matches(before), "live state untouched after rejection");

        byte[] trailing =
        {
            0x01,
            0x00, 0x00, 0xC0, 0x3F,
            0x00, 0x00, 0x10, 0xC0,
            0x00, 0x00, 0xB4, 0x42,
            0xFF, 0xFF
        };
        Expect(!CoreWire.TryDecode(trailing, 0, trailing.Length, Format, ref live),
               "trailing bytes rejected by TryDecode");
        Expect(live.Matches(before), "live state untouched after trailing-byte reject");
    }

    /// <summary>
    /// A format method is author-written code and can throw. On the receive
    /// path an escaping exception becomes a malformed-message strike against a
    /// peer under NetSession.HandleData, so the safe entry points contain it.
    ///
    /// This narrows the throw surface to the format method only. It does not
    /// make the whole receive path exception-proof - notably,
    /// OrreryPresentationNetwork.PublishForSend uses try/finally rather than
    /// try/catch, so throws from capture code that runs before any wire call
    /// still escape into native packet sending.
    /// </summary>
    private static void ContainsThrowingFormat()
    {
        Debug.Log("[CoreWire] (the two format-exception reports below are expected)");

        SampleState live = default(SampleState);
        live.ProjectilePresent = true;
        live.ProjectilePosition = new Vector2(7f, 7f);
        SampleState before = live;

        byte[] payload = { 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        bool decoded;
        try
        {
            decoded = CoreWire.TryDecode(payload, 0, payload.Length,
                                         ThrowingFormat, ref live);
        }
        catch (System.Exception)
        {
            Expect(false, "TryDecode let a format exception escape");
            return;
        }
        Expect(!decoded, "throwing format rejected on decode");
        Expect(live.Matches(before), "live state untouched after format throw");

        byte[] buffer = new byte[64];
        int length;
        bool encoded;
        try
        {
            encoded = CoreWire.TryEncode(buffer, 0, buffer.Length,
                                         ThrowingFormat, ref live, out length);
        }
        catch (System.Exception)
        {
            Expect(false, "TryEncode let a format exception escape");
            return;
        }
        Expect(!encoded && length == 0, "throwing format rejected on encode");
    }

    /// <summary>
    /// An encode cannot be staged in a temporary without a fixed scratch size,
    /// so a failure has usually already written part of the buffer. Callers
    /// must publish only on success; this checks that a caller who ignores that
    /// still cannot emit a half-formatted payload.
    /// </summary>
    private static void FailedEncodeLeavesNoPartialPayload()
    {
        SampleState sent = default(SampleState);
        sent.ProjectilePresent = true;
        sent.ExplosionPresent = true;
        sent.ProjectilePosition = new Vector2(1.5f, -2.25f);
        sent.ProjectileAngleDegrees = 90f;
        sent.ExplosionPosition = new Vector2(0f, 8f);
        sent.ExplosionRadiusWorld = 3f;

        byte[] tooSmall = new byte[16];   // needs 25
        int length;
        Expect(!CoreWire.TryEncode(tooSmall, 0, tooSmall.Length, Format,
                                   ref sent, out length),
               "undersized buffer refused");
        Expect(length == 0, "failed encode reports zero length");

        for (int i = 0; i < tooSmall.Length; i++)
        {
            if (tooSmall[i] == 0) continue;
            Expect(false, "byte " + i + " left non-zero after failed encode");
            return;
        }
        Expect(true, "failed encode left no partial payload");
    }

    private static void RoundTripAllFlagCombinations()
    {
        for (int mask = 0; mask < 4; mask++)
        {
            SampleState sent = default(SampleState);
            sent.ProjectilePresent = (mask & 1) != 0;
            sent.ExplosionPresent = (mask & 2) != 0;
            sent.ProjectilePosition = new Vector2(-1843.25f, 907.5f);
            sent.ProjectileAngleDegrees = 271.125f;
            sent.ExplosionPosition = new Vector2(12.75f, -0.5f);
            sent.ExplosionRadiusWorld = 6.25f;

            byte[] buffer = new byte[64];
            CoreWire writer = CoreWire.Write(buffer, 0, buffer.Length);
            Wire(ref writer, ref sent);
            Expect(writer.Ok, "write ok, mask " + mask);

            SampleState got = default(SampleState);
            CoreWire reader = CoreWire.Read(buffer, 0, writer.Length);
            Wire(ref reader, ref got);

            Expect(reader.Ok, "read ok, mask " + mask);
            Expect(reader.AtEnd, "consumed exactly, mask " + mask);
            Expect(got.Matches(sent), "values survived, mask " + mask);

            // The length the reader agrees on is not computed anywhere. It
            // falls out of running the same statements. This is the property
            // that a hand-written expectedLength constant cannot give you.
            int expected = 1 + (sent.ProjectilePresent ? 12 : 0) +
                               (sent.ExplosionPresent ? 12 : 0);
            Expect(writer.Length == expected,
                   "length " + writer.Length + " == " + expected + ", mask " + mask);
        }
    }

    private static void RejectsTruncatedPayload()
    {
        SampleState sent = default(SampleState);
        sent.ProjectilePresent = true;
        sent.ProjectilePosition = new Vector2(3f, 4f);
        sent.ProjectileAngleDegrees = 45f;

        byte[] buffer = new byte[64];
        CoreWire writer = CoreWire.Write(buffer, 0, buffer.Length);
        Wire(ref writer, ref sent);

        SampleState got = default(SampleState);
        CoreWire reader = CoreWire.Read(buffer, 0, writer.Length - 1);
        Wire(ref reader, ref got);
        Expect(!reader.Ok, "truncated payload rejected");
    }

    private static void RejectsTrailingBytes()
    {
        SampleState sent = default(SampleState);
        sent.ExplosionPresent = true;
        sent.ExplosionPosition = new Vector2(1f, 2f);
        sent.ExplosionRadiusWorld = 3f;

        byte[] buffer = new byte[64];
        CoreWire writer = CoreWire.Write(buffer, 0, buffer.Length);
        Wire(ref writer, ref sent);

        SampleState got = default(SampleState);
        CoreWire reader = CoreWire.Read(buffer, 0, writer.Length + 4);
        Wire(ref reader, ref got);
        Expect(reader.Ok && !reader.AtEnd, "trailing bytes caught by AtEnd");
    }

    private static void RejectsNonFiniteFloat()
    {
        SampleState sent = default(SampleState);
        sent.ProjectilePresent = true;
        sent.ProjectilePosition = new Vector2(float.NaN, 0f);

        byte[] buffer = new byte[64];
        CoreWire writer = CoreWire.Write(buffer, 0, buffer.Length);
        Wire(ref writer, ref sent);
        Expect(!writer.Ok, "NaN refused on write");

        // And refused inbound, so a hostile or desynced sender cannot inject one.
        byte[] hostile = new byte[13];
        hostile[0] = 1;
        hostile[1] = 0x00; hostile[2] = 0x00; hostile[3] = 0xC0; hostile[4] = 0x7F;
        SampleState got = default(SampleState);
        CoreWire reader = CoreWire.Read(hostile, 0, hostile.Length);
        Wire(ref reader, ref got);
        Expect(!reader.Ok, "NaN refused on read");
    }

    private static void RejectsNonPositiveRadius()
    {
        SampleState sent = default(SampleState);
        sent.ExplosionPresent = true;
        sent.ExplosionPosition = Vector2.zero;
        sent.ExplosionRadiusWorld = 0f;

        byte[] buffer = new byte[64];
        CoreWire writer = CoreWire.Write(buffer, 0, buffer.Length);
        Wire(ref writer, ref sent);
        Expect(!writer.Ok, "zero radius refused");
    }

    private static void RejectsUnknownFlagBit()
    {
        // A sender running a newer layout sets a bit this build has no field
        // for. Decoding the rest against our shape would be garbage, so the
        // spare-bit rule fails it closed.
        byte[] hostile = new byte[16];
        hostile[0] = 0x04;

        SampleState got = default(SampleState);
        CoreWire reader = CoreWire.Read(hostile, 0, hostile.Length);
        Wire(ref reader, ref got);
        Expect(!reader.Ok, "unknown flag bit refused");
    }

    /// <summary>
    /// The regression that shipped. CoreNetwork.MaxPayloadBytes is 384, so a
    /// full Orrery frame carries a length that does not fit in a byte. Writing
    /// it narrow and reading it wide is what killed every packet. Here the
    /// width is stated once, so the failure is not expressible.
    /// </summary>
    private static void LengthSurvivesAboveByteRange()
    {
        ushort[] lengths = { 0, 1, 254, 255, 256, 341, 384, 65535 };
        for (int i = 0; i < lengths.Length; i++)
        {
            ushort sent = lengths[i];
            byte[] buffer = new byte[8];

            CoreWire writer = CoreWire.Write(buffer, 0, buffer.Length);
            writer.UInt16(ref sent);

            ushort got = 0;
            CoreWire reader = CoreWire.Read(buffer, 0, writer.Length);
            reader.UInt16(ref got);

            Expect(reader.Ok && got == lengths[i],
                   "length " + lengths[i] + " survived round trip (got " + got + ")");
        }
    }

    private static void WriteRefusesOverrun()
    {
        SampleState sent = default(SampleState);
        sent.ProjectilePresent = true;
        sent.ExplosionPresent = true;
        sent.ProjectilePosition = Vector2.one;
        sent.ExplosionPosition = Vector2.one;
        sent.ExplosionRadiusWorld = 1f;

        byte[] tooSmall = new byte[8];
        CoreWire writer = CoreWire.Write(tooSmall, 0, tooSmall.Length);
        Wire(ref writer, ref sent);
        Expect(!writer.Ok, "overrun refused instead of throwing");
    }

    // =========================================================================

    private static void Expect(bool condition, string what)
    {
        checks++;
        if (condition) return;
        failures++;
        Debug.LogError("[CoreWire] FAILED: " + what);
    }
}
#endif
