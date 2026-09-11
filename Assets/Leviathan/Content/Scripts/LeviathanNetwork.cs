using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Co-op presentation transport for Leviathan.
///
/// ---------------------------------------------------------------------------
/// WHAT THIS DOES
/// ---------------------------------------------------------------------------
/// Star Vortex replicates each player's ship state 20 times a second in a
/// PlayerShipState message. This class appends a small, versioned Leviathan
/// payload to the end of that message and parses it back out on the receiving
/// side. Nothing about the native packet is modified; the payload is strictly
/// additional trailing bytes.
///
/// Two kinds of data travel in that payload:
///
///   SPEC BLOCK     The owner's Leviathan specialization tree ranks, packed to
///                  4 bits per player-chosen node. Auto-granted nodes are
///                  omitted: they are derived from the received specialization
///                  choices plus native-replicated tree unlock ranks, and the
///                  receiver rebuilds them. Sent on a slow heartbeat and burst after
///                  any change. Remote clients register it as transient
///                  network-replicated specialization state against the remote
///                  player's Pilot, which lets every existing resolver
///                  (GetResolvedState, ApplyKnob, IsTreeUnlocked, ...) produce
///                  the owner's exact numbers locally.
///
///   DYNAMIC BLOCK  Per-skill state that changes moment to moment and cannot be
///                  derived: phase, charge progress, event counters, projectile
///                  progress. Sent every packet. Skills claim a slot id and
///                  write a few bytes into it.
///
/// The split matters: derived values (beam width, range, colors, which VFX,
/// which sound) are NOT sent. They are recomputed on each client from the
/// replicated ranks plus the spec block. Adding a skill therefore costs zero
/// protocol work unless that skill needs a genuinely new moment-in-time signal.
///
/// ---------------------------------------------------------------------------
/// WHY THIS IS SAFE IN THIS GAME
/// ---------------------------------------------------------------------------
/// * NetSession.HandleHello -> ModsMatch rejects any joiner whose mod id/version
///   set differs from the host's. Every peer in a session therefore runs the
///   identical Leviathan build. There is no mixed-version case to negotiate,
///   and no unmodded peer to protect against.
///
/// * NetWorldBridge.TryDecodeShipState reads the native ShipStateSample and does
///   not assert that the stream was fully consumed, so trailing bytes are
///   ignored by any code path that has not been patched.
///
/// * NetSession.RelayShipState forwards the original received byte buffer
///   verbatim rather than re-encoding, so the host relays the payload untouched.
///
/// * The native ranks this design leans on (Upgrade.Key 81-86) already replicate:
///   Pilot.upgradeUnlocks is a serialized field inside the Ship JSON that
///   NetSession.ShareLocalShip broadcasts, and NetWorldBridge rebuilds each
///   remote rep from it. LeviathanRemoteSkillVisuals already depends on this.
///
/// ---------------------------------------------------------------------------
/// WIRE FORMAT
/// ---------------------------------------------------------------------------
/// Appended immediately after the native ShipStateSample fields:
///
///   offset 0    byte    magic0        0x4C 'L'
///   offset 1    byte    magic1        0x56 'V'
///   offset 2    byte    protocol      ProtocolVersion
///   offset 3    byte    blockFlags    bit0 dynamic present, bit1 spec present
///   offset 4    byte    payloadLength bytes following this header (<= MaxPayload)
///   offset 5..  payload
///
/// blockFlags and every count inside the payload describe what was actually
/// serialised, never what was intended. The builder writes the payload first and
/// backfills the counts, so a truncated build emits a smaller-but-valid packet
/// rather than a packet the receiver rejects.
///
/// Dynamic block, a TLV list so unknown slots can be skipped and inactive
/// skills cost nothing:
///
///   byte    slotCount
///   repeat slotCount times:
///       byte    slotId
///       byte    slotLength
///       bytes   slotLength bytes of slot data
///
/// Every packet a Leviathan sends carries this block, including when it holds
/// zero slots. That costs six bytes (header plus a zero count), about 120 bytes
/// per second per player, and it buys exact transition timing: a received block
/// with no slot for a skill is an authoritative statement that the skill is
/// idle right now, applied on the next packet rather than after a timeout.
/// Without it, "stopped publishing" and "packets stopped arriving" would be
/// indistinguishable and every skill would go idle only once the freshness
/// window expired - half a second of stale charge or firing visuals on every
/// normal transition. DynamicStaleSeconds is therefore reserved for genuine
/// stream loss and plays no part in ordinary state changes.
///
/// Spec block (present when bit1 set):
///
///   ushort  schemaHash     identity of the node ordering this build compiled
///   uint    contentHash    identity of this particular rank set
///   byte    nodeCount
///   bytes   ceil(nodeCount / 2) packed ranks, 4 bits each, low nibble first
///
/// A receiver that does not recognise schemaHash discards the spec block rather
/// than misapplying ranks to the wrong nodes. Under ModsMatch this should never
/// fire; it exists so a schema desync fails loudly instead of silently.
///
/// contentHash is the identity of one particular rank set, and a receiver skips
/// re-applying a tree whose hash it already holds. It is 32-bit rather than
/// folded to 16, because a collision between two legitimate rank sets would
/// leave a remote silently presenting the owner's previous build after a respec
/// - a rare fault with no visible cause. Two bytes on an occasional packet is
/// not worth that failure mode. schemaHash stays 16-bit: it guards an event
/// that ModsMatch already makes impossible.
///
/// ---------------------------------------------------------------------------
/// SIZE
/// ---------------------------------------------------------------------------
/// A typical native PlayerShipState is 44 bytes (41 fixed plus three empty
/// array counts). Header is 5 bytes. A dynamic block with one active skill slot
/// is about 8; an idle packet carries a one-byte zero count instead. The spec
/// block for the current node set is about 27. Worst observed case is therefore
/// roughly 84 bytes on the packets that carry a spec block and about 57 on the
/// rest, dropping to 50 when no skill is active.
///
/// LiteNetLib does not fragment DeliveryMethod.Sequenced; NetPeer.Send throws
/// TooBigPacketException past MTU minus header, and LiteNetLibTransport.Send has
/// no try/catch. The native packet is variable length (auto targets, tractor
/// containers, grapple links) with a worst case near 1044 bytes, so this class
/// refuses to append anything once the native packet is already large. See
/// MaxCombinedBytes.
///
/// ---------------------------------------------------------------------------
/// FAILURE POLICY
/// ---------------------------------------------------------------------------
/// The read path must never throw. NetWorldBridge.TryDecodeShipState has no
/// try/catch of its own, and NetSession.HandleData converts any exception into
/// RegisterMalformed, which kicks the sending connection after 10 strikes. At
/// 20 Hz that is half a second. Every parse is bounds-checked and wrapped, and
/// the reader position is restored untouched whenever the trailing bytes turn
/// out not to be ours.
/// </summary>
public static class LeviathanNetwork
{
    // =========================================================================
    // PROTOCOL CONSTANTS
    // =========================================================================

    private const byte Magic0 = 0x4C; // 'L'
    private const byte Magic1 = 0x56; // 'V'

    /// <summary>
    /// Bump when the wire format changes shape. Because ModsMatch guarantees
    /// identical builds this is diagnostic rather than a negotiation mechanism:
    /// it makes a stale dev build fail visibly instead of misparsing.
    /// </summary>
    public const byte ProtocolVersion = 2;

    private const int HeaderBytes = 5;

    private const byte BlockFlagDynamic = 1 << 0;
    private const byte BlockFlagSpec = 1 << 1;

    /// <summary>Upper bound on bytes following the header.</summary>
    private const int MaxPayloadBytes = 192;

    /// <summary>
    /// Refuse to append if the finished packet would exceed this. Chosen well
    /// below LiteNetLib's smallest pre-discovery MTU so an unusual native packet
    /// (many grapple links) degrades to no-extension instead of throwing.
    /// </summary>
    private const int MaxCombinedBytes = 500;

    /// <summary>Maximum rank storable in one 4-bit slot.</summary>
    private const int MaxPackedRank = 15;

    /// <summary>Largest packed spec array the parser will accept.</summary>
    private const int MaxPackedSpecBytes = 128;

    /// <summary>schemaHash (2) + contentHash (4) + nodeCount (1).</summary>
    private const int SpecBlockHeaderBytes = 7;

    // Skill slot ids for the dynamic block. Stable forever once shipped; a
    // receiver skips ids it does not know, so new skills append new ids.
    public const byte SlotStellarConverter = 1;
    public const byte SlotStarfire = 2;
    public const byte SlotPredator = 3;
    public const byte SlotConstrictor = 4;
    public const byte SlotBehemoth = 5;

    private const int MaxSlots = 16;
    private const int MaxSlotBytes = 32;

    // =========================================================================
    // FRESHNESS AND CLEANUP
    // =========================================================================

    /// <summary>
    /// Dynamic presentation state older than this is treated as absent. At 20 Hz
    /// this is ten missed packets.
    ///
    /// This is a stream-loss backstop, not part of normal state transitions. A
    /// skill going idle is communicated by the next packet carrying a dynamic
    /// block without that skill's slot, which applies immediately. This window
    /// only covers a remote whose packets stop arriving entirely, so their
    /// visuals stop rather than freezing mid-charge forever.
    /// </summary>
    private const float DynamicStaleSeconds = 0.5f;

    /// <summary>
    /// Snapshots untouched for this long are dropped entirely, releasing their
    /// buffers and any transient specialization they registered.
    /// </summary>
    private const float SnapshotExpirySeconds = 10f;

    private const float PruneIntervalSeconds = 2f;

    private static float nextPruneTime;

    // =========================================================================
    // SEND SCHEDULING
    // =========================================================================

    /// <summary>Packets between unsolicited spec-block heartbeats (40 = 2s).</summary>
    private const int SpecHeartbeatPackets = 40;

    /// <summary>
    /// Consecutive packets carrying the spec block after a local change. The
    /// stream is unreliable, so a burst makes loss recovery immediate rather
    /// than waiting up to a full heartbeat.
    /// </summary>
    private const int SpecBurstPackets = 12;

    private static int packetCounter;
    private static int specBurstRemaining;
    private static int lastSeenConfigurationRevision = int.MinValue;
    private static int lastSeenRegistryRevision = int.MinValue;

    // =========================================================================
    // OUTGOING STATE
    // =========================================================================

    private static readonly byte[] localSlotBuffer = new byte[MaxSlots * MaxSlotBytes];
    private static readonly byte[] localSlotIds = new byte[MaxSlots];
    private static readonly int[] localSlotLengths = new int[MaxSlots];
    private static int localSlotCount;

    private static int openSlotIndex = -1;

    private static readonly byte[] scratch = new byte[MaxPayloadBytes + HeaderBytes];

    // =========================================================================
    // SPEC SCHEMA
    // =========================================================================

    private sealed class SchemaEntry
    {
        public string TreeId;
        public string NodeId;
        public int MaxRank;
    }

    private static SchemaEntry[] schema;
    private static ushort schemaHash;
    private static int schemaBuiltForRegistryRevision = int.MinValue;

    private static byte[] localSpecPacked;
    private static uint localSpecContentHash;

    // =========================================================================
    // INCOMING STATE
    // =========================================================================

    private sealed class RemoteSnapshot
    {
        public readonly byte[] SlotData = new byte[MaxSlots * MaxSlotBytes];
        public readonly byte[] SlotIds = new byte[MaxSlots];
        public readonly int[] SlotLengths = new int[MaxSlots];
        public int SlotCount;

        /// <summary>contentHash of the spec set currently applied to a Pilot.</summary>
        public uint AppliedSpecHash;

        /// <summary>contentHash received but not yet pushed into the runtime.</summary>
        public uint PendingSpecHash;
        public readonly byte[] PendingSpecPacked = new byte[MaxPackedSpecBytes];
        public int PendingSpecLength;
        public bool HasPendingSpec;

        /// <summary>
        /// Exact replica ship this snapshot currently describes. This is kept
        /// independently of AppliedPilot because dynamic state can arrive before
        /// the first specialization block has been applied.
        /// </summary>
        public GameShip RepShip;

        /// <summary>
        /// Pilot the spec set was last applied to, so it can be cleared if this
        /// snapshot expires or the session ends.
        /// </summary>
        public Pilot AppliedPilot;

        /// <summary>Time.unscaledTime of the last accepted packet.</summary>
        public float LastReceived;

        public void ResetDynamic()
        {
            SlotCount = 0;
            LastReceived = 0f;
        }

        public void ResetSpec()
        {
            AppliedSpecHash = 0u;
            PendingSpecHash = 0u;
            PendingSpecLength = 0;
            HasPendingSpec = false;
            AppliedPilot = null;
            RepShip = null;
        }
    }

    private static readonly Dictionary<int, RemoteSnapshot> remoteSnapshots =
        new Dictionary<int, RemoteSnapshot>();

    // Steady-state reverse lookup for per-frame skill presentation. The reflected
    // reps dictionary remains a fallback only when a replica has not been bound yet.
    private static readonly Dictionary<GameShip, int> playerIdByRep =
        new Dictionary<GameShip, int>();

    private static readonly List<int> pruneScratch = new List<int>();

    // Handoff between the ShipStateSample.Read postfix (which has the reader
    // positioned correctly but no player id) and the TryDecodeShipState postfix
    // (which has the player id). Star Vortex polls transports synchronously from
    // NetSession's update path with no LiteNetLib UnsyncedEvents, so this is
    // main-thread only and does not need [ThreadStatic]. All buffers are
    // preallocated; the read path performs no steady-state allocation.
    private static bool pendingValid;
    private static int pendingSlotCount;
    private static readonly byte[] pendingSlotData = new byte[MaxSlots * MaxSlotBytes];
    private static readonly byte[] pendingSlotIds = new byte[MaxSlots];
    private static readonly int[] pendingSlotLengths = new int[MaxSlots];
    private static bool pendingHasSpec;
    private static uint pendingSpecHash;
    private static readonly byte[] pendingSpecPacked = new byte[MaxPackedSpecBytes];
    private static int pendingSpecLength;

    private static readonly FieldInfo RepsField =
        AccessTools.Field(typeof(NetWorldBridge), "reps");

    private static readonly FieldInfo ActiveBridgeField =
        AccessTools.Field(typeof(NetSession), "activeBridge");


    // =========================================================================
    // GENERIC COMBAT DAMAGE FRAMING
    // =========================================================================

    /// <summary>
    /// Damage provenance has its own protocol version.  It is independent from
    /// the 20 Hz ShipState presentation protocol above.
    /// </summary>
    public const byte CombatProtocolVersion = 1;

    // Trailer layout:
    //
    //   payload...
    //   uint magic       "LVCT"
    //   byte protocol
    //   byte kind        1 DamageEvent, 2 DamageResult
    //   byte payloadLen
    //
    // The footer is at the end of the native message, so parsing never needs to
    // know or hard-code the native DamageEvent/DamageResult byte length.
    private const uint CombatTrailerMagic = 0x5443564CU; // L V C T
    private const byte CombatTrailerEvent = 1;
    private const byte CombatTrailerResult = 2;
    private const int CombatTrailerFooterBytes = 7;
    private const int MaxCombatPayloadBytes = 12;
    private const int MaxCombatMessageAssociations = 4096;
    private const float CombatAssociationTimeoutSeconds = 10f;
    private const float CombatAssociationPruneIntervalSeconds = 1f;

    private const byte CombatEventFlagAckMask = 0x03;
    private const byte CombatEventFlagHasAttackInstance = 1 << 2;

    private static readonly Dictionary<MsgDamageEvent, LeviathanCombat.CombatEventMetadata>
        combatEventMetadata =
            new Dictionary<MsgDamageEvent, LeviathanCombat.CombatEventMetadata>(256);

    private static readonly Dictionary<MsgDamageResult, LeviathanCombat.CombatResultMetadata>
        combatResultMetadata =
            new Dictionary<MsgDamageResult, LeviathanCombat.CombatResultMetadata>(256);

    private static readonly List<MsgDamageEvent> combatEventPruneScratch =
        new List<MsgDamageEvent>(128);
    private static readonly List<MsgDamageResult> combatResultPruneScratch =
        new List<MsgDamageResult>(128);

    private static float nextCombatAssociationPruneAt;

    internal static void SetCombatEventMetadata(
        MsgDamageEvent message,
        LeviathanCombat.CombatEventMetadata metadata)
    {
        if (message == null || !metadata.Semantic.IsValid)
            return;

        PruneCombatAssociations(false);
        if (!combatEventMetadata.ContainsKey(message) &&
            combatEventMetadata.Count >= MaxCombatMessageAssociations)
        {
            PruneCombatAssociations(true);
            if (combatEventMetadata.Count >= MaxCombatMessageAssociations)
                return;
        }

        metadata.AttachedAtUnscaled = Time.unscaledTime;
        combatEventMetadata[message] = metadata;
    }

    internal static bool TryGetCombatEventMetadata(
        MsgDamageEvent message,
        out LeviathanCombat.CombatEventMetadata metadata)
    {
        if (message == null)
        {
            metadata = default(LeviathanCombat.CombatEventMetadata);
            return false;
        }

        return combatEventMetadata.TryGetValue(message, out metadata);
    }

    internal static void ReleaseCombatEventMetadata(MsgDamageEvent message)
    {
        if (message != null)
            combatEventMetadata.Remove(message);
    }

    internal static void SetCombatResultMetadata(
        MsgDamageResult message,
        LeviathanCombat.CombatResultMetadata metadata)
    {
        if (message == null || metadata.EventId == 0U)
            return;

        PruneCombatAssociations(false);
        if (!combatResultMetadata.ContainsKey(message) &&
            combatResultMetadata.Count >= MaxCombatMessageAssociations)
        {
            PruneCombatAssociations(true);
            if (combatResultMetadata.Count >= MaxCombatMessageAssociations)
                return;
        }

        metadata.AttachedAtUnscaled = Time.unscaledTime;
        combatResultMetadata[message] = metadata;
    }

    internal static bool TryGetCombatResultMetadata(
        MsgDamageResult message,
        out LeviathanCombat.CombatResultMetadata metadata)
    {
        if (message == null)
        {
            metadata = default(LeviathanCombat.CombatResultMetadata);
            return false;
        }

        return combatResultMetadata.TryGetValue(message, out metadata);
    }

    internal static void ReleaseCombatResultMetadata(MsgDamageResult message)
    {
        if (message != null)
            combatResultMetadata.Remove(message);
    }

    internal static void AppendCombatEventTrailer(
        NetSerializer serializer,
        MsgDamageEvent message)
    {
        if (serializer == null || serializer.writer == null || message == null)
            return;

        LeviathanCombat.CombatEventMetadata metadata;
        if (!combatEventMetadata.TryGetValue(message, out metadata))
            return;

        BinaryWriter writer = serializer.writer;
        long payloadStart = writer.BaseStream.Position;
        try
        {
            writer.Write(metadata.EventId);
            writer.Write(metadata.Semantic.Value);

            byte flags = (byte)((byte)metadata.Acknowledgement &
                CombatEventFlagAckMask);
            if (metadata.HasAttackInstance)
                flags |= CombatEventFlagHasAttackInstance;

            writer.Write(flags);
            if (metadata.HasAttackInstance)
                writer.Write(metadata.AttackInstanceId);

            int payloadLength =
                (int)(writer.BaseStream.Position - payloadStart);

            if (payloadLength <= 0 || payloadLength > MaxCombatPayloadBytes)
            {
                // Should never happen with the fixed v1 layout.  Restore the
                // native message rather than emitting malformed combat framing.
                writer.BaseStream.Position = payloadStart;
                writer.BaseStream.SetLength(payloadStart);
                return;
            }

            writer.Write(CombatTrailerMagic);
            writer.Write(CombatProtocolVersion);
            writer.Write(CombatTrailerEvent);
            writer.Write((byte)payloadLength);
        }
        catch (Exception ex)
        {
            try
            {
                writer.BaseStream.Position = payloadStart;
                writer.BaseStream.SetLength(payloadStart);
            }
            catch (Exception)
            {
            }

            Debug.LogWarning(
                "[LeviathanNetwork] Combat DamageEvent trailer failed: " +
                ex.Message);
        }
        finally
        {
            // Source serialization and host relay both finish with this message
            // object here. Host requests waiting for authority are not written
            // until they are actually dispatched, so their metadata remains.
            combatEventMetadata.Remove(message);
        }
    }

    internal static void AppendCombatResultTrailer(
        NetSerializer serializer,
        MsgDamageResult message)
    {
        if (serializer == null || serializer.writer == null || message == null)
            return;

        LeviathanCombat.CombatResultMetadata metadata;
        if (!combatResultMetadata.TryGetValue(message, out metadata))
            return;

        BinaryWriter writer = serializer.writer;
        long payloadStart = writer.BaseStream.Position;
        try
        {
            writer.Write(metadata.EventId);
            writer.Write((byte)metadata.Outcomes);

            if ((metadata.Outcomes &
                 LeviathanCombat.OutcomeFlags.StatusInflicted) != 0 &&
                metadata.StatusDisposition !=
                    LeviathanCombat.StatusDisposition.None)
            {
                writer.Write(metadata.NativeStatusType);
                writer.Write((byte)metadata.StatusDisposition);
            }

            int payloadLength =
                (int)(writer.BaseStream.Position - payloadStart);

            if (payloadLength <= 0 || payloadLength > MaxCombatPayloadBytes)
            {
                writer.BaseStream.Position = payloadStart;
                writer.BaseStream.SetLength(payloadStart);
                return;
            }

            writer.Write(CombatTrailerMagic);
            writer.Write(CombatProtocolVersion);
            writer.Write(CombatTrailerResult);
            writer.Write((byte)payloadLength);
        }
        catch (Exception ex)
        {
            try
            {
                writer.BaseStream.Position = payloadStart;
                writer.BaseStream.SetLength(payloadStart);
            }
            catch (Exception)
            {
            }

            Debug.LogWarning(
                "[LeviathanNetwork] Combat DamageResult trailer failed: " +
                ex.Message);
        }
        finally
        {
            combatResultMetadata.Remove(message);
        }
    }

    internal static void ParseCombatEventTrailer(
        byte[] buffer,
        int length,
        MsgDamageEvent message)
    {
        if (message == null)
            return;

        int payloadOffset;
        int payloadLength;
        if (!TryLocateCombatTrailer(
            buffer, length, CombatTrailerEvent,
            out payloadOffset, out payloadLength))
        {
            return;
        }

        // v1 event core: uint EventId + ushort SemanticKey + byte flags.
        if (payloadLength != 7 && payloadLength != 9)
            return;

        try
        {
            uint eventId = ReadUInt32(buffer, payloadOffset);
            ushort semanticValue = ReadUInt16(buffer, payloadOffset + 4);
            byte flags = buffer[payloadOffset + 6];

            bool hasAttackInstance =
                (flags & CombatEventFlagHasAttackInstance) != 0;
            if (hasAttackInstance != (payloadLength == 9))
                return;

            LeviathanCombat.AcknowledgementMode acknowledgement =
                (LeviathanCombat.AcknowledgementMode)
                    (flags & CombatEventFlagAckMask);

            byte acknowledgementValue = (byte)acknowledgement;
            if (acknowledgementValue >
                (byte)LeviathanCombat.AcknowledgementMode.GuaranteedOutcome)
            {
                return;
            }

            LeviathanCombat.CombatEventMetadata metadata =
                new LeviathanCombat.CombatEventMetadata();
            metadata.EventId = eventId;
            metadata.Semantic =
                LeviathanCombat.SemanticKey.FromPacked(semanticValue);
            metadata.Acknowledgement = acknowledgement;
            metadata.HasAttackInstance = hasAttackInstance;
            metadata.AttackInstanceId = hasAttackInstance
                ? ReadUInt16(buffer, payloadOffset + 7)
                : (ushort)0;
            metadata.AttachedAtUnscaled = Time.unscaledTime;

            if (metadata.Semantic.IsValid)
                SetCombatEventMetadata(message, metadata);
        }
        catch (Exception)
        {
            // Native decoding already succeeded. A malformed optional trailer
            // must never turn valid native damage into a malformed packet.
        }
    }

    internal static void ParseCombatResultTrailer(
        byte[] buffer,
        int length,
        MsgDamageResult message)
    {
        if (message == null)
            return;

        int payloadOffset;
        int payloadLength;
        if (!TryLocateCombatTrailer(
            buffer, length, CombatTrailerResult,
            out payloadOffset, out payloadLength))
        {
            return;
        }

        // v1 result core: uint EventId + byte OutcomeFlags.
        // Accepted direct native status adds two bytes.
        if (payloadLength != 5 && payloadLength != 7)
            return;

        try
        {
            LeviathanCombat.CombatResultMetadata metadata =
                new LeviathanCombat.CombatResultMetadata();
            metadata.EventId = ReadUInt32(buffer, payloadOffset);
            byte outcomeBits = buffer[payloadOffset + 4];
            const byte knownOutcomeBits =
                (byte)(LeviathanCombat.OutcomeFlags.Processed |
                       LeviathanCombat.OutcomeFlags.Damaged |
                       LeviathanCombat.OutcomeFlags.Destroyed |
                       LeviathanCombat.OutcomeFlags.StatusInflicted);
            if ((outcomeBits & ~knownOutcomeBits) != 0)
                return;

            metadata.Outcomes =
                (LeviathanCombat.OutcomeFlags)outcomeBits;
            metadata.AttachedAtUnscaled = Time.unscaledTime;

            bool hasStatus =
                (metadata.Outcomes &
                 LeviathanCombat.OutcomeFlags.StatusInflicted) != 0;

            if (hasStatus != (payloadLength == 7))
                return;

            if (hasStatus)
            {
                metadata.NativeStatusType = buffer[payloadOffset + 5];
                metadata.StatusDisposition =
                    (LeviathanCombat.StatusDisposition)
                        buffer[payloadOffset + 6];

                if (metadata.StatusDisposition !=
                        LeviathanCombat.StatusDisposition.New &&
                    metadata.StatusDisposition !=
                        LeviathanCombat.StatusDisposition.Merged)
                {
                    return;
                }
            }

            if (metadata.EventId != 0U)
                SetCombatResultMetadata(message, metadata);
        }
        catch (Exception)
        {
        }
    }

    private static bool TryLocateCombatTrailer(
        byte[] buffer,
        int length,
        byte expectedKind,
        out int payloadOffset,
        out int payloadLength)
    {
        payloadOffset = 0;
        payloadLength = 0;

        if (buffer == null || length <= CombatTrailerFooterBytes ||
            length > buffer.Length)
        {
            return false;
        }

        int footer = length - CombatTrailerFooterBytes;
        if (footer < 1)
            return false;

        if (ReadUInt32(buffer, footer) != CombatTrailerMagic ||
            buffer[footer + 4] != CombatProtocolVersion ||
            buffer[footer + 5] != expectedKind)
        {
            return false;
        }

        payloadLength = buffer[footer + 6];
        if (payloadLength <= 0 ||
            payloadLength > MaxCombatPayloadBytes ||
            payloadLength > footer - 1)
        {
            return false;
        }

        payloadOffset = footer - payloadLength;
        return payloadOffset >= 1;
    }

    private static ushort ReadUInt16(byte[] buffer, int offset)
    {
        return (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
    }

    private static uint ReadUInt32(byte[] buffer, int offset)
    {
        return (uint)buffer[offset] |
               ((uint)buffer[offset + 1] << 8) |
               ((uint)buffer[offset + 2] << 16) |
               ((uint)buffer[offset + 3] << 24);
    }

    private static void PruneCombatAssociations(bool force)
    {
        float now = Time.unscaledTime;
        if (!force && now < nextCombatAssociationPruneAt)
            return;

        nextCombatAssociationPruneAt =
            now + CombatAssociationPruneIntervalSeconds;

        combatEventPruneScratch.Clear();
        foreach (KeyValuePair<MsgDamageEvent, LeviathanCombat.CombatEventMetadata>
            pair in combatEventMetadata)
        {
            if (pair.Key == null ||
                now - pair.Value.AttachedAtUnscaled >=
                    CombatAssociationTimeoutSeconds)
            {
                combatEventPruneScratch.Add(pair.Key);
            }
        }

        for (int i = 0; i < combatEventPruneScratch.Count; i++)
            combatEventMetadata.Remove(combatEventPruneScratch[i]);
        combatEventPruneScratch.Clear();

        combatResultPruneScratch.Clear();
        foreach (KeyValuePair<MsgDamageResult, LeviathanCombat.CombatResultMetadata>
            pair in combatResultMetadata)
        {
            if (pair.Key == null ||
                now - pair.Value.AttachedAtUnscaled >=
                    CombatAssociationTimeoutSeconds)
            {
                combatResultPruneScratch.Add(pair.Key);
            }
        }

        for (int i = 0; i < combatResultPruneScratch.Count; i++)
            combatResultMetadata.Remove(combatResultPruneScratch[i]);
        combatResultPruneScratch.Clear();
    }

    private static void ResetCombatTransport()
    {
        combatEventMetadata.Clear();
        combatResultMetadata.Clear();
        combatEventPruneScratch.Clear();
        combatResultPruneScratch.Clear();
        nextCombatAssociationPruneAt = 0f;
    }

    // =========================================================================
    // PUBLIC API - OWNER SIDE
    // =========================================================================

    /// <summary>
    /// Writer for one skill's dynamic slot. Obtain with BeginSlot, write a few
    /// values, then call EndSlot. Everything written is presentation state only;
    /// damage, heat, cooldowns and hit registration stay owner-authoritative and
    /// must never be driven from what a remote reads back out of here.
    /// </summary>
    public struct SlotWriter
    {
        internal int Offset;
        internal int Written;
        internal bool Valid;

        public void Bool(bool value)
        {
            Byte(value ? (byte)1 : (byte)0);
        }

        public void Byte(byte value)
        {
            if (!Valid || Written >= MaxSlotBytes)
                return;

            localSlotBuffer[Offset + Written] = value;
            Written++;
        }

        /// <summary>Small unsigned integer, clamped to 0-255.</summary>
        public void Count(int value)
        {
            Byte((byte)Mathf.Clamp(value, 0, 255));
        }

        /// <summary>0..1 progress quantised to one byte (about 0.4% steps).</summary>
        public void Percent(float value)
        {
            Byte(NetSerializer.PackPercent(value));
        }

        /// <summary>
        /// Rolling event counter. Send the low byte of a monotonically
        /// increasing sequence; a receiver treats any change as a new event,
        /// which tolerates roughly 127 consecutive dropped packets.
        /// </summary>
        public void Sequence(int sequence)
        {
            Byte((byte)(sequence & 0xFF));
        }

        /// <summary>Up to 8 booleans in one byte.</summary>
        public void Flags(bool b0, bool b1 = false, bool b2 = false, bool b3 = false,
                          bool b4 = false, bool b5 = false,
                          bool b6 = false, bool b7 = false)
        {
            int v = 0;
            if (b0) v |= 1 << 0;
            if (b1) v |= 1 << 1;
            if (b2) v |= 1 << 2;
            if (b3) v |= 1 << 3;
            if (b4) v |= 1 << 4;
            if (b5) v |= 1 << 5;
            if (b6) v |= 1 << 6;
            if (b7) v |= 1 << 7;
            Byte((byte)v);
        }
    }

    /// <summary>
    /// Begin publishing a skill's per-frame presentation state.
    ///
    /// Each slot id holds exactly one latest-value snapshot. Publishing the same
    /// id more than once between two network sends overwrites the earlier value
    /// rather than appending, so a skill may safely publish from Update or
    /// FixedUpdate at a rate unrelated to the 20 Hz send cadence. The value in
    /// place when the packet is serialised is the one that goes out.
    ///
    /// Not calling BeginSlot at all costs one byte of shared slot count, and the
    /// remote observes no slot for that skill on the next packet. Remote code
    /// should treat an absent slot as "idle", not as "unchanged": the dynamic
    /// block is sent on every packet, so absence is authoritative and immediate
    /// rather than something that has to time out. Outgoing slots are cleared
    /// after every send, so a skill that stops publishing stops appearing in the
    /// stream on the very next packet.
    /// </summary>
    public static SlotWriter BeginSlot(byte slotId)
    {
        SlotWriter writer = default(SlotWriter);

        if (openSlotIndex >= 0)
        {
            Debug.LogWarning(
                "[LeviathanNetwork] BeginSlot(" + slotId +
                ") called while slot " + localSlotIds[openSlotIndex] +
                " is still open. Missing EndSlot; ignoring.");

            return writer;
        }

        int index = -1;

        for (int i = 0; i < localSlotCount; i++)
        {
            if (localSlotIds[i] == slotId)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            if (localSlotCount >= MaxSlots)
            {
                Debug.LogWarning(
                    "[LeviathanNetwork] Dropping slot " + slotId +
                    "; all " + MaxSlots + " dynamic slots are in use.");

                return writer;
            }

            index = localSlotCount;
            localSlotCount++;
            localSlotIds[index] = slotId;
        }

        openSlotIndex = index;
        localSlotLengths[index] = 0;

        writer.Offset = index * MaxSlotBytes;
        writer.Written = 0;
        writer.Valid = true;
        return writer;
    }

    public static void EndSlot(SlotWriter writer)
    {
        if (openSlotIndex < 0)
            return;

        if (writer.Valid)
        {
            localSlotLengths[openSlotIndex] = writer.Written;
        }
        else
        {
            // The caller passed a writer that never opened, so nothing was
            // recorded for this slot. Drop it rather than emitting an empty
            // one, which a remote would read as "this skill is active".
            RemoveSlot(openSlotIndex);
        }

        openSlotIndex = -1;
    }

    /// <summary>
    /// Removes one outgoing slot, shifting the rest down. Only used on the
    /// error paths where a slot was opened but never completed.
    /// </summary>
    private static void RemoveSlot(int index)
    {
        if (index < 0 || index >= localSlotCount)
            return;

        for (int i = index; i < localSlotCount - 1; i++)
        {
            localSlotIds[i] = localSlotIds[i + 1];
            localSlotLengths[i] = localSlotLengths[i + 1];

            Buffer.BlockCopy(
                localSlotBuffer,
                (i + 1) * MaxSlotBytes,
                localSlotBuffer,
                i * MaxSlotBytes,
                MaxSlotBytes);
        }

        localSlotCount--;
    }

    /// <summary>
    /// Clears the outgoing dynamic slots. Called automatically after each send,
    /// so skills republish every frame and stale state cannot linger.
    /// </summary>
    public static void ClearLocalSlots()
    {
        localSlotCount = 0;
        openSlotIndex = -1;
    }

    // =========================================================================
    // PUBLIC API - REMOTE SIDE
    // =========================================================================

    /// <summary>
    /// Reader over one remote skill slot. Reads past the end of the slot return
    /// zero rather than throwing, so a skill that grows its slot still parses
    /// old packets safely during a live session.
    /// </summary>
    public struct SlotReader
    {
        internal byte[] Data;
        internal int Offset;
        internal int Length;
        internal int Position;

        public bool Bool()
        {
            return Byte() != 0;
        }

        public byte Byte()
        {
            if (Data == null || Position >= Length)
                return 0;

            byte value = Data[Offset + Position];
            Position++;
            return value;
        }

        public int Count()
        {
            return Byte();
        }

        public float Percent()
        {
            return NetSerializer.UnpackPercent(Byte());
        }

        public int Sequence()
        {
            return Byte();
        }

        /// <summary>Reads a byte written by SlotWriter.Flags.</summary>
        public byte FlagsByte()
        {
            return Byte();
        }
    }

    /// <summary>
    /// Read a remote player's slot for this skill, if a recent packet carried
    /// one. Returns false once the stream goes stale, so callers naturally stop
    /// presenting rather than holding the last frame indefinitely.
    /// </summary>
    public static bool TryReadSlot(int playerId, byte slotId, out SlotReader reader)
    {
        reader = default(SlotReader);

        RemoteSnapshot snapshot;
        if (!remoteSnapshots.TryGetValue(playerId, out snapshot))
            return false;

        if (Time.unscaledTime - snapshot.LastReceived > DynamicStaleSeconds)
            return false;

        for (int i = 0; i < snapshot.SlotCount; i++)
        {
            if (snapshot.SlotIds[i] != slotId)
                continue;

            reader.Data = snapshot.SlotData;
            reader.Offset = i * MaxSlotBytes;
            reader.Length = snapshot.SlotLengths[i];
            reader.Position = 0;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Convenience for remote presentation code that holds a replica GameShip
    /// rather than a player id.
    /// </summary>
    public static bool TryReadSlot(
        GameShip remoteShip,
        byte slotId,
        out SlotReader reader)
    {
        reader = default(SlotReader);

        int playerId;
        if (!TryGetPlayerId(remoteShip, out playerId))
            return false;

        return TryReadSlot(playerId, slotId, out reader);
    }

    /// <summary>
    /// True once a remote player's specialization tree has been received and
    /// pushed into the runtime for their Pilot.
    ///
    /// Resolvers that currently reject remote ships and fall back to legacy
    /// behaviour should gate on this: until it returns true the replicated tree
    /// is not yet available and legacy fallback remains the correct answer.
    /// </summary>
    public static bool HasSynchronizedSpecialization(int playerId)
    {
        RemoteSnapshot snapshot;

        return remoteSnapshots.TryGetValue(playerId, out snapshot) &&
               snapshot.AppliedSpecHash != 0u &&
               snapshot.RepShip != null &&
               snapshot.AppliedPilot != null;
    }

    public static bool HasSynchronizedSpecialization(GameShip remoteShip)
    {
        if (remoteShip == null)
            return false;

        int playerId;
        if (!TryGetPlayerId(remoteShip, out playerId))
            return false;

        RemoteSnapshot snapshot;
        if (!remoteSnapshots.TryGetValue(playerId, out snapshot))
            return false;

        Pilot pilot = GameShip.GetPlayerSourcePilot(remoteShip);

        return pilot != null &&
               snapshot.AppliedSpecHash != 0u &&
               ReferenceEquals(snapshot.RepShip, remoteShip) &&
               ReferenceEquals(snapshot.AppliedPilot, pilot);
    }

    /// <summary>Resolve a replica GameShip back to its owning player id.</summary>
    public static bool TryGetPlayerId(GameShip remoteShip, out int playerId)
    {
        playerId = -1;

        if (remoteShip == null)
            return false;

        if (playerIdByRep.TryGetValue(remoteShip, out playerId))
            return true;

        Dictionary<int, RemoteShipDriver> reps = GetReps();
        if (reps == null)
            return false;

        foreach (KeyValuePair<int, RemoteShipDriver> pair in reps)
        {
            if (pair.Value != null &&
                ReferenceEquals(pair.Value.gameShip, remoteShip))
            {
                playerId = pair.Key;
                playerIdByRep[remoteShip] = playerId;
                return true;
            }
        }

        return false;
    }


    // =========================================================================
    // LIFECYCLE
    // =========================================================================

    /// <summary>
    /// Drop all replicated state and release every remote Pilot's transient
    /// specialization. Wired to NetWorldBridge.Teardown; also safe to call
    /// manually when leaving a world.
    /// </summary>
    public static void Reset()
    {
        foreach (KeyValuePair<int, RemoteSnapshot> pair in remoteSnapshots)
            ReleaseSpecialization(pair.Value);

        remoteSnapshots.Clear();
        playerIdByRep.Clear();
        pruneScratch.Clear();

        ClearLocalSlots();
        ClearPending();

        packetCounter = 0;
        specBurstRemaining = 0;
        nextPruneTime = 0f;
        lastSeenConfigurationRevision = int.MinValue;
        lastSeenRegistryRevision = int.MinValue;
        localSpecPacked = null;
        localSpecContentHash = 0u;

        ResetCombatTransport();
        LeviathanCombat.Reset();
    }

    /// <summary>Drop one player's replicated state.</summary>
    public static void Forget(int playerId)
    {
        RemoteSnapshot snapshot;
        if (!remoteSnapshots.TryGetValue(playerId, out snapshot))
            return;

        if (snapshot.RepShip != null)
            playerIdByRep.Remove(snapshot.RepShip);

        ReleaseSpecialization(snapshot);
        remoteSnapshots.Remove(playerId);
    }

    /// <summary>
    /// Drop the transient specialization attached to a replica ship's Pilot.
    /// Wired to RemoteShipDriver.DestroyRep, which every rep-removal path in
    /// NetWorldBridge funnels through, including Teardown.
    /// </summary>
    public static void ForgetRep(GameShip repShip)
    {
        if (repShip == null)
            return;

        playerIdByRep.Remove(repShip);

        Pilot pilot = GameShip.GetPlayerSourcePilot(repShip);

        if (pilot != null)
        {
            LeviathanSpecializationRuntime.ClearRemoteSpecialization(pilot);
        }

        // Match the exact replica, not only AppliedPilot. Dynamic state can be
        // received before the first specialization heartbeat, in which case
        // AppliedPilot is still null. A rebuilt rep must never inherit either
        // half of the destroyed rep's snapshot.
        foreach (KeyValuePair<int, RemoteSnapshot> pair in remoteSnapshots)
        {
            RemoteSnapshot snapshot = pair.Value;

            bool sameRep =
                snapshot != null &&
                ReferenceEquals(snapshot.RepShip, repShip);

            bool sameAppliedPilot =
                snapshot != null &&
                pilot != null &&
                ReferenceEquals(snapshot.AppliedPilot, pilot);

            if (sameRep || sameAppliedPilot)
            {
                ReleaseSpecialization(snapshot);
            }
        }
    }

    private static void ReleaseSpecialization(RemoteSnapshot snapshot)
    {
        if (snapshot == null)
            return;

        if (snapshot.AppliedPilot != null)
        {
            LeviathanSpecializationRuntime.ClearRemoteSpecialization(
                snapshot.AppliedPilot);
        }

        if (snapshot.RepShip != null)
            playerIdByRep.Remove(snapshot.RepShip);

        // Dynamic state exists independently of specialization state. Always
        // clear both halves, including the case where a rep is destroyed before
        // its first spec block has ever been applied.
        snapshot.ResetDynamic();
        snapshot.ResetSpec();
    }

    /// <summary>
    /// Drops snapshots for players who have stopped sending. Runs at most once
    /// every PruneIntervalSeconds from the receive path.
    /// </summary>
    private static void PruneStaleSnapshots(float now)
    {
        if (now < nextPruneTime)
            return;

        nextPruneTime = now + PruneIntervalSeconds;

        pruneScratch.Clear();

        foreach (KeyValuePair<int, RemoteSnapshot> pair in remoteSnapshots)
        {
            if (now - pair.Value.LastReceived > SnapshotExpirySeconds)
                pruneScratch.Add(pair.Key);
        }

        for (int i = 0; i < pruneScratch.Count; i++)
        {
            RemoteSnapshot snapshot;
            if (remoteSnapshots.TryGetValue(pruneScratch[i], out snapshot))
            {
                ReleaseSpecialization(snapshot);
                remoteSnapshots.Remove(pruneScratch[i]);
            }
        }

        pruneScratch.Clear();
    }

    // =========================================================================
    // WRITE PATH
    // =========================================================================

    /// <summary>
    /// Appends the Leviathan payload to a ShipStateSample that has just finished
    /// writing its native fields. Runs from the ShipStateSample.Write postfix,
    /// which has exactly one caller: NetWorldBridge.SendLocalState.
    ///
    /// Appending through the BinaryWriter rather than by rewriting the framed
    /// byte array means NetSerializer.Length already accounts for these bytes,
    /// the backing MemoryStream grows itself, and there is no assumption about
    /// spare capacity in the serializer's buffer.
    /// </summary>
    internal static void AppendLocalExtension(BinaryWriter writer)
    {
        if (writer == null)
            return;

        try
        {
            packetCounter++;

            if (openSlotIndex >= 0)
            {
                // The writer that holds the byte count lives in the caller's
                // SlotWriter struct, so an unclosed slot has no recoverable
                // length. Drop it entirely rather than sending a zero-length
                // slot, which a remote would read as "active with no payload".
                Debug.LogWarning(
                    "[LeviathanNetwork] Slot " + localSlotIds[openSlotIndex] +
                    " was never closed with EndSlot; dropping the incomplete " +
                    "slot for this packet.");

                RemoveSlot(openSlotIndex);
                openSlotIndex = -1;
            }

            // Nothing to say if this player is not a Leviathan.
            if (!LeviathanMod.PlayerHasLeviathan())
                return;

            bool wantSpec = ShouldSendSpecBlock();

            byte blockFlags;
            int payloadLength = BuildPayload(wantSpec, out blockFlags);

            if (payloadLength <= 0 || blockFlags == 0)
                return;

            long nativeLength = writer.BaseStream.Position;
            int total = HeaderBytes + payloadLength;

            // Native ShipStateSample is variable length. Rather than risk
            // LiteNetLib's non-fragmenting Sequenced path, skip the extension
            // entirely on an unusually large packet. Presentation degrades for
            // that frame; the connection survives.
            if (nativeLength + total > MaxCombinedBytes)
                return;

            writer.Write(Magic0);
            writer.Write(Magic1);
            writer.Write(ProtocolVersion);
            writer.Write(blockFlags);
            writer.Write((byte)payloadLength);
            writer.Write(scratch, 0, payloadLength);

            // Only count the burst down against packets that actually carried
            // the spec block, so a truncated build does not silently consume
            // burst attempts.
            if ((blockFlags & BlockFlagSpec) != 0 && specBurstRemaining > 0)
                specBurstRemaining--;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[LeviathanNetwork] Send failed: " + ex.Message);
        }
        finally
        {
            ClearLocalSlots();
        }
    }

    /// <summary>
    /// Serialises both blocks into <see cref="scratch"/> and reports what was
    /// actually written. Counts and flags are derived from the finished buffer,
    /// never from intent, so truncation produces a smaller valid packet instead
    /// of one the receiver rejects.
    /// </summary>
    private static int BuildPayload(bool wantSpec, out byte blockFlags)
    {
        blockFlags = 0;

        int position = 0;
        int writtenSlots = 0;

        // The dynamic block is always emitted, even with zero slots. A received
        // block is an authoritative statement of which skills are active right
        // now, so a skill that stopped publishing goes idle on the receiver's
        // next packet instead of waiting out the freshness window.
        int slotCountOffset = position;
        position++; // backfilled below

        for (int i = 0; i < localSlotCount; i++)
        {
            int slotLength = localSlotLengths[i];

            if (position + 2 + slotLength > MaxPayloadBytes)
                break;

            scratch[position++] = localSlotIds[i];
            scratch[position++] = (byte)slotLength;

            if (slotLength > 0)
            {
                Buffer.BlockCopy(
                    localSlotBuffer,
                    i * MaxSlotBytes,
                    scratch,
                    position,
                    slotLength);

                position += slotLength;
            }

            writtenSlots++;
        }

        scratch[slotCountOffset] = (byte)writtenSlots;
        blockFlags |= BlockFlagDynamic;

        if (wantSpec && localSpecPacked != null && schema != null)
        {
            int specLength = SpecBlockHeaderBytes + localSpecPacked.Length;

            if (position + specLength <= MaxPayloadBytes)
            {
                scratch[position++] = (byte)(schemaHash & 0xFF);
                scratch[position++] = (byte)((schemaHash >> 8) & 0xFF);
                scratch[position++] = (byte)(localSpecContentHash & 0xFF);
                scratch[position++] = (byte)((localSpecContentHash >> 8) & 0xFF);
                scratch[position++] = (byte)((localSpecContentHash >> 16) & 0xFF);
                scratch[position++] = (byte)((localSpecContentHash >> 24) & 0xFF);
                scratch[position++] = (byte)schema.Length;

                Buffer.BlockCopy(
                    localSpecPacked,
                    0,
                    scratch,
                    position,
                    localSpecPacked.Length);

                position += localSpecPacked.Length;
                blockFlags |= BlockFlagSpec;
            }
        }

        return position;
    }

    /// <summary>
    /// True when this packet should carry the spec block: either the local
    /// specialization changed recently (burst) or the slow heartbeat is due.
    /// </summary>
    private static bool ShouldSendSpecBlock()
    {
        EnsureSchema();

        if (schema == null || schema.Length == 0)
            return false;

        int configurationRevision =
            LeviathanSpecializationRuntime.ConfigurationRevision;

        int registryRevision = LeviathanSpecializationRegistry.Revision;

        if (configurationRevision != lastSeenConfigurationRevision ||
            registryRevision != lastSeenRegistryRevision ||
            localSpecPacked == null)
        {
            lastSeenConfigurationRevision = configurationRevision;
            lastSeenRegistryRevision = registryRevision;

            if (RebuildLocalSpec())
                specBurstRemaining = SpecBurstPackets;
        }

        if (localSpecPacked == null)
            return false;

        if (specBurstRemaining > 0)
            return true;

        return (packetCounter % SpecHeartbeatPackets) == 0;
    }

    /// <summary>
    /// Packs the local pilot's ranks for every registered node into 4-bit
    /// fields. Returns true if the packed content differs from last time.
    /// </summary>
    private static bool RebuildLocalSpec()
    {
        Pilot pilot = LeviathanSpecializationRuntime.GetCurrentPilot();
        if (pilot == null)
            return false;

        int packedLength = (schema.Length + 1) / 2;

        if (packedLength > MaxPackedSpecBytes)
        {
            Debug.LogError(
                "[LeviathanNetwork] Packed specialization is " + packedLength +
                " bytes, above the " + MaxPackedSpecBytes + " limit.");

            localSpecPacked = null;
            return false;
        }

        if (localSpecPacked == null || localSpecPacked.Length != packedLength)
            localSpecPacked = new byte[packedLength];
        else
            Array.Clear(localSpecPacked, 0, packedLength);

        for (int i = 0; i < schema.Length; i++)
        {
            SchemaEntry entry = schema[i];

            LeviathanSpecializationState state =
                LeviathanSpecializationRuntime.GetState(pilot, entry.TreeId);

            int rank = state == null ? 0 : state.GetRank(entry.NodeId);
            rank = Mathf.Clamp(rank, 0, MaxPackedRank);

            if ((i & 1) == 0)
                localSpecPacked[i >> 1] |= (byte)(rank & 0x0F);
            else
                localSpecPacked[i >> 1] |= (byte)((rank & 0x0F) << 4);
        }

        uint hash = Fnv32(localSpecPacked, 0, packedLength);

        if (hash == localSpecContentHash)
            return false;

        localSpecContentHash = hash;
        return true;
    }

    // =========================================================================
    // READ PATH
    // =========================================================================

    /// <summary>
    /// Parses the trailing payload. Runs from the ShipStateSample.Read postfix,
    /// where the reader sits immediately past the native variable-length arrays,
    /// so no offset arithmetic or backward scanning is required.
    ///
    /// If the trailing bytes are not ours, or there are none, the reader is left
    /// exactly where the native read finished. That keeps the seam usable by
    /// another mod appending after us.
    ///
    /// This must never throw. An exception here reaches NetSession.HandleData,
    /// which registers a malformed-message strike against the sender and kicks
    /// them after ten.
    /// </summary>
    internal static void ParseIncomingExtension(BinaryReader reader)
    {
        ClearPending();

        if (reader == null)
            return;

        Stream stream = null;
        long startPosition = 0;

        try
        {
            stream = reader.BaseStream;

            if (stream == null || !stream.CanSeek)
                return;

            startPosition = stream.Position;

            if (stream.Length - startPosition < HeaderBytes)
            {
                stream.Position = startPosition;
                return;
            }

            if (reader.ReadByte() != Magic0 ||
                reader.ReadByte() != Magic1 ||
                reader.ReadByte() != ProtocolVersion)
            {
                // Not ours. Hand the seam back untouched.
                stream.Position = startPosition;
                return;
            }

            byte blockFlags = reader.ReadByte();
            int payloadLength = reader.ReadByte();

            if (payloadLength <= 0 ||
                payloadLength > MaxPayloadBytes ||
                stream.Length - stream.Position < payloadLength)
            {
                // The header claimed to be ours but is inconsistent, so the
                // bytes are ours and broken. Rewinding would not help another
                // reader; discard and leave the stream where it is.
                return;
            }

            long payloadEnd = stream.Position + payloadLength;

            if ((blockFlags & BlockFlagDynamic) != 0)
            {
                if (!ReadDynamicBlock(reader, payloadEnd))
                {
                    ClearPending();
                    return;
                }
            }

            if ((blockFlags & BlockFlagSpec) != 0)
                ReadSpecBlock(reader, payloadEnd);

            pendingValid = true;
        }
        catch (Exception)
        {
            // Malformed trailing data is discarded silently. Logging here would
            // spam at 20 Hz per player and does not help the receiver.
            ClearPending();

            try
            {
                if (stream != null && stream.CanSeek)
                    stream.Position = startPosition;
            }
            catch (Exception)
            {
            }
        }
    }

    private static bool ReadDynamicBlock(BinaryReader reader, long payloadEnd)
    {
        Stream stream = reader.BaseStream;

        if (stream.Position >= payloadEnd)
            return false;

        int slotCount = reader.ReadByte();

        if (slotCount > MaxSlots)
            return false;

        for (int i = 0; i < slotCount; i++)
        {
            if (payloadEnd - stream.Position < 2)
                return false;

            byte slotId = reader.ReadByte();
            int slotLength = reader.ReadByte();

            if (slotLength > MaxSlotBytes)
                return false;

            if (payloadEnd - stream.Position < slotLength)
                return false;

            int offset = i * MaxSlotBytes;

            if (slotLength > 0)
            {
                int read = reader.Read(pendingSlotData, offset, slotLength);
                if (read != slotLength)
                    return false;
            }

            pendingSlotIds[i] = slotId;
            pendingSlotLengths[i] = slotLength;
        }

        pendingSlotCount = slotCount;
        return true;
    }

    private static void ReadSpecBlock(BinaryReader reader, long payloadEnd)
    {
        Stream stream = reader.BaseStream;

        if (payloadEnd - stream.Position < SpecBlockHeaderBytes)
            return;

        int incomingSchemaHash =
            reader.ReadByte() |
            (reader.ReadByte() << 8);

        uint incomingContentHash =
            (uint)reader.ReadByte() |
            ((uint)reader.ReadByte() << 8) |
            ((uint)reader.ReadByte() << 16) |
            ((uint)reader.ReadByte() << 24);

        int nodeCount = reader.ReadByte();

        EnsureSchema();

        // A schema mismatch means the sender's compiled node ordering differs
        // from ours. ModsMatch should make this impossible; if it happens,
        // discarding is the only safe response, because applying these ranks
        // would assign them to the wrong nodes.
        if (schema == null ||
            incomingSchemaHash != schemaHash ||
            nodeCount != schema.Length)
        {
            return;
        }

        int packedLength = (nodeCount + 1) / 2;

        if (packedLength > MaxPackedSpecBytes)
            return;

        if (payloadEnd - stream.Position < packedLength)
            return;

        int read = reader.Read(pendingSpecPacked, 0, packedLength);
        if (read != packedLength)
            return;

        pendingHasSpec = true;
        pendingSpecHash = incomingContentHash;
        pendingSpecLength = packedLength;
    }

    /// <summary>
    /// Moves the parsed payload from the single-packet staging area into the
    /// per-player store. Runs from the TryDecodeShipState postfix, which is the
    /// first point where the player id is known.
    ///
    /// Committing here rather than in OnRemoteShipState matters because the host
    /// decodes every client's packet, including players in other stars where
    /// OnRemoteShipState is never called and staged data would go stale.
    /// </summary>
    internal static void CommitIncoming(int playerId, bool decodeSucceeded)
    {
        float now = Time.unscaledTime;

        try
        {
            if (!decodeSucceeded || !pendingValid || playerId < 0)
                return;

            if (NetSession.instance != null &&
                playerId == NetSession.instance.localPlayerId)
            {
                return;
            }

            RemoteSnapshot snapshot;
            if (!remoteSnapshots.TryGetValue(playerId, out snapshot))
            {
                snapshot = new RemoteSnapshot();
                remoteSnapshots[playerId] = snapshot;
            }

            snapshot.SlotCount = pendingSlotCount;
            snapshot.LastReceived = now;

            for (int i = 0; i < pendingSlotCount; i++)
            {
                snapshot.SlotIds[i] = pendingSlotIds[i];
                snapshot.SlotLengths[i] = pendingSlotLengths[i];

                if (pendingSlotLengths[i] > 0)
                {
                    Buffer.BlockCopy(
                        pendingSlotData,
                        i * MaxSlotBytes,
                        snapshot.SlotData,
                        i * MaxSlotBytes,
                        pendingSlotLengths[i]);
                }
            }

            if (pendingHasSpec && pendingSpecHash != snapshot.AppliedSpecHash)
            {
                Buffer.BlockCopy(
                    pendingSpecPacked,
                    0,
                    snapshot.PendingSpecPacked,
                    0,
                    pendingSpecLength);

                snapshot.PendingSpecLength = pendingSpecLength;
                snapshot.PendingSpecHash = pendingSpecHash;
                snapshot.HasPendingSpec = true;
            }
        }
        finally
        {
            ClearPending();
            PruneStaleSnapshots(now);
        }
    }

    /// <summary>
    /// Pushes a received spec set into the specialization runtime for a remote
    /// player's Pilot. Runs from the OnRemoteShipState postfix, which is the
    /// first point where both the player id and the replica ship are available.
    ///
    /// The state is registered as network-replicated, so the runtime treats it
    /// as transient: no persistence path is consulted for it and no file is ever
    /// written for a remote pilot.
    /// </summary>
    internal static void ApplyPendingSpecialization(int playerId, GameShip repShip)
    {
        RemoteSnapshot snapshot;
        if (!remoteSnapshots.TryGetValue(playerId, out snapshot))
            return;

        if (repShip == null)
            return;

        // Bind dynamic state to the exact replica immediately. This happens even
        // before the first spec block arrives, so DestroyRep can always identify
        // and clear the snapshot that belongs to this ship object.
        if (snapshot.RepShip != null &&
            !ReferenceEquals(snapshot.RepShip, repShip))
        {
            // A new replica appeared for the same network player id. Release the
            // specialization attached to the old Pilot, but preserve the
            // dynamic and pending-spec data already committed from this current
            // packet: those describe the replacement replica.
            playerIdByRep.Remove(snapshot.RepShip);

            if (snapshot.AppliedPilot != null)
            {
                LeviathanSpecializationRuntime.ClearRemoteSpecialization(
                    snapshot.AppliedPilot);
            }

            snapshot.AppliedPilot = null;
            snapshot.AppliedSpecHash = 0u;
        }

        snapshot.RepShip = repShip;
        playerIdByRep[repShip] = playerId;

        if (!snapshot.HasPendingSpec)
            return;

        Pilot pilot = GameShip.GetPlayerSourcePilot(repShip);
        if (pilot == null)
            return;

        // Never let a received payload be applied to the local pilot. Nothing in
        // the native routing should deliver our own player id back to us, but
        // the consequence of a mistake here is corrupting the local save's
        // in-memory specialization, so it is checked explicitly.
        Pilot localPilot = LeviathanSpecializationRuntime.GetCurrentPilot();
        if (localPilot != null && ReferenceEquals(localPilot, pilot))
        {
            snapshot.HasPendingSpec = false;
            return;
        }

        EnsureSchema();
        if (schema == null)
            return;

        try
        {
            // If this Pilot instance was replaced (the bridge rebuilds a rep
            // whenever its ship JSON changes), release the old one first.
            if (snapshot.AppliedPilot != null &&
                !ReferenceEquals(snapshot.AppliedPilot, pilot))
            {
                LeviathanSpecializationRuntime.ClearRemoteSpecialization(
                    snapshot.AppliedPilot);
            }

            byte[] packed = snapshot.PendingSpecPacked;

            LeviathanSpecializationRuntime.BeginRemoteSpecialization(pilot);

            for (int i = 0; i < schema.Length; i++)
            {
                SchemaEntry entry = schema[i];

                int rank = ((i & 1) == 0)
                    ? (packed[i >> 1] & 0x0F)
                    : ((packed[i >> 1] >> 4) & 0x0F);

                rank = Mathf.Clamp(rank, 0, entry.MaxRank);

                LeviathanSpecializationRuntime.SetRemoteRank(
                    pilot,
                    entry.TreeId,
                    entry.NodeId,
                    rank);
            }

            LeviathanSpecializationRuntime.EndRemoteSpecialization(pilot);

            snapshot.RepShip = repShip;
            snapshot.AppliedPilot = pilot;
            snapshot.AppliedSpecHash = snapshot.PendingSpecHash;
            snapshot.HasPendingSpec = false;
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                "[LeviathanNetwork] Failed applying remote specialization for player " +
                playerId + ": " + ex.Message);

            snapshot.HasPendingSpec = false;
        }
    }

    private static void ClearPending()
    {
        pendingValid = false;
        pendingSlotCount = 0;
        pendingHasSpec = false;
        pendingSpecHash = 0u;
        pendingSpecLength = 0;
    }

    // =========================================================================
    // SCHEMA
    // =========================================================================

    /// <summary>
    /// Builds the canonical node ordering that both sides index the packed ranks
    /// by. Registry order is already deterministic (DisplayOrder then Id), and
    /// node order within a tree is the tree file's Add order, so two builds of
    /// the same source produce the same schema. The hash makes any divergence
    /// detectable rather than silent.
    /// </summary>
    private static void EnsureSchema()
    {
        LeviathanSpecializationRuntime.RegisterDefaults();

        int registryRevision = LeviathanSpecializationRegistry.Revision;

        if (schema != null && schemaBuiltForRegistryRevision == registryRevision)
            return;

        List<SchemaEntry> entries = new List<SchemaEntry>();
        IList<LeviathanSpecializationTree> trees =
            LeviathanSpecializationRegistry.All();

        for (int t = 0; t < trees.Count; t++)
        {
            LeviathanSpecializationTree tree = trees[t];
            IList<LeviathanSpecializationNode> nodes = tree.Nodes;

            for (int n = 0; n < nodes.Count; n++)
            {
                // Auto-granted nodes are derived state. The receiver has both
                // the transmitted player choices and native-replicated unlock
                // ranks, and EndRemoteSpecialization rebuilds the resulting
                // roots. Sending them would only be overwritten on arrival.
                if (nodes[n].AutoGranted)
                    continue;

                SchemaEntry entry = new SchemaEntry();
                entry.TreeId = tree.Id;
                entry.NodeId = nodes[n].Id;
                entry.MaxRank = nodes[n].MaxRank;

                if (entry.MaxRank > MaxPackedRank)
                {
                    Debug.LogError(
                        "[LeviathanNetwork] Node " + tree.Id + "/" + entry.NodeId +
                        " has MaxRank " + entry.MaxRank + ", above the " +
                        MaxPackedRank + " the 4-bit wire format supports. " +
                        "Remote clients will see this node capped.");
                }

                entries.Add(entry);
            }
        }

        if (entries.Count > 255)
        {
            Debug.LogError(
                "[LeviathanNetwork] " + entries.Count +
                " specialization nodes exceeds the 255 the wire format supports.");

            entries.RemoveRange(255, entries.Count - 255);
        }

        schema = entries.ToArray();
        schemaBuiltForRegistryRevision = registryRevision;
        schemaHash = ComputeSchemaHash(schema);

        // Force a spec rebuild against the new ordering.
        localSpecPacked = null;
        localSpecContentHash = 0u;
    }

    private static ushort ComputeSchemaHash(SchemaEntry[] entries)
    {
        unchecked
        {
            uint hash = 2166136261u;

            for (int i = 0; i < entries.Length; i++)
            {
                hash = HashString(hash, entries[i].TreeId);
                hash = HashString(hash, "/");
                hash = HashString(hash, entries[i].NodeId);
                hash = HashString(hash, ";");
            }

            hash ^= (uint)entries.Length;
            hash *= 16777619u;

            return Fold16(hash);
        }
    }

    private static uint HashString(uint hash, string value)
    {
        unchecked
        {
            if (value == null)
                return hash;

            for (int i = 0; i < value.Length; i++)
            {
                hash ^= value[i];
                hash *= 16777619u;
            }

            return hash;
        }
    }

    /// <summary>
    /// Full 32-bit FNV-1a. Used for contentHash, where a collision between two
    /// legitimate rank sets would make a remote silently keep the owner's
    /// previous tree after a respec.
    /// </summary>
    private static uint Fnv32(byte[] data, int offset, int length)
    {
        unchecked
        {
            uint hash = 2166136261u;

            for (int i = 0; i < length; i++)
            {
                hash ^= data[offset + i];
                hash *= 16777619u;
            }

            // Zero is reserved as "nothing applied yet".
            return hash == 0u ? 1u : hash;
        }
    }

    /// <summary>
    /// 16-bit fold, used only for schemaHash. That value guards against a node
    /// ordering divergence that ModsMatch already makes impossible, so it is a
    /// sanity check rather than an identity that must not collide.
    /// </summary>
    private static ushort Fold16(uint hash)
    {
        unchecked
        {
            ushort folded = (ushort)((hash ^ (hash >> 16)) & 0xFFFF);

            return folded == 0 ? (ushort)1 : folded;
        }
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    private static NetWorldBridge GetActiveBridge()
    {
        if (ActiveBridgeField == null || NetSession.instance == null)
            return null;

        return ActiveBridgeField.GetValue(NetSession.instance) as NetWorldBridge;
    }

    private static Dictionary<int, RemoteShipDriver> GetReps()
    {
        NetWorldBridge bridge = GetActiveBridge();

        if (bridge == null || RepsField == null)
            return null;

        return RepsField.GetValue(bridge) as Dictionary<int, RemoteShipDriver>;
    }
}

// =============================================================================
// GENERIC COMBAT DAMAGE CODEC PATCHES
// =============================================================================

[HarmonyPatch(typeof(NetDamageCodec), "Write",
    new Type[] { typeof(NetSerializer), typeof(MsgDamageEvent) })]
public static class LeviathanNetworkCombatDamageEventWritePatch
{
    public static void Postfix(NetSerializer __0, MsgDamageEvent __1)
    {
        LeviathanNetwork.AppendCombatEventTrailer(__0, __1);
    }
}

[HarmonyPatch(typeof(NetDamageCodec), "ReadDamageEvent")]
public static class LeviathanNetworkCombatDamageEventReadPatch
{
    public static void Postfix(
        byte[] __0,
        int __1,
        MsgDamageEvent __result)
    {
        LeviathanNetwork.ParseCombatEventTrailer(__0, __1, __result);
    }
}

[HarmonyPatch(typeof(NetDamageCodec), "Write",
    new Type[] { typeof(NetSerializer), typeof(MsgDamageResult) })]
public static class LeviathanNetworkCombatDamageResultWritePatch
{
    public static void Postfix(NetSerializer __0, MsgDamageResult __1)
    {
        LeviathanNetwork.AppendCombatResultTrailer(__0, __1);
    }
}

[HarmonyPatch(typeof(NetDamageCodec), "ReadDamageResult")]
public static class LeviathanNetworkCombatDamageResultReadPatch
{
    public static void Postfix(
        byte[] __0,
        int __1,
        MsgDamageResult __result)
    {
        LeviathanNetwork.ParseCombatResultTrailer(__0, __1, __result);
    }
}

// =============================================================================
// HARMONY PATCHES
// =============================================================================

// Appends the Leviathan payload after the native sample's fields. Write has
// exactly one caller, NetWorldBridge.SendLocalState, which writes into the
// NetSerializer whose Length is then handed to NetSession.SendPlayerShipState.
// Appending here means the sent length is correct with no buffer surgery.
[HarmonyPatch(typeof(ShipStateSample), "Write")]
public static class LeviathanNetworkShipStateWritePatch
{
    public static void Postfix(BinaryWriter __0)
    {
        LeviathanNetwork.AppendLocalExtension(__0);
    }
}

// Parses the payload with the reader positioned immediately past the native
// variable-length arrays. Read has exactly one caller,
// NetWorldBridge.TryDecodeShipState.
[HarmonyPatch(typeof(ShipStateSample), "Read")]
public static class LeviathanNetworkShipStateReadPatch
{
    public static void Postfix(BinaryReader __0)
    {
        LeviathanNetwork.ParseIncomingExtension(__0);
    }
}

// First point at which the parsed payload can be associated with a player id.
[HarmonyPatch(typeof(NetWorldBridge), "TryDecodeShipState")]
public static class LeviathanNetworkDecodePatch
{
    public static void Postfix(bool __result, ref int __2)
    {
        LeviathanNetwork.CommitIncoming(__2, __result);
    }
}

// First point at which both the player id and that player's replica ship are
// available, so this is where a received specialization set is pushed into the
// runtime for the remote Pilot.
[HarmonyPatch(typeof(NetWorldBridge), "OnRemoteShipState")]
public static class LeviathanNetworkRemoteStatePatch
{
    private static readonly FieldInfo RepsField =
        AccessTools.Field(typeof(NetWorldBridge), "reps");

    public static void Postfix(NetWorldBridge __instance, int __0)
    {
        if (RepsField == null)
            return;

        Dictionary<int, RemoteShipDriver> reps =
            RepsField.GetValue(__instance) as Dictionary<int, RemoteShipDriver>;

        RemoteShipDriver driver;
        if (reps == null || !reps.TryGetValue(__0, out driver) || driver == null)
            return;

        LeviathanNetwork.ApplyPendingSpecialization(__0, driver.gameShip);
    }
}

// Every rep-removal path in NetWorldBridge funnels through DestroyRep, including
// Teardown, player-left, death and jump-out handling. Releasing the transient
// specialization here means no replicated ranks outlive the replica they
// describe.
[HarmonyPatch(typeof(RemoteShipDriver), "DestroyRep")]
public static class LeviathanNetworkDestroyRepPatch
{
    public static void Prefix(GameShip __0)
    {
        LeviathanNetwork.ForgetRep(__0);
    }
}

// Full session/world teardown.
[HarmonyPatch(typeof(NetWorldBridge), "Teardown")]
public static class LeviathanNetworkTeardownPatch
{
    public static void Postfix()
    {
        LeviathanNetwork.Reset();
    }
}
