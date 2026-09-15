using StarVortex;

/// <summary>
/// Read-only Orrery content templates resolved through ModContent so native or
/// modded overrides are honored. Successful lookups are cached; missing optional
/// presentation assets are retried on later use rather than cached as failures.
/// </summary>
public static class OrreryContent
{
    private const string FrostNovaPulsePath =
        "Base/Items/Special/Frost Nova Pulse";

    private static PulseItemBase frostNovaPulse;

    public static PulseItemBase FrostNovaPulse
    {
        get
        {
            if (frostNovaPulse == null)
            {
                PulseItemBase resolved =
                    ModContent.Load<PulseItemBase>(FrostNovaPulsePath);
                if (resolved != null)
                    frostNovaPulse = resolved;
            }

            return frostNovaPulse;
        }
    }
}
