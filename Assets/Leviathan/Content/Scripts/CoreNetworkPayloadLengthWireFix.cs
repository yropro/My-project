using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;

/// <summary>
/// Repairs CoreNetwork's ship-state extension header at the compiled write seam.
///
/// The declared v3 wire format, HeaderBytes constant and receive path all use a
/// two-byte ushort payload length, but AppendLocalExtension currently casts the
/// length to byte before BinaryWriter.Write. That both misaligns the receiver by
/// one byte and truncates payloads above 255 bytes -- exactly the range the shared
/// Orrery presentation bank can legitimately reach under MaxPayloadBytes=384.
///
/// Keep this correction deliberately narrow and self-validating: the current
/// AppendLocalExtension contains five BinaryWriter.Write(byte) calls, with the
/// final one being the explicit payloadLength cast immediately before the payload
/// buffer write. If that source shape changes, refuse to guess rather than patching
/// a different byte field silently.
/// </summary>
[HarmonyPatch(typeof(CoreNetwork), "AppendLocalExtension")]
public static class CoreNetworkPayloadLengthWireFixPatch
{
    private static readonly MethodInfo WriteByte = AccessTools.Method(
        typeof(BinaryWriter),
        nameof(BinaryWriter.Write),
        new Type[] { typeof(byte) });

    private static readonly MethodInfo WriteUShort = AccessTools.Method(
        typeof(BinaryWriter),
        nameof(BinaryWriter.Write),
        new Type[] { typeof(ushort) });

    public static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = new List<CodeInstruction>(instructions);
        int byteWriteCount = 0;
        int targetCallIndex = -1;

        for (int i = 0; i < code.Count; i++)
        {
            if (WriteByte != null && code[i].Calls(WriteByte))
            {
                byteWriteCount++;
                targetCallIndex = i;
            }
        }

        if (WriteByte == null || WriteUShort == null ||
            byteWriteCount != 5 || targetCallIndex <= 0 ||
            code[targetCallIndex - 1].opcode != OpCodes.Conv_U1)
        {
            throw new InvalidOperationException(
                "CoreNetwork payload-length wire fix no longer matches " +
                "AppendLocalExtension; refusing an unsafe transpile.");
        }

        // Preserve the existing int local, but widen its explicit conversion and
        // select BinaryWriter.Write(ushort). Wire order stays little-endian, which
        // matches BinaryReader.ReadUInt16 on every supported Star Vortex target.
        code[targetCallIndex - 1].opcode = OpCodes.Conv_U2;
        code[targetCallIndex].operand = WriteUShort;

        return code;
    }
}
