using StarVortex;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Presentation-only Cold Fusion aura.
///
/// Three disabled Frost Nova Wave sprites are held over the buffed player for
/// the resolved spell duration. Native Wave behavior and colliders stay disabled,
/// so these layers never perform collision, damage, fog stamping or pulse logic.
///
/// Cross-owner grants are already reliable star-wide events. This presentation
/// observes that event on every peer and resolves the target player's local ship
/// or remote replica, keeping visuals derived from the gameplay grant rather than
/// creating a second spell-specific replication stream.
/// </summary>
public static class OrreryColdFusionPresentation
{
    private const int MaxHaloLayers = 6;

    private sealed class HaloLayer
    {
        public Wave Wave;
        public CircleCollider2D Collider;
        public SpriteRenderer Sprite;
        public bool WaveEnabled;
        public bool ColliderEnabled;
        public Vector3 BaseScale;
        public Color BaseColor;
        public float RadiusScale;
        public float RotationDegreesPerSecond;
    }

    private sealed class AuraState
    {
        public GameShip Target;
        public float ExpiresAt;
        public float BaseRadiusWorld;
        public readonly HaloLayer[] Layers = new HaloLayer[MaxHaloLayers];
        public int LayerCount;
    }

    private static readonly Dictionary<GameShip, AuraState> active =
        new Dictionary<GameShip, AuraState>(8);
    private static readonly List<GameShip> cleanupScratch =
        new List<GameShip>(8);

    public static void Show(GameShip target, float durationSeconds)
    {
        if (target == null || durationSeconds <= 0f)
            return;

        AuraState existing;
        if (active.TryGetValue(target, out existing) && existing != null)
        {
            existing.ExpiresAt = Time.time + durationSeconds;
            existing.BaseRadiusWorld = ResolveTargetRadius(target);
            return;
        }

        if (PoolController.instance == null)
            return;

        PulseItemBase frostNovaBase = OrreryContent.FrostNovaPulse;
        GameObject prefab = frostNovaBase == null ? null : frostNovaBase.wave;
        if (prefab == null)
            return;

        AuraState state = new AuraState();
        state.Target = target;
        state.ExpiresAt = Time.time + durationSeconds;
        state.BaseRadiusWorld = ResolveTargetRadius(target);

        int requested = Mathf.Clamp(
            OrrerySpellCompendium.ColdFusion.HaloCount,
            1,
            MaxHaloLayers);

        for (int i = 0; i < requested; i++)
        {
            GameObject visual = PoolController.instance.GetObject(
                prefab,
                target.transform.position,
                Quaternion.identity,
                false);
            if (visual == null)
                break;

            Wave wave;
            CircleCollider2D circle;
            SpriteRenderer sprite;
            if (!visual.TryGetComponent<Wave>(out wave) || wave == null ||
                !visual.TryGetComponent<CircleCollider2D>(out circle) || circle == null ||
                circle.radius <= 0f ||
                !visual.TryGetComponent<SpriteRenderer>(out sprite) || sprite == null)
            {
                ReturnUnexpectedVisual(visual);
                continue;
            }

            HaloLayer layer = new HaloLayer();
            layer.Wave = wave;
            layer.Collider = circle;
            layer.Sprite = sprite;
            layer.WaveEnabled = wave.enabled;
            layer.ColliderEnabled = circle.enabled;
            layer.BaseScale = wave.transform.localScale;
            layer.BaseColor = sprite.color;

            float centeredIndex = i - (requested - 1) * 0.5f;
            layer.RadiusScale = Mathf.Max(
                0.05f,
                1f + centeredIndex *
                    OrrerySpellCompendium.ColdFusion.HaloLayerSpacingFraction);
            float direction = (i & 1) == 0 ? 1f : -1f;
            layer.RotationDegreesPerSecond = direction *
                OrrerySpellCompendium.ColdFusion.HaloRotationDegreesPerSecond *
                (1f + Mathf.Abs(centeredIndex) *
                    OrrerySpellCompendium.ColdFusion.HaloOuterRotationMultiplier);

            wave.enabled = false;
            circle.enabled = false;
            OrreryWavePresentation.ResetMask(wave);
            Color color = layer.BaseColor;
            color.a *= Mathf.Clamp01(
                OrrerySpellCompendium.ColdFusion.HaloOpacity);
            sprite.color = color;
            wave.transform.rotation = Quaternion.Euler(
                0f,
                0f,
                i * (360f / Mathf.Max(1, requested)));

            state.Layers[state.LayerCount++] = layer;
        }

        if (state.LayerCount <= 0)
            return;

        active[target] = state;
        UpdateState(state, 0f);
    }

    public static void Tick(float deltaTime)
    {
        if (active.Count == 0)
            return;

        cleanupScratch.Clear();
        float now = Time.time;
        foreach (KeyValuePair<GameShip, AuraState> pair in active)
        {
            AuraState state = pair.Value;
            if (state == null || state.Target == null ||
                state.Target.gameObject == null ||
                !state.Target.gameObject.activeInHierarchy ||
                now >= state.ExpiresAt)
            {
                cleanupScratch.Add(pair.Key);
                continue;
            }

            UpdateState(state, deltaTime);
        }

        for (int i = 0; i < cleanupScratch.Count; i++)
            Hide(cleanupScratch[i]);
        cleanupScratch.Clear();
    }

    public static void Hide(GameShip target)
    {
        if (target == null)
            return;

        AuraState state;
        if (!active.TryGetValue(target, out state))
            return;

        CleanupState(state);
        active.Remove(target);
    }

    public static void Reset()
    {
        cleanupScratch.Clear();
        foreach (KeyValuePair<GameShip, AuraState> pair in active)
            cleanupScratch.Add(pair.Key);
        for (int i = 0; i < cleanupScratch.Count; i++)
            Hide(cleanupScratch[i]);
        cleanupScratch.Clear();
    }

    private static void UpdateState(AuraState state, float deltaTime)
    {
        if (state == null || state.Target == null)
            return;

        state.BaseRadiusWorld = Mathf.Lerp(
            state.BaseRadiusWorld,
            ResolveTargetRadius(state.Target),
            Mathf.Clamp01(Mathf.Max(0f, deltaTime) *
                OrrerySpellCompendium.ColdFusion.HaloRadiusFollowSpeed));

        Vector3 position = state.Target.transform.position;
        for (int i = 0; i < state.LayerCount; i++)
        {
            HaloLayer layer = state.Layers[i];
            if (layer == null || layer.Wave == null || layer.Collider == null)
                continue;

            Transform transform = layer.Wave.transform;
            transform.position = position;
            float desiredRadius = state.BaseRadiusWorld * layer.RadiusScale;
            float scale = desiredRadius / Mathf.Max(0.0001f, layer.Collider.radius);
            transform.localScale = new Vector3(scale, scale, scale);
            transform.Rotate(
                0f,
                0f,
                layer.RotationDegreesPerSecond * Mathf.Max(0f, deltaTime));
        }
    }

    private static float ResolveTargetRadius(GameShip target)
    {
        if (target == null)
            return OrreryUnits.MetersToWorld(
                OrrerySpellCompendium.ColdFusion.MinimumHaloRadiusMeters);

        float radius = target.GetShieldWorldRadius();
        float minimum = OrreryUnits.MetersToWorld(Mathf.Max(
            0f,
            OrrerySpellCompendium.ColdFusion.MinimumHaloRadiusMeters));
        radius = Mathf.Max(radius, minimum);
        return radius * Mathf.Max(
            0.01f,
            OrrerySpellCompendium.ColdFusion.HaloRadiusMultiplier);
    }

    private static void CleanupState(AuraState state)
    {
        if (state == null)
            return;

        for (int i = 0; i < state.LayerCount; i++)
        {
            HaloLayer layer = state.Layers[i];
            if (layer == null || layer.Wave == null)
                continue;

            layer.Wave.transform.localScale = layer.BaseScale;
            if (layer.Sprite != null)
                layer.Sprite.color = layer.BaseColor;
            if (layer.Collider != null)
                layer.Collider.enabled = layer.ColliderEnabled;
            layer.Wave.enabled = layer.WaveEnabled;
            layer.Wave.PoolDestroy();
            state.Layers[i] = null;
        }
        state.LayerCount = 0;
    }

    private static void ReturnUnexpectedVisual(GameObject visual)
    {
        if (visual == null)
            return;

        PoolableObject poolable;
        if (visual.TryGetComponent<PoolableObject>(out poolable) && poolable != null)
            poolable.PoolDestroy();
        else
            UnityEngine.Object.Destroy(visual);
    }
}
