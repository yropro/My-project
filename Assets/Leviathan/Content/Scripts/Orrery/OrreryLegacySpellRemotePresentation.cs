using HarmonyLib;
using StarVortex;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Remote-only reconstruction for Magma Cannon, Tesla Coil and Cone of Cold.
/// Every spawned object is presentation-only: projectile scripts and rigidbody
/// simulation are disabled, Tesla uses standalone LineRenderers, and explosions
/// instantiate only the native ExplosiveArea visual prefab.
/// </summary>
public static class OrreryLegacySpellRemotePresentation
{
    public static class Tuning
    {
        public const float RemoteGenerationRetentionSeconds = 2f;
        public const float MagmaPositionFollowPerSecond = 24f;
        public const int MaxCryoShards = 45;
    }

    private const string InfernoCannonPath =
        "Base/Items/PrimaryWeapon/Inferno Cannon";
    private const string CryoGunPath =
        "Base/Items/PrimaryWeapon/Cryo Gun";
    private const string TeslaCoilPath =
        "Base/Items/PrimaryWeapon/Tesla Coil";

    private struct InertProjectileState
    {
        public Projectile Projectile;
        public Rigidbody2D Body;
        public bool ProjectileEnabled;
        public bool BodySimulated;
        public Vector3 BaseScale;
        public bool Active;
    }

    private sealed class RemoteState
    {
        public uint MagmaGeneration;
        public uint MagmaExplosionGeneration;
        public InertProjectileState Magma;

        public uint TeslaGeneration;
        public readonly GameObject[] TeslaObjects =
            new GameObject[OrreryLegacySpellPresentation.Tuning.MaxTeslaSegments];
        public readonly LineRenderer[] TeslaLines =
            new LineRenderer[OrreryLegacySpellPresentation.Tuning.MaxTeslaSegments];
        public int TeslaSegmentCount;
        public GameObject TeslaAudioObject;
        public SoundEffectPlayer TeslaAudioPlayer;

        public uint CryoGeneration;
        public Vector2 CryoOrigin;
        public float CryoBaseAimDegrees;
        public float CryoSpeedWorld;
        public float CryoNextWaveAt;
        public int CryoNextWaveIndex;
        public readonly InertProjectileState[] CryoVisuals =
            new InertProjectileState[Tuning.MaxCryoShards];
        public readonly Vector2[] CryoVelocities =
            new Vector2[Tuning.MaxCryoShards];
        public readonly float[] CryoExpiresAt =
            new float[Tuning.MaxCryoShards];

        public float LastObservedAt;
    }

    private static readonly Dictionary<GameShip, RemoteState> states =
        new Dictionary<GameShip, RemoteState>(4);

    private static LauncherItemBase infernoBase;
    private static LauncherItemBase cryoBase;
    private static BeamWeaponItemBase teslaBase;
    private static float baseCryoVisualSpeedWorld;
    private static bool baseCryoSpeedResolved;

    public static void Tick(GameShip remoteOwner, float deltaTime)
    {
        if (remoteOwner == null || !remoteOwner.IsRemotePlayer())
            return;

        if (!CoreNetwork.HasSynchronizedSpecialization(
                remoteOwner,
                CoreClassId.Orrery))
        {
            Forget(remoteOwner);
            return;
        }

        OrreryLegacySpellPresentation.MagmaWireState magma;
        OrreryLegacySpellPresentation.CryoWireState cryo;
        OrreryLegacySpellPresentation.TeslaWireState tesla;
        bool hasMagma = OrreryLegacySpellPresentation.TryReadMagma(
            remoteOwner,
            out magma);
        bool hasCryo = OrreryLegacySpellPresentation.TryReadCryo(
            remoteOwner,
            out cryo);
        bool hasTesla = OrreryLegacySpellPresentation.TryReadTesla(
            remoteOwner,
            out tesla);

        RemoteState state;
        bool hasState = states.TryGetValue(remoteOwner, out state) &&
            state != null;
        if (!hasState && !hasMagma && !hasCryo && !hasTesla)
            return;

        if (!hasState)
        {
            state = new RemoteState();
            states[remoteOwner] = state;
        }

        if (hasMagma || hasCryo || hasTesla)
            state.LastObservedAt = Time.unscaledTime;

        TickMagma(remoteOwner, state, hasMagma, magma, deltaTime);
        TickCryo(remoteOwner, state, hasCryo, cryo, deltaTime);
        TickTesla(remoteOwner, state, hasTesla, tesla);

        if (!hasMagma && !hasCryo && !hasTesla &&
            !state.Magma.Active && state.TeslaSegmentCount == 0 &&
            !HasActiveCryo(state) &&
            Time.unscaledTime - state.LastObservedAt >=
                Tuning.RemoteGenerationRetentionSeconds)
        {
            Forget(remoteOwner);
        }
    }

    private static void TickMagma(
        GameShip owner,
        RemoteState state,
        bool present,
        OrreryLegacySpellPresentation.MagmaWireState wire,
        float deltaTime)
    {
        if (present && state.MagmaGeneration != wire.Generation)
        {
            CleanupInert(ref state.Magma);
            state.MagmaGeneration = wire.Generation;
            // Do not synthesize a delayed launch sound when the first packet we
            // received for this generation is already the explosion tail.
            if (wire.ProjectilePresent)
            {
                PlayLauncherOneShot(
                    GetInfernoBase(),
                    wire.ProjectilePosition,
                    "Orrery Remote Magma Cannon");
            }
        }

        if (present && wire.ProjectilePresent)
        {
            if (!state.Magma.Active)
            {
                TrySpawnInert(
                    ref state.Magma,
                    GetInfernoBase(),
                    owner,
                    wire.ProjectilePosition,
                    wire.ProjectileAngleDegrees,
                    OrrerySpellCompendium.MagmaCannon.ProjectileVisualScale);
            }

            UpdateInertPose(
                ref state.Magma,
                wire.ProjectilePosition,
                wire.ProjectileAngleDegrees,
                deltaTime,
                Tuning.MagmaPositionFollowPerSecond);
        }
        else
        {
            CleanupInert(ref state.Magma);
        }

        if (present && wire.ExplosionPresent &&
            state.MagmaExplosionGeneration != wire.Generation)
        {
            state.MagmaExplosionGeneration = wire.Generation;
            SpawnMagmaExplosion(owner, wire);
        }
    }

    private static void TickCryo(
        GameShip owner,
        RemoteState state,
        bool present,
        OrreryLegacySpellPresentation.CryoWireState wire,
        float deltaTime)
    {
        if (present && state.CryoGeneration != wire.Generation)
        {
            state.CryoGeneration = wire.Generation;
            state.CryoOrigin = wire.Origin;
            state.CryoBaseAimDegrees = wire.AimDegrees;
            state.CryoSpeedWorld = ResolveCryoVisualSpeedWorld(owner);
            state.CryoNextWaveIndex = 0;
            state.CryoNextWaveAt = Time.unscaledTime;
            ClearCryo(state);
            PlayLauncherOneShot(
                GetCryoBase(),
                wire.Origin,
                "Orrery Remote Cone of Cold");
        }

        while (state.CryoGeneration != 0u &&
            state.CryoNextWaveIndex <
                OrrerySpellCompendium.ConeOfCold.VisualWaveCount &&
            Time.unscaledTime + 0.0001f >= state.CryoNextWaveAt)
        {
            SpawnCryoWave(owner, state, state.CryoNextWaveIndex);
            state.CryoNextWaveIndex++;
            state.CryoNextWaveAt += Mathf.Max(
                0.01f,
                OrrerySpellCompendium.ConeOfCold.VisualWaveIntervalSeconds);
        }

        TickCryoVisuals(state, deltaTime);
    }

    private static void TickTesla(
        GameShip owner,
        RemoteState state,
        bool present,
        OrreryLegacySpellPresentation.TeslaWireState wire)
    {
        if (!present)
        {
            StopTesla(state);
            return;
        }

        if (state.TeslaGeneration != wire.Generation)
        {
            StopTesla(state);
            state.TeslaGeneration = wire.Generation;
            StartTeslaAudio(owner, state);
        }

        if (state.TeslaAudioObject != null)
            state.TeslaAudioObject.transform.position = owner.transform.position;

        EnsureTeslaLines(state, wire.SegmentCount);
        for (int i = 0; i < wire.SegmentCount; i++)
        {
            LineRenderer line = state.TeslaLines[i];
            if (line == null)
                continue;

            GameObject lineObject = state.TeslaObjects[i];
            if (lineObject != null && !lineObject.activeSelf)
                lineObject.SetActive(true);

            line.positionCount = 2;
            line.widthMultiplier = Mathf.Max(0.001f, wire.Width);
            line.SetPosition(0, wire.GetStart(i));
            line.SetPosition(1, wire.GetEnd(i));
        }

        for (int i = wire.SegmentCount;
            i < state.TeslaSegmentCount;
            i++)
        {
            if (state.TeslaObjects[i] != null)
                state.TeslaObjects[i].SetActive(false);
        }
        state.TeslaSegmentCount = wire.SegmentCount;
    }

    private static void SpawnMagmaExplosion(
        GameShip owner,
        OrreryLegacySpellPresentation.MagmaWireState wire)
    {
        if (owner == null || PoolController.instance == null ||
            wire.ExplosionRadiusWorld <= 0f)
        {
            return;
        }

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
            wire.ExplosionPosition,
            Utils.RandomRotation(),
            false);
        if (explosionObject == null)
            return;

        ExplosiveArea area;
        if (!explosionObject.TryGetComponent<ExplosiveArea>(out area) ||
            area == null)
        {
            ReturnUnexpectedVisual(explosionObject);
            return;
        }

        float diameter = wire.ExplosionRadiusWorld * 2f;
        area.SetScale(new Vector3(diameter, diameter, diameter));
        area.SetColor(template.explosionColor);
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

        int shotCount = Mathf.Max(
            1,
            OrrerySpellCompendium.ConeOfCold.VisualProjectileCount);
        float spread = Mathf.Max(
            0f,
            (float)OrrerySpellCompendium.ConeOfCold.VisualSpreadDegrees);
        float step = shotCount > 1
            ? spread / (shotCount - 1)
            : 0f;
        float firstDegrees = state.CryoBaseAimDegrees +
            GetCryoWaveOffset(waveIndex) - spread * 0.5f;
        float speed = Mathf.Max(0.01f, state.CryoSpeedWorld);
        float visualRangeWorld = OrreryUnits.MetersToWorld(
            Mathf.Max(0f, OrrerySpellCompendium.ConeOfCold.VisualRangeMeters));
        float lifetime = visualRangeWorld / speed;

        for (int shot = 0; shot < shotCount; shot++)
        {
            int slot = FindFreeCryoSlot(state);
            if (slot < 0)
                return;

            float degrees = firstDegrees + step * shot;
            Quaternion rotation = Quaternion.Euler(0f, 0f, degrees);
            if (!TrySpawnInert(
                    ref state.CryoVisuals[slot],
                    itemBase,
                    owner,
                    state.CryoOrigin,
                    degrees,
                    OrrerySpellCompendium.ConeOfCold.VisualProjectileScale *
                        OrrerySpellCompendium.ConeOfCold.WaveProjectileScaleMultiplier))
            {
                continue;
            }

            Vector2 direction = rotation * Vector2.right;
            state.CryoVelocities[slot] = direction * speed;
            state.CryoExpiresAt[slot] =
                Time.unscaledTime + Mathf.Max(0.05f, lifetime);
        }
    }

    private static void TickCryoVisuals(RemoteState state, float deltaTime)
    {
        float dt = Mathf.Max(0f, deltaTime);
        for (int i = 0; i < state.CryoVisuals.Length; i++)
        {
            if (!state.CryoVisuals[i].Active)
                continue;

            Projectile projectile = state.CryoVisuals[i].Projectile;
            if (projectile == null ||
                Time.unscaledTime >= state.CryoExpiresAt[i])
            {
                CleanupCryoSlot(state, i);
                continue;
            }

            Vector3 current = projectile.transform.position;
            Vector2 next = (Vector2)current +
                state.CryoVelocities[i] * dt;
            projectile.transform.position = new Vector3(
                next.x,
                next.y,
                current.z);
        }
    }

    private static int FindFreeCryoSlot(RemoteState state)
    {
        for (int i = 0; i < state.CryoVisuals.Length; i++)
        {
            if (!state.CryoVisuals[i].Active)
                return i;
        }
        return -1;
    }

    private static void CleanupCryoSlot(RemoteState state, int index)
    {
        if (state == null || index < 0 || index >= state.CryoVisuals.Length)
            return;

        CleanupInert(ref state.CryoVisuals[index]);
        state.CryoVelocities[index] = Vector2.zero;
        state.CryoExpiresAt[index] = 0f;
    }

    private static void ClearCryo(RemoteState state)
    {
        if (state == null)
            return;
        for (int i = 0; i < state.CryoVisuals.Length; i++)
            CleanupCryoSlot(state, i);
    }

    private static bool HasActiveCryo(RemoteState state)
    {
        if (state == null)
            return false;
        for (int i = 0; i < state.CryoVisuals.Length; i++)
        {
            if (state.CryoVisuals[i].Active)
                return true;
        }
        return false;
    }

    private static float GetCryoWaveOffset(int waveIndex)
    {
        switch (waveIndex)
        {
            case 0: return 0f;
            case 1:
                return -OrrerySpellCompendium.ConeOfCold.OuterAimOffsetDegrees;
            case 2:
                return OrrerySpellCompendium.ConeOfCold.OuterAimOffsetDegrees;
            case 3:
                return -OrrerySpellCompendium.ConeOfCold.InnerAimOffsetDegrees;
            case 4:
                return OrrerySpellCompendium.ConeOfCold.InnerAimOffsetDegrees;
            default:
                return 0f;
        }
    }

    private static void EnsureTeslaLines(RemoteState state, int count)
    {
        if (state == null || count <= 0)
            return;

        LineRenderer template = GetTeslaLineTemplate();
        for (int i = 0; i < count; i++)
        {
            if (state.TeslaLines[i] != null)
                continue;

            GameObject lineObject = new GameObject(
                "Orrery Remote Tesla Segment");
            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;

            if (template != null)
            {
                line.sharedMaterial = template.sharedMaterial;
                line.widthCurve = template.widthCurve;
                line.widthMultiplier = template.widthMultiplier;
                line.colorGradient = template.colorGradient;
                line.textureMode = template.textureMode;
                line.alignment = template.alignment;
                line.numCapVertices = template.numCapVertices;
                line.numCornerVertices = template.numCornerVertices;
                line.sortingLayerID = template.sortingLayerID;
                line.sortingOrder = template.sortingOrder;
            }

            SceneGlowFeature.RegisterLine(line);
            state.TeslaObjects[i] = lineObject;
            state.TeslaLines[i] = line;
        }
    }

    private static void StartTeslaAudio(GameShip owner, RemoteState state)
    {
        BeamWeaponItemBase itemBase = GetTeslaBase();
        if (owner == null || state == null || itemBase == null ||
            itemBase.soundEffect == null ||
            itemBase.soundEffect.audioClip == null)
        {
            return;
        }

        if (state.TeslaAudioObject == null ||
            state.TeslaAudioPlayer == null)
        {
            state.TeslaAudioObject = new GameObject(
                "Orrery Remote Tesla Audio");
            state.TeslaAudioPlayer =
                state.TeslaAudioObject.AddComponent<SoundEffectPlayer>();
            state.TeslaAudioPlayer.Attach(
                state.TeslaAudioObject,
                SoundEffectPlayer.Category.Effects);
        }

        state.TeslaAudioObject.transform.position = owner.transform.position;
        state.TeslaAudioPlayer.Play(itemBase.soundEffect, true, true);
    }

    private static void StopTesla(RemoteState state)
    {
        if (state == null)
            return;

        for (int i = 0; i < state.TeslaObjects.Length; i++)
        {
            if (state.TeslaObjects[i] != null)
                state.TeslaObjects[i].SetActive(false);
        }
        state.TeslaSegmentCount = 0;

        if (state.TeslaAudioPlayer != null &&
            state.TeslaAudioPlayer.IsPlaying())
        {
            state.TeslaAudioPlayer.Stop();
        }
    }

    private static void DestroyTesla(RemoteState state)
    {
        if (state == null)
            return;

        StopTesla(state);
        for (int i = 0; i < state.TeslaObjects.Length; i++)
        {
            if (state.TeslaLines[i] != null)
                SceneGlowFeature.UnregisterLine(state.TeslaLines[i]);
            if (state.TeslaObjects[i] != null)
                UnityEngine.Object.Destroy(state.TeslaObjects[i]);
            state.TeslaObjects[i] = null;
            state.TeslaLines[i] = null;
        }

        if (state.TeslaAudioObject != null)
            UnityEngine.Object.Destroy(state.TeslaAudioObject);
        state.TeslaAudioObject = null;
        state.TeslaAudioPlayer = null;
    }

    private static bool TrySpawnInert(
        ref InertProjectileState state,
        LauncherItemBase itemBase,
        GameShip owner,
        Vector2 position,
        float angleDegrees,
        float scaleMultiplier)
    {
        if (state.Active)
            CleanupInert(ref state);
        if (itemBase == null || owner == null || PoolController.instance == null)
            return false;

        GameObject prefab = itemBase.GetProjectileObject(owner.faction);
        if (prefab == null)
            return false;

        GameObject visualObject = PoolController.instance.GetObject(
            prefab,
            position,
            Quaternion.Euler(0f, 0f, angleDegrees),
            false);
        if (visualObject == null)
            return false;

        Projectile projectile;
        if (!visualObject.TryGetComponent<Projectile>(out projectile) ||
            projectile == null)
        {
            ReturnUnexpectedVisual(visualObject);
            return false;
        }

        state.Projectile = projectile;
        state.Body = projectile.rigidBody;
        state.ProjectileEnabled = projectile.enabled;
        state.BodySimulated = state.Body != null && state.Body.simulated;
        state.BaseScale = projectile.transform.localScale;
        state.Active = true;

        projectile.enabled = false;
        if (state.Body != null)
        {
            state.Body.velocity = Vector2.zero;
            state.Body.angularVelocity = 0f;
            state.Body.simulated = false;
        }
        projectile.transform.localScale = state.BaseScale * Mathf.Max(
            0.01f,
            scaleMultiplier);
        return true;
    }

    private static void UpdateInertPose(
        ref InertProjectileState state,
        Vector2 position,
        float angleDegrees,
        float deltaTime,
        float followPerSecond)
    {
        if (!state.Active || state.Projectile == null)
            return;

        Transform transform = state.Projectile.transform;
        Vector3 current3 = transform.position;
        float t = Mathf.Clamp01(
            Mathf.Max(0f, deltaTime) * Mathf.Max(0f, followPerSecond));
        Vector2 smoothed = Vector2.Lerp(
            (Vector2)current3,
            position,
            t);
        transform.position = new Vector3(
            smoothed.x,
            smoothed.y,
            current3.z);
        transform.rotation = Quaternion.Euler(0f, 0f, angleDegrees);
    }

    private static void CleanupInert(ref InertProjectileState state)
    {
        Projectile projectile = state.Projectile;
        if (projectile != null)
        {
            projectile.transform.localScale = state.BaseScale;
            if (state.Body != null)
            {
                state.Body.velocity = Vector2.zero;
                state.Body.angularVelocity = 0f;
                state.Body.simulated = state.BodySimulated;
            }
            projectile.enabled = state.ProjectileEnabled;
            projectile.PoolDestroy();
        }
        state = default(InertProjectileState);
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

    private static float ResolveCryoVisualSpeedWorld(GameShip owner)
    {
        if (!baseCryoSpeedResolved)
        {
            baseCryoSpeedResolved = true;
            LauncherItemBase itemBase = GetCryoBase();
            Launcher launcher = itemBase == null
                ? null
                : itemBase.GetItem(Item.Rarity.Common, 1, 0) as Launcher;
            baseCryoVisualSpeedWorld = launcher == null
                ? OrreryUnits.MetersToWorld(100f)
                : Mathf.Max(
                    0.01f,
                    launcher.BaseVelocity *
                        OrrerySpellCompendium.ConeOfCold.VisualVelocityMultiplier);
        }

        float speed = baseCryoVisualSpeedWorld;
        OrreryFocusProfile.Resolved focus;
        if (owner != null &&
            OrreryFocusProfile.TryResolve(
                owner,
                OrreryElement.Ice,
                out focus) &&
            focus.IsValid)
        {
            speed = focus.ApplyProjectileVelocityBonus(speed);
        }
        return Mathf.Max(0.01f, speed);
    }

    private static void PlayLauncherOneShot(
        LauncherItemBase itemBase,
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
        SoundEffectPlayer player =
            audioObject.AddComponent<SoundEffectPlayer>();
        player.Attach(audioObject, SoundEffectPlayer.Category.Effects);
        player.Play(effect, true, true);

        float lifetime = Mathf.Max(
            1f,
            effect.audioClip.length +
                Mathf.Max(0f, effect.fadeSeconds) + 0.5f);
        UnityEngine.Object.Destroy(audioObject, lifetime);
    }

    public static void Forget(GameShip owner)
    {
        if (object.ReferenceEquals(owner, null))
            return;

        RemoteState state;
        if (!states.TryGetValue(owner, out state) || state == null)
            return;

        CleanupInert(ref state.Magma);
        ClearCryo(state);
        DestroyTesla(state);
        states.Remove(owner);
    }

    public static void Reset()
    {
        foreach (KeyValuePair<GameShip, RemoteState> pair in states)
        {
            RemoteState state = pair.Value;
            if (state == null)
                continue;
            CleanupInert(ref state.Magma);
            ClearCryo(state);
            DestroyTesla(state);
        }
        states.Clear();
        infernoBase = null;
        cryoBase = null;
        teslaBase = null;
        baseCryoVisualSpeedWorld = 0f;
        baseCryoSpeedResolved = false;
    }
}

[HarmonyPatch(typeof(RemoteShipDriver), "Render")]
public static class OrreryLegacySpellRemoteRenderPatch
{
    public static void Postfix(RemoteShipDriver __instance)
    {
        GameShip owner = __instance == null
            ? null
            : __instance.gameShip;
        if (owner != null && owner.IsRemotePlayer())
        {
            OrreryLegacySpellRemotePresentation.Tick(
                owner,
                Time.deltaTime);
        }
    }
}

[HarmonyPatch(typeof(GameShip), "OnDestroy")]
public static class OrreryLegacySpellRemoteShipDestroyPatch
{
    public static void Prefix(GameShip __instance)
    {
        if (__instance != null && __instance.IsRemotePlayer())
            OrreryLegacySpellRemotePresentation.Forget(__instance);
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class OrreryLegacySpellRemoteWorldDestroyPatch
{
    public static void Prefix()
    {
        OrreryLegacySpellRemotePresentation.Reset();
    }
}
