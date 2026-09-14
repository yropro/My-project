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
/// Grid reference is R1C1 at top-left through R6C6 at bottom-right. Nothing is
/// labelled in-world so the samples can be judged without UI contaminating them.
/// </summary>
public static class OrreryVoidGalleryPresentation
{
    // Temporary kill switch. Non-const so production sector code remains reachable
    // to the compiler while the gallery is enabled for this visual test.
    public static bool Enabled = true;

    public const int GridSize = 6;
    public const float CellSizeMeters = 65f;

    private const float CellSizeWorld = CellSizeMeters / 20f;
    private const string MaterialAssetName = "OrreryVoidGalleryBase";
    private const string ThermalHaloPath = "Base/Items/AutoSpecial/Thermal Halo";

    private sealed class GalleryState
    {
        public GameShip Owner;
        public GameObject Root;
        public Mesh Quad;
        public readonly List<Material> Materials =
            new List<Material>(GridSize * GridSize);
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

    // Six aesthetic families, six variants each. The intent is not a sterile
    // parameter matrix; every row explores a different way of selling isolation,
    // distance, scale, and almost-total blackness.
    private static readonly Preset[] Presets = new Preset[]
    {
        // R1: extreme emptiness. Left is almost nothing; right adds only tiny depth cues.
        new Preset(0.002f, 0.014f, 0.30f, 0.06f, 0.03f, 0.55f, 0.000f, 0.000f,  7.1f, new Vector2( 0.00001f,  0.00000f), new Color(0.86f, 0.91f, 1.00f, 1f)),
        new Preset(0.004f, 0.015f, 0.34f, 0.08f, 0.04f, 0.72f, 0.000f, 0.000f, 13.4f, new Vector2(-0.00001f,  0.00001f), new Color(0.78f, 0.86f, 1.00f, 1f)),
        new Preset(0.006f, 0.016f, 0.38f, 0.10f, 0.05f, 0.92f, 0.000f, 0.000f, 19.8f, new Vector2( 0.00002f,  0.00000f), new Color(0.74f, 0.84f, 1.00f, 1f)),
        new Preset(0.009f, 0.017f, 0.42f, 0.11f, 0.06f, 1.18f, 0.000f, 0.000f, 26.1f, new Vector2( 0.00001f, -0.00001f), new Color(0.70f, 0.81f, 0.98f, 1f)),
        new Preset(0.013f, 0.015f, 0.40f, 0.09f, 0.08f, 1.58f, 0.000f, 0.000f, 32.5f, new Vector2(-0.00001f,  0.00001f), new Color(0.76f, 0.85f, 1.00f, 1f)),
        new Preset(0.018f, 0.012f, 0.34f, 0.06f, 0.10f, 2.10f, 0.000f, 0.000f, 38.8f, new Vector2( 0.00001f, -0.00001f), new Color(0.82f, 0.89f, 1.00f, 1f)),

        // R2: portal/parallax anchoring. Same population, progressively less attached to the panel.
        new Preset(0.012f, 0.019f, 0.46f, 0.14f, 0.00f, 1.00f, 0.000f, 0.000f, 45.2f, new Vector2( 0.00004f,  0.00001f), new Color(0.73f, 0.83f, 1.00f, 1f)),
        new Preset(0.012f, 0.019f, 0.46f, 0.14f, 0.15f, 1.00f, 0.000f, 0.000f, 51.5f, new Vector2( 0.000035f, -0.00001f), new Color(0.73f, 0.83f, 1.00f, 1f)),
        new Preset(0.012f, 0.019f, 0.46f, 0.14f, 0.32f, 1.00f, 0.000f, 0.000f, 57.9f, new Vector2( 0.00003f,  0.00001f), new Color(0.73f, 0.83f, 1.00f, 1f)),
        new Preset(0.012f, 0.019f, 0.46f, 0.14f, 0.52f, 1.00f, 0.000f, 0.000f, 64.2f, new Vector2(-0.000025f, 0.00001f), new Color(0.73f, 0.83f, 1.00f, 1f)),
        new Preset(0.012f, 0.019f, 0.46f, 0.14f, 0.76f, 1.00f, 0.000f, 0.000f, 70.6f, new Vector2( 0.000015f, 0.00000f), new Color(0.73f, 0.83f, 1.00f, 1f)),
        new Preset(0.012f, 0.019f, 0.46f, 0.14f, 1.00f, 1.00f, 0.000f, 0.000f, 76.9f, new Vector2( 0.00000f,  0.00000f), new Color(0.73f, 0.83f, 1.00f, 1f)),

        // R3: apparent scale. Broad lonely stars -> enormous numbers of microscopic distant points.
        new Preset(0.006f, 0.035f, 0.52f, 0.28f, 0.18f, 0.32f, 0.000f, 0.000f, 83.3f, new Vector2( 0.00002f,  0.00001f), new Color(0.70f, 0.80f, 0.98f, 1f)),
        new Preset(0.008f, 0.030f, 0.50f, 0.24f, 0.18f, 0.48f, 0.000f, 0.000f, 89.6f, new Vector2( 0.00002f,  0.00001f), new Color(0.72f, 0.82f, 0.99f, 1f)),
        new Preset(0.011f, 0.024f, 0.47f, 0.20f, 0.18f, 0.72f, 0.000f, 0.000f, 95.9f, new Vector2( 0.00002f, -0.00001f), new Color(0.74f, 0.84f, 1.00f, 1f)),
        new Preset(0.016f, 0.019f, 0.43f, 0.15f, 0.18f, 1.10f, 0.000f, 0.000f, 12.7f, new Vector2( 0.000015f,-0.00001f), new Color(0.76f, 0.86f, 1.00f, 1f)),
        new Preset(0.023f, 0.014f, 0.38f, 0.10f, 0.18f, 1.72f, 0.000f, 0.000f, 29.4f, new Vector2( 0.00001f, -0.00001f), new Color(0.79f, 0.88f, 1.00f, 1f)),
        new Preset(0.032f, 0.010f, 0.31f, 0.06f, 0.18f, 2.70f, 0.000f, 0.000f, 46.8f, new Vector2( 0.00001f,  0.00000f), new Color(0.84f, 0.91f, 1.00f, 1f)),

        // R4: rare landmarks. Most of the field is absent; occasional stars provide terrifying scale cues.
        new Preset(0.004f, 0.018f, 0.32f, 0.15f, 0.08f, 0.82f, 0.000f, 0.000f, 58.2f, new Vector2( 0.00001f,  0.00000f), new Color(0.82f, 0.89f, 1.00f, 1f)),
        new Preset(0.005f, 0.019f, 0.34f, 0.35f, 0.08f, 0.88f, 0.000f, 0.000f, 69.7f, new Vector2(-0.00001f,  0.00001f), new Color(0.80f, 0.88f, 1.00f, 1f)),
        new Preset(0.006f, 0.020f, 0.36f, 0.60f, 0.09f, 0.94f, 0.000f, 0.000f, 81.1f, new Vector2( 0.00001f, -0.00001f), new Color(0.77f, 0.86f, 1.00f, 1f)),
        new Preset(0.007f, 0.021f, 0.38f, 0.95f, 0.09f, 1.00f, 0.000f, 0.000f, 92.6f, new Vector2( 0.00001f,  0.00001f), new Color(0.74f, 0.84f, 1.00f, 1f)),
        new Preset(0.008f, 0.022f, 0.40f, 1.35f, 0.10f, 1.06f, 0.000f, 0.000f, 24.3f, new Vector2(-0.00001f,  0.00000f), new Color(0.72f, 0.82f, 0.99f, 1f)),
        new Preset(0.009f, 0.023f, 0.42f, 1.85f, 0.10f, 1.12f, 0.000f, 0.000f, 35.7f, new Vector2( 0.00000f, -0.00001f), new Color(0.69f, 0.80f, 0.98f, 1f)),

        // R5: almost-invisible large-scale structure without becoming a colorful nebula.
        new Preset(0.009f, 0.017f, 0.38f, 0.10f, 0.12f, 0.92f, 0.000f, 0.000f, 47.1f, new Vector2( 0.000015f, 0.00000f), new Color(0.78f, 0.86f, 1.00f, 1f)),
        new Preset(0.009f, 0.017f, 0.38f, 0.10f, 0.12f, 0.92f, 0.002f, 0.000f, 52.8f, new Vector2( 0.000015f, 0.00000f), new Color(0.75f, 0.84f, 1.00f, 1f)),
        new Preset(0.009f, 0.017f, 0.38f, 0.10f, 0.12f, 0.92f, 0.005f, 0.000f, 61.4f, new Vector2( 0.000015f, 0.00001f), new Color(0.71f, 0.82f, 0.99f, 1f)),
        new Preset(0.008f, 0.018f, 0.39f, 0.12f, 0.14f, 0.88f, 0.010f, 0.000f, 73.0f, new Vector2(-0.00001f, 0.00001f), new Color(0.66f, 0.78f, 0.97f, 1f)),
        new Preset(0.007f, 0.019f, 0.40f, 0.14f, 0.16f, 0.84f, 0.017f, 0.000f, 84.5f, new Vector2( 0.00001f,-0.00001f), new Color(0.61f, 0.74f, 0.94f, 1f)),
        new Preset(0.006f, 0.020f, 0.41f, 0.18f, 0.18f, 0.80f, 0.026f, 0.000f, 96.0f, new Vector2(-0.00001f,-0.00001f), new Color(0.57f, 0.70f, 0.91f, 1f)),

        // R6: cold populations / motion language. Still restrained; no colorful space wallpaper.
        new Preset(0.010f, 0.016f, 0.40f, 0.10f, 0.06f, 1.30f, 0.000f, 0.000f, 17.6f, new Vector2( 0.00000f,  0.00000f), new Color(0.96f, 0.98f, 1.00f, 1f)),
        new Preset(0.011f, 0.016f, 0.42f, 0.11f, 0.10f, 1.25f, 0.000f, 0.000f, 28.2f, new Vector2( 0.000008f, 0.000003f), new Color(0.83f, 0.90f, 1.00f, 1f)),
        new Preset(0.011f, 0.017f, 0.43f, 0.12f, 0.14f, 1.18f, 0.000f, 0.000f, 39.9f, new Vector2(-0.000012f, 0.000004f), new Color(0.70f, 0.82f, 1.00f, 1f)),
        new Preset(0.010f, 0.018f, 0.42f, 0.14f, 0.20f, 1.08f, 0.003f, 0.000f, 53.7f, new Vector2( 0.000018f,-0.000006f), new Color(0.63f, 0.76f, 0.96f, 1f)),
        new Preset(0.009f, 0.019f, 0.40f, 0.18f, 0.30f, 0.98f, 0.006f, 0.000f, 68.4f, new Vector2(-0.000022f,-0.000008f), new Color(0.58f, 0.71f, 0.92f, 1f)),
        new Preset(0.008f, 0.021f, 0.38f, 0.25f, 0.45f, 0.88f, 0.010f, 0.000f, 82.1f, new Vector2( 0.000028f, 0.000010f), new Color(0.54f, 0.67f, 0.89f, 1f)),
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

        int sortingLayerId;
        int sortingOrder;
        ResolveSorting(out sortingLayerId, out sortingOrder);

        float halfGrid = (GridSize - 1) * 0.5f;

        for (int row = 0; row < GridSize; row++)
        {
            for (int column = 0; column < GridSize; column++)
            {
                int sampleIndex = row * GridSize + column;
                Preset preset = Presets[sampleIndex];

                Material material = new Material(template);
                material.name = "Void Gallery R" + (row + 1) + "C" + (column + 1);
                // Standard transparent queue lets 2D sorting order decide against
                // the native Dust mesh instead of forcing the gallery 50 queue
                // steps earlier than it.
                material.renderQueue = 3000;
                ApplyPreset(material, preset, column, GridSize - 1 - row);
                next.Materials.Add(material);

                GameObject cell = new GameObject(
                    "Void Gallery R" + (row + 1) + "C" + (column + 1));
                cell.transform.SetParent(next.Root.transform, false);
                cell.transform.localPosition = new Vector3(
                    (column - halfGrid) * CellSizeWorld,
                    (halfGrid - row) * CellSizeWorld,
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

    private static void ResolveSorting(
        out int sortingLayerId,
        out int sortingOrder)
    {
        SpriteRenderer sortingSource = GetSortingSource();
        sortingLayerId = sortingSource != null ? sortingSource.sortingLayerID : 0;
        sortingOrder = sortingSource != null
            ? sortingSource.sortingOrder - 4
            : -1000;

        int selectedLayerValue =
            SortingLayer.GetLayerValueFromID(sortingLayerId);

        // Star Vortex's vanilla moving star/mote field is the Dust screen mesh.
        // Place the gallery just above every live Dust renderer if Dust would
        // otherwise sort over our halo-derived baseline. That preserves Dust
        // outside each square while making each square a true black occluder.
        Dust[] dustFields = Object.FindObjectsOfType<Dust>();
        for (int i = 0; i < dustFields.Length; i++)
        {
            Dust dust = dustFields[i];
            if (dust == null)
                continue;

            MeshRenderer dustRenderer = dust.GetComponent<MeshRenderer>();
            if (dustRenderer == null || !dustRenderer.enabled)
                continue;

            int dustLayerValue =
                SortingLayer.GetLayerValueFromID(dustRenderer.sortingLayerID);

            if (dustLayerValue > selectedLayerValue)
            {
                sortingLayerId = dustRenderer.sortingLayerID;
                sortingOrder = dustRenderer.sortingOrder + 1;
                selectedLayerValue = dustLayerValue;
            }
            else if (dustLayerValue == selectedLayerValue &&
                     dustRenderer.sortingOrder >= sortingOrder)
            {
                sortingOrder = dustRenderer.sortingOrder + 1;
            }
        }
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
