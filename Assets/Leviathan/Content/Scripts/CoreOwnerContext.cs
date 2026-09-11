using StarVortex;
using System;

/// <summary>One local owner lifetime. References held by old work never become valid again.</summary>
public sealed class CoreOwnerContext
{
    public readonly Pilot Pilot;
    public readonly GameShip Ship;
    public readonly CoreClassId ClassId;
    public readonly ulong Generation;
    public bool IsValid { get; private set; }

    internal CoreOwnerContext(Pilot pilot, GameShip ship, CoreClassId classId, ulong generation)
    {
        Pilot = pilot;
        Ship = ship;
        ClassId = classId;
        Generation = generation;
        IsValid = true;
    }

    internal void Invalidate() { IsValid = false; }
}

/// <summary>One class module's callbacks; Core controls ordering, not subscriber order.</summary>
public sealed class CoreClassLifecycle
{
    public readonly Action<CoreOwnerContext> Enter;
    public readonly Action<CoreOwnerContext> Exit;
    public CoreClassLifecycle(Action<CoreOwnerContext> enter, Action<CoreOwnerContext> exit)
    {
        Enter = enter;
        Exit = exit;
    }
}
