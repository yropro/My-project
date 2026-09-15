using StarVortex;
using System.Runtime.InteropServices;
using UnityEngine;

/// <summary>
/// Accretion Disk support spell.
///
/// The Orrery caster resolves one immutable profile. The protected player's own
/// client owns the absorption pool, incoming-damage transformation, projectile
/// interception and healing. Ally casts use CoreCrossOwnerEffects; no remote
/// replica is ever mutated as gameplay authority.
/// </summary>
public static class OrreryAccretionDisk
{
    // Core-wide Orrery effect ids. 0x0201 is Cold Fusion.
    public const ushort ApplyEffectId = 0x0202;
    public const ushort ProjectileCaptureRequestEffectId = 0x0203;
    public const ushort ProjectileCaptureAckEffectId = 0x0204;

    // Engineering bounds. Player-facing balance lives in the Compendium.
    private const int MaxPendingProjectileCaptures = 64;
    private const float PendingProjectileCaptureSeconds = 1.5f;
    private const float CapacityEpsilon = 0.0001f;

    private struct Snapshot
    {
        public float DurationSeconds;
        public float Capacity;
        public float HealFraction;
        public float RadiusMeters;
    }

    private struct PendingProjectileCapture
    {
        public bool Active;
        public uint ProjectileNetId;
        public int ProjectileOwnerPlayerId;
        public uint EffectRevision;
        public float ReservedAmount;
        public float ExpiresAtUnscaled;
    }

    private sealed class LocalState
    {
        public GameShip Target;
        public int SourcePlayerId;
        public uint Revision;
        public float ExpiresAt;
        public float CapacityMax;
        public float CapacityRemaining;
        public float ReservedCapacity;
        public float HealFraction;
        public float RadiusMeters;
        public float ScanAccumulator;

        public float TotalAbsorbed;
        public float ProjectileDamageAbsorbed;
        public float ShipDamageAbsorbed;

        public readonly PendingProjectileCapture[] Pending =
            new PendingProjectileCapture[MaxPendingProjectileCaptures];
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct FloatBits
    {
        [FieldOffset(0)] public float Float;
        [FieldOffset(0)] public uint UInt;
    }

    private static readonly GameShip[] targetScratch =
        new GameShip[OrrerySpellCompendium.AccretionDisk.MaxCandidateShips];

    private static LocalState active;
    private static uint nextRevision;
    private static bool initialized;

    public static bool Execute(
        GameShip owner,
        OrreryCastInvocation invocation,
        OrrerySpellRegistry.SpellDefinition spell)
    {
        if (owner == null || spell == null || invocation.Execution == null ||
            !invocation.Execution.IsValid || !OrreryRuntime.IsActive(owner) ||
            OrreryController.IsShuffling(owner))
        {
            return false;
        }

        EnsureInitialized();

        Snapshot snapshot;
        if (!TryResolveCast(owner, out snapshot))
            return false;

        GameShip target = FindTarget(owner);
        if (target == null)
            target = owner;

        bool accepted;
        if (object.ReferenceEquals(target, owner))
        {
            accepted = ApplyLocal(
                target,
                ResolveLocalPlayerId(),
                snapshot);
        }
        else
        {
            int targetPlayerId;
            if (!CoreNetwork.TryGetPlayerId(target, out targetPlayerId))
                return false;

            accepted = CoreCrossOwnerEffects.RequestGrant(
                targetPlayerId,
                ApplyEffectId,
                PackSnapshot(snapshot));
        }

        if (!accepted)
            return false;

        OrreryCasting.CompleteInvocation(owner, invocation.Execution, 0);
        OrreryController.StartShuffle(owner);
        return true;
    }

    public static void EnsureInitialized()
    {
        if (initialized)
            return;

        CoreCrossOwnerEffects.RegisterHandler(
            ApplyEffectId,
            ApplyRemoteGrant);
        CoreCrossOwnerEffects.RegisterHandler(
            ProjectileCaptureRequestEffectId,
            ApplyProjectileCaptureRequest);
        CoreCrossOwnerEffects.RegisterHandler(
            ProjectileCaptureAckEffectId,
            ApplyProjectileCaptureAck);
        CoreIncomingDamage.Register(
            "Orrery/AccretionDisk",
            FilterIncomingPacket,
            FilterIncomingDirectDamage);
        initialized = true;
    }

    /// <summary>
    /// Target-authority fixed step. This intentionally does not require the local
    /// target to be an Orrery: a protected Leviathan/other-class player owns the
    /// effect after receiving the grant.
    /// </summary>
    public static void FixedTickRecipient(GameShip localPlayer, float deltaTime)
    {
        LocalState state = active;
        if (state == null)
            return;

        if (localPlayer == null || !localPlayer.IsPlayer() ||
            !object.ReferenceEquals(localPlayer, state.Target) ||
            localPlayer.health <= 0f)
        {
            EndActive();
            return;
        }

        PrunePending(state, Time.unscaledTime);

        if (Time.time >= state.ExpiresAt)
        {
            if (state.ReservedCapacity <= CapacityEpsilon)
                EndActive();
            return;
        }

        if (GetAvailableCapacity(state) <= CapacityEpsilon)
            return;

        float interval = Mathf.Max(
            0f,
            OrrerySpellCompendium.AccretionDisk.ProjectileScanIntervalSeconds);
        if (interval > 0f)
        {
            state.ScanAccumulator += Mathf.Max(0f, deltaTime);
            if (state.ScanAccumulator < interval)
                return;
            state.ScanAccumulator %= interval;
        }

        ScanProjectiles(state);
    }

    public static void ForgetTarget(GameShip target)
    {
        if (active != null && target != null &&
            object.ReferenceEquals(active.Target, target))
        {
            EndActive();
        }
    }

    /// <summary>
    /// An Orrery class exit retires a self-cast disk. A disk already granted to a
    /// different player's ship remains owned by that recipient until its own
    /// duration/capacity/lifecycle ends.
    /// </summary>
    public static void ForgetOrreryOwner(GameShip owner)
    {
        if (active != null && owner != null &&
            object.ReferenceEquals(active.Target, owner))
        {
            EndActive();
        }
    }

    public static void Reset()
    {
        EndActive();
        nextRevision = 0u;
    }

    public static bool TryGetLocalPresentation(
        GameShip target,
        out uint revision,
        out float remainingSeconds,
        out float radiusMeters,
        out float capacity01)
    {
        revision = 0u;
        remainingSeconds = 0f;
        radiusMeters = 0f;
        capacity01 = 0f;

        LocalState state = active;
        if (!IsStateFor(state, target))
            return false;

        remainingSeconds = state.ExpiresAt - Time.time;
        if (remainingSeconds <= 0f)
            return false;

        revision = state.Revision;
        radiusMeters = state.RadiusMeters;
        capacity01 = state.CapacityMax <= 0f
            ? 0f
            : Mathf.Clamp01(
                GetAvailableCapacity(state) / state.CapacityMax);
        return revision != 0u;
    }

    private static bool ApplyRemoteGrant(
        GameShip localTarget,
        int sourcePlayerId,
        CoreCrossOwnerEffects.GrantPayload payload)
    {
        if (localTarget == null || !localTarget.IsPlayer() || sourcePlayerId < 0)
            return false;

        Snapshot snapshot;
        if (!TryUnpackSnapshot(payload, out snapshot))
            return false;

        return ApplyLocal(localTarget, sourcePlayerId, snapshot);
    }

    private static bool ApplyLocal(
        GameShip target,
        int sourcePlayerId,
        Snapshot snapshot)
    {
        if (target == null || !target.IsPlayer() || target.health <= 0f ||
            !SanitizeSnapshot(ref snapshot))
        {
            return false;
        }

        LocalState state = new LocalState();
        state.Target = target;
        state.SourcePlayerId = sourcePlayerId;
        state.Revision = NextRevision();
        state.ExpiresAt = Time.time + snapshot.DurationSeconds;
        state.CapacityMax = snapshot.Capacity;
        state.CapacityRemaining = snapshot.Capacity;
        state.HealFraction = snapshot.HealFraction;
        state.RadiusMeters = snapshot.RadiusMeters;
        active = state;

        OrreryAccretionDiskPresentation.Show(
            target,
            snapshot.DurationSeconds,
            snapshot.RadiusMeters,
            1f);
        return true;
    }

    private static void FilterIncomingPacket(
        GameShip target,
        Damageable.DamageType damageType,
        Damageable.DamageData[] damageData)
    {
        LocalState state = active;
        if (!CanAbsorb(state, target) || damageData == null)
            return;

        float baseDamage = 0f;
        for (int i = 0; i < damageData.Length; i++)
        {
            if (damageData[i].modifierType != Modifier.Type.Damage)
                continue;
            baseDamage = Mathf.Max(0f, damageData[i].damage);
            break;
        }

        if (baseDamage <= 0f)
            return;

        float absorbed = ConsumeAvailable(state, baseDamage, false);
        if (absorbed <= 0f)
            return;

        float survivingFraction = Mathf.Clamp01(
            (baseDamage - absorbed) / baseDamage);
        for (int i = 0; i < damageData.Length; i++)
        {
            Damageable.DamageData component = damageData[i];
            component.damage *= survivingFraction;
            component.dps *= survivingFraction;
            damageData[i] = component;
        }
    }

    private static void FilterIncomingDirectDamage(
        GameShip target,
        Damageable.DamageType damageType,
        ref float damage,
        bool destroy)
    {
        LocalState state = active;
        if (!CanAbsorb(state, target) || damage <= 0f)
            return;

        float absorbed = ConsumeAvailable(state, damage, false);
        if (absorbed > 0f)
            damage = Mathf.Max(0f, damage - absorbed);
    }

    private static float ConsumeAvailable(
        LocalState state,
        float requested,
        bool projectile)
    {
        if (state == null || requested <= 0f)
            return 0f;

        float absorbed = Mathf.Min(requested, GetAvailableCapacity(state));
        if (absorbed <= 0f)
            return 0f;

        state.CapacityRemaining = Mathf.Max(
            0f,
            state.CapacityRemaining - absorbed);
        state.TotalAbsorbed += absorbed;
        if (projectile)
            state.ProjectileDamageAbsorbed += absorbed;
        else
            state.ShipDamageAbsorbed += absorbed;

        HealFromAbsorption(state, absorbed);
        RefreshPresentation(state);
        FinishIfDepleted(state);
        return absorbed;
    }

    private static void HealFromAbsorption(LocalState state, float absorbed)
    {
        if (state == null || state.Target == null || absorbed <= 0f ||
            state.HealFraction <= 0f || state.Target.health <= 0f)
        {
            return;
        }

        CoreNativeCriticalHits.Heal(
            state.Target,
            absorbed * state.HealFraction,
            0,
            state.Target.transform.position,
            null,
            false,
            false);
    }

    private static void ScanProjectiles(LocalState state)
    {
        if (state == null || state.Target == null ||
            PhysicsController.instance == null || state.RadiusMeters <= 0f)
        {
            return;
        }

        float radiusWorld = OrreryUnits.MetersToWorld(state.RadiusMeters);
        Collider2D[] hits = PhysicsController.instance.OverlapCircle(
            state.Target.transform.position,
            radiusWorld);
        if (hits == null)
            return;

        int processed = 0;
        int limit = Mathf.Max(
            1,
            OrrerySpellCompendium.AccretionDisk.MaxProjectileCandidatesPerScan);

        for (int i = 0; i < hits.Length && processed < limit; i++)
        {
            if (GetAvailableCapacity(state) <= CapacityEpsilon)
                break;

            Collider2D collider = hits[i];
            if (collider == null)
                break;

            Projectile projectile;
            if (!collider.gameObject.TryGetComponent<Projectile>(out projectile) ||
                projectile == null || projectile is CapturedProjectile ||
                projectile.IsDestroying() ||
                !projectile.CanBeDamagedBy(state.Target, true))
            {
                continue;
            }

            processed++;

            if (!projectile.netRendered)
            {
                float value = GetProjectileValue(projectile);
                projectile.CaptureDestroy();
                ConsumeAvailable(state, value, true);
                if (active == null)
                    return;
                continue;
            }

            TryRequestRemoteCapture(state, projectile);
        }
    }

    private static void TryRequestRemoteCapture(
        LocalState state,
        Projectile projectile)
    {
        if (state == null || projectile == null || projectile.netId == 0u ||
            !NetSession.InSession || NetSession.instance == null ||
            !NetIds.IsProjectileNetId(projectile.netId) ||
            FindPending(state, projectile.netId) >= 0)
        {
            return;
        }

        int projectileOwnerPlayerId =
            NetIds.ProjectileOwnerOf(projectile.netId);
        if (projectileOwnerPlayerId < 0 ||
            projectileOwnerPlayerId == NetSession.instance.localPlayerId)
        {
            return;
        }

        int free = FindFreePending(state);
        if (free < 0)
            return;

        float estimate = GetProjectileValue(projectile);
        float reservation = Mathf.Min(
            Mathf.Max(0f, estimate),
            GetAvailableCapacity(state));

        CoreCrossOwnerEffects.GrantPayload request =
            new CoreCrossOwnerEffects.GrantPayload(
                projectile.netId,
                state.Revision,
                PackFloat(state.RadiusMeters),
                0u,
                0u,
                0u);

        if (!CoreCrossOwnerEffects.RequestGrant(
                projectileOwnerPlayerId,
                ProjectileCaptureRequestEffectId,
                request))
        {
            return;
        }

        PendingProjectileCapture pending = default(PendingProjectileCapture);
        pending.Active = true;
        pending.ProjectileNetId = projectile.netId;
        pending.ProjectileOwnerPlayerId = projectileOwnerPlayerId;
        pending.EffectRevision = state.Revision;
        pending.ReservedAmount = reservation;
        pending.ExpiresAtUnscaled =
            Time.unscaledTime + PendingProjectileCaptureSeconds;
        state.Pending[free] = pending;
        state.ReservedCapacity += reservation;
        RefreshPresentation(state);
    }

    private static bool ApplyProjectileCaptureRequest(
        GameShip localProjectileOwner,
        int sourcePlayerId,
        CoreCrossOwnerEffects.GrantPayload payload)
    {
        uint projectileNetId = payload.A;
        uint effectRevision = payload.B;
        float radiusMeters = UnpackFloat(payload.C);

        if (localProjectileOwner == null || !localProjectileOwner.IsPlayer() ||
            sourcePlayerId < 0 || projectileNetId == 0u || effectRevision == 0u ||
            !IsFinite(radiusMeters) || radiusMeters <= 0f ||
            NetSession.instance == null ||
            !NetIds.IsProjectileNetId(projectileNetId) ||
            NetIds.ProjectileOwnerOf(projectileNetId) !=
                NetSession.instance.localPlayerId)
        {
            return false;
        }

        Projectile projectile;
        GameShip protectedPlayer;
        if (!CoreProjectileAuthority.TryGetLocalAuthoritative(
                projectileNetId,
                out projectile) ||
            !CoreProjectileAuthority.TryGetPlayerShip(
                sourcePlayerId,
                out protectedPlayer) ||
            projectile == null || protectedPlayer == null ||
            projectile is CapturedProjectile || projectile.IsDestroying() ||
            !projectile.CanBeDamagedBy(protectedPlayer, true))
        {
            return false;
        }

        float padding = Mathf.Max(
            0f,
            OrrerySpellCompendium.AccretionDisk.RemoteValidationRadiusPaddingFraction);
        float allowedRadius = OrreryUnits.MetersToWorld(
            radiusMeters * (1f + padding));
        if (!CoreSpatial.IsPointInCircle(
                projectile.transform.position,
                protectedPlayer.transform.position,
                allowedRadius))
        {
            return false;
        }

        float value = GetProjectileValue(projectile);
        projectile.CaptureDestroy();

        CoreCrossOwnerEffects.GrantPayload acknowledgement =
            new CoreCrossOwnerEffects.GrantPayload(
                projectileNetId,
                effectRevision,
                PackFloat(value),
                0u,
                0u,
                0u);

        // The projectile is already authoritatively captured. If the reverse
        // acknowledgement cannot dispatch, the requester simply releases its
        // reservation on timeout; gameplay never resurrects the projectile.
        CoreCrossOwnerEffects.RequestGrant(
            sourcePlayerId,
            ProjectileCaptureAckEffectId,
            acknowledgement);
        return true;
    }

    private static bool ApplyProjectileCaptureAck(
        GameShip localProtectedPlayer,
        int sourcePlayerId,
        CoreCrossOwnerEffects.GrantPayload payload)
    {
        uint projectileNetId = payload.A;
        uint effectRevision = payload.B;
        float value = UnpackFloat(payload.C);

        if (localProtectedPlayer == null || !localProtectedPlayer.IsPlayer() ||
            sourcePlayerId < 0 || projectileNetId == 0u || effectRevision == 0u ||
            !IsFinite(value) || value < 0f ||
            !NetIds.IsProjectileNetId(projectileNetId) ||
            NetIds.ProjectileOwnerOf(projectileNetId) != sourcePlayerId)
        {
            return false;
        }

        LocalState state = active;
        if (!IsStateFor(state, localProtectedPlayer) ||
            state.Revision != effectRevision)
        {
            // Valid late acknowledgement for an expired/replaced disk. Consume
            // it without touching the replacement runtime.
            return true;
        }

        int index = FindPending(state, projectileNetId);
        if (index < 0)
            return true;

        PendingProjectileCapture pending = state.Pending[index];
        if (pending.ProjectileOwnerPlayerId != sourcePlayerId ||
            pending.EffectRevision != effectRevision)
        {
            return true;
        }

        ReleasePending(state, index);

        // Breaking-projectile grace: capture is already complete. Debit at most
        // what remains in the pool; no partial replacement projectile is created.
        ConsumeAvailable(state, value, true);
        return true;
    }

    private static void PrunePending(LocalState state, float nowUnscaled)
    {
        if (state == null)
            return;

        bool changed = false;
        for (int i = 0; i < state.Pending.Length; i++)
        {
            PendingProjectileCapture pending = state.Pending[i];
            if (!pending.Active || pending.ExpiresAtUnscaled > nowUnscaled)
                continue;
            ReleasePending(state, i);
            changed = true;
        }

        if (changed)
        {
            RefreshPresentation(state);
            FinishIfDepleted(state);
        }
    }

    private static int FindPending(LocalState state, uint projectileNetId)
    {
        if (state == null || projectileNetId == 0u)
            return -1;

        for (int i = 0; i < state.Pending.Length; i++)
        {
            if (state.Pending[i].Active &&
                state.Pending[i].ProjectileNetId == projectileNetId)
            {
                return i;
            }
        }
        return -1;
    }

    private static int FindFreePending(LocalState state)
    {
        if (state == null)
            return -1;

        for (int i = 0; i < state.Pending.Length; i++)
        {
            if (!state.Pending[i].Active)
                return i;
        }
        return -1;
    }

    private static void ReleasePending(LocalState state, int index)
    {
        if (state == null || index < 0 || index >= state.Pending.Length)
            return;

        PendingProjectileCapture pending = state.Pending[index];
        if (!pending.Active)
            return;

        state.ReservedCapacity = Mathf.Max(
            0f,
            state.ReservedCapacity - pending.ReservedAmount);
        state.Pending[index] = default(PendingProjectileCapture);
    }

    private static float GetProjectileValue(Projectile projectile)
    {
        Launcher launcher = projectile == null
            ? null
            : projectile.GetParentLauncher();
        return launcher == null ? 0f : Mathf.Max(0f, launcher.Damage);
    }

    private static float GetAvailableCapacity(LocalState state)
    {
        return state == null
            ? 0f
            : Mathf.Max(
                0f,
                state.CapacityRemaining - state.ReservedCapacity);
    }

    private static bool CanAbsorb(LocalState state, GameShip target)
    {
        return IsStateFor(state, target) &&
            Time.time < state.ExpiresAt &&
            GetAvailableCapacity(state) > CapacityEpsilon;
    }

    private static bool IsStateFor(LocalState state, GameShip target)
    {
        return state != null && target != null && target.IsPlayer() &&
            object.ReferenceEquals(state.Target, target);
    }

    private static void FinishIfDepleted(LocalState state)
    {
        if (state == null || !object.ReferenceEquals(active, state))
            return;

        if (state.CapacityRemaining <= CapacityEpsilon &&
            state.ReservedCapacity <= CapacityEpsilon)
        {
            EndActive();
        }
    }

    private static void RefreshPresentation(LocalState state)
    {
        if (state == null || state.Target == null)
            return;

        float remaining = state.ExpiresAt - Time.time;
        if (remaining <= 0f)
            return;

        float fraction = state.CapacityMax <= 0f
            ? 0f
            : Mathf.Clamp01(GetAvailableCapacity(state) / state.CapacityMax);
        OrreryAccretionDiskPresentation.Show(
            state.Target,
            remaining,
            state.RadiusMeters,
            fraction);
    }

    private static void EndActive()
    {
        LocalState state = active;
        active = null;
        if (state != null && state.Target != null)
            OrreryAccretionDiskPresentation.Hide(state.Target);
    }

    private static bool TryResolveCast(GameShip owner, out Snapshot snapshot)
    {
        snapshot = default(Snapshot);

        OrreryFocusProfile.Resolved stellar;
        OrreryFocusProfile.Resolved voidFocus;
        if (!OrreryFocusProfile.TryResolve(
                owner,
                OrreryElement.Fire,
                out stellar) ||
            !stellar.IsValid ||
            !OrreryFocusProfile.TryResolve(
                owner,
                OrreryElement.Ice,
                out voidFocus) ||
            !voidFocus.IsValid)
        {
            return false;
        }

        float voidWeight = Mathf.Clamp01(
            OrrerySpellCompendium.AccretionDisk.MixedFocusVoidWeight);
        float referenceDps = Mathf.Lerp(
            stellar.GetReferenceDps(),
            voidFocus.GetReferenceDps(),
            voidWeight);
        float durationBonus = Mathf.Lerp(
            stellar.DurationBonus,
            voidFocus.DurationBonus,
            voidWeight) *
            OrrerySpellCompendium.AccretionDisk.DurationModifierScale;
        float rangeBonus = Mathf.Lerp(
            stellar.RangeBonus,
            voidFocus.RangeBonus,
            voidWeight) *
            OrrerySpellCompendium.AccretionDisk.RadiusModifierScale;

        snapshot.DurationSeconds = Mathf.Max(
            0.01f,
            OrrerySpellCompendium.AccretionDisk.BaseDurationSeconds *
            Mathf.Max(0f, 1f + durationBonus));
        snapshot.Capacity = Mathf.Max(
            0f,
            referenceDps *
            OrrerySpellCompendium.AccretionDisk.CapacityReferenceSeconds *
            OrrerySpellCompendium.AccretionDisk.CapacityMultiplier);
        snapshot.HealFraction = Mathf.Max(
            0f,
            OrrerySpellCompendium.AccretionDisk.HealFraction);
        snapshot.RadiusMeters = Mathf.Max(
            0.01f,
            OrrerySpellCompendium.AccretionDisk.BaseRadiusMeters *
            Mathf.Max(0f, 1f + rangeBonus));

        return SanitizeSnapshot(ref snapshot);
    }

    private static GameShip FindTarget(GameShip owner)
    {
        if (owner == null || PhysicsController.instance == null)
            return owner;

        Vector2 cursor = GetAimPoint(owner);
        float cursorRadius = OrreryUnits.MetersToWorld(Mathf.Max(
            0f,
            OrrerySpellCompendium.AccretionDisk.CursorTargetRadiusMeters));
        if (cursorRadius <= 0f)
            return owner;

        Collider2D[] overlaps = PhysicsController.instance.OverlapCircle(
            cursor,
            cursorRadius);
        if (overlaps == null)
            return owner;

        float maxRangeMeters =
            OrrerySpellCompendium.AccretionDisk.MaximumTargetRangeMeters;
        float maxRangeWorld = maxRangeMeters <= 0f
            ? float.PositiveInfinity
            : OrreryUnits.MetersToWorld(maxRangeMeters);
        float maxRangeSquared = maxRangeWorld * maxRangeWorld;

        GameShip best = null;
        float bestCursorDistanceSquared = float.PositiveInfinity;
        int candidateCount = 0;
        int maxCandidates = Mathf.Min(
            targetScratch.Length,
            Mathf.Max(
                0,
                OrrerySpellCompendium.AccretionDisk.MaxCandidateShips));

        for (int i = 0; i < overlaps.Length && candidateCount < maxCandidates; i++)
        {
            Collider2D collider = overlaps[i];
            if (collider == null)
                break;

            GameObject targetObject = collider.gameObject;
            if (targetObject.CompareTag("Shield") &&
                targetObject.transform.parent != null)
            {
                targetObject = targetObject.transform.parent.gameObject;
            }

            GameShip candidate;
            if (!targetObject.TryGetComponent<GameShip>(out candidate) ||
                !IsEligibleAlly(owner, candidate) ||
                ContainsTarget(candidate, candidateCount))
            {
                continue;
            }

            targetScratch[candidateCount++] = candidate;
            Vector2 candidatePosition = candidate.transform.position;
            if ((candidatePosition - (Vector2)owner.transform.position).sqrMagnitude >
                maxRangeSquared)
            {
                continue;
            }

            float cursorDistanceSquared =
                (candidatePosition - cursor).sqrMagnitude;
            if (cursorDistanceSquared < bestCursorDistanceSquared)
            {
                bestCursorDistanceSquared = cursorDistanceSquared;
                best = candidate;
            }
        }

        for (int i = 0; i < candidateCount; i++)
            targetScratch[i] = null;

        return best != null ? best : owner;
    }

    private static bool ContainsTarget(GameShip candidate, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (object.ReferenceEquals(targetScratch[i], candidate))
                return true;
        }
        return false;
    }

    private static bool IsEligibleAlly(GameShip owner, GameShip candidate)
    {
        return owner != null && candidate != null &&
            candidate.gameObject != null &&
            candidate.gameObject.activeInHierarchy &&
            !object.ReferenceEquals(owner, candidate) &&
            candidate.IsAnyPlayerShip() &&
            !Faction.IsHostile(owner.faction, candidate.faction);
    }

    private static Vector2 GetAimPoint(GameShip owner)
    {
        InputController input = InputController.instance;
        if (input != null && owner != null &&
            object.ReferenceEquals(input.controlShip, owner))
        {
            return input.GetCursorWorldPoint();
        }

        return owner == null
            ? Vector2.zero
            : (Vector2)owner.transform.position;
    }

    public static bool TryReadPresentationGrant(
        CoreCrossOwnerEffects.GrantPayload payload,
        out float durationSeconds,
        out float radiusMeters)
    {
        durationSeconds = UnpackFloat(payload.A);
        radiusMeters = UnpackFloat(payload.D);
        return IsFinite(durationSeconds) && durationSeconds > 0f &&
            IsFinite(radiusMeters) && radiusMeters > 0f;
    }

    private static bool SanitizeSnapshot(ref Snapshot snapshot)
    {
        if (!IsFinite(snapshot.DurationSeconds) ||
            !IsFinite(snapshot.Capacity) ||
            !IsFinite(snapshot.HealFraction) ||
            !IsFinite(snapshot.RadiusMeters))
        {
            return false;
        }

        snapshot.DurationSeconds = Mathf.Max(0f, snapshot.DurationSeconds);
        snapshot.Capacity = Mathf.Max(0f, snapshot.Capacity);
        snapshot.HealFraction = Mathf.Max(0f, snapshot.HealFraction);
        snapshot.RadiusMeters = Mathf.Max(0f, snapshot.RadiusMeters);
        return snapshot.DurationSeconds > 0f &&
            snapshot.Capacity > 0f && snapshot.RadiusMeters > 0f;
    }

    private static CoreCrossOwnerEffects.GrantPayload PackSnapshot(
        Snapshot snapshot)
    {
        return new CoreCrossOwnerEffects.GrantPayload(
            PackFloat(snapshot.DurationSeconds),
            PackFloat(snapshot.Capacity),
            PackFloat(snapshot.HealFraction),
            PackFloat(snapshot.RadiusMeters),
            0u,
            0u);
    }

    private static bool TryUnpackSnapshot(
        CoreCrossOwnerEffects.GrantPayload payload,
        out Snapshot snapshot)
    {
        snapshot = default(Snapshot);
        snapshot.DurationSeconds = UnpackFloat(payload.A);
        snapshot.Capacity = UnpackFloat(payload.B);
        snapshot.HealFraction = UnpackFloat(payload.C);
        snapshot.RadiusMeters = UnpackFloat(payload.D);
        return SanitizeSnapshot(ref snapshot);
    }

    private static int ResolveLocalPlayerId()
    {
        return NetSession.InSession && NetSession.instance != null
            ? NetSession.instance.localPlayerId
            : -1;
    }

    private static uint NextRevision()
    {
        unchecked
        {
            nextRevision++;
            if (nextRevision == 0u)
                nextRevision++;
            return nextRevision;
        }
    }

    private static uint PackFloat(float value)
    {
        FloatBits bits = default(FloatBits);
        bits.Float = value;
        return bits.UInt;
    }

    private static float UnpackFloat(uint value)
    {
        FloatBits bits = default(FloatBits);
        bits.UInt = value;
        return bits.Float;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
