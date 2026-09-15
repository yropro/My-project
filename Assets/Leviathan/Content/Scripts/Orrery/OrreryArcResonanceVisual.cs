using StarVortex;
using UnityEngine;
using UnityEngine.Rendering;
using Profile = OrrerySpellCompendium.ArcResonance.Profile;

/// <summary>
/// Reusable presentation-only blue Zap. Samples an authored visible particle
/// frame once per strike and freezes it, so the artist's short erosion/alpha
/// curves cannot erase a requested 0.5-second connection. The enclosing transform
/// tracks both endpoints; it never restarts the particles during that connection.
/// </summary>
internal sealed class OrreryArcResonanceVisual
{
    private const int MaximumLayers = 32;
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int TintColorId = Shader.PropertyToID("_TintColor");
    private static readonly int ColorId = Shader.PropertyToID("_Color");
    private static GameObject prefab;
    private static bool attemptedLoad, warned;
    private GameObject root;
    private ParticleSystem[] particles;
    private ParticleSystemRenderer[] renderers;
    private ParticleSystem.Particle[][] samples;
    private Color32[] originalColors;
    private int[] materialTintIds;
    private Color[] materialColors;
    private readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();
    private readonly ParticleSystem.Burst[] singleBurst = { new ParticleSystem.Burst(0f, (short)1) };

    public void Show(GameShip owner, Profile p)
    {
        try
        {
            if (!Ensure()) return;
            float sampleAge = Safe(p.ZapSampleNormalizedAge, 0.02f, 0.95f, 0.20f);
            SortingGroup sorting = owner.GetComponent<SortingGroup>();
            root.SetActive(true);
            for (int i = 0; i < particles.Length; i++)
            {
                ParticleSystem ps = particles[i];
                ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = ps.main;
                main.startRotation = Safe(p.ZapTextureRotationDegrees, -360f, 360f, 0f) * Mathf.Deg2Rad;
                ParticleSystemRenderer renderer = renderers[i];
                renderer.sortingLayerID = sorting == null ? 0 : sorting.sortingLayerID;
                renderer.sortingOrder = Mathf.Clamp((sorting == null ? 0 : sorting.sortingOrder) +
                    Mathf.Clamp(p.BoltSortingOrder, -16000, 16000), -32767, 32767);
                renderer.enabled = true;
                // Native blue materials and authored atlas/custom-data curves are
                // retained. Only their simulation time is frozen at this sample.
                ps.Simulate(sampleAge, false, true, false);
                ps.Pause(false);
                int count = ps.GetParticles(samples[i]);
                if (count != 1) { renderer.enabled = false; continue; }
                originalColors[i] = samples[i][0].startColor;
                properties.Clear();
                if (materialTintIds[i] != -1)
                {
                    Color color = materialColors[i];
                    float brightness = Safe(p.BoltBrightnessMultiplier, 0f, 16f, 1f);
                    color.r *= brightness; color.g *= brightness; color.b *= brightness;
                    properties.SetColor(materialTintIds[i], color);
                }
                renderer.SetPropertyBlock(properties);
            }
        }
        catch (System.Exception error)
        {
            Warn("Blue Zap setup failed: " + error.Message);
            Dispose(); // Presentation failure cannot stop the sound or gameplay.
        }
    }

    private bool Ensure()
    {
        if (root != null) return true;
        if (!attemptedLoad)
        {
            attemptedLoad = true;
            prefab = ModContent.Load<GameObject>(OrrerySpellCompendium.ArcResonance.ZapPrefabPath);
        }
        if (prefab == null)
        {
            Warn("Blue Zap asset is missing. Run Star Vortex Mod > Prepare Arc Resonance VFX, " +
                "then Build AssetBundles and Package Mod (leviathanarcvfx.bundle).");
            return false;
        }
        // The editor preparation command removes scripts from the mod-owned
        // copy. Never allow an arbitrary vendor/game MonoBehaviour to Awake in
        // a visual-only remote replica. Missing scripts cannot execute.
        MonoBehaviour[] scripts = prefab.GetComponentsInChildren<MonoBehaviour>(true);
        for (int i = 0; i < scripts.Length; i++)
        {
            if (scripts[i] == null) continue;
            Warn("Blue Zap still contains scripts. Run Prepare Arc Resonance VFX and rebuild the bundle.");
            return false;
        }
        root = new GameObject("Orrery Arc Resonance Blue Zap");
        root.SetActive(false);
        GameObject clone = Object.Instantiate(prefab, root.transform, false);
        particles = clone.GetComponentsInChildren<ParticleSystem>(true);
        if (particles.Length == 0 || particles.Length > MaximumLayers)
        {
            Warn("Blue Zap has an unsupported particle layer count.");
            Dispose();
            return false;
        }
        Collider2D[] colliders2D = clone.GetComponentsInChildren<Collider2D>(true);
        for (int i = 0; i < colliders2D.Length; i++) colliders2D[i].enabled = false;
        Collider[] colliders3D = clone.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders3D.Length; i++) colliders3D[i].enabled = false;
        AudioSource[] audio = clone.GetComponentsInChildren<AudioSource>(true);
        for (int i = 0; i < audio.Length; i++) audio[i].enabled = false;
        Renderer[] allRenderers = clone.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < allRenderers.Length; i++) allRenderers[i].enabled = false;
        renderers = new ParticleSystemRenderer[particles.Length];
        samples = new ParticleSystem.Particle[particles.Length][];
        originalColors = new Color32[particles.Length];
        materialTintIds = new int[particles.Length];
        materialColors = new Color[particles.Length];
        for (int i = 0; i < particles.Length; i++)
        {
            ParticleSystem ps = particles[i];
            // Flatten only the cloned presentation hierarchy, never the asset.
            ps.transform.SetParent(root.transform, false);
            ps.transform.localPosition = Vector3.zero;
            ps.transform.localRotation = Quaternion.identity;
            ps.transform.localScale = Vector3.one;
            ps.gameObject.SetActive(true); // parent is inactive until configured
            ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = false;
            main.prewarm = false;
            main.duration = 1f;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.startSpeed = 0f; main.startDelay = 0f; main.startLifetime = 1f;
            main.startSize3D = true;
            main.startSizeX = main.startSizeY = main.startSizeZ = 1f;
            main.startRotation3D = false; main.startRotation = 0f;
            main.gravityModifier = 0f; main.maxParticles = 1;
            main.stopAction = ParticleSystemStopAction.None;
            var shape = ps.shape; shape.enabled = false;
            var emission = ps.emission;
            emission.enabled = true; emission.rateOverTime = 0f; emission.rateOverDistance = 0f;
            emission.SetBursts(singleBurst);
            var subEmitters = ps.subEmitters; subEmitters.enabled = false;
            var velocity = ps.velocityOverLifetime; velocity.enabled = false;
            var force = ps.forceOverLifetime; force.enabled = false;
            var noise = ps.noise; noise.enabled = false;
            var size = ps.sizeOverLifetime; size.enabled = false;
            var sizeBySpeed = ps.sizeBySpeed; sizeBySpeed.enabled = false;
            var rotation = ps.rotationOverLifetime; rotation.enabled = false;
            var rotationBySpeed = ps.rotationBySpeed; rotationBySpeed.enabled = false;
            var collision = ps.collision; collision.enabled = false;
            var trigger = ps.trigger; trigger.enabled = false;
            var trails = ps.trails; trails.enabled = false;
            var lights = ps.lights; lights.enabled = false;
            ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
            if (renderer == null) throw new System.InvalidOperationException("Zap particle has no renderer.");
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.alignment = ParticleSystemRenderSpace.Local;
            renderer.velocityScale = renderer.cameraVelocityScale = 0f;
            renderer.pivot = Vector3.zero;
            renderers[i] = renderer;
            samples[i] = new ParticleSystem.Particle[1];
            Material material = renderer.sharedMaterial;
            int id = material != null && material.HasProperty(BaseColorId) ? BaseColorId :
                material != null && material.HasProperty(TintColorId) ? TintColorId :
                material != null && material.HasProperty(ColorId) ? ColorId : -1;
            materialTintIds[i] = id;
            materialColors[i] = id == -1 ? Color.white : material.GetColor(id);
        }
        return true;
    }

    public void Draw(Vector2 start, Vector2 end, Profile p, float fade)
    {
        if (root == null || !root.activeSelf) return;
        Vector2 delta = end - start;
        float length = delta.magnitude;
        if (!OrreryNetwork.IsFinite(length) || length < 0.0001f)
        {
            // Coincident endpoints hide geometry, not the accepted strike. They
            // may separate again before the original visibility deadline.
            root.transform.localScale = Vector3.zero;
            return;
        }
        float width = OrreryUnits.MetersToWorld(Safe(p.BoltWidthMeters, 0.01f, 1000f, 10f)) *
            Safe(p.ZapWidthMultiplier, 0.01f, 20f, 2f);
        root.transform.position = (Vector3)((start + end) * 0.5f);
        root.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
        root.transform.localScale = new Vector3(length, width, 1f);
        for (int i = 0; i < particles.Length; i++)
        {
            Color color = originalColors[i];
            // Known material tint properties retain HDR brightness. For a custom
            // shader without one, vertex tint still works within Color32's range.
            float vertexBrightness = materialTintIds[i] == -1 ? Safe(p.BoltBrightnessMultiplier, 0f, 16f, 1f) : 1f;
            color.r *= Safe(p.BoltTintR, 0f, 16f, 1f) * vertexBrightness;
            color.g *= Safe(p.BoltTintG, 0f, 16f, 1f) * vertexBrightness;
            color.b *= Safe(p.BoltTintB, 0f, 16f, 1f) * vertexBrightness;
            color.a *= Safe(p.BoltOpacity, 0f, 1f, 1f) * Mathf.Clamp01(fade);
            samples[i][0].startColor = color;
            particles[i].SetParticles(samples[i], 1);
        }
    }

    public void Hide()
    {
        if (root == null || !root.activeSelf) return;
        if (particles != null)
            for (int i = 0; i < particles.Length; i++)
                particles[i].Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
        root.SetActive(false);
    }

    public void Dispose()
    {
        if (root != null) Object.Destroy(root);
        root = null; particles = null; renderers = null; samples = null;
        originalColors = null; materialTintIds = null; materialColors = null;
    }

    public static void ResetAssetCache() { prefab = null; attemptedLoad = warned = false; }
    private static void Warn(string message)
    {
        if (warned) return;
        warned = true;
        Debug.LogWarning("[Orrery] Arc Resonance: " + message);
    }
    private static float Safe(float value, float min, float max, float fallback)
    { return OrreryNetwork.IsFinite(value) ? Mathf.Clamp(value, min, max) : fallback; }
}
