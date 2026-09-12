using StarVortex;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Local gameplay-space presentation for Orrery elemental sectors.
///
/// Each sector is a procedural wedge that samples the real native Halo field art
/// for its element. Geometry performs the angular cut; native Halo textures and
/// materials provide the actual visual language. This is presentation only and
/// never participates in element resolution or collision.
/// </summary>
public static class OrrerySectorPresentation
{
    public static class Tuning
    {
        public const float OuterPaddingMeters = 8f;
        public const float SectorGapDegrees = 0f;
        public const int ArcSegmentsPer120Degrees = 28;

        // Two copies of each elemental Halo slice give a readable field without
        // making the wheel opaque enough to hide combat underneath it.
        public const float OuterLayerRadiusMultiplier = 1.00f;
        public const float OuterLayerOpacity = 0.32f;
        public const float InnerLayerRadiusMultiplier = 0.84f;
        public const float InnerLayerOpacity = 0.18f;
    }

    private const string ThermalHaloPath = "Base/Items/AutoSpecial/Thermal Halo";
    private const string ColdHaloPath = "Base/Items/AutoSpecial/Cold Halo";
    private const string ElectricHaloPath = "Base/Items/AutoSpecial/Electric Halo";

    private static readonly List<Mesh> meshes = new List<Mesh>(8);
    private static readonly List<Material> materials = new List<Material>(8);

    private static GameShip owner;
    private static GameObject root;

    public static void Show(GameShip newOwner)
    {
        if (newOwner == null || !OrreryRuntime.IsActive(newOwner))
        {
            Hide();
            return;
        }

        if (object.ReferenceEquals(owner, newOwner) && root != null)
            return;

        Hide();
        owner = newOwner;

        OrreryRuntime.ResolvedState resolved =
            OrreryRuntime.GetResolvedState(newOwner);
        if (resolved == null || !resolved.Active || resolved.Sectors == null)
        {
            owner = null;
            return;
        }

        root = new GameObject("Orrery Element Sectors");
        root.transform.position = newOwner.transform.position;
        root.transform.rotation = Quaternion.identity;

        float outerRadiusMeters = resolved.BaseOrbitRadiusMeters +
            resolved.OrbitLaneSpacingMeters * Mathf.Max(0, resolved.SatelliteCount - 1) +
            Tuning.OuterPaddingMeters;
        float outerRadiusWorld = OrreryUnits.MetersToWorld(outerRadiusMeters);

        for (int i = 0; i < resolved.Sectors.Count; i++)
        {
            OrreryRuntime.Sector sector = resolved.Sectors.Get(i);
            if (sector.Element == OrreryElement.None || sector.ArcDegrees <= 0f)
                continue;

            SpriteRenderer haloSource = GetHaloFieldRenderer(sector.Element);
            if (haloSource == null || haloSource.sprite == null)
                continue;

            CreateLayer(
                sector,
                haloSource,
                outerRadiusWorld * Tuning.OuterLayerRadiusMultiplier,
                Tuning.OuterLayerOpacity,
                0);
            CreateLayer(
                sector,
                haloSource,
                outerRadiusWorld * Tuning.InnerLayerRadiusMultiplier,
                Tuning.InnerLayerOpacity,
                1);
        }
    }

    public static void Tick(GameShip currentOwner)
    {
        if (currentOwner == null || !OrreryRuntime.IsActive(currentOwner))
        {
            Hide();
            return;
        }

        if (!object.ReferenceEquals(owner, currentOwner) || root == null)
            Show(currentOwner);

        if (root == null)
            return;

        Vector3 position = currentOwner.transform.position;
        root.transform.position = position;
        // Baseline wheel is world-fixed. Do not inherit ship rotation.
        root.transform.rotation = Quaternion.identity;
    }

    public static void Hide(GameShip expectedOwner)
    {
        if (expectedOwner != null && owner != null &&
            !object.ReferenceEquals(expectedOwner, owner))
        {
            return;
        }
        Hide();
    }

    public static void Hide()
    {
        if (root != null)
            UnityEngine.Object.Destroy(root);
        root = null;
        owner = null;

        for (int i = 0; i < meshes.Count; i++)
        {
            if (meshes[i] != null)
                UnityEngine.Object.Destroy(meshes[i]);
        }
        meshes.Clear();

        for (int i = 0; i < materials.Count; i++)
        {
            if (materials[i] != null)
                UnityEngine.Object.Destroy(materials[i]);
        }
        materials.Clear();
    }

    private static void CreateLayer(
        OrreryRuntime.Sector sector,
        SpriteRenderer source,
        float radiusWorld,
        float opacityMultiplier,
        int layerIndex)
    {
        if (root == null || source == null || source.sprite == null || radiusWorld <= 0f)
            return;

        float gap = sector.ArcDegrees >= 359.9f
            ? 0f
            : Mathf.Min(Tuning.SectorGapDegrees, sector.ArcDegrees * 0.20f);
        float start = sector.StartDegrees + gap * 0.5f;
        float arc = Mathf.Max(0.1f, sector.ArcDegrees - gap);
        int segments = Mathf.Max(
            3,
            Mathf.CeilToInt(
                Tuning.ArcSegmentsPer120Degrees * arc / 120f));

        Mesh mesh = BuildWedgeMesh(source.sprite, start, arc, segments);
        if (mesh == null)
            return;
        meshes.Add(mesh);

        Material material = CreateHaloMaterial(source, opacityMultiplier);
        if (material == null)
        {
            UnityEngine.Object.Destroy(mesh);
            meshes.Remove(mesh);
            return;
        }
        materials.Add(material);

        GameObject wedge = new GameObject(
            "Orrery " + sector.Element + " Sector " + layerIndex);
        wedge.transform.SetParent(root.transform, false);
        wedge.transform.localPosition = Vector3.zero;
        wedge.transform.localRotation = Quaternion.identity;
        wedge.transform.localScale = new Vector3(radiusWorld, radiusWorld, 1f);

        MeshFilter filter = wedge.AddComponent<MeshFilter>();
        filter.sharedMesh = mesh;
        MeshRenderer renderer = wedge.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.sortingLayerID = source.sortingLayerID;
        renderer.sortingOrder = source.sortingOrder + layerIndex;
    }

    private static Mesh BuildWedgeMesh(
        Sprite sprite,
        float startDegrees,
        float arcDegrees,
        int segments)
    {
        if (sprite == null || sprite.texture == null || segments < 1)
            return null;

        int vertexCount = segments + 2;
        Vector3[] vertices = new Vector3[vertexCount];
        Vector2[] uv = new Vector2[vertexCount];
        int[] triangles = new int[segments * 3];

        Rect textureRect = sprite.textureRect;
        float textureWidth = Mathf.Max(1f, sprite.texture.width);
        float textureHeight = Mathf.Max(1f, sprite.texture.height);
        Vector2 uvCenter = new Vector2(
            textureRect.center.x / textureWidth,
            textureRect.center.y / textureHeight);
        Vector2 uvHalf = new Vector2(
            textureRect.width * 0.5f / textureWidth,
            textureRect.height * 0.5f / textureHeight);

        vertices[0] = Vector3.zero;
        uv[0] = uvCenter;

        for (int i = 0; i <= segments; i++)
        {
            float t = (float)i / segments;
            float angle = (startDegrees + arcDegrees * t) * Mathf.Deg2Rad;
            float x = Mathf.Cos(angle);
            float y = Mathf.Sin(angle);
            int vertex = i + 1;
            vertices[vertex] = new Vector3(x, y, 0f);
            uv[vertex] = new Vector2(
                uvCenter.x + x * uvHalf.x,
                uvCenter.y + y * uvHalf.y);

            if (i < segments)
            {
                int triangle = i * 3;
                triangles[triangle] = 0;
                triangles[triangle + 1] = vertex;
                triangles[triangle + 2] = vertex + 1;
            }
        }

        Mesh mesh = new Mesh();
        mesh.name = "Orrery Halo Sector";
        mesh.vertices = vertices;
        mesh.uv = uv;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();
        return mesh;
    }

    private static Material CreateHaloMaterial(
        SpriteRenderer source,
        float opacityMultiplier)
    {
        if (source == null || source.sprite == null)
            return null;

        Material material;
        if (source.sharedMaterial != null)
        {
            material = new Material(source.sharedMaterial);
        }
        else
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
                return null;
            material = new Material(shader);
        }

        material.name = "Orrery Halo Sector Material";
        Texture texture = source.sprite.texture;
        if (material.HasProperty("_MainTex"))
            material.SetTexture("_MainTex", texture);

        Color color = source.color;
        // Sector readability owns final renderer opacity. Multiplying by a Halo
        // prefab's already-low alpha can make the wheel disappear entirely.
        color.a = Mathf.Clamp01(opacityMultiplier);
        if (material.HasProperty("_Color"))
            material.SetColor("_Color", color);
        if (material.HasProperty("_RendererColor"))
            material.SetColor("_RendererColor", color);

        return material;
    }

    private static SpriteRenderer GetHaloFieldRenderer(OrreryElement element)
    {
        string path = GetHaloPath(element);
        if (string.IsNullOrEmpty(path))
            return null;

        HaloItemBase itemBase = Resources.Load<HaloItemBase>(path);
        if (itemBase == null || itemBase.field == null)
        {
            Debug.LogWarning(
                "[Orrery] Could not load native Halo field for " + element + ".");
            return null;
        }

        SpriteRenderer renderer = itemBase.field.GetComponent<SpriteRenderer>();
        if (renderer == null || renderer.sprite == null)
        {
            Debug.LogWarning(
                "[Orrery] Native Halo field for " + element +
                " has no root SpriteRenderer.");
            return null;
        }
        return renderer;
    }

    private static string GetHaloPath(OrreryElement element)
    {
        if (element == OrreryElement.Fire)
            return ThermalHaloPath;
        if (element == OrreryElement.Ice)
            return ColdHaloPath;
        if (element == OrreryElement.Lightning)
            return ElectricHaloPath;
        return null;
    }
}
