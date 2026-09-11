using StarVortex;
using System;
using System.Collections.Generic;

/// <summary>Identity only. The class owns behavior, membership and destruction.</summary>
public sealed class CoreClassEntity
{
    public readonly CoreOwnerContext Owner;
    public readonly ushort Kind;
    public readonly uint InstanceId;
    public readonly ulong Generation;
    public readonly GameShip Ship;
    private bool released;
    public bool IsValid { get { return !released && Owner.IsValid && Ship != null; } }
    internal CoreClassEntity(CoreOwnerContext owner, ushort kind, uint instanceId, ulong generation, GameShip ship)
    { Owner = owner; Kind = kind; InstanceId = instanceId; Generation = generation; Ship = ship; }
    internal void Invalidate() { released = true; }
}

public static class CoreClassEntities
{
    public const int MaxEntities = 256;
    private static readonly List<CoreClassEntity> entities = new List<CoreClassEntity>(MaxEntities);
    private static ulong generation;
    public static CoreClassEntity Register(CoreOwnerContext owner, ushort kind, uint instanceId, GameShip ship)
    {
        if (owner == null || !owner.IsValid || owner.ClassId == CoreClassId.None || ship == null)
            throw new InvalidOperationException("A live class owner and entity are required.");
        if (entities.Count >= MaxEntities) throw new InvalidOperationException("Class entity limit reached.");
        for (int i = 0; i < entities.Count; i++)
            if (ReferenceEquals(entities[i].Owner, owner) && entities[i].Kind == kind && entities[i].InstanceId == instanceId)
                throw new InvalidOperationException("Duplicate live class entity identity.");
        CoreClassEntity entity = new CoreClassEntity(owner, kind, instanceId, ++generation, ship);
        entities.Add(entity);
        return entity;
    }
    public static void Release(CoreClassEntity entity)
    { if (entity != null) { entity.Invalidate(); entities.Remove(entity); } }
    internal static void ReleaseOwner(CoreOwnerContext owner)
    {
        for (int i = entities.Count - 1; i >= 0; i--)
            if (ReferenceEquals(entities[i].Owner, owner)) Release(entities[i]);
    }
}
