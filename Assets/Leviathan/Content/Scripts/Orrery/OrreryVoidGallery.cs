using System.Collections.Generic;
using StarVortex;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Temporary local-only, ship-following 4x4 void material comparison.</summary>
public static class OrreryVoidGallery
{
    private const string AssetPath = "Assets/Leviathan/Content/Scripts/Orrery/VoidGallery/";
    private const float PanelMeters = 65f;
    private const float GapMeters = 1f;
    private static readonly List<Material> materials = new List<Material>();
    private static readonly List<Mesh> meshes = new List<Mesh>();
    private static readonly List<MeshRenderer> renderers = new List<MeshRenderer>();
    private static readonly List<int> rendererOffsets = new List<int>();
    private static float nextSortingRefresh;
    private static GameObject root;
    private static GameShip owner;
    private static Camera view;
    private static Vector3 cameraOrigin;
    private static Vector3 shipOrigin;
    private static int sortingLayer;
    private static int sortingOrder;
    private static int renderQueue;
    private static readonly Dictionary<Renderer, Vector2Int> ambientOrders = new Dictionary<Renderer, Vector2Int>();
    private static readonly Dictionary<SortingGroup, Vector2Int> ambientGroups = new Dictionary<SortingGroup, Vector2Int>();
    private static float started;
    private static bool warned;
    private static bool missingAssets;

    // The four selected C1 recipes, top to bottom. Keep these exact as controls.
    // Source (1 = Starfield, 2 = hybrid), brightness, motion, central emptiness.
    // Columns: original / more small stars / larger mid-near stars / more parallax.
    private static readonly Vector4[] recipes = {
        new Vector4(1, 0.70f, 0.004f, 0),
        new Vector4(1, 0.65f, 0.003f, 0.10f),
        new Vector4(2, 0.72f, 0.004f, 0.10f),
        new Vector4(2, 0.90f, 0.006f, 0.25f)
    };

    public static void Tick(GameShip currentOwner)
    {
        if (currentOwner == null || !OrreryRuntime.IsActive(currentOwner))
        {
            Hide();
            return;
        }
        if (owner != currentOwner || (root == null && !missingAssets))
        {
            Hide();
            owner = currentOwner;
            Create(currentOwner);
        }
        if (root == null)
            return;
        root.transform.position = currentOwner.transform.position;
        root.transform.rotation = Quaternion.identity;
        if (view == null || !view.isActiveAndEnabled)
            view = Camera.main;
        if (view == null)
            return;
        if (Time.unscaledTime >= nextSortingRefresh)
        {
            RefreshAmbientSorting();
            nextSortingRefresh = Time.unscaledTime + 1f;
        }
        // Actual camera displacement, not ship velocity: survives camera lag and free flight.
        Vector3 delta = view.transform.position - cameraOrigin;
        float width = OrreryUnits.MetersToWorld(PanelMeters);
        Vector4 camera = new Vector4(delta.x / width, delta.y / width, 0, 0);
        Vector3 shipDelta = currentOwner.transform.position - shipOrigin;
        Vector4 panelTravel = new Vector4(shipDelta.x / width, shipDelta.y / width, 0, 0);
        float elapsed = Time.time - started;
        foreach (Material material in materials)
        {
            material.SetVector("_CameraTravel", camera);
            material.SetVector("_PanelTravel", panelTravel);
            if (material.HasProperty("_WorldUnitsPerPixel"))
                material.SetFloat("_WorldUnitsPerPixel", 2f * view.orthographicSize / Mathf.Max(1, view.pixelHeight));
            material.SetFloat("_Elapsed", elapsed);
        }
    }

    private static void Create(GameShip ship)
    {
        Material backdrop = LoadMaterial("VoidTexture.mat");
        Material points = LoadMaterial("VoidStarfield.mat");
        if (backdrop == null || points == null)
        {
            missingAssets = true;
            if (!warned)
            {
                Debug.LogWarning("[Orrery] Void gallery assets missing. Build AssetBundles and Package Mod (leviathanvoidgallery.bundle).");
                warned = true;
            }
            return;
        }
        view = Camera.main;
        cameraOrigin = view != null ? view.transform.position : ship.transform.position;
        shipOrigin = ship.transform.position;
        RefreshAmbientSorting();
        nextSortingRefresh = Time.unscaledTime + 1f;
        started = Time.time;
        root = new GameObject("Orrery Void Gallery 4x4");
        root.transform.position = ship.transform.position;
        float width = OrreryUnits.MetersToWorld(PanelMeters);
        float pitch = OrreryUnits.MetersToWorld(PanelMeters + GapMeters);
        Mesh quad = new Mesh { name = "Void panel quad" };
        quad.vertices = new[] { new Vector3(-0.5f,-0.5f), new Vector3(-0.5f,0.5f),
            new Vector3(0.5f,0.5f), new Vector3(0.5f,-0.5f) };
        quad.uv = new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right };
        quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        quad.RecalculateBounds();
        meshes.Add(quad);
        for (int i = 0; i < recipes.Length * 4; i++)
        {
            int column = i % 4;
            int baselineIndex = i / 4 * 4;
            Vector4 recipe = recipes[i / 4];
            if (column == 3) recipe.z *= 1.8f;
            Vector3 position = new Vector3((i % 4 - 1.5f) * pitch, (1.5f - i / 4) * pitch, 0);
            Material mat = Copy(backdrop, recipe, baselineIndex);
            mat.SetFloat("_TextureWeight", recipe.x == 1 ? 0 : recipe.x == 2 ? 0.60f : 1);
            mat.SetFloat("_Haze", baselineIndex == 4 ? 0.025f : 0);
            AddRenderer(quad, mat, position, width, 0);
            if (recipe.x == 0)
                continue;
            for (int layer = 0; layer < 3; layer++)
            {
                Gradient gradient = new Gradient();
                gradient.SetKeys(new[] {
                    new GradientColorKey(new Color(0.28f, 0.46f, 0.78f), 0),
                    new GradientColorKey(new Color(0.78f, 0.88f, 1f), 0.80f),
                    new GradientColorKey(new Color(0.94f, 0.97f, 1f), 1)
                }, new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(1, 1) });
                int detail = layer == 0 ? (column == 1 ? 12 : 9)
                    : layer == 1 ? 4 + baselineIndex % 3 : 2;
                Mesh stars = SS_Starfield2DMesh.GenerateMesh(2701 + baselineIndex * 71 + layer * 907,
                    detail, detail, gradient, 0.35f, false, SS_Starfield2D.Style.Normal);
                // Vertex shader wraps stars into the panel; CPU bounds must cover that area.
                stars.bounds = new Bounds(Vector3.zero, new Vector3(11, 11, 1));
                meshes.Add(stars);
                Material starMat = Copy(points, recipe, baselineIndex);
                starMat.SetFloat("_Layer", layer);
                starMat.SetFloat("_MaxSize", layer == 0 ? 0.10f : 0.07f);
                starMat.SetFloat("_MinSize", layer == 0 ? 0.016f : 0.012f);
                {
                    // Radius independent of mesh density; progressively larger stars
                    // move at 1x / 4x / 7x the far layer's parallax rate.
                    float scale = 0.8f * (column == 2 && layer > 0 ? 1.35f : 1f);
                    starMat.SetVector("_RadiusRange", layer == 0
                        ? new Vector4(0.06f, 0.12f, 0, 0) * scale
                        : layer == 1 ? new Vector4(0.16f, 0.26f, 0, 0) * scale
                        : new Vector4(0.32f, 0.48f, 0, 0) * scale);
                    starMat.SetFloat("_VisibilityFloor", layer == 0 ? 0.20f : 0.30f);
                }
                AddRenderer(stars, starMat, position, width / 10f, 1 + layer);
            }
        }
    }

    private static Material LoadMaterial(string file)
    {
        // Package Mod ships the editor-compiled DLL, so UNITY_EDITOR does not
        // mean this code will run inside Unity. Always use the game's bundle loader.
        return ModContent.Load<Material>(AssetPath + file);
    }

    private static void RefreshAmbientSorting()
    {
        sortingLayer = 0;
        sortingOrder = -100;
        renderQueue = 3000;
        bool foundShip = false;
        foreach (GameShip ship in Object.FindObjectsOfType<GameShip>())
        {
            // Ships are grouped: their child sprite orders are local to the group.
            SortingGroup group = ship.GetComponent<SortingGroup>();
            if (group == null) continue;
            int value = SortingLayer.GetLayerValueFromID(group.sortingLayerID);
            int current = SortingLayer.GetLayerValueFromID(sortingLayer);
            if (!foundShip || value < current || (value == current && group.sortingOrder - 4 < sortingOrder))
            {
                sortingLayer = group.sortingLayerID;
                sortingOrder = Mathf.Max(-32767, group.sortingOrder - 4);
                foundShip = true;
            }
        }
        // Keep the entire gallery below the lowest ship group. Move only ambient
        // presentation below that, restoring its original order when testing ends.
        // Rescan because node transitions can replace background objects.
        foreach (Dust effect in Object.FindObjectsOfType<Dust>(true)) ConsiderAmbient(effect);
        foreach (Clouds effect in Object.FindObjectsOfType<Clouds>(true)) ConsiderAmbient(effect);
        foreach (BackgroundControl effect in Object.FindObjectsOfType<BackgroundControl>(true)) ConsiderAmbient(effect);
        for (int i = 0; i < renderers.Count; i++)
        {
            renderers[i].sortingLayerID = sortingLayer;
            renderers[i].sortingOrder = sortingOrder + rendererOffsets[i];
        }
        foreach (Material material in materials) material.renderQueue = renderQueue;
    }

    private static void ConsiderAmbient(Component effect)
    {
        foreach (Renderer ambient in effect.GetComponentsInChildren<Renderer>(true))
        {
            if (view != null && (view.cullingMask & (1 << ambient.gameObject.layer)) == 0) continue;
            if (!ambientOrders.ContainsKey(ambient))
                ambientOrders.Add(ambient, new Vector2Int(ambient.sortingLayerID, ambient.sortingOrder));
            ambient.sortingLayerID = sortingLayer;
            ambient.sortingOrder = sortingOrder - 1;
            foreach (SortingGroup group in ambient.GetComponentsInParent<SortingGroup>(true))
            {
                if (!ambientGroups.ContainsKey(group))
                    ambientGroups.Add(group, new Vector2Int(group.sortingLayerID, group.sortingOrder));
                group.sortingLayerID = sortingLayer;
                group.sortingOrder = sortingOrder - 1;
            }
        }
    }

    private static Material Copy(Material source, Vector4 recipe, int index)
    {
        Material material = new Material(source);
        material.renderQueue = renderQueue;
        material.SetFloat("_Brightness", recipe.y);
        material.SetFloat("_Motion", recipe.z);
        material.SetFloat("_Void", recipe.w);
        material.SetFloat("_Seed", index * 0.137f);
        materials.Add(material);
        return material;
    }

    private static void AddRenderer(Mesh mesh, Material material, Vector3 position, float scale, int order)
    {
        GameObject panel = new GameObject("Void sample");
        panel.transform.SetParent(root.transform, false);
        panel.transform.localPosition = position;
        panel.transform.localScale = new Vector3(scale, scale, 1);
        panel.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer renderer = panel.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.sortingLayerID = sortingLayer;
        renderer.sortingOrder = sortingOrder + order;
        renderers.Add(renderer);
        rendererOffsets.Add(order);
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
    }

    public static void Hide()
    {
        foreach (var pair in ambientOrders)
            if (pair.Key != null) { pair.Key.sortingLayerID = pair.Value.x; pair.Key.sortingOrder = pair.Value.y; }
        foreach (var pair in ambientGroups)
            if (pair.Key != null) { pair.Key.sortingLayerID = pair.Value.x; pair.Key.sortingOrder = pair.Value.y; }
        ambientOrders.Clear();
        ambientGroups.Clear();
        if (root != null) Object.Destroy(root);
        foreach (Material material in materials) if (material != null) Object.Destroy(material);
        foreach (Mesh mesh in meshes) if (mesh != null) Object.Destroy(mesh);
        materials.Clear();
        meshes.Clear();
        renderers.Clear();
        rendererOffsets.Clear();
        root = null;
        owner = null;
        view = null;
        missingAssets = false;
    }
}
