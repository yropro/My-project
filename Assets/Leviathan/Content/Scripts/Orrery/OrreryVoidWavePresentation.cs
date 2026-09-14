using StarVortex;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Presentation-only renderer/codec for Void Wave.
///
/// Gameplay is never inferred from this stream. The owner publishes only cast
/// generation, aim and normalized progress in one ordinary Core dynamic slot.
/// Remote clients reconstruct the placeholder annular-sector mesh locally from
/// shared spell tuning. The same mesh path is used for the local caster so visual
/// geometry cannot quietly diverge between local and remote presentation.
/// </summary>
public static class OrreryVoidWavePresentation
{
    private const byte PayloadVersion = 1;

    private sealed class VisualState
    {
        public GameShip Owner;
        public uint Generation;
        public Vector2 Origin;
        public Vector2 Direction;
        public float Progress01;
        public float LastSeenAt;
        public GameObject Root;
        public Mesh Mesh;
        public MeshRenderer Renderer;
        public Material Material;
        public Vector3[] Vertices;
        public int[] Triangles;
    }

    private static readonly Dictionary<GameShip, VisualState> remote =
        new Dictionary<GameShip, VisualState>(4);
    private static VisualState local;
    private static bool initialized;
    private static bool warnedMissingMaterial;

    public static void EnsureInitialized()
    {
        if (initialized)
            return;

        CoreNetwork.RegisterSlot(
            OrrerySpellCompendium.VoidWave.PresentationSlotId,
            CoreClassId.Orrery,
            "Orrery Void Wave");

        CoreNetworkPresentation.Register(
            "Orrery/VoidWave",
            publish: PublishLocal,
            render: RenderRemote,
            forget: Forget,
            reset: Reset,
            update: UpdateLocal);

        initialized = true;
    }

    private static void PublishLocal()
    {
        CoreOwnerContext context = CoreClassRuntime.CurrentContext;
        GameShip owner = context != null && context.IsValid &&
            context.ClassId == CoreClassId.Orrery
                ? context.Ship
                : null;
        if (owner == null)
            return;

        OrreryVoidWave.PresentationSnapshot snapshot;
        if (!OrreryVoidWave.TryGetPresentation(owner, out snapshot) ||
            !snapshot.Active || snapshot.Generation == 0u)
        {
            return;
        }

        CoreNetwork.SlotWriter writer = CoreNetwork.BeginSlot(
            OrrerySpellCompendium.VoidWave.PresentationSlotId);
        if (!writer.Valid)
            return;

        writer.Byte(PayloadVersion);
        WriteUInt(ref writer, snapshot.Generation);
        writer.Byte(PackAngle(snapshot.Direction));
        writer.Percent(snapshot.Progress01);
        CoreNetwork.EndSlot(writer);
    }

    private static void UpdateLocal(float deltaTime)
    {
        CoreOwnerContext context = CoreClassRuntime.CurrentContext;
        GameShip owner = context != null && context.IsValid &&
            context.ClassId == CoreClassId.Orrery
                ? context.Ship
                : null;

        OrreryVoidWave.PresentationSnapshot snapshot;
        if (owner == null ||
            !OrreryVoidWave.TryGetPresentation(owner, out snapshot) ||
            !snapshot.Active)
        {
            DestroyVisual(ref local);
            return;
        }

        if (local == null || local.Owner == null ||
            !object.ReferenceEquals(local.Owner, owner) ||
            local.Generation != snapshot.Generation)
        {
            DestroyVisual(ref local);
            local = CreateVisual(
                owner,
                snapshot.Generation,
                snapshot.Origin,
                snapshot.Direction);
        }

        if (local == null)
            return;

        local.Origin = snapshot.Origin;
        local.Direction = NormalizeOrRight(snapshot.Direction);
        local.Progress01 = Mathf.Clamp01(snapshot.Progress01);
        local.LastSeenAt = Time.unscaledTime;
        UpdateMesh(local);
    }

    private static void RenderRemote(GameShip owner, float deltaTime)
    {
        if (owner == null)
            return;

        CoreNetwork.SlotReader reader;
        if (!CoreNetwork.TryReadSlot(
                owner,
                OrrerySpellCompendium.VoidWave.PresentationSlotId,
                out reader))
        {
            VisualState missing;
            if (remote.TryGetValue(owner, out missing) && missing != null &&
                Time.unscaledTime - missing.LastSeenAt >
                    OrrerySpellCompendium.VoidWave.RemotePresentationGraceSeconds)
            {
                DestroyRemote(owner, missing);
            }
            return;
        }

        if (reader.Byte() != PayloadVersion)
        {
            VisualState invalid;
            if (remote.TryGetValue(owner, out invalid))
                DestroyRemote(owner, invalid);
            return;
        }

        uint generation = ReadUInt(ref reader);
        Vector2 direction = UnpackAngle(reader.Byte());
        float progress = Mathf.Clamp01(reader.Percent());
        if (generation == 0u)
            return;

        VisualState state;
        if (!remote.TryGetValue(owner, out state) || state == null ||
            state.Generation != generation)
        {
            if (state != null)
                DestroyRemote(owner, state);

            state = CreateVisual(
                owner,
                generation,
                owner.transform.position,
                direction);
            if (state == null)
                return;
            remote[owner] = state;
        }

        state.Direction = direction;
        state.Progress01 = progress;
        state.LastSeenAt = Time.unscaledTime;
        UpdateMesh(state);
    }

    private static VisualState CreateVisual(
        GameShip owner,
        uint generation,
        Vector2 origin,
        Vector2 direction)
    {
        Material template = ModContent.Load<Material>(
            OrrerySpellCompendium.VoidWave.VisualMaterialAssetName);
        bool destroyTemplate = false;

        if (template == null)
        {
            Shader fallback = Shader.Find("Sprites/Default");
            if (fallback != null)
            {
                template = new Material(fallback);
                template.color = new Color(0.025f, 0.035f, 0.065f, 0.72f);
                destroyTemplate = true;
            }
        }

        if (template == null)
        {
            if (!warnedMissingMaterial)
            {
                warnedMissingMaterial = true;
                Debug.LogWarning(
                    "[Orrery] Void Wave presentation material unavailable. " +
                    "Gameplay remains active; wave VFX is suppressed.");
            }
            return null;
        }

        VisualState state = new VisualState();
        state.Owner = owner;
        state.Generation = generation;
        state.Origin = origin;
        state.Direction = NormalizeOrRight(direction);
        state.LastSeenAt = Time.unscaledTime;
        state.Root = new GameObject("Orrery Void Wave");
        state.Mesh = new Mesh();
        state.Mesh.name = "Orrery Void Wave Arc";

        int segments = Mathf.Max(4, OrrerySpellCompendium.VoidWave.VisualArcSegments);
        state.Vertices = new Vector3[(segments + 1) * 2];
        state.Triangles = new int[segments * 6];
        BuildTriangles(state.Triangles, segments);
        state.Mesh.vertices = state.Vertices;
        state.Mesh.triangles = state.Triangles;
        state.Mesh.RecalculateBounds();

        MeshFilter filter = state.Root.AddComponent<MeshFilter>();
        filter.sharedMesh = state.Mesh;
        state.Renderer = state.Root.AddComponent<MeshRenderer>();
        state.Material = new Material(template);
        state.Material.name = "Orrery Void Wave Material";
        ApplyOpacity(state.Material);
        state.Renderer.sharedMaterial = state.Material;
        state.Renderer.sortingOrder =
            OrrerySpellCompendium.VoidWave.VisualSortingOrder;

        if (destroyTemplate)
            Object.Destroy(template);

        UpdateMesh(state);
        return state;
    }

    private static void UpdateMesh(VisualState state)
    {
        if (state == null || state.Root == null || state.Mesh == null ||
            state.Vertices == null)
        {
            return;
        }

        float progress = Mathf.Clamp01(state.Progress01);
        float start = OrrerySpellCompendium.VoidWave.InitialFrontDistanceMeters;
        float end = Mathf.Max(
            start,
            OrrerySpellCompendium.VoidWave.MaximumFrontDistanceMeters);
        float frontMeters = Mathf.Lerp(start, end, progress);
        float innerMeters = Mathf.Max(
            0f,
            frontMeters - OrrerySpellCompendium.VoidWave.WaveDepthMeters);
        float outerWorld = CoreSpatial.MetersToWorldUnits(frontMeters);
        float innerWorld = CoreSpatial.MetersToWorldUnits(innerMeters);
        float halfAngle = OrreryVoidWave.GetHalfAngleDegrees(frontMeters);

        int segments = state.Vertices.Length / 2 - 1;
        for (int i = 0; i <= segments; i++)
        {
            float t = segments <= 0 ? 0f : (float)i / segments;
            float angle = Mathf.Lerp(-halfAngle, halfAngle, t) * Mathf.Deg2Rad;
            float x = Mathf.Cos(angle);
            float y = Mathf.Sin(angle);
            int vertex = i * 2;
            state.Vertices[vertex] = new Vector3(
                x * innerWorld,
                y * innerWorld,
                0f);
            state.Vertices[vertex + 1] = new Vector3(
                x * outerWorld,
                y * outerWorld,
                0f);
        }

        state.Mesh.vertices = state.Vertices;
        state.Mesh.RecalculateBounds();

        state.Root.transform.position = state.Origin;
        Vector2 direction = NormalizeOrRight(state.Direction);
        float angleDegrees = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
        state.Root.transform.rotation = Quaternion.Euler(0f, 0f, angleDegrees);
    }

    private static void BuildTriangles(int[] triangles, int segments)
    {
        for (int i = 0; i < segments; i++)
        {
            int vertex = i * 2;
            int triangle = i * 6;
            triangles[triangle] = vertex;
            triangles[triangle + 1] = vertex + 2;
            triangles[triangle + 2] = vertex + 1;
            triangles[triangle + 3] = vertex + 1;
            triangles[triangle + 4] = vertex + 2;
            triangles[triangle + 5] = vertex + 3;
        }
    }

    private static void ApplyOpacity(Material material)
    {
        if (material == null)
            return;

        float opacity = Mathf.Clamp01(
            OrrerySpellCompendium.VoidWave.VisualOpacity);
        if (material.HasProperty("_Color"))
        {
            Color color = material.GetColor("_Color");
            color.a *= opacity;
            material.SetColor("_Color", color);
        }
        if (material.HasProperty("_StarTint"))
        {
            Color color = material.GetColor("_StarTint");
            color.a *= opacity;
            material.SetColor("_StarTint", color);
        }
    }

    private static void Forget(GameShip owner)
    {
        if (owner == null)
            return;

        VisualState state;
        if (remote.TryGetValue(owner, out state))
            DestroyRemote(owner, state);

        if (local != null && object.ReferenceEquals(local.Owner, owner))
            DestroyVisual(ref local);
    }

    private static void Reset()
    {
        DestroyVisual(ref local);

        foreach (KeyValuePair<GameShip, VisualState> pair in remote)
            DestroyVisual(pair.Value);
        remote.Clear();
        warnedMissingMaterial = false;
    }

    private static void DestroyRemote(GameShip owner, VisualState state)
    {
        remote.Remove(owner);
        DestroyVisual(state);
    }

    private static void DestroyVisual(ref VisualState state)
    {
        VisualState old = state;
        state = null;
        DestroyVisual(old);
    }

    private static void DestroyVisual(VisualState state)
    {
        if (state == null)
            return;
        if (state.Root != null)
            Object.Destroy(state.Root);
        if (state.Material != null)
            Object.Destroy(state.Material);
        if (state.Mesh != null)
            Object.Destroy(state.Mesh);
        state.Root = null;
        state.Material = null;
        state.Mesh = null;
        state.Owner = null;
    }

    private static void WriteUInt(ref CoreNetwork.SlotWriter writer, uint value)
    {
        writer.Byte((byte)value);
        writer.Byte((byte)(value >> 8));
        writer.Byte((byte)(value >> 16));
        writer.Byte((byte)(value >> 24));
    }

    private static uint ReadUInt(ref CoreNetwork.SlotReader reader)
    {
        return (uint)reader.Byte() |
            ((uint)reader.Byte() << 8) |
            ((uint)reader.Byte() << 16) |
            ((uint)reader.Byte() << 24);
    }

    private static byte PackAngle(Vector2 direction)
    {
        direction = NormalizeOrRight(direction);
        float degrees = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
        if (degrees < 0f)
            degrees += 360f;
        return (byte)Mathf.RoundToInt(degrees / 360f * 255f);
    }

    private static Vector2 UnpackAngle(byte packed)
    {
        float radians = packed / 255f * Mathf.PI * 2f;
        return new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));
    }

    private static Vector2 NormalizeOrRight(Vector2 value)
    {
        if (value.sqrMagnitude <= 0.000001f ||
            float.IsNaN(value.x) || float.IsNaN(value.y) ||
            float.IsInfinity(value.x) || float.IsInfinity(value.y))
        {
            return Vector2.right;
        }
        return value.normalized;
    }
}
