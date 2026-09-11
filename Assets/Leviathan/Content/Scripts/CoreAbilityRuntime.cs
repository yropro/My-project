using StarVortex;
using System;
using System.Collections.Generic;
using UnityEngine;

public enum CoreExecutionPhase { Acquiring, Active, Committing, Completed, Cancelled }
public enum CoreExecutionEndReason { Completed, Cancelled, OwnerInvalidated, CommitRejected }
public enum CoreAbilityInput { Press, Hold, Release, Confirm, Cancel }

/// <summary>
/// World units and simulation seconds (Time.time). Relative offsets optionally
/// rotate with the reference's current Z orientation; scale is never inherited.
/// Captured inertial points use p(t)=p0+v0*(t-t0), with no later owner acceleration.
/// A missing reference fails, never substitutes another ship with the same ID.
/// </summary>
public struct CoreTargetAnchor
{
    public enum Frame { World, ShipTranslated, ShipRotated, CapturedInertial, ClassEntity }
    private Frame frame;
    private Vector3 point;
    private Vector3 velocity;
    private float capturedAt;
    private GameShip reference;
    private CoreClassEntity entity;
    private CoreOwnerContext lifetime;
    private bool rotateEntity;

    public static CoreTargetAnchor World(Vector3 position)
    { return new CoreTargetAnchor { frame = Frame.World, point = position }; }

    public static CoreTargetAnchor Inertial(Vector3 position, Vector3 velocity, float simulationTime)
    { return new CoreTargetAnchor { frame = Frame.CapturedInertial, point = position, velocity = velocity, capturedAt = simulationTime }; }

    public static CoreTargetAnchor Relative(GameShip ship, Vector3 offset, bool rotate, CoreOwnerContext ownerLifetime = null)
    { return new CoreTargetAnchor { frame = rotate ? Frame.ShipRotated : Frame.ShipTranslated, reference = ship, point = offset, lifetime = ownerLifetime }; }

    public static CoreTargetAnchor Attached(CoreClassEntity entity, Vector3 offset, bool rotate)
    { return new CoreTargetAnchor { frame = Frame.ClassEntity, entity = entity, point = offset, rotateEntity = rotate }; }

    public bool TryResolve(float simulationTime, out Vector3 position)
    {
        position = point;
        if (lifetime != null && !lifetime.IsValid) return false;
        if (frame == Frame.World) return true;
        if (frame == Frame.CapturedInertial)
        { position += velocity * Mathf.Max(0f, simulationTime - capturedAt); return true; }
        GameShip ship = reference;
        bool rotate = frame == Frame.ShipRotated;
        if (frame == Frame.ClassEntity)
        {
            if (entity == null || !entity.IsValid) return false;
            ship = entity.Ship;
            rotate = rotateEntity;
        }
        if (ship == null) return false;
        position = ship.transform.position + (rotate ?
            Quaternion.Euler(0f, 0f, ship.transform.eulerAngles.z) * point : point);
        return true;
    }
}

public sealed class CoreAbilityExecution
{
    public const int MaxAnchors = 8;
    public readonly ulong ExecutionId;
    public readonly CoreOwnerContext Owner;
    public readonly ushort AbilityId;
    private readonly CoreTargetAnchor[] anchors = new CoreTargetAnchor[MaxAnchors];
    public int AnchorCount { get; private set; }
    public CoreExecutionPhase Phase { get; private set; }
    public bool IsValid { get { return Owner.IsValid && Phase != CoreExecutionPhase.Completed && Phase != CoreExecutionPhase.Cancelled; } }
    private readonly Action<CoreExecutionEndReason> onEnd;

    internal CoreAbilityExecution(ulong id, CoreOwnerContext owner, ushort abilityId, Action<CoreExecutionEndReason> onEnd)
    { ExecutionId = id; Owner = owner; AbilityId = abilityId; this.onEnd = onEnd; }

    public bool AddAnchor(CoreTargetAnchor anchor)
    {
        if (!IsValid || Phase != CoreExecutionPhase.Acquiring || AnchorCount == MaxAnchors) return false;
        anchors[AnchorCount++] = anchor;
        return true;
    }

    public bool TryResolveAnchor(int index, float simulationTime, out Vector3 position)
    {
        position = default(Vector3);
        return IsValid && index >= 0 && index < AnchorCount && anchors[index].TryResolve(simulationTime, out position);
    }

    /// <summary>Class callback atomically commits its resources/cooldown. Invoked at most once.</summary>
    public bool TryCommit(Func<bool> commit)
    {
        if (!IsValid || Phase != CoreExecutionPhase.Acquiring || commit == null) return false;
        Phase = CoreExecutionPhase.Committing;
        bool accepted = false;
        try { accepted = commit(); }
        finally
        {
            if (!accepted) End(CoreExecutionEndReason.CommitRejected);
            else if (!Owner.IsValid) End(CoreExecutionEndReason.OwnerInvalidated);
        }
        if (!IsValid) return false;
        Phase = CoreExecutionPhase.Active;
        return true;
    }

    public void End(CoreExecutionEndReason reason)
    {
        if (Phase == CoreExecutionPhase.Completed || Phase == CoreExecutionPhase.Cancelled) return;
        Phase = reason == CoreExecutionEndReason.Completed ? CoreExecutionPhase.Completed : CoreExecutionPhase.Cancelled;
        CoreAbilityRuntime.Forget(this);
        if (onEnd != null) onEnd(reason);
    }
}

public static class CoreAbilityRuntime
{
    public const int MaxActiveExecutions = 64;
    private static readonly List<CoreAbilityExecution> active = new List<CoreAbilityExecution>(MaxActiveExecutions);
    private static ulong nextId;

    public static CoreAbilityExecution Begin(CoreOwnerContext owner, ushort abilityId, Action<CoreExecutionEndReason> onEnd = null)
    {
        if (owner == null || !owner.IsValid || owner.ClassId == CoreClassId.None || active.Count >= MaxActiveExecutions) return null;
        CoreAbilityExecution execution = new CoreAbilityExecution(++nextId, owner, abilityId, onEnd);
        active.Add(execution);
        return execution;
    }
    internal static void Forget(CoreAbilityExecution execution) { active.Remove(execution); }
    internal static void CancelOwner(CoreOwnerContext owner)
    {
        if (owner == null) return;
        for (int i = active.Count - 1; i >= 0; i--)
            if (i < active.Count && ReferenceEquals(active[i].Owner, owner))
            {
                try { active[i].End(CoreExecutionEndReason.OwnerInvalidated); }
                catch (Exception ex) { Debug.LogError("[CoreAbilityRuntime] End failed: " + ex); }
            }
    }
}
