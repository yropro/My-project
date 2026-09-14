using UnityEngine;

/// <summary>
/// Bidirectional presentation cursor.
///
/// ---------------------------------------------------------------------------
/// WHY THIS EXISTS
/// ---------------------------------------------------------------------------
/// Every wire format in this mod used to be written twice: once in a publish
/// method and once in a mirrored read method. The two copies are independent
/// code, so they drift. Two shipped bugs came from exactly that - a header
/// written as a byte and read as a ushort, and a per-spell expectedLength
/// constant recomputed by hand on the read side.
///
/// A CoreWire is a cursor that runs in one of two directions over the same
/// buffer. A spell declares its field order ONCE, in a single method that takes
/// its state by ref:
///
///     private static void Wire(ref CoreWire w, ref MagmaState s)
///     {
///         w.Flags(ref s.ProjectilePresent, ref s.ExplosionPresent);
///         if (s.ProjectilePresent) { w.Position(ref s.ProjectilePosition);
///                                    w.Float(ref s.ProjectileAngleDegrees); }
///         if (s.ExplosionPresent)  { w.Position(ref s.ExplosionPosition);
///                                    w.Positive(ref s.ExplosionRadiusWorld); }
///     }
///
/// Publish runs it in write mode, remote read runs it in read mode. The lengths
/// agree because they are produced by the same statements. Conditional layouts
/// work because the branch condition is itself a wired field: on the read pass
/// the flags are populated before the branch is evaluated.
///
/// That imposes a rule worth stating plainly. A branch may only test a field
/// serialised EARLIER in this same method, or a protocol constant both peers
/// agree on. It may never test live gameplay state - the sender's ship, the
/// local clock, a runtime lookup - because the reader has none of that and
/// will take a different branch, decoding the remaining bytes against the
/// wrong shape. A format method should be able to run with access to nothing
/// but its cursor and its state struct.
///
/// ---------------------------------------------------------------------------
/// FAILURE POLICY
/// ---------------------------------------------------------------------------
/// Nothing here throws. Any overrun, any non-finite float, any out-of-range
/// value latches Ok to false and every subsequent operation becomes a no-op.
/// Callers check Ok once at the end instead of after every field. A failed
/// wire means "suppress this presentation", never "kick the sender" - the
/// read path still runs under NetSession.HandleData, which converts an
/// exception into a malformed-message strike.
/// </summary>
/// <summary>
/// A format declaration: the single method that describes a payload's field
/// order, run in whichever direction the cursor is pointing.
/// </summary>
public delegate void CoreWireFormat<T>(ref CoreWire wire, ref T state) where T : struct;

public struct CoreWire
{
    private byte[] buffer;
    private int start;
    private int limit;
    private int position;
    private bool writing;
    private bool ok;

    /// <summary>False once any field has failed. Latched; never recovers.</summary>
    public bool Ok { get { return ok; } }

    /// <summary>True when this cursor is serialising rather than parsing.</summary>
    public bool IsWriting { get { return writing; } }

    /// <summary>Bytes consumed or produced so far.</summary>
    public int Length { get { return position - start; } }

    /// <summary>
    /// True when a read pass consumed the payload exactly. Use this instead of
    /// a hand-written trailing-bytes check: a short read means the sender wrote
    /// a layout this build does not agree with.
    /// </summary>
    public bool AtEnd { get { return !writing && position == limit; } }

    public static CoreWire Write(byte[] destination, int offset, int capacity)
    {
        CoreWire w = default(CoreWire);
        w.buffer = destination;
        w.start = offset;
        w.position = offset;
        w.writing = true;
        // Subtraction, never addition: offset + capacity can overflow to a
        // negative value that silently passes a <= Length test, after which
        // buffer[position] throws - breaking the no-throw guarantee at exactly
        // the point where it matters.
        w.ok = destination != null && offset >= 0 && capacity >= 0 &&
               offset <= destination.Length &&
               capacity <= destination.Length - offset;
        if (w.ok) w.limit = offset + capacity;
        return w;
    }

    public static CoreWire Read(byte[] source, int offset, int length)
    {
        CoreWire w = default(CoreWire);
        w.buffer = source;
        w.start = offset;
        w.position = offset;
        w.writing = false;
        w.ok = source != null && offset >= 0 && length >= 0 &&
               offset <= source.Length &&
               length <= source.Length - offset;
        if (w.ok) w.limit = offset + length;
        return w;
    }

    /// <summary>Latch failure explicitly, for a spell-specific range check.</summary>
    public void Fail()
    {
        ok = false;
    }

    private bool Claim(int count)
    {
        if (!ok || count > limit - position)
        {
            ok = false;
            return false;
        }
        return true;
    }


    // =========================================================================
    // SAFE ENTRY POINTS
    // =========================================================================
    // A raw Read() cursor writes into the state struct as it decodes, so a
    // payload that fails halfway leaves that struct partially populated. If the
    // caller passed live presentation state by ref, a rejected packet has
    // already corrupted it. These wrappers decode into a temporary and publish
    // it only once the whole payload validated, so a rejected packet is a
    // no-op. Prefer them over Read()/Write() at call sites.
    //
    // The delegate is stored once per format:
    //     private static readonly CoreWireFormat<MagmaWireState> Format = Wire;
    // which costs one allocation at static init and none per frame.

    public static bool TryDecode<T>(
        byte[] source,
        int offset,
        int length,
        CoreWireFormat<T> format,
        ref T destination) where T : struct
    {
        if (format == null) return false;

        T scratch = default(T);
        CoreWire wire = Read(source, offset, length);
        try
        {
            if (!wire.Ok) return false;
            format(ref wire, ref scratch);
        }
        catch (System.Exception ex)
        {
            // CoreWire's own operations cannot throw, but a format method can -
            // an index, a null, anything the author put in it. On the receive
            // path that would surface under NetSession.HandleData as a
            // malformed-message strike against a peer whose only mistake was
            // talking to us. Contain it here. This narrows the throw surface to
            // the format method; it does NOT make the whole receive path
            // exception-proof, and is not a substitute for guarding the publish
            // and dispatch paths that call into this.
            ReportFormatThrow("decode", ex);
            return false;
        }

        // AtEnd as well as Ok: a payload this build parses successfully but
        // does not fully consume was written against a layout we do not share.
        if (!wire.Ok || !wire.AtEnd) return false;

        destination = scratch;
        return true;
    }

    public static bool TryEncode<T>(
        byte[] destination,
        int offset,
        int capacity,
        CoreWireFormat<T> format,
        ref T state,
        out int length) where T : struct
    {
        length = 0;
        if (format == null) return false;

        CoreWire wire = Write(destination, offset, capacity);
        bool encoded;
        try
        {
            if (!wire.Ok) return false;
            T scratch = state;
            format(ref wire, ref scratch);
            encoded = wire.Ok;
        }
        catch (System.Exception ex)
        {
            ReportFormatThrow("encode", ex);
            encoded = false;
        }

        if (!encoded)
        {
            // Unlike TryDecode, an encode cannot be staged in a temporary
            // without imposing a fixed scratch size, so a failure has usually
            // already written part of the buffer. Blank what was written: a
            // caller that ignores the return value then publishes zeroes rather
            // than a half-formatted payload, which could otherwise satisfy a
            // length check on the far side and decode as something plausible.
            // Callers must still publish only on success - out length is 0 here.
            int written = wire.Length;
            if (destination != null && written > 0 && offset >= 0 &&
                offset <= destination.Length &&
                written <= destination.Length - offset)
            {
                System.Array.Clear(destination, offset, written);
            }
            return false;
        }

        length = wire.Length;
        return true;
    }

    private const int MaxFormatThrowReports = 8;
    private static int formatThrowReports;

    private static void ReportFormatThrow(string direction, System.Exception ex)
    {
        formatThrowReports++;
        if (formatThrowReports > MaxFormatThrowReports) return;

        Debug.LogError("[CoreWire] format threw during " + direction + " (" +
            formatThrowReports + " of " + MaxFormatThrowReports + "): " + ex);

        if (formatThrowReports == MaxFormatThrowReports)
            Debug.LogError("[CoreWire] further format exceptions suppressed.");
    }

    // =========================================================================
    // PRIMITIVES
    // =========================================================================

    public void Byte(ref byte value)
    {
        if (!Claim(1)) return;
        if (writing) buffer[position] = value;
        else value = buffer[position];
        position++;
    }

    public void Bool(ref bool value)
    {
        byte raw = value ? (byte)1 : (byte)0;
        Byte(ref raw);
        if (!ok) return;
        if (raw > 1) { ok = false; return; }
        if (!writing) value = raw != 0;
    }

    /// <summary>Small unsigned integer. Values outside 0-255 fail the wire.</summary>
    public void Count(ref int value)
    {
        if (writing && (value < 0 || value > 255)) { ok = false; return; }
        byte raw = writing ? (byte)value : (byte)0;
        Byte(ref raw);
        if (!ok || writing) return;
        value = raw;
    }

    public void UInt16(ref ushort value)
    {
        if (!Claim(2)) return;
        if (writing)
        {
            buffer[position] = (byte)value;
            buffer[position + 1] = (byte)(value >> 8);
        }
        else
        {
            value = (ushort)(buffer[position] | (buffer[position + 1] << 8));
        }
        position += 2;
    }

    public void UInt32(ref uint value)
    {
        if (!Claim(4)) return;
        if (writing)
        {
            buffer[position] = (byte)value;
            buffer[position + 1] = (byte)(value >> 8);
            buffer[position + 2] = (byte)(value >> 16);
            buffer[position + 3] = (byte)(value >> 24);
        }
        else
        {
            value = (uint)buffer[position] |
                    ((uint)buffer[position + 1] << 8) |
                    ((uint)buffer[position + 2] << 16) |
                    ((uint)buffer[position + 3] << 24);
        }
        position += 4;
    }

    /// <summary>
    /// IEEE float. Non-finite fails the wire in BOTH directions, so a NaN can
    /// neither be published nor accepted. This is the check that used to be
    /// repeated by hand around every WriteFloat/ReadFloat pair.
    /// </summary>
    public void Float(ref float value)
    {
        if (writing && !IsFinite(value)) { ok = false; return; }

        uint bits = writing ? BitsOf(value) : 0u;
        UInt32(ref bits);
        if (!ok || writing) return;

        float decoded = FloatOf(bits);
        if (!IsFinite(decoded)) { ok = false; return; }
        value = decoded;
    }

    /// <summary>Float that must be strictly greater than zero (radii, widths).</summary>
    public void Positive(ref float value)
    {
        Float(ref value);
        if (ok && value <= 0f) ok = false;
    }

    public void Position(ref Vector2 value)
    {
        Float(ref value.x);
        Float(ref value.y);
    }

    // =========================================================================
    // PACKED FLAGS
    // =========================================================================

    /// <summary>
    /// Up to eight booleans in one byte. On a read pass these are populated
    /// before the caller branches on them, which is what makes a conditional
    /// layout survive the round trip. Unused high bits must be zero, so a
    /// sender with more fields than this build knows about fails closed rather
    /// than being parsed against the wrong layout.
    /// </summary>
    public void Flags(ref bool b0)
    {
        bool d1 = false, d2 = false, d3 = false, d4 = false, d5 = false, d6 = false, d7 = false;
        Flags(ref b0, ref d1, ref d2, ref d3, ref d4, ref d5, ref d6, ref d7);
        RejectSpare(d1 || d2 || d3 || d4 || d5 || d6 || d7);
    }

    public void Flags(ref bool b0, ref bool b1)
    {
        bool d2 = false, d3 = false, d4 = false, d5 = false, d6 = false, d7 = false;
        Flags(ref b0, ref b1, ref d2, ref d3, ref d4, ref d5, ref d6, ref d7);
        RejectSpare(d2 || d3 || d4 || d5 || d6 || d7);
    }

    public void Flags(ref bool b0, ref bool b1, ref bool b2)
    {
        bool d3 = false, d4 = false, d5 = false, d6 = false, d7 = false;
        Flags(ref b0, ref b1, ref b2, ref d3, ref d4, ref d5, ref d6, ref d7);
        RejectSpare(d3 || d4 || d5 || d6 || d7);
    }

    public void Flags(ref bool b0, ref bool b1, ref bool b2, ref bool b3)
    {
        bool d4 = false, d5 = false, d6 = false, d7 = false;
        Flags(ref b0, ref b1, ref b2, ref b3, ref d4, ref d5, ref d6, ref d7);
        RejectSpare(d4 || d5 || d6 || d7);
    }

    /// <summary>
    /// A sender that set a flag bit this build has no field for is running a
    /// layout we cannot parse. Fail closed rather than decode the remaining
    /// bytes against the wrong shape.
    /// </summary>
    private void RejectSpare(bool anySpareSet)
    {
        if (!writing && anySpareSet) ok = false;
    }

    public void Flags(
        ref bool b0, ref bool b1, ref bool b2, ref bool b3,
        ref bool b4, ref bool b5, ref bool b6, ref bool b7)
    {
        byte raw = 0;
        if (writing)
        {
            if (b0) raw |= 1 << 0;
            if (b1) raw |= 1 << 1;
            if (b2) raw |= 1 << 2;
            if (b3) raw |= 1 << 3;
            if (b4) raw |= 1 << 4;
            if (b5) raw |= 1 << 5;
            if (b6) raw |= 1 << 6;
            if (b7) raw |= 1 << 7;
        }

        Byte(ref raw);
        if (!ok || writing) return;

        b0 = (raw & (1 << 0)) != 0;
        b1 = (raw & (1 << 1)) != 0;
        b2 = (raw & (1 << 2)) != 0;
        b3 = (raw & (1 << 3)) != 0;
        b4 = (raw & (1 << 4)) != 0;
        b5 = (raw & (1 << 5)) != 0;
        b6 = (raw & (1 << 6)) != 0;
        b7 = (raw & (1 << 7)) != 0;
    }

    // =========================================================================
    // BIT REINTERPRETATION
    // =========================================================================

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Explicit)]
    private struct FloatBits
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public float Value;
        [System.Runtime.InteropServices.FieldOffset(0)] public uint Bits;
    }

    private static uint BitsOf(float value)
    {
        FloatBits f = default(FloatBits);
        f.Value = value;
        return f.Bits;
    }

    private static float FloatOf(uint bits)
    {
        FloatBits f = default(FloatBits);
        f.Bits = bits;
        return f.Value;
    }

    internal static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
