using HarmonyLib;
using StarVortex;

/// <summary>
/// Fixed physical transport bank for transient Orrery presentation state.
///
/// This layer deliberately owns only transport identity/capacity. Spell codecs
/// continue to own payload layout, history/refresh semantics, visual lifetime,
/// and all gameplay behavior.
///
/// The physical records are multiplexed per ship-state send. A codec's preferred
/// record is only a stable packing hint; if that range is occupied this layer finds
/// another contiguous range and readers scan the bank by self-identifying codec / 
/// group headers. This prevents record identity from becoming spell identity and
/// lets short-lived spell presentation share one bounded transport bank.
/// </summary>
public static class OrreryPresentationNetwork
{
    // Eight records keeps the worst current overlap (active spell + bounded
    // Shatterbolt tail + Plasma refresh) under the existing Core dynamic budget
    // without consuming the remaining Core slot id space spell-by-spell.
    public const int RecordCount = 8;
    public const int RecordBytes = 32;

    public const byte Record0SlotId = 7;
    public const byte Record1SlotId = 8;
    public const byte Record2SlotId = 9;
    public const byte Record3SlotId = 10;
    public const byte Record4SlotId = 11;
    public const byte Record5SlotId = 12;
    public const byte Record6SlotId = 13;
    public const byte Record7SlotId = 14;

    // Explicit codec ids. Payload meaning remains spell-owned.
    public const byte CodecShatterbolt = 1;
    public const byte CodecPlasmaBolt = 2;
    public const byte CodecMagmaCannon = 3;
    public const byte CodecTeslaCoil = 4;
    public const byte CodecConeOfCold = 5;

    // Record framing:
    //   every part: byte codec, byte descriptor
    //   leader only: uint generation
    // descriptor bits 0-1 = part index, 2-3 = part count minus one,
    // 4-7 = group id. A group is therefore bounded to four contiguous records.
    public const int LeaderHeaderBytes = 6;
    public const int ContinuationHeaderBytes = 2;
    public const int MaximumPartsPerGroup = 4;
    public const int MaximumGroupId = 15;

    // Only the final pre-serialization sample is allowed to claim physical bank
    // records. Transition-time calls to OrreryNetwork.PublishLocal may still update
    // slot 6, but spell codecs fail closed until PublishForSend opens the bank.
    private static readonly bool[] sendRecordUsed = new bool[RecordCount];
    private static bool buildingSendFrame;

    public static byte GetSlotId(int recordIndex)
    {
        if (recordIndex < 0 || recordIndex >= RecordCount)
            return 0;
        return (byte)(Record0SlotId + recordIndex);
    }

    public static int GetPayloadCapacity(int partCount)
    {
        if (partCount < 1 || partCount > MaximumPartsPerGroup)
            return 0;
        return RecordBytes - LeaderHeaderBytes +
            (partCount - 1) * (RecordBytes - ContinuationHeaderBytes);
    }

    /// <summary>
    /// Writes one complete multipart presentation group. firstRecordIndex is a
    /// packing preference only; another contiguous free range is chosen when the
    /// preferred range is already occupied by a higher-priority publisher.
    ///
    /// Calls outside the final Core send sample are intentionally suppressed so a
    /// mid-frame transition can never leave stale physical records in the next
    /// packet after the bank has been repacked.
    /// </summary>
    public static bool WriteGroup(
        int firstRecordIndex,
        byte codecId,
        byte groupId,
        int partCount,
        uint generation,
        byte[] payload,
        int payloadLength)
    {
        if (!buildingSendFrame || codecId == 0 || generation == 0u ||
            groupId > MaximumGroupId ||
            partCount < 1 || partCount > MaximumPartsPerGroup ||
            payload == null || payloadLength < 0 || payloadLength > payload.Length ||
            payloadLength > GetPayloadCapacity(partCount))
        {
            return false;
        }

        int actualFirst = FindFreeRange(firstRecordIndex, partCount);
        if (actualFirst < 0)
            return false;

        int payloadOffset = 0;
        for (int partIndex = 0; partIndex < partCount; partIndex++)
        {
            byte slotId = GetSlotId(actualFirst + partIndex);
            if (slotId == 0)
                return false;

            CoreNetwork.SlotWriter writer = CoreNetwork.BeginSlot(slotId);
            writer.Byte(codecId);
            writer.Byte(PackDescriptor(groupId, partIndex, partCount));
            if (partIndex == 0)
                WriteUInt(ref writer, generation);

            int capacity = RecordBytes -
                (partIndex == 0 ? LeaderHeaderBytes : ContinuationHeaderBytes);
            int remaining = payloadLength - payloadOffset;
            int toWrite = remaining < capacity ? remaining : capacity;
            for (int i = 0; i < toWrite; i++)
                writer.Byte(payload[payloadOffset + i]);
            payloadOffset += toWrite;
            CoreNetwork.EndSlot(writer);
        }

        if (payloadOffset != payloadLength)
            return false;

        for (int i = 0; i < partCount; i++)
            sendRecordUsed[actualFirst + i] = true;
        return true;
    }

    public struct GroupReader
    {
        public uint Generation;
        public int PartCount;
        public int Length;

        private CoreNetwork.SlotReader part0;
        private CoreNetwork.SlotReader part1;
        private CoreNetwork.SlotReader part2;
        private CoreNetwork.SlotReader part3;
        private int currentPart;
        private int position;

        internal void SetPart(int index, CoreNetwork.SlotReader reader)
        {
            switch (index)
            {
                case 0: part0 = reader; break;
                case 1: part1 = reader; break;
                case 2: part2 = reader; break;
                case 3: part3 = reader; break;
            }
        }

        public int Position { get { return position; } }
        public int Remaining { get { return Length - position; } }

        public byte Byte()
        {
            while (currentPart < PartCount)
            {
                switch (currentPart)
                {
                    case 0:
                        if (part0.Position < part0.Length)
                        { position++; return part0.Byte(); }
                        break;
                    case 1:
                        if (part1.Position < part1.Length)
                        { position++; return part1.Byte(); }
                        break;
                    case 2:
                        if (part2.Position < part2.Length)
                        { position++; return part2.Byte(); }
                        break;
                    case 3:
                        if (part3.Position < part3.Length)
                        { position++; return part3.Byte(); }
                        break;
                }
                currentPart++;
            }
            return 0;
        }
    }

    /// <summary>
    /// Finds and validates a complete multipart group. firstRecordIndex is tried
    /// first for cache/locality stability, then the remaining physical records are
    /// scanned because multiplexing may have relocated the group for this packet.
    /// Missing or malformed parts fail presentation-only.
    /// </summary>
    public static bool TryReadGroup(
        GameShip remoteOwner,
        int firstRecordIndex,
        byte expectedCodecId,
        byte expectedGroupId,
        int expectedPartCount,
        out GroupReader group)
    {
        group = default(GroupReader);
        if (remoteOwner == null || expectedCodecId == 0 ||
            expectedGroupId > MaximumGroupId ||
            expectedPartCount < 1 || expectedPartCount > MaximumPartsPerGroup)
        {
            return false;
        }

        if (TryReadGroupAt(
                remoteOwner,
                firstRecordIndex,
                expectedCodecId,
                expectedGroupId,
                expectedPartCount,
                out group))
        {
            return true;
        }

        for (int candidate = 0;
            candidate + expectedPartCount <= RecordCount;
            candidate++)
        {
            if (candidate == firstRecordIndex)
                continue;

            if (TryReadGroupAt(
                    remoteOwner,
                    candidate,
                    expectedCodecId,
                    expectedGroupId,
                    expectedPartCount,
                    out group))
            {
                return true;
            }
        }

        group = default(GroupReader);
        return false;
    }

    private static bool TryReadGroupAt(
        GameShip remoteOwner,
        int firstRecordIndex,
        byte expectedCodecId,
        byte expectedGroupId,
        int expectedPartCount,
        out GroupReader group)
    {
        group = default(GroupReader);
        if (firstRecordIndex < 0 ||
            firstRecordIndex + expectedPartCount > RecordCount)
        {
            return false;
        }

        group.PartCount = expectedPartCount;
        for (int partIndex = 0; partIndex < expectedPartCount; partIndex++)
        {
            CoreNetwork.SlotReader reader;
            if (!CoreNetwork.TryReadSlot(
                    remoteOwner,
                    GetSlotId(firstRecordIndex + partIndex),
                    out reader))
            {
                group = default(GroupReader);
                return false;
            }

            int minimumLength = partIndex == 0
                ? LeaderHeaderBytes
                : ContinuationHeaderBytes;
            if (reader.Length < minimumLength ||
                reader.Byte() != expectedCodecId)
            {
                group = default(GroupReader);
                return false;
            }

            byte descriptor = reader.Byte();
            byte groupId;
            int decodedPartIndex;
            int decodedPartCount;
            UnpackDescriptor(
                descriptor,
                out groupId,
                out decodedPartIndex,
                out decodedPartCount);
            if (groupId != expectedGroupId ||
                decodedPartIndex != partIndex ||
                decodedPartCount != expectedPartCount)
            {
                group = default(GroupReader);
                return false;
            }

            if (partIndex == 0)
                group.Generation = ReadUInt(ref reader);

            group.Length += reader.Length - reader.Position;
            group.SetPart(partIndex, reader);
        }

        return group.Generation != 0u;
    }

    /// <summary>
    /// All physical records belong to this transport bank. A record has no spell
    /// meaning until an explicit codec writes a self-identifying group.
    /// </summary>
    private static bool initialized;

    public static void EnsureInitialized()
    {
        if (initialized) return;
        for (int i = 0; i < RecordCount; i++)
        {
            CoreNetwork.RegisterSlot(
                GetSlotId(i),
                CoreClassId.Orrery,
                "Orrery presentation record " + i);
        }
        initialized = true;
    }

    /// <summary>
    /// Samples the local Orrery presentation immediately before Core serializes
    /// this ship-state packet. Primary spell state claims the bank first,
    /// Shatterbolt/Plasma fill their bounded history/refresh needs next, and
    /// secondary reflected Magma projectiles consume only leftover capacity.
    /// Gameplay remains owner-authoritative regardless of presentation pressure.
    /// </summary>
    public static void PublishForSend()
    {
        CoreOwnerContext context = CoreClassRuntime.CurrentContext;
        GameShip owner = context != null && context.IsValid &&
            context.ClassId == CoreClassId.Orrery
                ? context.Ship
                : null;

        for (int i = 0; i < sendRecordUsed.Length; i++)
            sendRecordUsed[i] = false;

        buildingSendFrame = true;
        try
        {
            if (owner != null && OrreryRuntime.IsActive(owner))
            {
                OrreryLegacySpellPresentation.Publish(owner);
                OrreryNetwork.PublishLocal(owner);
            }

            OrreryPlasmaBoltPresentation.Publish();

            if (owner != null && OrreryRuntime.IsActive(owner))
                OrreryReflectedMagmaPresentation.Publish(owner);
        }
        finally
        {
            buildingSendFrame = false;
        }
    }

    private static int FindFreeRange(int preferredFirst, int partCount)
    {
        if (IsRangeFree(preferredFirst, partCount))
            return preferredFirst;

        for (int first = 0; first + partCount <= RecordCount; first++)
        {
            if (first == preferredFirst)
                continue;
            if (IsRangeFree(first, partCount))
                return first;
        }
        return -1;
    }

    private static bool IsRangeFree(int first, int partCount)
    {
        if (first < 0 || partCount < 1 || first + partCount > RecordCount)
            return false;

        for (int i = 0; i < partCount; i++)
        {
            if (sendRecordUsed[first + i])
                return false;
        }
        return true;
    }

    private static byte PackDescriptor(byte groupId, int partIndex, int partCount)
    {
        return (byte)((groupId << 4) |
            (((partCount - 1) & 0x03) << 2) |
            (partIndex & 0x03));
    }

    private static void UnpackDescriptor(
        byte descriptor,
        out byte groupId,
        out int partIndex,
        out int partCount)
    {
        groupId = (byte)(descriptor >> 4);
        partIndex = descriptor & 0x03;
        partCount = ((descriptor >> 2) & 0x03) + 1;
    }

    private static void WriteUInt(ref CoreNetwork.SlotWriter writer, uint value)
    {
        writer.Byte((byte)value);
        writer.Byte((byte)(value >> 8));
        writer.Byte((byte)(value >> 16));
        writer.Byte((byte)(value >> 24));
    }

    private static uint ReadUInt(ref CoreNetwork.SlotReader reader)
    {
        return (uint)reader.Byte() |
            ((uint)reader.Byte() << 8) |
            ((uint)reader.Byte() << 16) |
            ((uint)reader.Byte() << 24);
    }
}

[HarmonyPatch(typeof(CoreNetwork), "AppendLocalExtension")]
public static class OrreryPresentationNetworkSendPatch
{
    public static void Prefix()
    {
        OrreryPresentationNetwork.PublishForSend();
    }
}
