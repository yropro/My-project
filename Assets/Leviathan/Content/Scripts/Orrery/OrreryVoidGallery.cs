using System.Collections.Generic;
using StarVortex;
using UnityEngine;

/// <summary>Temporary local-only, world-anchored 4x4 void material comparison.</summary>
public static class OrreryVoidGallery
{
    private const string AssetPath = "Assets/Leviathan/Content/Scripts/Orrery/VoidGallery/";
    private const float PanelMeters = 65f;
    private const float GapMeters = 7f;
    private static readonly List<Material> materials = new List<Material>();
    private static readonly List<Mesh> meshes = new List<Mesh>();
    private static GameObject root;
    private static GameShip owner;
    private static Camera view;
    private static Vector3 cameraOrigin;
    private static float started;
    private static bool warned;
    private static bool missingAssets;

    // Row-major, top left to bottom right. 0 = DinV, 1 = Starfield, 2 = hybrid.
    // Source, brightness, relative star motion, central emptiness.
    private static readonly Vector4[] recipes = {
        new Vector4(0, 0.28f, 0.004f, 0), new Vector4(0, 0.40f, 0.012f, 0),
        new Vector4(0, 0.34f, 0.025f, 0.55f), new Vector4(0, 0.48f, 0.008f, 0.85f),
        new Vector4(0, 0.30f, 0.018f, 0.70f), new Vector4(1, 0.48f, 0.005f, 0),
        new Vector4(1, 0.62f, 0.016f, 0.25f), new Vector4(1, 0.52f, 0.035f, 0.75f),
        new Vector4(1, 0.68f, 0.010f, 0.90f), new Vector4(1, 0.42f, 0.023f, 0.50f),
        new Vector4(2, 0.42f, 0.006f, 0), new Vector4(2, 0.52f, 0.014f, 0.35f),
        new Vector4(2, 0.48f, 0.026f, 0.72f), new Vector4(2, 0.60f, 0.009f, 1),
        new Vector4(2, 0.36f, 0.018f, 0.85f), new Vector4(2, 0.54f, 0.032f, 0.60f)
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
        if (view == null || !view.isActiveAndEnabled)
            view = Camera.main;
        if (view == null)
            return;
        // Actual camera displacement, not ship velocity: survives camera lag and free flight.
        Vector3 delta = view.transform.position - cameraOrigin;
        float width = OrreryUnits.MetersToWorld(PanelMeters);
        Vector4 camera = new Vector4(delta.x / width, delta.y / width, 0, 0);
        float elapsed = Time.time - started;
        foreach (Material material in materials)
        {
            material.SetVector("_CameraTravel", camera);
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
        for (int i = 0; i < recipes.Length; i++)
        {
            Vector4 recipe = recipes[i];
            Vector3 position = new Vector3((i % 4 - 1.5f) * pitch, (1.5f - i / 4) * pitch, 0);
            Material mat = Copy(backdrop, recipe, i);
            mat.SetFloat("_TextureWeight", recipe.x == 1 ? 0 : recipe.x == 2 ? 0.60f : 1);
            mat.SetFloat("_Haze", i == 4 ? 0.025f : i == 14 ? 0.018f : i == 15 ? 0.009f : 0);
            AddRenderer(quad, mat, position, width, -100);
            if (recipe.x == 0)
                continue;
            for (int layer = 0; layer < 2; layer++)
            {
                Gradient gradient = new Gradient();
                gradient.SetKeys(new[] {
                    new GradientColorKey(new Color(0.28f, 0.46f, 0.78f), 0),
                    new GradientColorKey(new Color(0.78f, 0.88f, 1f), 0.80f),
                    new GradientColorKey(new Color(0.94f, 0.97f, 1f), 1)
                }, new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(1, 1) });
                int detail = layer == 0 ? 10 + i % 3 * 3 : 4 + i % 2;
                Mesh stars = SS_Starfield2DMesh.GenerateMesh(2701 + i * 71 + layer * 907,
                    detail, detail, gradient, 0.35f, false, SS_Starfield2D.Style.Normal);
                // Vertex shader wraps stars into the panel; CPU bounds must cover that area.
                stars.bounds = new Bounds(Vector3.zero, new Vector3(11, 11, 1));
                meshes.Add(stars);
                Material starMat = Copy(points, recipe, i);
                starMat.SetFloat("_Layer", layer);
                starMat.SetFloat("_MaxSize", layer == 0 ? 0.10f : 0.07f);
                starMat.SetFloat("_MinSize", layer == 0 ? 0.016f : 0.012f);
                AddRenderer(stars, starMat, position, width / 10f, -99 + layer);
            }
        }
    }

    private static Material LoadMaterial(string file)
    {
#if UNITY_EDITOR
        return UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(AssetPath + file);
#else
        return ModContent.Load<Material>(AssetPath + file);
#endif
    }

    private static Material Copy(Material source, Vector4 recipe, int index)
    {
        Material material = new Material(source);
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
        renderer.sortingOrder = order;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
    }

    public static void Hide()
    {
        if (root != null) Object.Destroy(root);
        foreach (Material material in materials) if (material != null) Object.Destroy(material);
        foreach (Mesh mesh in meshes) if (mesh != null) Object.Destroy(mesh);
        materials.Clear();
        meshes.Clear();
        root = null;
        owner = null;
        view = null;
        missingAssets = false;
    }
}
