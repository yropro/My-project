using StarVortex;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Presentation-only Accretion Disk placeholder using the same Frost Nova Pulse
/// family as Cold Fusion. Native Wave behavior and collision stay disabled.
/// </summary>
public static class OrreryAccretionDiskPresentation
{
    private sealed class State
    {
        public GameShip Target;
        public Wave Wave;
        public CircleCollider2D Collider;
        public SpriteRenderer Sprite;
        public Vector3 BaseScale;
        public Color BaseColor;
        public bool WaveEnabled;
        public bool ColliderEnabled;
        public float ExpiresAt;
        public float RadiusMeters;
        public float Capacity01;
        public float DurationSeconds;
        public uint Generation;
        public Transform Parent;
        public Quaternion BaseRotation;
    }

    private static readonly Dictionary<GameShip, State> active =
        new Dictionary<GameShip, State>(8);
    private static readonly List<GameShip> cleanupScratch =
        new List<GameShip>(8);
    private static bool warnedMissingAsset;

    public static void Show(
        GameShip target,
        float durationSeconds,
        float radiusMeters,
        float capacity01,
        uint generation,
        float duration01)
    {
        if (target == null || durationSeconds <= 0f || radiusMeters <= 0f)
            return;

        State existing;
        if (active.TryGetValue(target, out existing) && existing != null)
        {
            existing.ExpiresAt = existing.Generation == generation
                ? Mathf.Min(existing.ExpiresAt, Time.time + durationSeconds)
                : Time.time + durationSeconds;
            existing.Generation = generation;
            existing.DurationSeconds = durationSeconds / Mathf.Max(0.000001f, duration01);
            existing.RadiusMeters = radiusMeters;
            existing.Capacity01 = Mathf.Clamp01(capacity01);
            UpdateState(existing, 0f);
            return;
        }

        if (PoolController.instance == null || active.Count >= 16)
            return;

        PulseItemBase pulse = OrreryContent.FrostNovaPulse;
        GameObject prefab = pulse == null ? null : pulse.wave;
        if (prefab == null)
        {
            if (!warnedMissingAsset)
            {
                warnedMissingAsset = true;
                Debug.LogWarning(
                    "[Orrery/AccretionDisk] Optional Frost Nova placeholder " +
                    "visual is unavailable; gameplay remains active.");
            }
            return;
        }

        GameObject visual = PoolController.instance.GetObject(
            prefab,
            target.transform.position,
            Quaternion.identity,
            false);
        if (visual == null)
            return;

        Wave wave;
        CircleCollider2D circle;
        SpriteRenderer sprite;
        if (!visual.TryGetComponent<Wave>(out wave) || wave == null ||
            !visual.TryGetComponent<CircleCollider2D>(out circle) ||
            circle == null || circle.radius <= 0f ||
            !visual.TryGetComponent<SpriteRenderer>(out sprite) || sprite == null)
        {
            ReturnUnexpectedVisual(visual);
            return;
        }

        State state = new State();
        state.Target = target;
        state.Generation = generation;
        state.DurationSeconds = durationSeconds / Mathf.Max(0.000001f, duration01);
        state.Parent = wave.transform.parent;
        state.BaseRotation = wave.transform.localRotation;
        state.Wave = wave;
        state.Collider = circle;
        state.Sprite = sprite;
        state.BaseScale = wave.transform.localScale;
        state.BaseColor = sprite.color;
        state.WaveEnabled = wave.enabled;
        state.ColliderEnabled = circle.enabled;
        state.ExpiresAt = Time.time + durationSeconds;
        state.RadiusMeters = radiusMeters;
        state.Capacity01 = Mathf.Clamp01(capacity01);

        // Render in world space so a nonuniform pooled parent cannot distort
        // the mechanical circle into an ellipse as the placeholder rotates.
        wave.transform.SetParent(null, true);
        wave.enabled = false;
        circle.enabled = false;
        OrreryWavePresentation.ResetMask(wave);
        active[target] = state;
        UpdateState(state, 0f);
    }

    public static void Tick(float deltaTime)
    {
        if (active.Count == 0)
            return;

        cleanupScratch.Clear();
        float now = Time.time;
        foreach (KeyValuePair<GameShip, State> pair in active)
        {
            State state = pair.Value;
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
        if (object.ReferenceEquals(target, null))
            return;

        State state;
        if (!active.TryGetValue(target, out state))
            return;

        CleanupState(state);
        active.Remove(target);
    }

    public static void Reset()
    {
        cleanupScratch.Clear();
        foreach (KeyValuePair<GameShip, State> pair in active)
            cleanupScratch.Add(pair.Key);
        for (int i = 0; i < cleanupScratch.Count; i++)
            Hide(cleanupScratch[i]);
        cleanupScratch.Clear();
        warnedMissingAsset = false;
    }

    private static void UpdateState(State state, float deltaTime)
    {
        if (state == null || state.Target == null || state.Wave == null ||
            state.Collider == null)
        {
            return;
        }

        Transform transform = state.Wave.transform;
        transform.position = state.Target.transform.position;

        float desiredRadius = OrreryUnits.MetersToWorld(
            Mathf.Max(0.01f, state.RadiusMeters)) *
            Mathf.Max(0.01f, OrrerySpellCompendium.AccretionDisk.VisualRadiusMultiplier);
        float scale = desiredRadius / Mathf.Max(0.0001f, state.Collider.radius);
        transform.localScale = new Vector3(scale, scale, scale);
        transform.Rotate(
            0f,
            0f,
            OrrerySpellCompendium.AccretionDisk.VisualRotationDegreesPerSecond *
                Mathf.Max(0f, deltaTime));

        // Match the collider's authored centre, not merely its prefab pivot.
        transform.position = state.Target.transform.position - transform.TransformVector(state.Collider.offset);
        if (state.Sprite != null)
        {
            float depletion = 1f - Mathf.Clamp01(state.Capacity01);
            float brightness = Mathf.Lerp(
                OrrerySpellCompendium.AccretionDisk.FullCapacityBrightness,
                OrrerySpellCompendium.AccretionDisk.DepletedCapacityBrightness, depletion);
            float duration = Mathf.Clamp01((state.ExpiresAt - Time.time) / Mathf.Max(0.001f, state.DurationSeconds));
            float opacity = OrrerySpellCompendium.AccretionDisk.FadeWithDuration
                ? Mathf.Lerp(OrrerySpellCompendium.AccretionDisk.MinimumDurationOpacityFraction, 1f, duration)
                : 1f;
            Color color = state.BaseColor;
            color.r *= brightness;
            color.g *= brightness;
            color.b *= brightness;
            color.a *= Mathf.Clamp01(OrrerySpellCompendium.AccretionDisk.VisualOpacity * opacity);
            state.Sprite.color = color;
        }
    }

    private static void CleanupState(State state)
    {
        if (state == null || state.Wave == null)
            return;

        state.Wave.transform.SetParent(state.Parent, true);
        state.Wave.transform.localScale = state.BaseScale;
        state.Wave.transform.localRotation = state.BaseRotation;
        if (state.Sprite != null)
            state.Sprite.color = state.BaseColor;
        if (state.Collider != null)
            state.Collider.enabled = state.ColliderEnabled;
        state.Wave.enabled = state.WaveEnabled;
        state.Wave.PoolDestroy();
    }

    private static void ReturnUnexpectedVisual(GameObject visual)
    {
        if (visual == null)
            return;

        PoolableObject poolable;
        if (visual.TryGetComponent<PoolableObject>(out poolable) &&
            poolable != null)
        {
            poolable.PoolDestroy();
        }
        else
        {
            Object.Destroy(visual);
        }
    }
}
