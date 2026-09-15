using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Bounded owner-to-owner projectile capture transactions over the existing Core
/// reliable grant lane. Field announcements are gameplay hints, not replica
/// pools. A prepare holds the exact native projectile; the target earmarks its
/// capacity before authorization; only a confirmed capture can consume/heal.
/// Uncertain outcomes never time out into refunds. See the maintained guide.
/// </summary>
public static class CoreProjectileCapture
{
    public const ushort TransportEffectId = 0x0101;
    public const int ProviderLimit = 4;
    public const int PeerLimit = 16;
    public const int TransactionLimit = 64;
    public const int StartsPerSecond = 32;
    public const int SendsPerSecond = 192;
    public const int SendsPerTick = 12;
    public const float HoldSeconds = 1.25f;
    public const float RetrySeconds = 0.20f;
    public const float UncertainSeconds = 8f;
    private const float FieldInterval = 0.25f;
    private const float FieldLeaseSeconds = 1f;
    private const uint Version = 1u;

    public struct Field
    {
        public GameShip Ship;
        public uint Generation;
        public bool Active;
        public float RadiusWorld;
        public float RemainingSeconds;
    }
    public delegate bool ReadField(out Field field);
    public delegate bool Reserve(uint generation, float value, out ulong ticket);
    public delegate void Settle(uint generation, ulong ticket, bool captured);
    public delegate void Fault(uint generation);
    public delegate bool Eligible(GameShip recipient, Projectile projectile);
    private sealed class Provider
    {
        public byte Id;
        public ReadField Read;
        public Reserve Reserve;
        public Settle Settle;
        public Fault Fault;
        public Eligible Eligible;
        public uint Sequence;
    }
    private struct RemoteField
    {
        public bool Used, Active, ShipRetired;
        public byte Provider;
        public int Peer;
        public uint Generation, Sequence;
        public GameShip Ship;
        public float Radius, Deadline, LeaseUntil;
    }
    private enum Phase : byte { Announce = 1, Prepare = 2, Authorize = 3, Reject = 4,
        Captured = 5, Aborted = 6, Receipt = 7, Unknown = 8, Query = 9 }
    private enum OwnerPhase : byte { Empty, Held, Committing, Captured, Aborted, Unknown }
    private struct Owned
    {
        public OwnerPhase Phase;
        public byte Provider;
        public int Peer;
        public uint Generation, Id, NetId;
        public Projectile Projectile;
        public bool Enabled, Simulated;
        public Vector2 Velocity;
        public float AngularVelocity, Value, Deadline, NextSend;
    }
    private struct Incoming
    {
        public bool Used;
        public byte Provider;
        public int Peer;
        public uint Generation, Id, NetId;
        public ulong Ticket;
        public float StartedAt, NextSend;
    }
    private struct PeerWatermark
    {
        public bool Used;
        public int Peer;
        public uint Highest;
    }
    [StructLayout(LayoutKind.Explicit)]
    private struct FloatBits
    {
        [FieldOffset(0)] public float Float;
        [FieldOffset(0)] public uint UInt;
    }

    private static readonly Provider[] providers = new Provider[ProviderLimit];
    private static readonly RemoteField[] remote = new RemoteField[ProviderLimit * PeerLimit];
    private static readonly Owned[] owned = new Owned[TransactionLimit];
    private static readonly Incoming[] incoming = new Incoming[TransactionLimit];
    private static readonly PeerWatermark[] watermarks = new PeerWatermark[PeerLimit];
    private static uint nextId;
    private static int starts, sends, tickSends, ownerCursor, incomingCursor;
    private static float rateWindow, announceAt;
    private static int announceProvider, announcePeer;
    private static bool initialized;
    public static int PendingOwnerCount { get; private set; }
    public static int PendingRecipientCount { get; private set; }
    public static int ConfirmedCaptures { get; private set; }
    public static int RejectedCaptures { get; private set; }

    public static void Register(byte id, ReadField read, Reserve reserve, Settle settle,
        Fault fault, Eligible eligible)
    {
        if (id == 0 || id > ProviderLimit || read == null || reserve == null ||
            settle == null || fault == null || eligible == null)
            throw new ArgumentException("Invalid capture provider.");
        if (providers[id - 1] != null)
            throw new InvalidOperationException("Duplicate capture provider.");
        providers[id - 1] = new Provider { Id = id, Read = read, Reserve = reserve,
            Settle = settle, Fault = fault, Eligible = eligible };
        if (!initialized)
        {
            CoreCrossOwnerEffects.RegisterHandler(TransportEffectId, Receive);
            initialized = true;
        }
    }
    private static Provider Get(byte id)
    {
        return id > 0 && id <= ProviderLimit ? providers[id - 1] : null;
    }
    public static bool HasFields
    {
        get
        {
            Field f;
            for (int i = 0; i < providers.Length; i++)
                if (providers[i] != null && providers[i].Read(out f) && f.Active) return true;
            for (int i = 0; i < remote.Length; i++)
                if (remote[i].Used && remote[i].Active && !remote[i].ShipRetired &&
                    Time.unscaledTime < remote[i].LeaseUntil && Time.time < remote[i].Deadline)
                    return true;
            return false;
        }
    }
    private static bool ValidProjectile(Projectile p)
    {
        if (p == null || p.netRendered || p.IsDestroying() || p.rigidBody == null) return false;
        CapturedProjectile captured = p as CapturedProjectile;
        return captured == null || (!captured.IsOrbiting() && !captured.IsDrawn());
    }
    public static bool IsHeld(Projectile p)
    {
        if (CoreProjectileSpawnGuard.IsHeld(p)) return true;
        for (int i = 0; i < owned.Length; i++)
            if (owned[i].Phase == OwnerPhase.Held && ReferenceEquals(owned[i].Projectile, p))
                return true;
        return false;
    }

    /// <summary>Try fields in native travel order, with deterministic peer/provider
    /// tie-breaking. Rejected fields do not erase an earlier native obstacle.</summary>
    public static bool TrySweep(Projectile p, Vector2 origin, Vector2 direction,
        float distance, float projectileRadius, out float contactDistance)
    {
        contactDistance = 0f;
        if (!ValidProjectile(p) || IsHeld(p) || !Finite(distance) || distance < 0f) return false;
        Vector2 unit = direction.sqrMagnitude > 0f ? direction.normalized : Vector2.zero;
        int previousKey = -1;
        float previousDistance = -1f;
        int localId = NetSession.InSession && NetSession.instance != null
            ? NetSession.instance.localPlayerId : 0;
        for (int pass = 0; pass < remote.Length + providers.Length; pass++)
        {
            float best = float.PositiveInfinity;
            int bestKey = int.MaxValue, bestPeer = -1;
            uint generation = 0;
            Provider provider = null;
            GameShip recipient = null;
            for (int i = 0; i < providers.Length; i++)
            {
                Provider candidate = providers[i];
                Field field;
                if (candidate == null || !candidate.Read(out field) || !field.Active ||
                    field.Ship == null || !candidate.Eligible(field.Ship, p)) continue;
                int key = localId * ProviderLimit + candidate.Id;
                float at;
                if (!CoreCaptureGeometry.CircleEntry(origin.x, origin.y, unit.x, unit.y,
                        distance, field.Ship.transform.position.x, field.Ship.transform.position.y,
                        field.RadiusWorld + Mathf.Max(0f, projectileRadius), out at) ||
                    !After(at, key, previousDistance, previousKey) || !Earlier(at, key, best, bestKey)) continue;
                best = at; bestKey = key; bestPeer = localId; provider = candidate;
                generation = field.Generation; recipient = field.Ship;
            }
            for (int i = 0; i < remote.Length; i++)
            {
                RemoteField f = remote[i];
                if (!f.Used || !f.Active || f.ShipRetired || f.Ship == null ||
                    Time.unscaledTime >= f.LeaseUntil || Time.time >= f.Deadline) continue;
                Provider candidate = Get(f.Provider);
                if (candidate == null || !candidate.Eligible(f.Ship, p)) continue;
                int key = f.Peer * ProviderLimit + f.Provider;
                float at;
                if (!CoreCaptureGeometry.CircleEntry(origin.x, origin.y, unit.x, unit.y,
                        distance, f.Ship.transform.position.x, f.Ship.transform.position.y,
                        f.Radius + Mathf.Max(0f, projectileRadius), out at) ||
                    !After(at, key, previousDistance, previousKey) || !Earlier(at, key, best, bestKey)) continue;
                best = at; bestKey = key; bestPeer = f.Peer; provider = candidate;
                generation = f.Generation; recipient = f.Ship;
            }
            if (provider == null) return false;
            previousDistance = best; previousKey = bestKey;
            float value;
            if (!TryNominalValue(p, recipient, out value)) continue;
            Vector2 point = origin + unit * best;
            if (CoreProjectileSpawnGuard.IsInitializing(p))
            {
                if (!CoreProjectileSpawnGuard.Defer(p, point, projectileRadius)) return false;
                contactDistance = best;
                return true;
            }
            if (bestPeer == localId)
            {
                ulong ticket;
                if (!provider.Reserve(generation, value, out ticket)) continue;
                // Earmarking precedes native despawn and all possible callbacks.
                p.rigidBody.position = point;
                p.transform.position = new Vector3(point.x, point.y, p.transform.position.z);
                try { p.CaptureDestroy(); }
                catch { provider.Fault(generation); throw; }
                provider.Settle(generation, ticket, true);
                ConfirmedCaptures++;
                contactDistance = best;
                return true;
            }
            if (StartRemote(provider.Id, bestPeer, generation, p, value, point))
            {
                contactDistance = best;
                return true;
            }
        }
        return false;
    }
    private static bool Earlier(float a, int ak, float b, int bk)
    { return a < b || (a == b && ak < bk); }
    private static bool After(float a, int ak, float b, int bk)
    { return a > b || (a == b && ak > bk); }

    // Captured projectiles carry an explicit flat budget and may add recipient
    // max-hull percentage. Do not use transient GravityCannon shot context or
    // roll a hypothetical crit/AoE victim count to invent a cost.
    private static readonly FieldInfo CapturedPercent =
        AccessTools.Field(typeof(CapturedProjectile), "capturedDamagePercentage");
    private static readonly FieldInfo CannonBonus =
        AccessTools.Field(typeof(GravityCannon), "currentShotBonusDamage");
    public static bool TryNominalValue(Projectile p, GameShip recipient, out float value)
    {
        value = 0f;
        Launcher launcher = p == null ? null : p.GetParentLauncher();
        if (launcher == null) return false;
        value = launcher.Damage;
        CapturedProjectile captured = p as CapturedProjectile;
        if (captured != null)
        {
            if (captured.IsOrbiting() || captured.IsDrawn() || recipient == null ||
                CapturedPercent == null || CannonBonus == null || !(launcher is GravityCannon)) return false;
            value -= (float)CannonBonus.GetValue(launcher);
            value += captured.CapturedFlatDamage + Mathf.Max(0f,
                (float)CapturedPercent.GetValue(captured)) * recipient.HealthMax;
        }
        return Finite(value) && value > 0f;
    }

    private static bool StartRemote(byte provider, int peer, uint generation,
        Projectile p, float value, Vector2 point)
    {
        RefreshRates();
        if (starts >= StartsPerSecond || PendingOwnerCount >= TransactionLimit ||
            NetSession.instance == null || !NetSession.InSession) return false;
        int index = -1;
        for (int i = 0; i < owned.Length; i++)
            if (owned[i].Phase == OwnerPhase.Empty) { index = i; break; }
        if (index < 0) return false;
        do { unchecked { nextId++; } } while (nextId == 0u);
        Owned t = new Owned { Phase = OwnerPhase.Held, Provider = provider, Peer = peer,
            Generation = generation, Id = nextId, NetId = p.netId, Projectile = p,
            Enabled = p.enabled, Simulated = p.rigidBody.simulated,
            Velocity = p.rigidBody.velocity, AngularVelocity = p.rigidBody.angularVelocity,
            Value = value, Deadline = Time.unscaledTime + HoldSeconds };
        // Record BEFORE dispatch: host targeting can synchronously reenter.
        owned[index] = t;
        PendingOwnerCount++;
        starts++;
        p.rigidBody.position = point;
        p.transform.position = new Vector3(point.x, point.y, p.transform.position.z);
        p.enabled = false;
        p.rigidBody.simulated = false;
        SendOwner(index);
        return true;
    }

    private static bool Receive(GameShip target, int source, CoreCrossOwnerEffects.GrantPayload p)
    {
        byte providerId = (byte)(p.F & 0xffu);
        Phase phase = (Phase)((p.F >> 8) & 0xffu);
        if ((p.F >> 16) != Version || source < 0 || Get(providerId) == null ||
            p.A == 0u || target == null || !target.IsPlayer()) return false;
        Provider provider = Get(providerId);
        if (phase == Phase.Announce) return ReceiveField(source, providerId, p);
        if (p.B == 0u || p.E != 0u || phase < Phase.Prepare || phase > Phase.Query ||
            (phase != Phase.Prepare && p.D != 0u)) return false;
        if (phase == Phase.Prepare)
        {
            float value = Unpack(p.D);
            if (!Finite(value) || value <= 0f || (p.C != 0u &&
                (!NetIds.IsProjectileNetId(p.C) || NetIds.ProjectileOwnerOf(p.C) != source))) return false;
            int existing = FindIncoming(source, providerId, p.A, p.B);
            if (existing >= 0)
            {
                Incoming old = incoming[existing];
                if (old.NetId != p.C) return false;
                Send(source, providerId, Phase.Authorize, old.Generation, old.Id, old.NetId, 0u);
                return true;
            }
            if (!AcceptNewId(source, p.B))
            {
                Send(source, providerId, Phase.Reject, p.A, p.B, p.C, 0u);
                return true;
            }
            int free = -1;
            for (int i = 0; i < incoming.Length; i++) if (!incoming[i].Used) { free = i; break; }
            ulong ticket;
            if (free < 0 || !provider.Reserve(p.A, value, out ticket))
            {
                RejectedCaptures++;
                Send(source, providerId, Phase.Reject, p.A, p.B, p.C, 0u);
                return true;
            }
            incoming[free] = new Incoming { Used = true, Provider = providerId, Peer = source,
                Generation = p.A, Id = p.B, NetId = p.C, Ticket = ticket,
                StartedAt = Time.unscaledTime, NextSend = Time.unscaledTime + RetrySeconds };
            PendingRecipientCount++;
            Send(source, providerId, Phase.Authorize, p.A, p.B, p.C, 0u);
            return true;
        }
        if (phase == Phase.Captured || phase == Phase.Aborted || phase == Phase.Unknown)
        {
            int index = FindIncoming(source, providerId, p.A, p.B);
            if (index >= 0)
            {
                Incoming t = incoming[index];
                if (t.NetId != p.C) return false;
                incoming[index] = default(Incoming);
                PendingRecipientCount--;
                // Retire the exchange BEFORE recovery/presentation callbacks.
                if (phase == Phase.Unknown) provider.Fault(t.Generation);
                else provider.Settle(t.Generation, t.Ticket, phase == Phase.Captured);
            }
            // An abort may arrive before a queued prepare; tombstone it, too.
            RememberId(source, p.B);
            Send(source, providerId, Phase.Receipt, p.A, p.B, p.C, 0u);
            return true;
        }
        int ownerIndex = FindOwned(source, providerId, p.A, p.B);
        if (ownerIndex < 0) return true; // old generation/tombstone, never touch a replacement
        Owned transaction = owned[ownerIndex];
        if (transaction.NetId != p.C) return false;
        if (phase == Phase.Receipt)
        {
            if (transaction.Phase == OwnerPhase.Captured || transaction.Phase == OwnerPhase.Aborted ||
                transaction.Phase == OwnerPhase.Unknown)
            {
                owned[ownerIndex] = default(Owned);
                PendingOwnerCount--;
            }
            return true;
        }
        if (phase == Phase.Query) { SendOwner(ownerIndex); return true; }
        if (phase == Phase.Reject)
        {
            AbortOwner(ownerIndex);
            SendOwner(ownerIndex);
            return true;
        }
        if (phase != Phase.Authorize) return false;
        if (transaction.Phase != OwnerPhase.Held) { SendOwner(ownerIndex); return true; }
        Projectile projectile = transaction.Projectile;
        if (Time.unscaledTime >= transaction.Deadline || !ValidProjectile(projectile) ||
            (transaction.NetId != 0u && projectile.netId != transaction.NetId))
        {
            AbortOwner(ownerIndex); SendOwner(ownerIndex); return true;
        }
        // Allow native pooling callbacks without turning our own capture into an abort.
        transaction.Phase = OwnerPhase.Committing;
        owned[ownerIndex] = transaction;
        RestoreNative(transaction);
        try
        {
            projectile.CaptureDestroy();
            transaction.Phase = OwnerPhase.Captured;
            ConfirmedCaptures++;
        }
        catch (Exception ex)
        {
            transaction.Phase = OwnerPhase.Unknown;
            Debug.LogError("[CoreProjectileCapture] Native capture has uncertain outcome: " + ex);
        }
        transaction.Projectile = null;
        transaction.NextSend = 0f;
        owned[ownerIndex] = transaction;
        SendOwner(ownerIndex);
        return true;
    }

    private static bool ReceiveField(int source, byte provider, CoreCrossOwnerEffects.GrantPayload p)
    {
        float radius = Unpack(p.C), remaining = Unpack(p.D);
        if (p.B == 0u || p.E != 0u || !Finite(radius) || !Finite(remaining) ||
            radius < 0f || radius > 100000f || remaining < 0f || remaining > 3600f) return false;
        int index = -1, free = -1;
        for (int i = 0; i < remote.Length; i++)
        {
            if (!remote[i].Used && free < 0) free = i;
            if (remote[i].Used && remote[i].Peer == source && remote[i].Provider == provider) { index = i; break; }
        }
        if (index < 0) index = free;
        if (index < 0) return false;
        RemoteField old = remote[index];
        if (old.Used && !Newer(p.B, old.Sequence)) return true;
        if (old.Used && old.Generation != p.A && !Newer(p.A, old.Generation)) return true;
        GameShip ship;
        CoreProjectileAuthority.TryGetPlayerShip(source, out ship);
        bool same = old.Used && old.Generation == p.A;
        bool retired = same && (old.ShipRetired || (old.Ship != null && !ReferenceEquals(old.Ship, ship)));
        remote[index] = new RemoteField { Used = true, Provider = provider, Peer = source,
            Generation = p.A, Sequence = p.B, Ship = ship,
            Active = radius > 0f && remaining > 0f, ShipRetired = retired,
            Radius = radius, LeaseUntil = Time.unscaledTime + FieldLeaseSeconds,
            Deadline = same ? Mathf.Min(old.Deadline, Time.time + remaining) : Time.time + remaining };
        return true;
    }

    public static void Tick()
    {
        if (!initialized) return;
        RefreshRates();
        tickSends = 0;
        float now = Time.unscaledTime;
        CoreProjectileSpawnGuard.Flush();
        RefreshRemoteShips();
        // Field hints receive first opportunity once per interval; transaction
        // retries retain the remaining budget. Neither class can starve forever.
        if (now >= announceAt) AnnounceFields(now);
        ownerCursor = (ownerCursor + 1) % owned.Length;
        incomingCursor = (incomingCursor + 1) % incoming.Length;
        for (int n = 0; n < owned.Length; n++)
        {
            int i = (ownerCursor + n) % owned.Length;
            Owned t = owned[i];
            if (t.Phase == OwnerPhase.Empty) continue;
            if (t.Phase == OwnerPhase.Held && (now >= t.Deadline || !ValidProjectile(t.Projectile)))
                AbortOwner(i);
            if (now >= owned[i].NextSend) SendOwner(i);
        }
        for (int n = 0; n < incoming.Length; n++)
        {
            int i = (incomingCursor + n) % incoming.Length;
            Incoming t = incoming[i];
            if (!t.Used) continue;
            if (now - t.StartedAt > UncertainSeconds)
            {
                // Fail closed for this old field. Do not refund a capture which
                // may already have disappeared on its simulator.
                incoming[i] = default(Incoming); PendingRecipientCount--;
                Get(t.Provider).Fault(t.Generation);
                continue;
            }
            if (now >= t.NextSend)
            {
                incoming[i].NextSend = now + RetrySeconds;
                Send(t.Peer, t.Provider, Phase.Query, t.Generation, t.Id, t.NetId, 0u);
                Send(t.Peer, t.Provider, Phase.Authorize, t.Generation, t.Id, t.NetId, 0u);
            }
        }
    }

    private static void RefreshRemoteShips()
    {
        for (int i = 0; i < remote.Length; i++)
        {
            if (!remote[i].Used || !remote[i].Active || remote[i].ShipRetired ||
                Time.unscaledTime >= remote[i].LeaseUntil) continue;
            GameShip ship;
            CoreProjectileAuthority.TryGetPlayerShip(remote[i].Peer, out ship);
            if (remote[i].Ship != null && !ReferenceEquals(remote[i].Ship, ship))
                remote[i].ShipRetired = true;
            else remote[i].Ship = ship;
        }
    }
    private static void AnnounceFields(float now)
    {
        NetSession session = NetSession.instance;
        if (!NetSession.InSession || session == null) return;
        // Persist the cursor when the send budget is exhausted so later players
        // are not permanently starved by an earlier peer/provider pair.
        while (announceProvider < providers.Length)
        {
            Provider provider = providers[announceProvider];
            Field field;
            if (provider == null || !provider.Read(out field) || field.Generation == 0u)
            { announceProvider++; announcePeer = 0; continue; }
            if (announcePeer == 0) do { unchecked { provider.Sequence++; } } while (provider.Sequence == 0u);
            while (announcePeer < session.players.Count && announcePeer < PeerLimit)
            {
                NetPlayer peer = session.players[announcePeer];
                if (peer == null || peer.playerId == session.localPlayerId) { announcePeer++; continue; }
                if (!CanSend()) return;
                Send(peer.playerId, provider.Id, Phase.Announce, field.Generation, provider.Sequence,
                    Pack(field.Active ? field.RadiusWorld : 0f),
                    Pack(field.Active ? Mathf.Clamp(field.RemainingSeconds, 0f, 3600f) : 0f));
                announcePeer++;
            }
            announceProvider++; announcePeer = 0;
        }
        announceProvider = announcePeer = 0;
        announceAt = now + FieldInterval;
    }
    public static void FieldChanged() { announceAt = 0f; }

    private static void SendOwner(int index)
    {
        Owned t = owned[index];
        if (t.Phase == OwnerPhase.Empty || t.Phase == OwnerPhase.Committing) return;
        Phase phase = t.Phase == OwnerPhase.Held ? Phase.Prepare :
            t.Phase == OwnerPhase.Captured ? Phase.Captured :
            t.Phase == OwnerPhase.Aborted ? Phase.Aborted : Phase.Unknown;
        // Assign before dispatch; synchronous receipt can clear this exact slot.
        owned[index].NextSend = Time.unscaledTime + RetrySeconds;
        Send(t.Peer, t.Provider, phase, t.Generation, t.Id, t.NetId,
            phase == Phase.Prepare ? Pack(t.Value) : 0u);
    }
    private static bool Send(int peer, byte provider, Phase phase, uint generation,
        uint id, uint c, uint d)
    {
        if (!CanSend()) return false;
        sends++; tickSends++;
        return CoreCrossOwnerEffects.RequestGrant(peer, TransportEffectId,
            new CoreCrossOwnerEffects.GrantPayload(generation, id, c, d, 0u,
                (Version << 16) | ((uint)phase << 8) | provider));
    }
    private static bool CanSend()
    { RefreshRates(); return sends < SendsPerSecond && tickSends < SendsPerTick; }
    private static void RefreshRates()
    {
        if (Time.unscaledTime - rateWindow >= 1f || Time.unscaledTime < rateWindow)
        { rateWindow = Time.unscaledTime; sends = starts = 0; }
    }
    private static int FindOwned(int peer, byte provider, uint generation, uint id)
    {
        for (int i = 0; i < owned.Length; i++)
            if (owned[i].Phase != OwnerPhase.Empty && owned[i].Peer == peer &&
                owned[i].Provider == provider && owned[i].Generation == generation && owned[i].Id == id) return i;
        return -1;
    }
    private static int FindIncoming(int peer, byte provider, uint generation, uint id)
    {
        for (int i = 0; i < incoming.Length; i++)
            if (incoming[i].Used && incoming[i].Peer == peer && incoming[i].Provider == provider &&
                incoming[i].Generation == generation && incoming[i].Id == id) return i;
        return -1;
    }
    private static bool AcceptNewId(int peer, uint id)
    {
        int free = -1;
        for (int i = 0; i < watermarks.Length; i++)
        {
            if (!watermarks[i].Used) { if (free < 0) free = i; continue; }
            if (watermarks[i].Peer != peer) continue;
            if (!Newer(id, watermarks[i].Highest)) return false;
            watermarks[i].Highest = id; return true;
        }
        if (free < 0) return false;
        watermarks[free] = new PeerWatermark { Used = true, Peer = peer, Highest = id };
        return true;
    }
    private static void RememberId(int peer, uint id) { AcceptNewId(peer, id); }
    private static bool Newer(uint a, uint b) { return unchecked((int)(a - b)) > 0; }
    private static void RestoreNative(Owned t)
    {
        Projectile p = t.Projectile;
        if (p == null) return;
        p.enabled = t.Enabled;
        if (p.rigidBody != null)
        {
            p.rigidBody.simulated = t.Simulated;
            p.rigidBody.velocity = t.Velocity;
            p.rigidBody.angularVelocity = t.AngularVelocity;
        }
    }
    private static void AbortOwner(int index)
    {
        Owned t = owned[index];
        if (t.Phase != OwnerPhase.Held) return;
        RestoreNative(t);
        t.Projectile = null;
        t.Phase = OwnerPhase.Aborted;
        t.NextSend = 0f;
        owned[index] = t;
    }
    public static void ForgetProjectile(Projectile p)
    {
        if (ReferenceEquals(p, null)) return;
        CoreProjectileSpawnGuard.Forget(p);
        for (int i = 0; i < owned.Length; i++)
            if (owned[i].Phase == OwnerPhase.Held && ReferenceEquals(owned[i].Projectile, p)) AbortOwner(i);
    }
    public static void ForgetShip(GameShip ship)
    {
        for (int i = 0; i < remote.Length; i++)
            if (remote[i].Used && ReferenceEquals(remote[i].Ship, ship)) remote[i].ShipRetired = true;
    }
    public static void Retire(byte provider, uint generation)
    {
        for (int i = 0; i < incoming.Length; i++)
            if (incoming[i].Used && incoming[i].Provider == provider && incoming[i].Generation == generation)
            { incoming[i] = default(Incoming); PendingRecipientCount--; }
        FieldChanged();
    }
    public static void ResetWorld()
    {
        CoreProjectileSpawnGuard.Reset();
        for (int i = 0; i < owned.Length; i++) AbortOwner(i);
        Array.Clear(owned, 0, owned.Length);
        Array.Clear(incoming, 0, incoming.Length);
        Array.Clear(remote, 0, remote.Length);
        Array.Clear(watermarks, 0, watermarks.Length);
        PendingOwnerCount = PendingRecipientCount = 0;
        announceAt = 0f; announceProvider = announcePeer = 0;
        // nextId and provider.Sequence deliberately survive world replacement.
    }
    private static uint Pack(float value) { return new FloatBits { Float = value }.UInt; }
    private static float Unpack(uint value) { return new FloatBits { UInt = value }.Float; }
    private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
}

/// <summary>Pure swept-circle geometry used by the native-query adapter and the
/// actual regression executable; directions must be normalized or zero.</summary>
public static class CoreCaptureGeometry
{
    public static bool CircleEntry(float x, float y, float dx, float dy, float distance,
        float cx, float cy, float radius, out float at)
    {
        at = 0f;
        if (!Finite(x) || !Finite(y) || !Finite(dx) || !Finite(dy) || !Finite(distance) ||
            !Finite(cx) || !Finite(cy) || !Finite(radius) || distance < 0f || radius <= 0f) return false;
        double ox = (double)x - cx, oy = (double)y - cy;
        double c = ox * ox + oy * oy - (double)radius * radius;
        if (c <= 0d) return true;
        double a = (double)dx * dx + (double)dy * dy;
        if (a <= 0d) return false;
        double b = ox * dx + oy * dy;
        double discriminant = b * b - a * c;
        if (discriminant < 0d) return false;
        double entry = (-b - Math.Sqrt(discriminant)) / a;
        if (entry < 0d || entry > distance) return false;
        at = (float)entry;
        return true;
    }
    private static bool Finite(float f) { return !float.IsNaN(f) && !float.IsInfinity(f); }
}

[HarmonyPatch(typeof(Projectile), "ResetObject")]
public static class CoreCaptureProjectileResetPatch
{
    public static void Prefix(Projectile __instance) { CoreProjectileCapture.ForgetProjectile(__instance); }
}
[HarmonyPatch(typeof(Projectile), "PoolDestroy")]
public static class CoreCaptureProjectilePoolPatch
{
    public static void Prefix(Projectile __instance) { CoreProjectileCapture.ForgetProjectile(__instance); }
}
