using StarVortex;
using UnityEngine;

// Native Wave.ResetObject restores color/scale, but Wave.Init is what normally
// clears its obstruction shader mask. Orrery spell presenters skip Init to
// avoid native gameplay. Reset only that visual property, preserving other
// material properties. Native reuse initializes its own gameplay mask normally.
internal static class OrreryWavePresentation
{
    private static readonly int blockRadiusId = Shader.PropertyToID("_BlockRadius");
    private static readonly float[] unblockedRadii = CreateUnblockedRadii();
    private static readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();

    private static float[] CreateUnblockedRadii()
    {
        float[] radii = new float[128];
        for (int i = 0; i < radii.Length; i++)
            radii[i] = 1000000f;
        return radii;
    }

    public static void ResetMask(Wave wave)
    {
        SpriteRenderer renderer;
        if (wave == null || !wave.TryGetComponent<SpriteRenderer>(out renderer))
            return;
        renderer.GetPropertyBlock(properties);
        properties.SetFloatArray(blockRadiusId, unblockedRadii);
        renderer.SetPropertyBlock(properties);
    }
}

