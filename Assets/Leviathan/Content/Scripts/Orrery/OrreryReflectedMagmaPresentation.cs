using HarmonyLib;
using StarVortex;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Presentation-only overflow lane for reflected Magma Cannon projectiles.
///
/// The ordinary Magma presentation stream owns the current cast and the most
/// recently reflected orb. Reflection cancels that cast and allows Orrery to
/// rearm while the detached native projectile can remain alive for up to five
/// seconds. Starting another Magma cast necessarily reuses the ordinary capture,
/// so older reflected projectiles are promoted here instead of disappearing for
/// remote observers while their owner-authoritative gameplay objects still exist.
///
/// This stream is intentionally bounded and lowest-priority in the shared Orrery
/// presentation bank. It never controls damage, collision, ownership or lifetime.
/// Under presentation pressure it may be omitted; active spell state, Shatterbolt
/// history and Plasma refreshes claim the bank first.
/// </summary>
public static class OrreryReflectedMagmaPresentation
{
    public const int MaxTrackedProjectiles = 16;

    private const byte GroupId = 1;
    private const int PreferredRecord = 0;
    private const byte FlagLive = 1 << 0;
    private const byte FlagExplosion = 1 << 1;
    private const int BytesPerEntry = 7;
    private const float RelativePositionStepMeters = 0.25f;
    private const float RemoteStreamStaleSeconds = 0.25f;
    private const string InfernoCannonPath =
        "Base/Items/PrimaryWeapon/Inferno Cannon";

    private struct OwnerEntry
    {
        public bool Active;
        public GameShip Owner;
        public Projectile Projectile;
        public byte VisualId;
        public bool Secondary;
        public float RegisteredAt;
        public float ExplosionUntil;
        public Vector2 ExplosionPosition;
    }

    private sealed class RemoteVisual
    {
        public bool Active;
        public byte VisualId;
        public uint SeenGeneration;
        public Projectile Projectile;
        public Rigidbody2D Body;
        public bool ProjectileWasEnabled;
        public bool BodyWasSimulated;
        public Vector3 BaseScale;
        public bool ExplosionShown;
    }

    private sealed class RemoteOwnerState
    {
        public readonly RemoteVisual[] Visuals =
            new RemoteVisual[MaxTrackedProjectiles];
        public float LastRefreshAt;

        public RemoteOwnerState()
        {
            for (int i = 0; i < Visuals.Length; i++)
                Visuals[i] = new RemoteVisual();
        }
    }

    private static readonly OwnerEntry[] ownerEntries =
        new OwnerEntry[MaxTrackedProjectiles];
    private static readonly Dictionary<GameShip, RemoteOwnerState> remoteStates =
        new Dictionary<GameShip, RemoteOwnerState>(4);

    // 1 count byte + 16 compact entries = 113 bytes. Four bank records provide
    // 116 bytes of codec payload capacity, so the entire bounded set is atomic.
    private static readonly byte[] payload =
        new byte[1 + MaxTrackedProjectiles * BytesPerEntry];
    private static readonly byte[] readIds = new byte[MaxTrackedProjectiles];
    private static readonly byte[] readFlags = new byte[MaxTrackedProjectiles];
    private static readonly Vector2[] readPositions =
        new Vector2[MaxTrackedProjectiles];
    private static readonly float[] readAngles =
        new float[MaxTrackedProjectiles];

    private static LauncherItemBase infernoBase;
    private static byte nextVisualId;
    private static uint publishGeneration;

    /// <summary>
    /// Called after OrreryFireballReflectionSafety has detached the projectile
    /// from its authored cast. The ordinary Magma stream continues presenting
    /// this newest reflected projectile until another Magma attempt reuses it.
    /// </summary>
    public static void RegisterReflected(Projectile projectile)
    {
        if (projectile == null ||
            !OrreryFireballLifecycleSafety.IsDetached(projectile))
        {
            return;
        }

        GameShip owner;
        if (!OrreryFireballLifecycleSafety.TryGetOriginalOwner(
                projectile,
                out owner) || owner == null)
        {
            return;
        }

        PruneOwnerEntries(Time.unscaledTime);

        for (int i = 0; i < ownerEntries.Length; i++)
        {
            if (ownerEntries[i].Active &&
                object.ReferenceEquals(ownerEntries[i].Projectile, projectile))
            {
                return;
            }
        }

        int slot = FindOwnerSlot();
        if (slot < 0)
            return;

        OwnerEntry entry = default(OwnerEntry);
        entry.Active = true;
        entry.Owner = owner;
        entry.Projectile = projectile;
        entry.VisualId = NextVisualId();
        entry.Secondary = false;
        entry.RegisteredAt = Time.unscaledTime;
        ownerEntries[slot] = entry;
    }

    /// <summary>
    /// The ordinary one-projectile Magma capture is about to be reused. Any
    /// reflected projectile it had been carrying becomes a secondary entry here.
    /// </summary>
    public static void PromoteForNewMagma(GameShip owner)
    {
        if (owner == null)
            return;

        PruneOwnerEntries(Time.unscaledTime);
        for (int i = 0; i < ownerEntries.Length; i++)
        {
            OwnerEntry entry = ownerEntries[i];
            if (!entry.Active || !object.ReferenceEquals(entry.Owner, owner))
                continue;

            entry.Secondary = true;
            ownerEntries[i] = entry;
        }
    }

    public static void RecordExplosion(Projectile projectile)
    {
        if (projectile == null)
            return;

        float now = Time.unscaledTime;
        for (int i = 0; i < ownerEntries.Length; i++)
        {
            OwnerEntry entry = ownerEntries[i];
            if (!entry.Active ||
                !object.ReferenceEquals(entry.Projectile, projectile))
            {
                continue;
            }

            entry.ExplosionPosition = projectile.transform.position;
            entry.ExplosionUntil = now +
                OrreryLegacySpellPresentation.Tuning.DiscreteEventPublishSeconds;
            ownerEntries[i] = entry;
            return;
        }
    }

    public static void OnProjectilePooled(Projectile projectile)
    {
        if (projectile == null)
            return;

        float now = Time.unscaledTime;
        for (int i = 0; i < ownerEntries.Length; i++)
        {
            OwnerEntry entry = ownerEntries[i];
            if (!entry.Active ||
                !object.ReferenceEquals(entry.Projectile, projectile))
            {
                continue;
            }

            entry.Projectile = null;
            if (entry.ExplosionUntil <= now)
                ownerEntries[i] = default(OwnerEntry);
            else
                ownerEntries[i] = entry;
            return;
        }
    }

    /// <summary>
    /// Publish after the normal Orrery spell, Shatterbolt and Plasma publishers
    /// have claimed records. This lane therefore consumes only spare contiguous
    /// bank capacity and cannot crowd out primary presentation.
    /// </summary>
    public static void Publish(GameShip owner)
    {
        if (owner == null || !OrreryRuntime.IsActive(owner))
            return;

        float now = Time.unscaledTime;
        PruneOwnerEntries(now);

        Vector2 ownerPosition = owner.transform.position;
        int offset = 1;
        int count = 0;

        for (int i = 0; i < ownerEntries.Length; i++)
        {
            OwnerEntry entry = ownerEntries[i];
            if (!entry.Active || !entry.Secondary ||
                !object.ReferenceEquals(entry.Owner, owner))
            {
                continue;
            }

            bool explosion = entry.ExplosionUntil > now;
            bool live = !explosion && IsLive(entry.Projectile);
            if (!live && !explosion)
                continue;

            Vector2 position = explosion
                ? entry.ExplosionPosition
                : (Vector2)entry.Projectile.transform.position;
            short relativeX;
            short relativeY;
            if (!TryQuantizeRelative(
                    position - ownerPosition,
                    out relativeX,
                    out relativeY))
            {
                continue;
            }

            payload[offset++] = entry.VisualId;
            payload[offset++] = explosion ? FlagExplosion : FlagLive;
            WriteShort(payload, ref offset, relativeX);
            WriteShort(payload, ref offset, relativeY);
            payload[offset++] = live
                ? QuantizeAngle(entry.Projectile.transform.eulerAngles.z)
                : (byte)0;
            count++;
        }

        if (count <= 0)
            return;

        payload[0] = (byte)count;
        int partCount = RequiredPartCount(offset);
        if (partCount <= 0)
            return;

        OrreryPresentationNetwork.WriteGroup(
            PreferredRecord,
            OrreryPresentationNetwork.CodecMagmaCannon,
            GroupId,
            partCount,
            NextPublishGeneration(),
            payload,
            offset);
    }

    public static void TickRemote(GameShip owner, float deltaTime)
    {
        if (owner == null || !owner.IsRemotePlayer())
            return;

        if (!CoreNetwork.HasSynchronizedSpecialization(owner, CoreClassId.Orrery))
        {
            ForgetRemote(owner);
            return;
        }

        uint generation;
        int count;
        bool present = TryRead(owner, out generation, out count);

        RemoteOwnerState state;
        bool hasState = remoteStates.TryGetValue(owner, out state) &&
            state != null;
        if (!hasState && !present)
            return;

        if (!hasState)
        {
            state = new RemoteOwnerState();
            remoteStates[owner] = state;
        }

        if (!present)
        {
            if (Time.unscaledTime - state.LastRefreshAt >=
                RemoteStreamStaleSeconds)
            {
                ForgetRemote(owner);
            }
            return;
        }

        state.LastRefreshAt = Time.unscaledTime;
        for (int i = 0; i < count; i++)
        {
            RemoteVisual visual = FindOrCreateRemoteVisual(state, readIds[i]);
            if (visual == null)
                continue;

            visual.SeenGeneration = generation;
            byte flags = readFlags[i];
            if ((flags & FlagLive) != 0)
            {
                visual.ExplosionShown = false;
                if (visual.Projectile == null)
                    SpawnRemoteProjectile(owner, visual, readPositions[i], readAngles[i]);
                UpdateRemoteProjectile(
                    visual,
                    readPositions[i],
                    readAngles[i],
                    deltaTime);
            }
            else if ((flags & FlagExplosion) != 0)
            {
                CleanupRemoteProjectile(visual);
                if (!visual.ExplosionShown)
                {
                    visual.ExplosionShown = true;
                    SpawnRemoteExplosion(owner, readPositions[i]);
                }
            }
        }

        // A present group is an atomic authoritative list. Entries omitted from
        // it have retired; only whole-group omission gets the short stale grace.
        for (int i = 0; i < state.Visuals.Length; i++)
        {
            RemoteVisual visual = state.Visuals[i];
            if (visual.Active && visual.SeenGeneration != generation)
                ClearRemoteVisual(visual);
        }
    }

    public static void ForgetOwner(GameShip owner)
    {
        if (object.ReferenceEquals(owner, null))
            return;

        for (int i = 0; i < ownerEntries.Length; i++)
        {
            if (ownerEntries[i].Active &&
                object.ReferenceEquals(ownerEntries[i].Owner, owner))
            {
                ownerEntries[i] = default(OwnerEntry);
            }
        }
    }

    public static void ForgetRemote(GameShip owner)
    {
        if (object.ReferenceEquals(owner, null))
            return;

        RemoteOwnerState state;
        if (!remoteStates.TryGetValue(owner, out state) || state == null)
            return;

        for (int i = 0; i < state.Visuals.Length; i++)
            ClearRemoteVisual(state.Visuals[i]);
        remoteStates.Remove(owner);
    }

    public static void Reset()
    {
        for (int i = 0; i < ownerEntries.Length; i++)
            ownerEntries[i] = default(OwnerEntry);

        foreach (KeyValuePair<GameShip, RemoteOwnerState> pair in remoteStates)
        {
            RemoteOwnerState state = pair.Value;
            if (state == null)
                continue;
            for (int i = 0; i < state.Visuals.Length; i++)
                ClearRemoteVisual(state.Visuals[i]);
        }
        remoteStates.Clear();
        infernoBase = null;
        nextVisualId = 0;
        publishGeneration = 0u;
    }

    private static bool TryRead(
        GameShip owner,
        out uint generation,
        out int count)
    {
        generation = 0u;
        count = 0;

        OrreryPresentationNetwork.GroupReader reader =
            default(OrreryPresentationNetwork.GroupReader);
        bool found = false;
        for (int parts = 1;
            parts <= OrreryPresentationNetwork.MaximumPartsPerGroup;
            parts++)
        {
            if (OrreryPresentationNetwork.TryReadGroup(
                    owner,
                    PreferredRecord,
                    OrreryPresentationNetwork.CodecMagmaCannon,
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

        count = reader.Byte();
        if (count < 1 || count > MaxTrackedProjectiles)
            return false;

        int expectedLength = 1 + count * BytesPerEntry;
        if (reader.Length != expectedLength ||
            RequiredPartCount(expectedLength) != reader.PartCount)
        {
            return false;
        }

        Vector2 ownerPosition = owner.transform.position;
        for (int i = 0; i < count; i++)
        {
            byte id = reader.Byte();
            byte flags = reader.Byte();
            short relativeX = ReadShort(ref reader);
            short relativeY = ReadShort(ref reader);
            byte angle = reader.Byte();

            if (id == 0 || (flags != FlagLive && flags != FlagExplosion))
                return false;
            for (int previous = 0; previous < i; previous++)
            {
                if (readIds[previous] == id)
                    return false;
            }

            readIds[i] = id;
            readFlags[i] = flags;
            readPositions[i] = ownerPosition + new Vector2(
                OrreryUnits.MetersToWorld(relativeX * RelativePositionStepMeters),
                OrreryUnits.MetersToWorld(relativeY * RelativePositionStepMeters));
            readAngles[i] = angle * (360f / 255f);
        }

        if (reader.Remaining != 0)
            return false;

        generation = reader.Generation;
        return generation != 0u;
    }

    private static void PruneOwnerEntries(float now)
    {
        for (int i = 0; i < ownerEntries.Length; i++)
        {
            OwnerEntry entry = ownerEntries[i];
            if (!entry.Active)
                continue;

            if (entry.ExplosionUntil > now)
                continue;

            if (entry.Projectile != null && entry.Projectile.hasExploded)
            {
                entry.ExplosionPosition = entry.Projectile.transform.position;
                entry.ExplosionUntil = now +
                    OrreryLegacySpellPresentation.Tuning.DiscreteEventPublishSeconds;
                ownerEntries[i] = entry;
                continue;
            }

            if (!IsLive(entry.Projectile))
                ownerEntries[i] = default(OwnerEntry);
        }
    }

    private static int FindOwnerSlot()
    {
        int oldestSecondary = -1;
        float oldestRegisteredAt = float.PositiveInfinity;
        for (int i = 0; i < ownerEntries.Length; i++)
        {
            if (!ownerEntries[i].Active)
                return i;

            if (ownerEntries[i].Secondary &&
                ownerEntries[i].RegisteredAt < oldestRegisteredAt)
            {
                oldestRegisteredAt = ownerEntries[i].RegisteredAt;
                oldestSecondary = i;
            }
        }

        // Capacity is presentation-only. Preserve the newest primary reflection
        // and replace the oldest secondary if pathological cadence exceeds the
        // 16-entry bound.
        return oldestSecondary;
    }

    private static bool IsLive(Projectile projectile)
    {
        return projectile != null &&
            projectile.gameObject != null &&
            projectile.gameObject.activeInHierarchy &&
            !projectile.IsDestroying() &&
            !projectile.hasExploded;
    }

    private static bool TryQuantizeRelative(
        Vector2 worldOffset,
        out short x,
        out short y)
    {
        x = 0;
        y = 0;
        float worldUnitsPerMeter = Mathf.Max(
            0.0001f,
            OrreryUnits.WorldUnitsPerMeter);
        float xSteps = worldOffset.x /
            worldUnitsPerMeter / RelativePositionStepMeters;
        float ySteps = worldOffset.y /
            worldUnitsPerMeter / RelativePositionStepMeters;
        if (xSteps < short.MinValue || xSteps > short.MaxValue ||
            ySteps < short.MinValue || ySteps > short.MaxValue)
        {
            return false;
        }

        x = (short)Mathf.RoundToInt(xSteps);
        y = (short)Mathf.RoundToInt(ySteps);
        return true;
    }

    private static byte QuantizeAngle(float degrees)
    {
        return (byte)Mathf.Clamp(
            Mathf.RoundToInt(Mathf.Repeat(degrees, 360f) * (255f / 360f)),
            0,
            255);
    }

    private static int RequiredPartCount(int payloadLength)
    {
        for (int parts = 1;
            parts <= OrreryPresentationNetwork.MaximumPartsPerGroup;
            parts++)
        {
            if (payloadLength <=
                OrreryPresentationNetwork.GetPayloadCapacity(parts))
            {
                return parts;
            }
        }
        return 0;
    }

    private static void WriteShort(byte[] buffer, ref int offset, short value)
    {
        ushort raw = unchecked((ushort)value);
        buffer[offset++] = (byte)raw;
        buffer[offset++] = (byte)(raw >> 8);
    }

    private static short ReadShort(
        ref OrreryPresentationNetwork.GroupReader reader)
    {
        ushort raw = (ushort)(reader.Byte() | (reader.Byte() << 8));
        return unchecked((short)raw);
    }

    private static byte NextVisualId()
    {
        nextVisualId++;
        if (nextVisualId == 0)
            nextVisualId++;
        return nextVisualId;
    }

    private static uint NextPublishGeneration()
    {
        publishGeneration++;
        if (publishGeneration == 0u)
            publishGeneration++;
        return publishGeneration;
    }

    private static RemoteVisual FindOrCreateRemoteVisual(
        RemoteOwnerState state,
        byte visualId)
    {
        if (state == null || visualId == 0)
            return null;

        RemoteVisual free = null;
        for (int i = 0; i < state.Visuals.Length; i++)
        {
            RemoteVisual visual = state.Visuals[i];
            if (visual.Active && visual.VisualId == visualId)
                return visual;
            if (!visual.Active && free == null)
                free = visual;
        }

        if (free == null)
            return null;

        free.Active = true;
        free.VisualId = visualId;
        free.SeenGeneration = 0u;
        free.ExplosionShown = false;
        return free;
    }

    private static void SpawnRemoteProjectile(
        GameShip owner,
        RemoteVisual visual,
        Vector2 position,
        float angleDegrees)
    {
        if (owner == null || visual == null || PoolController.instance == null)
            return;

        if (infernoBase == null)
            infernoBase = Resources.Load<LauncherItemBase>(InfernoCannonPath);
        GameObject prefab = infernoBase == null
            ? null
            : infernoBase.GetProjectileObject(owner.faction);
        if (prefab == null)
            return;

        GameObject visualObject = PoolController.instance.GetObject(
            prefab,
            position,
            Quaternion.Euler(0f, 0f, angleDegrees),
            false);
        if (visualObject == null)
            return;

        Projectile projectile;
        if (!visualObject.TryGetComponent<Projectile>(out projectile) ||
            projectile == null)
        {
            ReturnUnexpectedVisual(visualObject);
            return;
        }

        visual.Projectile = projectile;
        visual.Body = projectile.rigidBody;
        visual.ProjectileWasEnabled = projectile.enabled;
        visual.BodyWasSimulated = visual.Body != null && visual.Body.simulated;
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
            OrrerySpellCompendium.MagmaCannon.ProjectileVisualScale);
    }

    private static void UpdateRemoteProjectile(
        RemoteVisual visual,
        Vector2 position,
        float angleDegrees,
        float deltaTime)
    {
        if (visual == null || visual.Projectile == null)
            return;

        Transform transform = visual.Projectile.transform;
        Vector3 current = transform.position;
        float t = Mathf.Clamp01(
            Mathf.Max(0f, deltaTime) *
            OrreryLegacySpellRemotePresentation.Tuning.MagmaPositionFollowPerSecond);
        Vector2 smoothed = Vector2.Lerp((Vector2)current, position, t);
        transform.position = new Vector3(smoothed.x, smoothed.y, current.z);
        transform.rotation = Quaternion.Euler(0f, 0f, angleDegrees);
    }

    private static void CleanupRemoteProjectile(RemoteVisual visual)
    {
        if (visual == null || visual.Projectile == null)
            return;

        Projectile projectile = visual.Projectile;
        projectile.transform.localScale = visual.BaseScale;
        if (visual.Body != null)
        {
            visual.Body.velocity = Vector2.zero;
            visual.Body.angularVelocity = 0f;
            visual.Body.simulated = visual.BodyWasSimulated;
        }
        projectile.enabled = visual.ProjectileWasEnabled;
        projectile.PoolDestroy();
        visual.Projectile = null;
        visual.Body = null;
    }

    private static void ClearRemoteVisual(RemoteVisual visual)
    {
        if (visual == null)
            return;

        CleanupRemoteProjectile(visual);
        visual.Active = false;
        visual.VisualId = 0;
        visual.SeenGeneration = 0u;
        visual.ExplosionShown = false;
    }

    private static void SpawnRemoteExplosion(GameShip owner, Vector2 position)
    {
        if (owner == null || PoolController.instance == null)
            return;

        if (infernoBase == null)
            infernoBase = Resources.Load<LauncherItemBase>(InfernoCannonPath);
        GameObject projectilePrefab = infernoBase == null
            ? null
            : infernoBase.GetProjectileObject(owner.faction);
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
        if (!explosionObject.TryGetComponent<ExplosiveArea>(out area) ||
            area == null)
        {
            ReturnUnexpectedVisual(explosionObject);
            return;
        }

        float radiusWorld = OrreryUnits.MetersToWorld(
            OrrerySpellCompendium.MagmaCannon.ExplosionRadiusMeters);
        float diameter = radiusWorld * 2f;
        area.SetScale(new Vector3(diameter, diameter, diameter));
        area.SetColor(template.explosionColor);
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
            Object.Destroy(visualObject);
        }
    }
}

[HarmonyPatch(typeof(OrreryFireballReflectionSafety),
    nameof(OrreryFireballReflectionSafety.OnReflected))]
public static class OrreryReflectedMagmaRegistrationPatch
{
    public static void Postfix(Projectile projectile)
    {
        OrreryReflectedMagmaPresentation.RegisterReflected(projectile);
    }
}

[HarmonyPatch(typeof(OrreryLegacySpellPresentation),
    nameof(OrreryLegacySpellPresentation.BeginMagma))]
public static class OrreryReflectedMagmaPromotionPatch
{
    public static void Prefix(GameShip owner)
    {
        OrreryReflectedMagmaPresentation.PromoteForNewMagma(owner);
    }
}

[HarmonyPatch(typeof(Projectile), nameof(Projectile.ScheduleDestroy))]
public static class OrreryReflectedMagmaExplosionPatch
{
    [HarmonyPriority(Priority.First)]
    public static void Prefix(Projectile __instance)
    {
        ExplosiveProjectile explosive = __instance as ExplosiveProjectile;
        if (explosive != null && explosive.hasExploded)
            OrreryReflectedMagmaPresentation.RecordExplosion(__instance);
    }
}

[HarmonyPatch(typeof(Projectile), nameof(Projectile.PoolDestroy))]
public static class OrreryReflectedMagmaPoolPatch
{
    public static void Postfix(Projectile __instance)
    {
        OrreryReflectedMagmaPresentation.OnProjectilePooled(__instance);
    }
}

[HarmonyPatch(typeof(RemoteShipDriver), "Render")]
public static class OrreryReflectedMagmaRemoteRenderPatch
{
    public static void Postfix(RemoteShipDriver __instance)
    {
        GameShip owner = __instance == null ? null : __instance.gameShip;
        if (owner != null && owner.IsRemotePlayer())
        {
            OrreryReflectedMagmaPresentation.TickRemote(
                owner,
                Time.deltaTime);
        }
    }
}

[HarmonyPatch(typeof(GameShip), "OnDestroy")]
public static class OrreryReflectedMagmaShipDestroyedPatch
{
    public static void Prefix(GameShip __instance)
    {
        OrreryReflectedMagmaPresentation.ForgetOwner(__instance);
        OrreryReflectedMagmaPresentation.ForgetRemote(__instance);
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class OrreryReflectedMagmaWorldDestroyedPatch
{
    public static void Prefix()
    {
        OrreryReflectedMagmaPresentation.Reset();
    }
}
