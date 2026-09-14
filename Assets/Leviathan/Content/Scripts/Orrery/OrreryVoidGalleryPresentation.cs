using StarVortex;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Temporary visual-only gallery for auditioning a cold / empty Void aesthetic.
///
/// The normal Orrery sector wheel is bypassed by OrrerySectorPresentationDriver
/// while this is enabled. The gallery is deliberately local-only: it has no
/// gameplay state, collision, networking, targeting, or spell behavior.
///
/// Grid reference is R1C1 at top-left through R4C4 at bottom-right. Nothing is
/// labelled in-world so the samples can be judged without UI contaminating them.
/// </summary>
public static class OrreryVoidGalleryPresentation
{
    // Temporary kill switch. Set false to restore the normal pie-sector visuals.
    public const bool Enabled = true;

    public const int GridSize = 4;
    public const float CellSizeMeters = 40f;

    private const float CellSizeWorld = CellSizeMeters / 20f;
    private const string MaterialAssetName = "OrreryVoidGalleryBase";
    private const string ThermalHaloPath = "Base/Items/AutoSpecial/Thermal Halo";

    private sealed class GalleryState
    {
        public GameShip Owner;
        public GameObject Root;
        public Mesh Quad;
        public readonly List<Material> Materials = new List<Material>(16);
    }

    private struct Preset
    {
        public readonly float Density;
        public readonly float StarSize;
        public readonly float Brightness;
        public readonly float RareStars;
        public readonly float WorldLock;
        public readonly float SpatialScale;
        public readonly float NebulaStrength;
        public readonly float VoidLift;
        public readonly float Seed;
        public readonly Vector2 Drift;
        public readonly Color Tint;

        public Preset(
            float density,
            float starSize,
            float brightness,
            float rareStars,
            float worldLock,
            float spatialScale,
            float nebulaStrength,
            float voidLift,
            float seed,
            Vector2 drift,
            Color tint)
        {
            Density = density;
            StarSize = starSize;
            Brightness = brightness;
            RareStars = rareStars;
            WorldLock = worldLock;
            SpatialScale = spatialScale;
            NebulaStrength = nebulaStrength;
            VoidLift = voidLift;
            Seed = seed;
            Drift = drift;
            Tint = tint;
        }
    }

    // These are intentionally aesthetic samples rather than a clean laboratory
    // sweep. Row 1 emphasizes emptiness, row 2 motion/parallax, row 3 apparent
    // scale, and row 4 near-black structure / colder star populations.
    private static readonly Preset[] Presets = new Preset[]
    {
        // R1: sparse, distant, almost motionless.
        new Preset(0.012f, 0.024f, 0.48f, 0.18f, 0.04f, 0.75f, 0.000f, 0.000f,  7.1f, new Vector2( 0.00008f,  0.00003f), new Color(0.72f, 0.82f, 1.00f, 1f)),
        new Preset(0.020f, 0.018f, 0.60f, 0.10f, 0.08f, 1.10f, 0.000f, 0.000f, 19.4f, new Vector2(-0.00005f,  0.00002f), new Color(0.88f, 0.93f, 1.00f, 1f)),
        new Preset(0.028f, 0.022f, 0.52f, 0.16f, 0.12f, 1.35f, 0.000f, 0.000f, 31.8f, new Vector2( 0.00004f, -0.00004f), new Color(0.62f, 0.74f, 0.92f, 1f)),
        new Preset(0.006f, 0.030f, 0.64f, 0.42f, 0.03f, 0.62f, 0.000f, 0.000f, 43.2f, new Vector2( 0.00002f,  0.00001f), new Color(0.80f, 0.88f, 1.00f, 1f)),

        // R2: same basic void, increasingly detached from the moving surface.
        new Preset(0.018f, 0.021f, 0.54f, 0.16f, 0.18f, 1.00f, 0.000f, 0.000f, 55.6f, new Vector2( 0.00004f,  0.00001f), new Color(0.74f, 0.84f, 1.00f, 1f)),
        new Preset(0.018f, 0.021f, 0.54f, 0.16f, 0.35f, 1.00f, 0.000f, 0.000f, 67.9f, new Vector2( 0.00003f, -0.00002f), new Color(0.74f, 0.84f, 1.00f, 1f)),
        new Preset(0.018f, 0.021f, 0.54f, 0.16f, 0.60f, 1.00f, 0.000f, 0.000f, 79.3f, new Vector2(-0.00002f,  0.00002f), new Color(0.74f, 0.84f, 1.00f, 1f)),
        new Preset(0.018f, 0.021f, 0.54f, 0.16f, 0.90f, 1.00f, 0.000f, 0.000f, 91.7f, new Vector2( 0.00001f,  0.00001f), new Color(0.74f, 0.84f, 1.00f, 1f)),

        // R3: apparent scale. Left is broad/empty; right becomes deep micro-stars.
        new Preset(0.014f, 0.030f, 0.56f, 0.28f, 0.20f, 0.45f, 0.000f, 0.000f, 13.5f, new Vector2( 0.00003f,  0.00001f), new Color(0.70f, 0.80f, 0.98f, 1f)),
        new Preset(0.018f, 0.025f, 0.52f, 0.20f, 0.20f, 0.72f, 0.000f, 0.000f, 26.9f, new Vector2( 0.00003f,  0.00001f), new Color(0.74f, 0.84f, 1.00f, 1f)),
        new Preset(0.024f, 0.018f, 0.46f, 0.14f, 0.20f, 1.45f, 0.000f, 0.000f, 38.4f, new Vector2( 0.00002f, -0.00001f), new Color(0.76f, 0.86f, 1.00f, 1f)),
        new Preset(0.034f, 0.013f, 0.38f, 0.08f, 0.20f, 2.30f, 0.000f, 0.000f, 50.8f, new Vector2( 0.00002f, -0.00001f), new Color(0.82f, 0.90f, 1.00f, 1f)),

        // R4: still black, but test whether vanishingly faint structure adds depth.
        new Preset(0.015f, 0.019f, 0.50f, 0.16f, 0.10f, 0.95f, 0.000f, 0.000f, 63.1f, new Vector2( 0.00002f,  0.00000f), new Color(0.95f, 0.97f, 1.00f, 1f)),
        new Preset(0.016f, 0.020f, 0.48f, 0.14f, 0.14f, 1.00f, 0.008f, 0.000f, 75.5f, new Vector2( 0.00002f,  0.00001f), new Color(0.68f, 0.79f, 0.98f, 1f)),
        new Preset(0.012f, 0.024f, 0.50f, 0.22f, 0.20f, 0.82f, 0.018f, 0.000f, 87.9f, new Vector2(-0.00001f,  0.00002f), new Color(0.56f, 0.70f, 0.91f, 1f)),
        new Preset(0.009f, 0.016f, 0.42f, 0.72f, 0.07f, 1.18f, 0.003f, 0.000f, 99.2f, new Vector2( 0.00001f, -0.00001f), new Color(0.78f, 0.88f, 1.00f, 1f)),
    };

    private static GalleryState state;
    private static bool warnedMissingMaterial;

    public static void Tick(GameShip owner)
    {
        if (!Enabled || owner == null)
        {
            Hide();
            return;
        }

        if (state == null || state.Root == null ||
            !object.ReferenceEquals(state.Owner, owner))
        {
            Rebuild(owner);
        }

        if (state == null || state.Root == null)
            return;

        state.Root.transform.position = owner.transform.position;
        state.Root.transform.rotation = Quaternion.identity;
    }

    public static void Hide()
    {
        GalleryState old = state;
        state = null;
        if (old == null)
            return;

        if (old.Root != null)
            Object.Destroy(old.Root);
        old.Root = null;

        for (int i = 0; i < old.Materials.Count; i++)
        {
            if (old.Materials[i] != null)
                Object.Destroy(old.Materials[i]);
        }
        old.Materials.Clear();

        if (old.Quad != null)
            Object.Destroy(old.Quad);
        old.Quad = null;
        old.Owner = null;
    }

    private static void Rebuild(GameShip owner)
    {
        Hide();
        if (owner == null)
            return;

        Material template = ModContent.Load<Material>(MaterialAssetName);
        bool destroyTemplate = false;
        if (template == null)
        {
            Shader shader = Shader.Find("Leviathan/Orrery Void Gallery");
            if (shader != null)
            {
                template = new Material(shader);
                destroyTemplate = true;
            }
        }

        if (template == null)
        {
            if (!warnedMissingMaterial)
            {
                warnedMissingMaterial = true;
                Debug.LogWarning(
                    "[Orrery] Void gallery material was not found. " +
                    "Build/package the leviathanvoidgallery.bundle asset.");
            }
            return;
        }

        GalleryState next = new GalleryState();
        next.Owner = owner;
        next.Root = new GameObject("Orrery Void Material Gallery");
        next.Root.transform.position = owner.transform.position;
        next.Root.transform.rotation = Quaternion.identity;
        next.Quad = BuildQuad();

        SpriteRenderer sortingSource = GetSortingSource();
        int sortingLayerId = sortingSource != null ? sortingSource.sortingLayerID : 0;
        int sortingOrder = sortingSource != null ? sortingSource.sortingOrder - 4 : -1000;

        for (int row = 0; row < GridSize; row++)
        {
            for (int column = 0; column < GridSize; column++)
            {
                int sampleIndex = row * GridSize + column;
                Preset preset = Presets[sampleIndex];

                Material material = new Material(template);
                material.name = "Void Gallery R" + (row + 1) + "C" + (column + 1);
                ApplyPreset(material, preset, column, GridSize - 1 - row);
                next.Materials.Add(material);

                GameObject cell = new GameObject(
                    "Void Gallery R" + (row + 1) + "C" + (column + 1));
                cell.transform.SetParent(next.Root.transform, false);
                cell.transform.localPosition = new Vector3(
                    (column - 1.5f) * CellSizeWorld,
                    (1.5f - row) * CellSizeWorld,
                    0f);
                cell.transform.localRotation = Quaternion.identity;
                cell.transform.localScale = new Vector3(
                    CellSizeWorld,
                    CellSizeWorld,
                    1f);

                MeshFilter filter = cell.AddComponent<MeshFilter>();
                filter.sharedMesh = next.Quad;

                MeshRenderer renderer = cell.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.sortingLayerID = sortingLayerId;
                renderer.sortingOrder = sortingOrder;
            }
        }

        if (destroyTemplate)
            Object.Destroy(template);

        state = next;
    }

    private static void ApplyPreset(
        Material material,
        Preset preset,
        int cellX,
        int cellY)
    {
        if (material == null)
            return;

        material.SetFloat("_Density", preset.Density);
        material.SetFloat("_StarSize", preset.StarSize);
        material.SetFloat("_Brightness", preset.Brightness);
        material.SetFloat("_RareStars", preset.RareStars);
        material.SetFloat("_WorldLock", preset.WorldLock);
        material.SetFloat("_SpatialScale", preset.SpatialScale);
        material.SetFloat("_NebulaStrength", preset.NebulaStrength);
        material.SetFloat("_VoidLift", preset.VoidLift);
        material.SetFloat("_Seed", preset.Seed);
        material.SetVector(
            "_Drift",
            new Vector4(preset.Drift.x, preset.Drift.y, 0f, 0f));
        material.SetVector(
            "_CellIndex",
            new Vector4(cellX, cellY, 0f, 0f));
        material.SetColor("_StarTint", preset.Tint);
    }

    private static Mesh BuildQuad()
    {
        Mesh mesh = new Mesh();
        mesh.name = "Orrery Void Gallery Quad";
        mesh.vertices = new Vector3[]
        {
            new Vector3(-0.5f, -0.5f, 0f),
            new Vector3( 0.5f, -0.5f, 0f),
            new Vector3( 0.5f,  0.5f, 0f),
            new Vector3(-0.5f,  0.5f, 0f),
        };
        mesh.uv = new Vector2[]
        {
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(1f, 1f),
            new Vector2(0f, 1f),
        };
        mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
        mesh.RecalculateBounds();
        return mesh;
    }

    private static SpriteRenderer GetSortingSource()
    {
        HaloItemBase itemBase = Resources.Load<HaloItemBase>(ThermalHaloPath);
        if (itemBase == null || itemBase.field == null)
            return null;
        return itemBase.field.GetComponent<SpriteRenderer>();
    }
}
