using System.Collections.Generic;
using StarVortex;
using UnityEngine;

/// <summary>Reusable R2C2 starfield, optionally clipped to a circular sector.</summary>
[DefaultExecutionOrder(10010)]
public sealed class VoidStarfieldSurface : MonoBehaviour
{
    private const string Assets = "Assets/Leviathan/Content/Scripts/Orrery/VoidGallery/";
    public const float Brightness = 0.65f;
    public const float Motion = 0.003f;
    public const float CentralEmptiness = 0.10f;
    private readonly List<Material> materials = new List<Material>();
    private readonly List<Mesh> meshes = new List<Mesh>();
    private readonly List<MeshRenderer> renderers = new List<MeshRenderer>();
    private Camera view;
    private Vector3 cameraOrigin, surfaceOrigin;
    private float width, started;
    private float motionMultiplier = 1f;
    private static bool warned;

    // Radius is in Unity world units. Angles follow Orrery's world XY convention.
    // Use 360 degrees for a disc; future spells can attach one to their own root.
    public static VoidStarfieldSurface CreateSector(Transform parent, float radius,
        float startDegrees, float arcDegrees, float motionMultiplier = 1f)
    {
        if (parent == null || radius <= 0 || arcDegrees <= 0) return null;
        Material backdrop = ModContent.Load<Material>(Assets + "VoidTexture.mat");
        Material points = ModContent.Load<Material>(Assets + "VoidStarfield.mat");
        if (backdrop == null || points == null)
        {
            if (!warned) Debug.LogWarning("[Orrery] R2C2 starfield bundle missing; using the native ice wedge.");
            warned = true;
            return null;
        }
        GameObject go = new GameObject("R2C2 Void Starfield");
        go.transform.SetParent(parent, false);
        VoidStarfieldSurface surface = go.AddComponent<VoidStarfieldSurface>();
        surface.motionMultiplier = Mathf.Max(0f, motionMultiplier);
        surface.width = radius * 2;
        surface.view = Camera.main;
        surface.surfaceOrigin = go.transform.position;
        surface.cameraOrigin = surface.view != null ? surface.view.transform.position : surface.surfaceOrigin;
        surface.started = Time.time;
        Vector4 sector = new Vector4(startDegrees * Mathf.Deg2Rad,
            Mathf.Min(360, arcDegrees) * Mathf.Deg2Rad, 0.5f, 1);
        Mesh quad = new Mesh { name = "Starfield surface" };
        quad.vertices = new[] { new Vector3(-0.5f,-0.5f), new Vector3(-0.5f,0.5f),
            new Vector3(0.5f,0.5f), new Vector3(0.5f,-0.5f) };
        quad.uv = new[] { Vector2.zero, Vector2.up, Vector2.one, Vector2.right };
        quad.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        quad.RecalculateBounds();
        Material black = surface.Material(backdrop, sector);
        black.SetFloat("_TextureWeight", 0);
        black.SetFloat("_Haze", 0.025f); // R2C2's faint DinV blue nebula.
        surface.Add(quad, black, surface.width);
        Gradient gradient = new Gradient();
        gradient.SetKeys(new[] {
            new GradientColorKey(new Color(0.28f,0.46f,0.78f),0),
            new GradientColorKey(new Color(0.78f,0.88f,1),0.8f),
            new GradientColorKey(new Color(0.94f,0.97f,1),1)
        }, new[] { new GradientAlphaKey(1,0), new GradientAlphaKey(1,1) });
        for (int layer = 0; layer < 3; layer++)
        {
            int detail = layer == 0 ? 12 : layer == 1 ? 5 : 2;
            Mesh stars = SS_Starfield2DMesh.GenerateMesh(2985 + layer * 907,
                detail, detail, gradient, 0.35f, false, SS_Starfield2D.Style.Normal);
            stars.bounds = new Bounds(Vector3.zero, new Vector3(11,11,1));
            Material mat = surface.Material(points, sector);
            mat.SetFloat("_Layer", layer);
            mat.SetFloat("_MaxSize", layer == 0 ? 0.10f : 0.07f);
            mat.SetFloat("_MinSize", layer == 0 ? 0.016f : 0.012f);
            mat.SetVector("_RadiusRange", (layer == 0 ? new Vector4(0.06f,0.12f,0,0)
                : layer == 1 ? new Vector4(0.16f,0.26f,0,0) : new Vector4(0.32f,0.48f,0,0)) * 0.8f);
            mat.SetFloat("_VisibilityFloor", layer == 0 ? 0.20f : 0.30f);
            surface.Add(stars, mat, surface.width / 10);
        }
        surface.LateUpdate();
        return surface;
    }

    private Material Material(Material source, Vector4 sector)
    {
        Material mat = new Material(source);
        mat.renderQueue = 3000;
        mat.SetFloat("_Brightness", Brightness);
        mat.SetFloat("_Motion", Motion * motionMultiplier);
        mat.SetFloat("_Void", CentralEmptiness);
        mat.SetFloat("_Seed", 4 * 0.137f);
        mat.SetVector("_Sector", sector);
        materials.Add(mat);
        return mat;
    }

    private void Add(Mesh mesh, Material material, float scale)
    {
        meshes.Add(mesh);
        GameObject go = new GameObject("Starfield layer");
        go.transform.SetParent(transform, false);
        go.transform.localScale = new Vector3(scale,scale,1);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        VoidStarfieldSorting.Register(renderer, renderers.Count);
        renderers.Add(renderer);
    }

    private void LateUpdate()
    {
        if (width <= 0) return;
        if (view == null || !view.isActiveAndEnabled) view = Camera.main;
        if (view == null) return;
        VoidStarfieldSorting.Tick(view);
        // Convert camera movement to the surface axes for future rotated spell roots.
        Vector3 camera = transform.InverseTransformVector(view.transform.position - cameraOrigin) / width;
        Vector3 travel = transform.InverseTransformVector(transform.position - surfaceOrigin) / width;
        foreach (Material mat in materials)
        {
            mat.SetVector("_CameraTravel", new Vector4(camera.x,camera.y,0,0));
            mat.SetVector("_PanelTravel", new Vector4(travel.x,travel.y,0,0));
            mat.SetFloat("_Elapsed", (Time.time - started) * motionMultiplier);
            if (mat.HasProperty("_WorldUnitsPerPixel"))
                mat.SetFloat("_WorldUnitsPerPixel", 2 * view.orthographicSize / Mathf.Max(1,view.pixelHeight));
        }
    }

    private void OnEnable()
    {
        for (int i = 0; i < renderers.Count; i++) VoidStarfieldSorting.Register(renderers[i],i);
    }
    private void OnDisable()
    {
        foreach (MeshRenderer renderer in renderers) VoidStarfieldSorting.Unregister(renderer);
    }
    private void OnDestroy()
    {
        foreach (MeshRenderer renderer in renderers) VoidStarfieldSorting.Unregister(renderer);
        foreach (Material mat in materials) if (mat != null) Destroy(mat);
        foreach (Mesh mesh in meshes) if (mesh != null) Destroy(mesh);
    }
}
