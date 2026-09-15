using System;

/// <summary>Bounded synchronous observation of one caller's first native damage
/// application. A forwarding prefix has not entered its own native body; only
/// that case propagates a child's veto. A reflected/reentrant hit after the
/// parent's native boundary cannot relabel its parent as blocked.</summary>
public static class CoreDamageApplicationObservation
{
    private const int Limit = 64;
    private struct Frame { public object Target; public bool Boundary, Blocked; }
    private struct Watch { public object Target; public int Depth; public bool Recorded, Blocked; }
    private static readonly Frame[] frames = new Frame[Limit];
    private static readonly Watch[] watches = new Watch[Limit];
    private static int depth, watchDepth;
    public static int BeginApplication(object target)
    {
        int token = depth++;
        if (token < Limit) frames[token] = new Frame { Target = target };
        return token;
    }
    public static void NativeBoundary(bool blocked)
    {
        if (depth <= 0 || depth > Limit) return;
        frames[depth - 1].Boundary = true;
        frames[depth - 1].Blocked = blocked;
    }
    public static void EndApplication(int token)
    {
        if (token < 0) return;
        if (token < Limit)
        {
            Frame frame = frames[token];
            if (watchDepth > 0 && watchDepth <= Limit)
            {
                int w = watchDepth - 1;
                if (!watches[w].Recorded && watches[w].Depth == token &&
                    ReferenceEquals(watches[w].Target, frame.Target))
                {
                    watches[w].Recorded = true;
                    watches[w].Blocked = frame.Blocked;
                }
            }
            if (token > 0 && frame.Blocked && !frames[token - 1].Boundary)
                frames[token - 1].Blocked = true;
            frames[token] = default(Frame);
        }
        depth = Math.Max(0, token);
    }
    public static int BeginWatch(object target)
    {
        int token = watchDepth++;
        if (token < Limit) watches[token] = new Watch { Target = target, Depth = depth };
        return token;
    }
    public static bool WatchedApplicationBlocked()
    {
        return watchDepth > 0 && watchDepth <= Limit && watches[watchDepth - 1].Recorded &&
            watches[watchDepth - 1].Blocked;
    }
    public static void EndWatch(int token)
    {
        if (token < 0) return;
        if (token < Limit) watches[token] = default(Watch);
        watchDepth = Math.Max(0, token);
    }
}
