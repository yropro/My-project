using HarmonyLib;
using StarVortex;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Owner-published / remote-only presentation for the original Orrery spell trio.
///
/// The hidden native adapters used by Magma Cannon, Tesla Coil and Cone of Cold
/// do not occupy real replicated player slots, so their local Unity objects are
/// intentionally invisible to peers. This layer samples only the irreducible
/// presentation result of those adapters and reconstructs mechanically inert VFX
/// and SFX on remote clients. Damage, status, target selection, cooldowns and
/// authoritative projectile/beam behavior remain owner-only.
/// </summary>
public static class OrreryLegacySpellPresentation
{
    public static class Tuning
    {
        public const float DiscreteEventPublishSeconds = 0.75f;
        public const float RemoteGenerationRetentionSeconds = 2.0f;
        public const float MagmaPositionFollowPerSecond = 24f;
        public const int MaxTeslaSegments = 3;
        public const int MaxCryoShards = 45;
        public const float TeslaStopCleanupSeconds = 1.0f;
    }

    private const string InfernoCannonPath = "Base/Items/PrimaryWeapon/Inferno Cannon";
    private const string CryoGunPath = "Base/Items/PrimaryWeapon/Cryo Gun";
    private const string TeslaCoilPath = "Base/Items/PrimaryWeapon/Tesla Coil";

    private const byte GroupId = 0;
    private const int MagmaPreferredRecord = 0;
    private const int TeslaPreferredRecord = 0;
    private const int CryoPreferredRecord = 0;

    private const byte MagmaFlagProjectile = 1 << 0;
    private const byte MagmaFlagExplosion = 1 << 1;

    private sealed class OwnerCapture
    {
        public uint MagmaGeneration;
        public bool MagmaPendingProjectile;
        public Projectile MagmaProjectile;
        public Vector2 MagmaExplosionPosition;
        public float MagmaExplosionPublishUntil;

        public uint TeslaGeneration;
        public BeamWeapon TeslaWeapon;

        public uint CryoGeneration;
        public Vector2 CryoOrigin;
        public float CryoAimDegrees;
        public float CryoPublishUntil;
    }

    private sealed class RemoteProjectileVisual
    {
        public Projectile Projectile;
        public Rigidbody2D Body;
        public bool ProjectileEnabled;
        public bool BodySimulated;
        public Vector3 BaseScale;
    }

    private struct RemoteCryoShard
    {
        public RemoteProjectileVisual Visual;
        public Vector2 Velocity;
        public float ExpiresAt;
        public bool Active;
    }

    private sealed class RemoteState
    {
        public uint MagmaGeneration;
        public uint MagmaExplosionGeneration;
        public RemoteProjectileVisual Magma;

        public uint TeslaGeneration;
        public readonly GameObject[] TeslaObjects =
            new GameObject[Tuning.MaxTeslaSegments];
        public readonly LineRenderer[] TeslaLines =
            new LineRenderer[Tuning.MaxTeslaSegments];
        public int TeslaSegmentCount;
        public GameObject TeslaAudioObject;
        public SoundEffectPlayer TeslaAudioPlayer;

        public uint CryoGeneration;
        public Vector2 CryoOrigin;
        public float CryoBaseAimDegrees;
        public float CryoNextWaveAt;
        public int CryoNextWaveIndex;
        public readonly RemoteCryoShard[] CryoShards =
            new RemoteCryoShard[Tuning.MaxCryoShards];

        public float LastObservedAt;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct FloatBits
    {
        [FieldOffset(0)] public float Value;
        [FieldOffset(0)] public uint Bits;
    }

    private static readonly Dictionary<GameShip, OwnerCapture> owners =
        new Dictionary<GameShip, OwnerCapture>(4);
    private static readonly Dictionary<Projectile, GameShip> magmaOwners =
        new Dictionary<Projectile, GameShip>(4);
    private static readonly Dictionary<GameShip, RemoteState> remotes =
        new Dictionary<GameShip, RemoteState>(4);

    private static readonly byte[] payload = new byte[128];
    private static readonly Vector2[] teslaStarts =
        new Vector2[Tuning.MaxTeslaSegments];
    private static readonly Vector2[] teslaEnds =
        new Vector2[Tuning.MaxTeslaSegments];

    private static readonly FieldInfo RuntimeOwnersField =
        AccessTools.Field(typeof(OrrerySpellRuntime), "owners");
    private static readonly Type RuntimeOwnerStateType =
        typeof(OrrerySpellRuntime).GetNestedType(
            "OwnerState",
            BindingFlags.NonPublic);
    private static readonly Type RuntimeVirtualWeaponType =
        typeof(OrrerySpellRuntime).GetNestedType(
            "VirtualWeapon",
            BindingFlags.NonPublic);
    private static readonly FieldInfo RuntimeTeslaField =
        RuntimeOwnerStateType == null
            ? null
            : AccessTools.Field(RuntimeOwnerStateType, "Tesla");
    private static readonly FieldInfo RuntimeVirtualWeaponField =
        RuntimeVirtualWeaponType == null
            ? null
            : AccessTools.Field(RuntimeVirtualWeaponType, "Weapon");
    private static readonly FieldInfo BeamScriptField =
        AccessTools.Field(typeof(BeamWeapon), "beamScript");
    private static readonly FieldInfo BeamLineField =
        AccessTools.Field(typeof(Beam), "beamLineRenderer");
    private static readonly FieldInfo BeamSubBeamField =
        AccessTools.Field(typeof(Beam), "subBeam");

    private static LauncherItemBase infernoBase;
    private static LauncherItemBase cryoBase;
    private static BeamWeaponItemBase teslaBase;
    private static float cryoVisualSpeedWorld;
    private static bool cryoSpeedResolved;
    private static bool warnedRuntimeReflection;

    private static uint magmaGenerationCounter;
    private static uint teslaGenerationCounter;
    private static uint cryoGenerationCounter;

    public static void BeginMagma(GameShip owner)
    {
        if (!IsLocalOrreryOwner(owner))
            return;

        OwnerCapture capture = GetOrCreateOwner(owner);
        if (capture.MagmaProjectile != null)
            magmaOwners.Remove(capture.MagmaProjectile);

        capture.MagmaGeneration = NextGeneration(ref magmaGenerationCounter);
        capture.MagmaPendingProjectile = true;
        capture.MagmaProjectile = null;
        capture.MagmaExplosionPublishUntil = 0f;
    }

    public static void CompleteMagmaAttempt(GameShip owner, bool succeeded)
    {
        OwnerCapture capture;
        if (owner == null || !owners.TryGetValue(owner, out capture) || capture == null)
            return;

        if (!succeeded)
        {
            capture.MagmaPendingProjectile = false;
            if (capture.MagmaProjectile != null)
                magmaOwners.Remove(capture.MagmaProjectile);
            capture.MagmaProjectile = null;
        }
    }

    public static void ObserveProjectile(Launcher launcher, Projectile projectile)
    {
        if (launcher == null || projectile == null ||
            !(projectile is ExplosiveProjectile) ||
            launcher.damageType != Damageable.DamageType.Thermal ||
            !OrreryWeaponSuppression.IsRuntimeAdapter(launcher))
        {
            return;
        }

        GameShip owner = launcher.parentShip;
        OwnerCapture capture;
        if (owner == null ||
            !owners.TryGetValue(owner, out capture) ||
            capture == null ||
            !capture.MagmaPendingProjectile)
        {
            return;
        }

        if (capture.MagmaProjectile != null &&
            !object.ReferenceEquals(capture.MagmaProjectile, projectile))
        {
            magmaOwners.Remove(capture.MagmaProjectile);
        }

        capture.MagmaPendingProjectile = false;
        capture.MagmaProjectile = projectile;
        magmaOwners[projectile] = owner;
    }

    public static void RecordMagmaExplosion(
        ExplosiveProjectile projectile,
        Vector2 position)
    {
        GameShip owner;
        OwnerCapture capture;
        if (projectile == null ||
            !magmaOwners.TryGetValue(projectile, out owner) ||
            owner == null ||
            !owners.TryGetValue(owner, out capture) ||
            capture == null)
        {
            return;
        }

        if (!OrreryNetwork.IsFinite(position))
            position = projectile.transform.position;

        capture.MagmaExplosionPosition = position;
        capture.MagmaExplosionPublishUntil =
            Time.unscaledTime + Tuning.DiscreteEventPublishSeconds;
        if (object.ReferenceEquals(capture.MagmaProjectile, projectile))
            capture.MagmaProjectile = null;
        capture.MagmaPendingProjectile = false;
        magmaOwners.Remove(projectile);
    }

    public static void ForgetMagmaProjectile(Projectile projectile)
    {
        GameShip owner;
        if (projectile == null || !magmaOwners.TryGetValue(projectile, out owner))
            return;

        OwnerCapture capture;
        if (owner != null && owners.TryGetValue(owner, out capture) &&
            capture != null &&
            object.ReferenceEquals(capture.MagmaProjectile, projectile))
        {
            capture.MagmaProjectile = null;
            capture.MagmaPendingProjectile = false;
        }
        magmaOwners.Remove(projectile);
    }

    public static void RecordCryo(GameShip owner)
    {
        if (!IsLocalOrreryOwner(owner))
            return;

        OwnerCapture capture = GetOrCreateOwner(owner);
        capture.CryoGeneration = NextGeneration(ref cryoGenerationCounter);
        capture.CryoOrigin = owner.transform.position;
        capture.CryoAimDegrees = ResolveLocalAimDegrees(owner);
        capture.CryoPublishUntil =
            Time.unscaledTime + Tuning.DiscreteEventPublishSeconds;
    }

    public static void RecordTesla(GameShip owner)
    {
        if (!IsLocalOrreryOwner(owner))
            return;

        OwnerCapture capture = GetOrCreateOwner(owner);
        capture.TeslaGeneration = NextGeneration(ref teslaGenerationCounter);
        capture.TeslaWeapon = ResolveRuntimeTesla(owner);
    }

    public static void ForgetOwner(GameShip owner)
    {
        if (object.ReferenceEquals(owner, null))
            return;

        OwnerCapture capture;
        if (owners.TryGetValue(owner, out capture) && capture != null &&
            capture.MagmaProjectile != null)
        {
            magmaOwners.Remove(capture.MagmaProjectile);
        }
        owners.Remove(owner);
    }

    public static void Publish(GameShip owner)
    {
        if (!IsLocalOrreryOwner(owner))
            return;

        OwnerCapture capture;
        if (!owners.TryGetValue(owner, out capture) || capture == null)
            return;

        OrreryNetwork.PresentationState baseState;
        bool hasBase = OrreryNetwork.TryBuildLocalState(owner, out baseState);
        ushort activeSpellId = hasBase && baseState.Phase == OrreryCastPhase.Invoking
            ? baseState.SpellId
            : (ushort)0;

        if (activeSpellId == 1)
            PublishMagma(capture);
        else if (activeSpellId == 2)
            PublishTesla(owner, capture);

        if (capture.CryoGeneration != 0u &&
            Time.unscaledTime < capture.CryoPublishUntil)
        {
            PublishCryo(capture);
        }

        if (activeSpellId != 1 && capture.MagmaGeneration != 0u &&
            Time.unscaledTime < capture.MagmaExplosionPublishUntil)
        {
            PublishMagma(capture);
        }
    }

    private static void PublishMagma(OwnerCapture capture)
    {
        if (capture == null || capture.MagmaGeneration == 0u)
            return;

        bool projectilePresent =
            capture.MagmaProjectile != null &&
            capture.MagmaProjectile.gameObject != null &&
            capture.MagmaProjectile.gameObject.activeInHierarchy &&
            !capture.MagmaProjectile.IsDestroying() &&
            !capture.MagmaProjectile.hasExploded;
        bool explosionPresent =
            Time.unscaledTime < capture.MagmaExplosionPublishUntil;

        if (!projectilePresent && !explosionPresent)
            return;

        int offset = 0;
        byte flags = 0;
        if (projectilePresent) flags |= MagmaFlagProjectile;
        if (explosionPresent) flags |= MagmaFlagExplosion;
        payload[offset++] = flags;

        if (projectilePresent)
        {
            Vector2 position = capture.MagmaProjectile.transform.position;
            float angle = capture.MagmaProjectile.transform.eulerAngles.z;
            if (!OrreryNetwork.IsFinite(position) || !OrreryNetwork.IsFinite(angle))
                return;
            WritePosition(payload, ref offset, position);
            WriteFloat(payload, ref offset, angle);
        }

        if (explosionPresent)
        {
            if (!OrreryNetwork.IsFinite(capture.MagmaExplosionPosition))
                return;
            WritePosition(payload, ref offset, capture.MagmaExplosionPosition);
        }

        OrreryPresentationNetwork.WriteGroup(
            MagmaPreferredRecord,
            OrreryPresentationNetwork.CodecMagmaCannon,
            GroupId,
            1,
            capture.MagmaGeneration,
            payload,
            offset);
    }

    private static void PublishCryo(OwnerCapture capture)
    {
        int offset = 0;
        WritePosition(payload, ref offset, capture.CryoOrigin);
        WriteFloat(payload, ref offset, capture.CryoAimDegrees);

        OrreryPresentationNetwork.WriteGroup(
            CryoPreferredRecord,
            OrreryPresentationNetwork.CodecConeOfCold,
            GroupId,
            1,
            capture.CryoGeneration,
            payload,
            offset);
    }

    private static void PublishTesla(GameShip owner, OwnerCapture capture)
    {
        if (capture.TeslaGeneration == 0u)
            return;

        if (capture.TeslaWeapon == null ||
            !object.ReferenceEquals(capture.TeslaWeapon.parentShip, owner))
        {
            capture.TeslaWeapon = ResolveRuntimeTesla(owner);
        }

        int segmentCount = CaptureTeslaSegments(capture.TeslaWeapon);
        if (segmentCount <= 0)
            return;

        int offset = 0;
        payload[offset++] = (byte)segmentCount;
        for (int i = 0; i < segmentCount; i++)
        {
            WritePosition(payload, ref offset, teslaStarts[i]);
            WritePosition(payload, ref offset, teslaEnds[i]);
        }

        int partCount = RequiredPartCount(offset);
        if (partCount <= 0)
            return;

        OrreryPresentationNetwork.WriteGroup(
            TeslaPreferredRecord,
            OrreryPresentationNetwork.CodecTeslaCoil,
            GroupId,
            partCount,
            capture.TeslaGeneration,
            payload,
            offset);
    }

    private static int CaptureTeslaSegments(BeamWeapon weapon)
    {
        if (weapon == null || BeamScriptField == null ||
            BeamLineField == null || BeamSubBeamField == null)
        {
            return 0;
        }

        Beam beam = BeamScriptField.GetValue(weapon) as Beam;
        int count = 0;
        while (beam != null && count < Tuning.MaxTeslaSegments)
        {
            LineRenderer line = BeamLineField.GetValue(beam) as LineRenderer;
            if (beam.active && line != null && line.enabled &&
                line.positionCount >= 2)
            {
                Vector3 start3 = line.GetPosition(0);
                Vector3 end3 = line.GetPosition(line.positionCount - 1);
                if (!line.useWorldSpace)
                {
                    start3 = line.transform.TransformPoint(start3);
                    end3 = line.transform.TransformPoint(end3);
                }

                Vector2 start = start3;
                Vector2 end = end3;
                if (OrreryNetwork.IsFinite(start) &&
                    OrreryNetwork.IsFinite(end))
                {
                    teslaStarts[count] = start;
                    teslaEnds[count] = end;
                    count++;
                }
            }

            beam = BeamSubBeamField.GetValue(beam) as Beam;
        }

        return count;
    }

    public static void TickRemote(GameShip remoteOwner, float deltaTime)
    {
        if (remoteOwner == null || !remoteOwner.IsRemotePlayer())
            return;

        if (!CoreNetwork.HasSynchronizedSpecialization(
                remoteOwner,
                CoreClassId.Orrery))
        {
            ForgetRemote(remoteOwner);
            return;
        }

        uint magmaGeneration;
        byte magmaFlags;
        Vector2 magmaPosition;
        float magmaAngle;
        Vector2 magmaExplosion;
        bool hasMagma = TryReadMagma(
            remoteOwner,
            out magmaGeneration,
            out magmaFlags,
            out magmaPosition,
            out magmaAngle,
            out magmaExplosion);

        uint cryoGeneration;
        Vector2 cryoOrigin;
        float cryoAim;
        bool hasCryo = TryReadCryo(
            remoteOwner,
            out cryoGeneration,
            out cryoOrigin,
            out cryoAim);

        uint teslaGeneration;
        int teslaCount;
        bool hasTesla = TryReadTesla(
            remoteOwner,
            out teslaGeneration,
            out teslaCount);

        RemoteState state;
        bool hasState = remotes.TryGetValue(remoteOwner, out state) && state != null;
        if (!hasState && !hasMagma && !hasCryo && !hasTesla)
            return;

        if (!hasState)
        {
            state = new RemoteState();
            remotes[remoteOwner] = state;
        }

        if (hasMagma || hasCryo || hasTesla)
            state.LastObservedAt = Time.unscaledTime;

        TickRemoteMagma(
            remoteOwner,
            state,
            hasMagma,
            magmaGeneration,
            magmaFlags,
            magmaPosition,
            magmaAngle,
            magmaExplosion,
            deltaTime);
        TickRemoteCryo(
            remoteOwner,
            state,
            hasCryo,
            cryoGeneration,
            cryoOrigin,
            cryoAim,
            deltaTime);
        TickRemoteTesla(
            remoteOwner,
            state,
            hasTesla,
            teslaGeneration,
            teslaCount);

        if (!hasMagma && !hasCryo && !hasTesla &&
            state.Magma == null && state.TeslaSegmentCount == 0 &&
            !HasActiveCryo(state) &&
            Time.unscaledTime - state.LastObservedAt >=
                Tuning.RemoteGenerationRetentionSeconds)
        {
            ForgetRemote(remoteOwner);
        }
    }

    private static void TickRemoteMagma(
        GameShip owner,
        RemoteState state,
        bool hasMagma,
        uint generation,
        byte flags,
        Vector2 position,
        float angle,
        Vector2 explosion,
        float deltaTime)
    {
        bool projectilePresent = hasMagma &&
            (flags & MagmaFlagProjectile) != 0;
        bool explosionPresent = hasMagma &&
            (flags & MagmaFlagExplosion) != 0;

        if (hasMagma && state.MagmaGeneration != generation)
        {
            CleanupRemoteProjectile(ref state.Magma);
            state.MagmaGeneration = generation;
            PlayLauncherOneShot(
                GetInfernoBase(),
                owner,
                projectilePresent ? position : (Vector2)owner.transform.position,
                "Orrery Remote Magma Cannon");
        }

        if (projectilePresent)
        {
            if (state.Magma == null)
                state.Magma = SpawnRemoteProjectile(
                    GetInfernoBase(),
                    owner,
                    position,
                    angle,
                    OrrerySpellCompendium.MagmaCannon.ProjectileVisualScale);

            UpdateRemoteProjectile(
                state.Magma,
                position,
                angle,
                deltaTime,
                Tuning.MagmaPositionFollowPerSecond);
        }
        else
        {
            CleanupRemoteProjectile(ref state.Magma);
        }

        if (explosionPresent &&
            state.MagmaExplosionGeneration != generation)
        {
            state.MagmaExplosionGeneration = generation;
            SpawnMagmaExplosion(owner, explosion);
        }
    }

    private static void TickRemoteCryo(
        GameShip owner,
        RemoteState state,
        bool hasCryo,
        uint generation,
        Vector2 origin,
        float aimDegrees,
        float deltaTime)
    {
        if (hasCryo && state.CryoGeneration != generation)
        {
            state.CryoGeneration = generation;
            state.CryoOrigin = origin;
            state.CryoBaseAimDegrees = aimDegrees;
            state.CryoNextWaveIndex = 0;
            state.CryoNextWaveAt = Time.unscaledTime;
            ClearCryoShards(state);
            PlayLauncherOneShot(
                GetCryoBase(),
                owner,
                origin,
                "Orrery Remote Cone of Cold");
        }

        while (state.CryoNextWaveIndex <
                OrrerySpellCompendium.ConeOfCold.VisualWaveCount &&
            Time.unscaledTime + 0.0001f >= state.CryoNextWaveAt)
        {
            SpawnCryoWave(owner, state, state.CryoNextWaveIndex);
            state.CryoNextWaveIndex++;
            state.CryoNextWaveAt += Mathf.Max(
                0.01f,
                OrrerySpellCompendium.ConeOfCold.VisualWaveIntervalSeconds);
        }

        TickCryoShards(state, deltaTime);
    }

    private static void TickRemoteTesla(
        GameShip owner,
        RemoteState state,
        bool hasTesla,
        uint generation,
        int segmentCount)
    {
        if (!hasTesla)
        {
            StopTesla(state);
            return;
        }

        if (state.TeslaGeneration != generation)
        {
            StopTesla(state);
            state.TeslaGeneration = generation;
            StartTeslaAudio(owner, state);
        }

        if (state.TeslaAudioObject != null)
            state.TeslaAudioObject.transform.position = owner.transform.position;

        EnsureTeslaLines(state, segmentCount);
        for (int i = 0; i < segmentCount; i++)
        {
            LineRenderer line = state.TeslaLines[i];
            if (line == null)
                continue;
            line.gameObject.SetActive(true);
            line.positionCount = 2;
            line.SetPosition(0, teslaStarts[i]);
            line.SetPosition(1, teslaEnds[i]);
        }

        for (int i = segmentCount; i < state.TeslaSegmentCount; i++)
        {
            if (state.TeslaObjects[i] != null)
                state.TeslaObjects[i].SetActive(false);
        }
        state.TeslaSegmentCount = segmentCount;
    }

    private static bool TryReadMagma(
        GameShip owner,
        out uint generation,
        out byte flags,
        out Vector2 position,
        out float angle,
        out Vector2 explosion)
    {
        generation = 0u;
        flags = 0;
        position = Vector2.zero;
        angle = 0f;
        explosion = Vector2.zero;

        OrreryPresentationNetwork.GroupReader reader;
        if (!OrreryPresentationNetwork.TryReadGroup(
                owner,
                MagmaPreferredRecord,
                OrreryPresentationNetwork.CodecMagmaCannon,
                GroupId,
                1,
                out reader) || reader.Length < 1)
        {
            return false;
        }

        flags = reader.Byte();
        if (flags == 0 ||
            (flags & ~(MagmaFlagProjectile | MagmaFlagExplosion)) != 0)
        {
            return false;
        }

        int expected = 1;
        if ((flags & MagmaFlagProjectile) != 0) expected += 12;
        if ((flags & MagmaFlagExplosion) != 0) expected += 8;
        if (reader.Length != expected)
            return false;

        if ((flags & MagmaFlagProjectile) != 0)
        {
            position = ReadPosition(ref reader);
            angle = ReadFloat(ref reader);
            if (!OrreryNetwork.IsFinite(position) || !OrreryNetwork.IsFinite(angle))
                return false;
        }

        if ((flags & MagmaFlagExplosion) != 0)
        {
            explosion = ReadPosition(ref reader);
            if (!OrreryNetwork.IsFinite(explosion))
                return false;
        }

        if (reader.Remaining != 0)
            return false;
        generation = reader.Generation;
        return generation != 0u;
    }

    private static bool TryReadCryo(
        GameShip owner,
        out uint generation,
        out Vector2 origin,
        out float aimDegrees)
    {
        generation = 0u;
        origin = Vector2.zero;
        aimDegrees = 0f;

        OrreryPresentationNetwork.GroupReader reader;
        if (!OrreryPresentationNetwork.TryReadGroup(
                owner,
                CryoPreferredRecord,
                OrreryPresentationNetwork.CodecConeOfCold,
                GroupId,
                1,
                out reader) || reader.Length != 12)
        {
            return false;
        }

        origin = ReadPosition(ref reader);
        aimDegrees = ReadFloat(ref reader);
        if (reader.Remaining != 0 ||
            !OrreryNetwork.IsFinite(origin) ||
            !OrreryNetwork.IsFinite(aimDegrees))
        {
            return false;
        }
        generation = reader.Generation;
        return generation != 0u;
    }

    private static bool TryReadTesla(
        GameShip owner,
        out uint generation,
        out int segmentCount)
    {
        generation = 0u;
        segmentCount = 0;

        OrreryPresentationNetwork.GroupReader reader =
            default(OrreryPresentationNetwork.GroupReader);
        bool found = false;
        for (int parts = 1; parts <= 2; parts++)
        {
            if (OrreryPresentationNetwork.TryReadGroup(
                    owner,
                    TeslaPreferredRecord,
                    OrreryPresentationNetwork.CodecTeslaCoil,
                    GroupId,
                    parts,
                    out reader))
            {
                found = true;
                break;
            }
        }
        if (!found || reader.Length < 1)
            return false;

        segmentCount = reader.Byte();
        if (segmentCount < 1 || segmentCount > Tuning.MaxTeslaSegments ||
            reader.Length != 1 + segmentCount * 16 ||
            RequiredPartCount(reader.Length) != reader.PartCount)
        {
            return false;
        }

        for (int i = 0; i < segmentCount; i++)
        {
            Vector2 start = ReadPosition(ref reader);
            Vector2 end = ReadPosition(ref reader);
            if (!OrreryNetwork.IsFinite(start) || !OrreryNetwork.IsFinite(end))
                return false;
            teslaStarts[i] = start;
            teslaEnds[i] = end;
        }

        if (reader.Remaining != 0)
            return false;
        generation = reader.Generation;
        return generation != 0u;
    }

    private static void SpawnCryoWave(
        GameShip owner,
        RemoteState state,
        int waveIndex)
    {
        LauncherItemBase itemBase = GetCryoBase();
        if (itemBase == null || PoolController.instance == null)
            return;

        GameObject prefab = itemBase.GetProjectileObject(owner.faction);
        if (prefab == null)
            return;

        float offsetDegrees = GetCryoWaveOffset(waveIndex);
        int shotCount = Mathf.Max(
            1,
            OrrerySpellCompendium.ConeOfCold.VisualProjectileCount);
        float spread = Mathf.Max(
            0f,
            (float)OrrerySpellCompendium.ConeOfCold.VisualSpreadDegrees);
        float step = shotCount > 1 ? spread / (shotCount - 1) : 0f;
        float first = state.CryoBaseAimDegrees + offsetDegrees - spread * 0.5f;
        float speed = ResolveCryoVisualSpeedWorld();
        float visualRange = OrreryUnits.MetersToWorld(
            Mathf.Max(0f, OrrerySpellCompendium.ConeOfCold.VisualRangeMeters));
        float lifetime = speed > 0.001f
            ? visualRange / speed
            : 1f;

        for (int shot = 0; shot < shotCount; shot++)
        {
            int slot = FindFreeCryoShard(state);
            if (slot < 0)
                break;

            float degrees = first + step * shot;
            Quaternion rotation = Quaternion.Euler(0f, 0f, degrees);
            GameObject visualObject = PoolController.instance.GetObject(
                prefab,
                state.CryoOrigin,
                rotation,
                false);
            if (visualObject == null)
                continue;

            Projectile projectile;
            if (!visualObject.TryGetComponent<Projectile>(out projectile) ||
                projectile == null)
            {
                ReturnUnexpectedVisual(visualObject);
                continue;
            }

            RemoteProjectileVisual visual = MakeInertProjectile(
                projectile,
                OrrerySpellCompendium.ConeOfCold.VisualProjectileScale *
                    OrrerySpellCompendium.ConeOfCold.WaveProjectileScaleMultiplier);
            if (visual == null)
                continue;

            Vector2 direction = rotation * Vector2.right;
            state.CryoShards[slot] = new RemoteCryoShard
            {
                Visual = visual,
                Velocity = direction * speed,
                ExpiresAt = Time.unscaledTime + Mathf.Max(0.05f, lifetime),
                Active = true
            };
        }
    }

    private static void TickCryoShards(RemoteState state, float deltaTime)
    {
        float dt = Mathf.Max(0f, deltaTime);
        for (int i = 0; i < state.CryoShards.Length; i++)
        {
            RemoteCryoShard shard = state.CryoShards[i];
            if (!shard.Active)
                continue;

            if (shard.Visual == null || shard.Visual.Projectile == null ||
                Time.unscaledTime >= shard.ExpiresAt)
            {
                CleanupCryoShard(state, i);
                continue;
            }

            Transform transform = shard.Visual.Projectile.transform;
            Vector3 current = transform.position;
            Vector2 next = (Vector2)current + shard.Velocity * dt;
            transform.position = new Vector3(next.x, next.y, current.z);
        }
    }

    private static void EnsureTeslaLines(RemoteState state, int count)
    {
        if (count <= 0)
            return;

        LineRenderer template = GetTeslaLineTemplate();
        for (int i = 0; i < count; i++)
        {
            if (state.TeslaLines[i] != null)
                continue;

            GameObject lineObject = new GameObject("Orrery Remote Tesla Segment");
            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;

            if (template != null)
            {
                line.sharedMaterial = template.sharedMaterial;
                line.widthCurve = template.widthCurve;
                line.widthMultiplier = template.widthMultiplier * Mathf.Max(
                    0.01f,
                    OrrerySpellCompendium.TeslaCoil.BeamWidthMultiplier);
                line.colorGradient = template.colorGradient;
                line.textureMode = template.textureMode;
                line.alignment = template.alignment;
                line.numCapVertices = template.numCapVertices;
                line.numCornerVertices = template.numCornerVertices;
            }

            state.TeslaObjects[i] = lineObject;
            state.TeslaLines[i] = line;
        }
    }

    private static void StartTeslaAudio(GameShip owner, RemoteState state)
    {
        BeamWeaponItemBase itemBase = GetTeslaBase();
        if (itemBase == null || itemBase.soundEffect == null ||
            itemBase.soundEffect.audioClip == null)
        {
            return;
        }

        GameObject audioObject = new GameObject("Orrery Remote Tesla Audio");
        audioObject.transform.position = owner.transform.position;
        SoundEffectPlayer player = audioObject.AddComponent<SoundEffectPlayer>();
        player.Attach(audioObject, SoundEffectPlayer.Category.Effects);
        player.Play(itemBase.soundEffect, true, true);
        state.TeslaAudioObject = audioObject;
        state.TeslaAudioPlayer = player;
    }

    private static void StopTesla(RemoteState state)
    {
        if (state == null)
            return;

        for (int i = 0; i < state.TeslaObjects.Length; i++)
        {
            if (state.TeslaObjects[i] != null)
                UnityEngine.Object.Destroy(state.TeslaObjects[i]);
            state.TeslaObjects[i] = null;
            state.TeslaLines[i] = null;
        }
        state.TeslaSegmentCount = 0;

        if (state.TeslaAudioPlayer != null)
            state.TeslaAudioPlayer.Stop();
        if (state.TeslaAudioObject != null)
        {
            UnityEngine.Object.Destroy(
                state.TeslaAudioObject,
                Mathf.Max(0f, Tuning.TeslaStopCleanupSeconds));
        }
        state.TeslaAudioObject = null;
        state.TeslaAudioPlayer = null;
    }

    private static RemoteProjectileVisual SpawnRemoteProjectile(
        LauncherItemBase itemBase,
        GameShip owner,
        Vector2 position,
        float angle,
        float scaleMultiplier)
    {
        if (itemBase == null || owner == null || PoolController.instance == null)
            return null;

        GameObject prefab = itemBase.GetProjectileObject(owner.faction);
        if (prefab == null)
            return null;

        GameObject visualObject = PoolController.instance.GetObject(
            prefab,
            position,
            Quaternion.Euler(0f, 0f, angle),
            false);
        if (visualObject == null)
            return null;

        Projectile projectile;
        if (!visualObject.TryGetComponent<Projectile>(out projectile) ||
            projectile == null)
        {
            ReturnUnexpectedVisual(visualObject);
            return null;
        }

        return MakeInertProjectile(projectile, scaleMultiplier);
    }

    private static RemoteProjectileVisual MakeInertProjectile(
        Projectile projectile,
        float scaleMultiplier)
    {
        if (projectile == null)
            return null;

        RemoteProjectileVisual visual = new RemoteProjectileVisual();
        visual.Projectile = projectile;
        visual.Body = projectile.rigidBody;
        visual.ProjectileEnabled = projectile.enabled;
        visual.BodySimulated = visual.Body != null && visual.Body.simulated;
        visual.BaseScale = projectile.transform.localScale;

        projectile.enabled = false;
        if (visual.Body != null)
        {
            visual.Body.velocity = Vector2.zero;
            visual.Body.angularVelocity = 0f;
            visual.Body.simulated = false;
        }
        projectile.transform.localScale = visual.BaseScale * Mathf.Max(
            0.01f,
            scaleMultiplier);
        return visual;
    }

    private static void UpdateRemoteProjectile(
        RemoteProjectileVisual visual,
        Vector2 position,
        float angle,
        float deltaTime,
        float followPerSecond)
    {
        if (visual == null || visual.Projectile == null)
            return;

        Transform transform = visual.Projectile.transform;
        Vector3 current3 = transform.position;
        Vector2 current = current3;
        float t = Mathf.Clamp01(Mathf.Max(0f, deltaTime) *
            Mathf.Max(0f, followPerSecond));
        Vector2 smoothed = Vector2.Lerp(current, position, t);
        transform.position = new Vector3(smoothed.x, smoothed.y, current3.z);
        transform.rotation = Quaternion.Euler(0f, 0f, angle);
    }

    private static void CleanupRemoteProjectile(
        ref RemoteProjectileVisual visual)
    {
        if (visual == null)
            return;

        Projectile projectile = visual.Projectile;
        if (projectile != null)
        {
            projectile.transform.localScale = visual.BaseScale;
            if (visual.Body != null)
            {
                visual.Body.velocity = Vector2.zero;
                visual.Body.angularVelocity = 0f;
                visual.Body.simulated = visual.BodySimulated;
            }
            projectile.enabled = visual.ProjectileEnabled;
            projectile.PoolDestroy();
        }
        visual = null;
    }

    private static void SpawnMagmaExplosion(GameShip owner, Vector2 position)
    {
        if (owner == null || PoolController.instance == null)
            return;

        LauncherItemBase itemBase = GetInfernoBase();
        GameObject projectilePrefab = itemBase == null
            ? null
            : itemBase.GetProjectileObject(owner.faction);
        ExplosiveProjectile template = projectilePrefab == null
            ? null
            : projectilePrefab.GetComponent<ExplosiveProjectile>();
        if (template == null || template.explosiveAreaPrefab == null)
            return;

        GameObject explosionObject = PoolController.instance.GetObject(
            template.explosiveAreaPrefab,
            position,
            Utils.RandomRotation(),
            false);
        if (explosionObject == null)
            return;

        ExplosiveArea area;
        if (!explosionObject.TryGetComponent<ExplosiveArea>(out area) || area == null)
        {
            ReturnUnexpectedVisual(explosionObject);
            return;
        }

        float radiusWorld = OrreryUnits.MetersToWorld(
            OrrerySpellCompendium.MagmaCannon.ExplosionRadiusMeters);
        area.SetScale(new Vector3(
            radiusWorld * 2f,
            radiusWorld * 2f,
            radiusWorld * 2f));
        area.SetColor(template.explosionColor);
    }

    private static void PlayLauncherOneShot(
        LauncherItemBase itemBase,
        GameShip owner,
        Vector2 position,
        string objectName)
    {
        if (itemBase == null || itemBase.soundEffect == null ||
            itemBase.soundEffect.audioClip == null)
        {
            return;
        }
        PlayNativeOneShot(itemBase.soundEffect, position, objectName);
    }

    internal static void PlayNativeOneShot(
        SoundEffectPlayer.SoundEffect effect,
        Vector2 position,
        string objectName)
    {
        if (effect == null || effect.audioClip == null)
            return;

        GameObject audioObject = new GameObject(
            string.IsNullOrEmpty(objectName)
                ? "Orrery Remote Audio"
                : objectName);
        audioObject.transform.position = position;
        SoundEffectPlayer player = audioObject.AddComponent<SoundEffectPlayer>();
        player.Attach(audioObject, SoundEffectPlayer.Category.Effects);
        player.Play(effect, true, true);

        float lifetime = Mathf.Max(
            1f,
            effect.audioClip.length +
                Mathf.Max(0f, effect.fadeSeconds) + 0.5f);
        UnityEngine.Object.Destroy(audioObject, lifetime);
    }

    private static LauncherItemBase GetInfernoBase()
    {
        if (infernoBase == null)
            infernoBase = Resources.Load<LauncherItemBase>(InfernoCannonPath);
        return infernoBase;
    }

    private static LauncherItemBase GetCryoBase()
    {
        if (cryoBase == null)
            cryoBase = Resources.Load<LauncherItemBase>(CryoGunPath);
        return cryoBase;
    }

    private static BeamWeaponItemBase GetTeslaBase()
    {
        if (teslaBase == null)
            teslaBase = Resources.Load<BeamWeaponItemBase>(TeslaCoilPath);
        return teslaBase;
    }

    private static LineRenderer GetTeslaLineTemplate()
    {
        BeamWeaponItemBase itemBase = GetTeslaBase();
        if (itemBase == null || itemBase.beamPrefab == null)
            return null;
        return itemBase.beamPrefab.GetComponent<LineRenderer>();
    }

    private static float ResolveCryoVisualSpeedWorld()
    {
        if (cryoSpeedResolved)
            return cryoVisualSpeedWorld;

        cryoSpeedResolved = true;
        LauncherItemBase itemBase = GetCryoBase();
        Launcher launcher = itemBase == null
            ? null
            : itemBase.GetItem(Item.Rarity.Common, 1, 0) as Launcher;
        if (launcher != null)
        {
            cryoVisualSpeedWorld = Mathf.Max(
                0.01f,
                launcher.BaseVelocity *
                    OrrerySpellCompendium.ConeOfCold.VisualVelocityMultiplier);
        }
        else
        {
            cryoVisualSpeedWorld = OrreryUnits.MetersToWorld(100f);
        }
        return cryoVisualSpeedWorld;
    }

    private static BeamWeapon ResolveRuntimeTesla(GameShip owner)
    {
        if (owner == null || RuntimeOwnersField == null ||
            RuntimeTeslaField == null || RuntimeVirtualWeaponField == null)
        {
            WarnRuntimeReflection();
            return null;
        }

        try
        {
            IDictionary runtimeOwners = RuntimeOwnersField.GetValue(null) as IDictionary;
            if (runtimeOwners == null || !runtimeOwners.Contains(owner))
                return null;

            object ownerState = runtimeOwners[owner];
            object virtualWeapon = ownerState == null
                ? null
                : RuntimeTeslaField.GetValue(ownerState);
            return virtualWeapon == null
                ? null
                : RuntimeVirtualWeaponField.GetValue(virtualWeapon) as BeamWeapon;
        }
        catch (Exception)
        {
            WarnRuntimeReflection();
            return null;
        }
    }

    private static void WarnRuntimeReflection()
    {
        if (warnedRuntimeReflection)
            return;
        warnedRuntimeReflection = true;
        Debug.LogWarning(
            "[Orrery] Remote Tesla presentation could not resolve the hidden " +
            "runtime adapter. Gameplay remains authoritative; Tesla VFX may be absent.");
    }

    private static OwnerCapture GetOrCreateOwner(GameShip owner)
    {
        OwnerCapture capture;
        if (!owners.TryGetValue(owner, out capture) || capture == null)
        {
            capture = new OwnerCapture();
            owners[owner] = capture;
        }
        return capture;
    }

    private static bool IsLocalOrreryOwner(GameShip owner)
    {
        CoreOwnerContext context = CoreClassRuntime.CurrentContext;
        return owner != null && context != null && context.IsValid &&
            context.ClassId == CoreClassId.Orrery &&
            object.ReferenceEquals(context.Ship, owner) &&
            OrreryRuntime.IsActive(owner);
    }

    private static float ResolveLocalAimDegrees(GameShip owner)
    {
        Vector2 direction = owner == null
            ? Vector2.right
            : (Vector2)owner.transform.right;
        InputController input = InputController.instance;
        if (input != null && owner != null &&
            object.ReferenceEquals(input.controlShip, owner))
        {
            Vector2 delta = input.GetCursorWorldPoint() -
                (Vector2)owner.transform.position;
            if (delta.sqrMagnitude > 0.0001f)
                direction = delta.normalized;
        }
        return Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
    }

    private static float GetCryoWaveOffset(int waveIndex)
    {
        switch (waveIndex)
        {
            case 0: return 0f;
            case 1: return -OrrerySpellCompendium.ConeOfCold.OuterAimOffsetDegrees;
            case 2: return OrrerySpellCompendium.ConeOfCold.OuterAimOffsetDegrees;
            case 3: return -OrrerySpellCompendium.ConeOfCold.InnerAimOffsetDegrees;
            case 4: return OrrerySpellCompendium.ConeOfCold.InnerAimOffsetDegrees;
            default: return 0f;
        }
    }

    private static int FindFreeCryoShard(RemoteState state)
    {
        for (int i = 0; i < state.CryoShards.Length; i++)
        {
            if (!state.CryoShards[i].Active)
                return i;
        }
        return -1;
    }

    private static bool HasActiveCryo(RemoteState state)
    {
        if (state == null)
            return false;
        for (int i = 0; i < state.CryoShards.Length; i++)
        {
            if (state.CryoShards[i].Active)
                return true;
        }
        return false;
    }

    private static void CleanupCryoShard(RemoteState state, int index)
    {
        if (state == null || index < 0 || index >= state.CryoShards.Length)
            return;

        RemoteCryoShard shard = state.CryoShards[index];
        if (shard.Visual != null)
        {
            RemoteProjectileVisual visual = shard.Visual;
            CleanupRemoteProjectile(ref visual);
        }
        state.CryoShards[index] = default(RemoteCryoShard);
    }

    private static void ClearCryoShards(RemoteState state)
    {
        if (state == null)
            return;
        for (int i = 0; i < state.CryoShards.Length; i++)
            CleanupCryoShard(state, i);
    }

    private static void ReturnUnexpectedVisual(GameObject visualObject)
    {
        if (visualObject == null)
            return;

        PoolableObject poolable;
        if (visualObject.TryGetComponent<PoolableObject>(out poolable) &&
            poolable != null)
        {
            poolable.PoolDestroy();
        }
        else
        {
            UnityEngine.Object.Destroy(visualObject);
        }
    }

    public static void ForgetRemote(GameShip owner)
    {
        if (object.ReferenceEquals(owner, null))
            return;

        RemoteState state;
        if (!remotes.TryGetValue(owner, out state) || state == null)
            return;

        CleanupRemoteProjectile(ref state.Magma);
        ClearCryoShards(state);
        StopTesla(state);
        remotes.Remove(owner);
    }

    public static void Reset()
    {
        foreach (KeyValuePair<GameShip, RemoteState> pair in remotes)
        {
            RemoteState state = pair.Value;
            if (state == null)
                continue;
            CleanupRemoteProjectile(ref state.Magma);
            ClearCryoShards(state);
            StopTesla(state);
        }
        remotes.Clear();
        owners.Clear();
        magmaOwners.Clear();
        infernoBase = null;
        cryoBase = null;
        teslaBase = null;
        cryoSpeedResolved = false;
        cryoVisualSpeedWorld = 0f;
        warnedRuntimeReflection = false;
    }

    private static uint NextGeneration(ref uint counter)
    {
        counter++;
        if (counter == 0u)
            counter++;
        return counter;
    }

    private static int RequiredPartCount(int payloadLength)
    {
        for (int parts = 1;
            parts <= OrreryPresentationNetwork.MaximumPartsPerGroup;
            parts++)
        {
            if (payloadLength <= OrreryPresentationNetwork.GetPayloadCapacity(parts))
                return parts;
        }
        return 0;
    }

    private static void WritePosition(byte[] buffer, ref int offset, Vector2 value)
    {
        WriteFloat(buffer, ref offset, value.x);
        WriteFloat(buffer, ref offset, value.y);
    }

    private static Vector2 ReadPosition(
        ref OrreryPresentationNetwork.GroupReader reader)
    {
        return new Vector2(ReadFloat(ref reader), ReadFloat(ref reader));
    }

    private static void WriteFloat(byte[] buffer, ref int offset, float value)
    {
        FloatBits bits = new FloatBits { Value = value };
        buffer[offset++] = (byte)bits.Bits;
        buffer[offset++] = (byte)(bits.Bits >> 8);
        buffer[offset++] = (byte)(bits.Bits >> 16);
        buffer[offset++] = (byte)(bits.Bits >> 24);
    }

    private static float ReadFloat(
        ref OrreryPresentationNetwork.GroupReader reader)
    {
        uint bits = (uint)reader.Byte() |
            ((uint)reader.Byte() << 8) |
            ((uint)reader.Byte() << 16) |
            ((uint)reader.Byte() << 24);
        return new FloatBits { Bits = bits }.Value;
    }
}

[HarmonyPatch(typeof(OrrerySpellRuntime), nameof(OrrerySpellRuntime.ExecuteMagma))]
public static class OrreryMagmaPresentationCapturePatch
{
    public static void Prefix(GameShip owner)
    {
        OrreryLegacySpellPresentation.BeginMagma(owner);
    }

    public static void Postfix(GameShip owner, bool __result)
    {
        OrreryLegacySpellPresentation.CompleteMagmaAttempt(owner, __result);
    }
}

[HarmonyPatch(typeof(OrrerySpellRuntime), nameof(OrrerySpellRuntime.ExecuteCryo))]
public static class OrreryCryoPresentationCapturePatch
{
    public static void Postfix(GameShip owner, bool __result)
    {
        if (__result)
            OrreryLegacySpellPresentation.RecordCryo(owner);
    }
}

[HarmonyPatch(typeof(OrrerySpellRuntime), nameof(OrrerySpellRuntime.ExecuteTesla))]
public static class OrreryTeslaPresentationCapturePatch
{
    public static void Postfix(GameShip owner, bool __result)
    {
        if (__result)
            OrreryLegacySpellPresentation.RecordTesla(owner);
    }
}

[HarmonyPatch(typeof(OrrerySpellRuntime), nameof(OrrerySpellRuntime.OnProjectileAdded))]
public static class OrreryMagmaPresentationProjectilePatch
{
    public static void Postfix(Launcher launcher, Projectile projectile)
    {
        OrreryLegacySpellPresentation.ObserveProjectile(launcher, projectile);
    }
}

[HarmonyPatch(typeof(OrrerySpellRuntime), nameof(OrrerySpellRuntime.OnExplosiveProjectileHit))]
public static class OrreryMagmaPresentationHitPatch
{
    public static void Postfix(
        ExplosiveProjectile projectile,
        Vector2 hitPoint)
    {
        OrreryLegacySpellPresentation.RecordMagmaExplosion(
            projectile,
            hitPoint);
    }
}

[HarmonyPatch(typeof(ExplosiveProjectile), nameof(ExplosiveProjectile.TimedDestroy))]
public static class OrreryMagmaPresentationExpiryPatch
{
    public static void Prefix(ExplosiveProjectile __instance)
    {
        if (__instance != null && __instance.explodeOnExpiry &&
            !__instance.netRendered)
        {
            OrreryLegacySpellPresentation.RecordMagmaExplosion(
                __instance,
                __instance.transform.position);
        }
    }
}

[HarmonyPatch(typeof(Projectile), nameof(Projectile.PoolDestroy))]
public static class OrreryMagmaPresentationPoolPatch
{
    public static void Postfix(Projectile __instance)
    {
        OrreryLegacySpellPresentation.ForgetMagmaProjectile(__instance);
    }
}

[HarmonyPatch(typeof(OrrerySpellRuntime), nameof(OrrerySpellRuntime.Forget))]
public static class OrreryLegacyPresentationOwnerForgetPatch
{
    public static void Postfix(GameShip owner)
    {
        OrreryLegacySpellPresentation.ForgetOwner(owner);
    }
}

[HarmonyPatch(typeof(RemoteShipDriver), "Render")]
public static class OrreryLegacySpellRemoteRenderPatch
{
    public static void Postfix(RemoteShipDriver __instance)
    {
        GameShip owner = __instance == null ? null : __instance.gameShip;
        if (owner != null && owner.IsRemotePlayer())
            OrreryLegacySpellPresentation.TickRemote(owner, Time.deltaTime);
    }
}

[HarmonyPatch(typeof(GameShip), "OnDestroy")]
public static class OrreryLegacySpellRemoteShipDestroyPatch
{
    public static void Prefix(GameShip __instance)
    {
        if (__instance == null)
            return;
        OrreryLegacySpellPresentation.ForgetOwner(__instance);
        if (__instance.IsRemotePlayer())
            OrreryLegacySpellPresentation.ForgetRemote(__instance);
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class OrreryLegacySpellPresentationWorldDestroyPatch
{
    public static void Prefix()
    {
        OrreryLegacySpellPresentation.Reset();
    }
}
