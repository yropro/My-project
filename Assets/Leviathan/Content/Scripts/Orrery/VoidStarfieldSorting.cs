using System.Collections.Generic;
using StarVortex;
using UnityEngine;
using UnityEngine.Rendering;

// Shared sorting lease for starfield surfaces. Original ambient orders are
// restored when the final surface goes away; remote wedges share this lease.
public static class VoidStarfieldSorting
{
    private static readonly List<MeshRenderer> renderers = new List<MeshRenderer>();
    private static readonly List<int> rendererOffsets = new List<int>();
    private static readonly Dictionary<Renderer, Vector2Int> ambientOrders = new Dictionary<Renderer, Vector2Int>();
    private static readonly Dictionary<SortingGroup, Vector2Int> ambientGroups = new Dictionary<SortingGroup, Vector2Int>();
    private static Camera view;
    private static int sortingLayer;
    private static int sortingOrder;
    private static float nextRefresh;

    public static void Register(MeshRenderer renderer, int offset)
    {
        if (renderers.Contains(renderer)) return;
        renderers.Add(renderer);
        rendererOffsets.Add(offset);
        nextRefresh = 0;
    }

    public static void Tick(Camera camera)
    {
        if (renderers.Count == 0 || Time.unscaledTime < nextRefresh) return;
        view = camera;
        RefreshAmbientSorting();
        nextRefresh = Time.unscaledTime + 1;
    }

    public static void Unregister(MeshRenderer renderer)
    {
        int index = renderers.IndexOf(renderer);
        if (index >= 0) { renderers.RemoveAt(index); rendererOffsets.RemoveAt(index); }
        if (renderers.Count != 0) return;
        foreach (var pair in ambientOrders)
            if (pair.Key != null) { pair.Key.sortingLayerID = pair.Value.x; pair.Key.sortingOrder = pair.Value.y; }
        foreach (var pair in ambientGroups)
            if (pair.Key != null) { pair.Key.sortingLayerID = pair.Value.x; pair.Key.sortingOrder = pair.Value.y; }
        ambientOrders.Clear();
        ambientGroups.Clear();
        view = null;
    }
    private static void RefreshAmbientSorting()
    {
        sortingLayer = 0;
        sortingOrder = -100;

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

}

