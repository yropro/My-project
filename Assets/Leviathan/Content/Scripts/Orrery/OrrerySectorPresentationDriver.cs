using HarmonyLib;
using StarVortex;
using UnityEngine;

/// <summary>
/// Lightweight local presentation driver. Logical sectors stay in OrreryRuntime;
/// this only keeps their presentation centered on the active Orrery ship.
/// </summary>
public sealed class OrrerySectorPresentationDriver : MonoBehaviour
{
    private static OrrerySectorPresentationDriver instance;

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }

        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void LateUpdate()
    {
        CoreOwnerContext context = CoreClassRuntime.CurrentContext;
        GameShip owner = context != null && context.IsValid &&
            context.ClassId == CoreClassId.Orrery
                ? context.Ship
                : null;

        if (OrreryVoidGalleryPresentation.Enabled)
        {
            // Temporary visual audition mode. Keep the production pie-sector
            // presentation fully intact, but make sure none of it remains visible
            // while the 4x4 void gallery is active.
            OrrerySectorPresentation.Hide();
            OrreryVoidGalleryPresentation.Tick(owner);
            return;
        }

        OrreryVoidGalleryPresentation.Hide();
        OrrerySectorPresentation.Tick(owner);
    }

    private void OnDestroy()
    {
        if (instance == this)
            instance = null;
        OrreryVoidGalleryPresentation.Hide();
        OrrerySectorPresentation.Hide();
    }

    public static bool Exists { get { return instance != null; } }
}

[HarmonyPatch(typeof(WorldController), "PostInit")]
public static class OrrerySectorPresentationBootstrapPatch
{
    public static void Postfix()
    {
        if (OrrerySectorPresentationDriver.Exists)
            return;

        GameObject runtime = new GameObject("Orrery Sector Presentation");
        runtime.AddComponent<OrrerySectorPresentationDriver>();
    }
}
