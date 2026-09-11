using StarVortex;
using System.Collections.Generic;
using UnityEngine;
using HarmonyLib;

/// <summary>
/// Local-only Predator presentation.
///
/// Prey remains an OwnerTarget combat state owned by the local Predator player.
/// Its visual is therefore intentionally local presentation rather than a native
/// Burning status or a globally replicated debuff.
///
/// Hunt Streak remains Predator-owned gameplay state. The HUD view borrows the
/// native status-icon prefab so the timer/stack language matches Star Vortex,
/// but no fake StatusEffect is inserted into GameShip.statusEffects.
/// </summary>
internal static class LeviathanPredatorPresentation
{
    private sealed class PreyVisual
    {
        public GameShip Owner;
        public GameShip Target;
        public GameObject Root;
        public float LastTrackedAt;
    }

    // Crimson rather than orange Burning. Presentation only.
    private static readonly Color PreyTint = new Color(1f, 0.045f, 0.065f, 1f);
    private const float UnconfirmedTargetRetentionSeconds = 6f;

    private static readonly Dictionary<GameShip, PreyVisual> PreyByTarget =
        new Dictionary<GameShip, PreyVisual>();
    private static readonly List<GameShip> TargetScratch = new List<GameShip>();

    private static HUDShipBar HuntBar;
    private static GameObject HuntRoot;
    private static StatusEffectIcon HuntIcon;

    public static void TrackPreyTarget(GameShip owner, GameShip target)
    {
        if (owner == null || target == null || object.ReferenceEquals(owner, target))
            return;

        PreyVisual entry;
        if (!PreyByTarget.TryGetValue(target, out entry) || entry == null)
        {
            entry = new PreyVisual();
            entry.Owner = owner;
            entry.Target = target;
            PreyByTarget[target] = entry;
        }
        else if (!object.ReferenceEquals(entry.Owner, owner))
        {
            DestroyVisual(entry);
            entry.Owner = owner;
            entry.Target = target;
        }

        entry.LastTrackedAt = Time.time;
    }

    public static void FixedTick(GameShip owner)
    {
        if (owner == null || PreyByTarget.Count == 0)
            return;

        float now = Time.time;
        TargetScratch.Clear();
        foreach (GameShip target in PreyByTarget.Keys)
            TargetScratch.Add(target);

        for (int i = 0; i < TargetScratch.Count; i++)
        {
            GameShip target = TargetScratch[i];
            PreyVisual entry;
            if (!PreyByTarget.TryGetValue(target, out entry) ||
                entry == null ||
                !object.ReferenceEquals(entry.Owner, owner))
            {
                continue;
            }

            bool targetAlive =
                target != null &&
                target.gameObject != null &&
                target.gameObject.activeInHierarchy &&
                target.health > 0f;

            bool prey = targetAlive &&
                LeviathanPredatorRuntime.IsPrey(owner, target);

            if (prey)
            {
                EnsureVisual(entry);
                continue;
            }

            DestroyVisual(entry);

            if (!targetAlive ||
                now - entry.LastTrackedAt >= UnconfirmedTargetRetentionSeconds)
            {
                PreyByTarget.Remove(target);
            }
        }

        TargetScratch.Clear();
    }

    public static void ResetOwner(GameShip owner)
    {
        if (owner == null)
            return;

        TargetScratch.Clear();
        foreach (KeyValuePair<GameShip, PreyVisual> pair in PreyByTarget)
        {
            if (pair.Value != null && object.ReferenceEquals(pair.Value.Owner, owner))
                TargetScratch.Add(pair.Key);
        }

        for (int i = 0; i < TargetScratch.Count; i++)
        {
            GameShip target = TargetScratch[i];
            PreyVisual entry;
            if (PreyByTarget.TryGetValue(target, out entry))
                DestroyVisual(entry);
            PreyByTarget.Remove(target);
        }
        TargetScratch.Clear();

        GameShip local = WorldController.instance == null
            ? null
            : WorldController.instance.GetCurrentPlayerShip();
        if (object.ReferenceEquals(local, owner))
            DestroyHuntHud();
    }

    public static void ResetAll()
    {
        foreach (PreyVisual entry in PreyByTarget.Values)
            DestroyVisual(entry);

        PreyByTarget.Clear();
        TargetScratch.Clear();
        DestroyHuntHud();
    }

    private static void EnsureVisual(PreyVisual entry)
    {
        if (entry == null || entry.Target == null)
            return;

        if (entry.Root)
            return;

        GameShip.AssignedLayer burning = null;
        List<GameShip.AssignedLayer> layers = entry.Target.statusEffectLayers;
        if (layers != null)
        {
            for (int i = 0; i < layers.Count; i++)
            {
                GameShip.AssignedLayer candidate = layers[i];
                if (candidate != null &&
                    candidate.type == StatusEffect.Type.Burning &&
                    candidate.layerPrefab)
                {
                    burning = candidate;
                    break;
                }
            }
        }

        if (burning == null || !burning.layerPrefab)
            return;

        GameObject root = Object.Instantiate<GameObject>(
            burning.layerPrefab.gameObject,
            entry.Target.transform);

        if (!root)
            return;

        root.name = "Leviathan Predator Prey Visual";
        root.transform.localPosition = Vector3.zero;
        root.transform.localRotation = Quaternion.identity;

        // This is a visual clone, not a native pooled status-layer instance.
        // Ensure cloned PoolableObjects are ordinary owned objects so teardown
        // through Object.Destroy cannot trip native pool ownership guards.
        PoolableObject[] poolables =
            root.GetComponentsInChildren<PoolableObject>(true);
        for (int i = 0; i < poolables.Length; i++)
            if (poolables[i]) poolables[i].UnsetPooled();

        // Disable all cloned StatusEffectLayer behavior and sound before letting
        // the presentation run.
        StatusEffectLayer[] nativeLayers =
            root.GetComponentsInChildren<StatusEffectLayer>(true);
        for (int i = 0; i < nativeLayers.Length; i++)
        {
            StatusEffectLayer layer = nativeLayers[i];
            if (!layer)
                continue;
            layer.activateSound = null;
            layer.deactivateSound = null;
            layer.loopSound = null;
            layer.enabled = false;
        }

        AudioSource[] audioSources = root.GetComponentsInChildren<AudioSource>(true);
        for (int i = 0; i < audioSources.Length; i++)
            if (audioSources[i]) audioSources[i].enabled = false;

        TintVisual(root);
        root.SetActive(true);

        ParticleSystem[] particles = root.GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < particles.Length; i++)
            if (particles[i]) particles[i].Play(true);

        entry.Root = root;
    }

    private static void TintVisual(GameObject root)
    {
        ParticleSystem[] particles = root.GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < particles.Length; i++)
        {
            ParticleSystem particle = particles[i];
            if (!particle)
                continue;
            ParticleSystem.MainModule main = particle.main;
            main.startColor = new ParticleSystem.MinMaxGradient(PreyTint);
        }

        SpriteRenderer[] sprites = root.GetComponentsInChildren<SpriteRenderer>(true);
        for (int i = 0; i < sprites.Length; i++)
        {
            SpriteRenderer sprite = sprites[i];
            if (!sprite)
                continue;
            float alpha = sprite.color.a;
            sprite.color = new Color(PreyTint.r, PreyTint.g, PreyTint.b, alpha);
        }

        TrailRenderer[] trails = root.GetComponentsInChildren<TrailRenderer>(true);
        for (int i = 0; i < trails.Length; i++)
        {
            TrailRenderer trail = trails[i];
            if (!trail)
                continue;
            trail.startColor = PreyTint;
            trail.endColor = new Color(PreyTint.r, PreyTint.g, PreyTint.b, 0f);
        }

        LineRenderer[] lines = root.GetComponentsInChildren<LineRenderer>(true);
        for (int i = 0; i < lines.Length; i++)
        {
            LineRenderer line = lines[i];
            if (!line)
                continue;
            line.startColor = PreyTint;
            line.endColor = PreyTint;
        }
    }

    private static void DestroyVisual(PreyVisual entry)
    {
        if (entry == null || !entry.Root)
            return;
        Object.Destroy(entry.Root);
        entry.Root = null;
    }

    public static void UpdateHuntHud(HUDShipBar bar)
    {
        if (!bar || WorldController.instance == null)
        {
            if (!bar || object.ReferenceEquals(HuntBar, bar))
                DestroyHuntHud();
            return;
        }

        GameShip owner = WorldController.instance.GetCurrentPlayerShip();
        if (!LeviathanPredatorRuntime.IsOwnerActive(owner))
        {
            if (object.ReferenceEquals(HuntBar, bar))
                DestroyHuntHud();
            return;
        }

        LeviathanPredatorRuntime.ResolvedState resolved =
            LeviathanPredatorRuntime.GetResolvedState(owner);
        if (!HasVisibleHuntEffect(resolved))
        {
            if (object.ReferenceEquals(HuntBar, bar))
                DestroyHuntHud();
            return;
        }

        int stacks = LeviathanPredatorRuntime.GetHuntStreakStacks(owner);
        float remaining =
            LeviathanPredatorRuntime.GetHuntStreakRemainingSeconds(owner);

        if (stacks <= 0 || remaining <= 0f)
        {
            if (object.ReferenceEquals(HuntBar, bar))
                DestroyHuntHud();
            return;
        }

        if (!EnsureHuntHud(bar))
            return;

        if (HuntIcon.iconImage)
        {
            HuntIcon.iconImage.sprite =
                WorldController.instance.GetStatusEffectIcon(
                    StatusEffect.Type.DamageIncrease);

            Color color = Palette.instance
                ? Palette.instance.GetStatusEffectColor(StatusEffect.Type.DamageIncrease)
                : Color.white;
            HuntIcon.iconImage.color = color;
            if (HuntIcon.borderImage) HuntIcon.borderImage.color = color;
            if (HuntIcon.stacksText) HuntIcon.stacksText.color = color;
        }

        if (HuntIcon.timeRemainingText)
        {
            int decimals = remaining >= 5f ? 0 : 2;
            HuntIcon.timeRemainingText.text =
                WorldController.instance.GetReadableSeconds(
                    remaining,
                    decimals,
                    true);
        }

        if (HuntIcon.stacksObject)
            HuntIcon.stacksObject.SetActive(true);

        if (HuntIcon.stacksText)
        {
            HuntIcon.stacksText.text = Core.instance
                ? Core.instance.NumberFormat((float)stacks, 0, false)
                : stacks.ToString();
        }
    }

    private static bool HasVisibleHuntEffect(
        LeviathanPredatorRuntime.ResolvedState state)
    {
        if (state == null)
            return false;

        LeviathanPredatorRuntime.ModifierValues v =
            state.PerHuntStreakStack;

        return
            !Mathf.Approximately(v.DamagePercent, 0f) ||
            !Mathf.Approximately(v.LungeDistancePercent, 0f) ||
            !Mathf.Approximately(v.LungeSpeedPercent, 0f) ||
            !Mathf.Approximately(v.CooldownPercent, 0f) ||
            !Mathf.Approximately(v.CooldownRecoveryPercent, 0f) ||
            !Mathf.Approximately(v.CritChanceBonus, 0f) ||
            !Mathf.Approximately(v.StatusChanceBonus, 0f) ||
            !Mathf.Approximately(v.MoveSpeedPercent, 0f) ||
            !Mathf.Approximately(v.AccelerationPercent, 0f) ||
            !Mathf.Approximately(v.TurnSpeedPercent, 0f);
    }

    private static bool EnsureHuntHud(HUDShipBar bar)
    {
        if (object.ReferenceEquals(HuntBar, bar) && HuntRoot && HuntIcon)
            return true;

        DestroyHuntHud();

        if (!bar || !bar.buffContainer || !bar.statusEffectIcon)
            return false;

        HuntRoot = Object.Instantiate<GameObject>(
            bar.statusEffectIcon,
            bar.buffContainer.transform);

        if (!HuntRoot)
            return false;

        HuntRoot.name = "Leviathan Predator Hunt Streak";
        HuntRoot.TryGetComponent<StatusEffectIcon>(out HuntIcon);
        if (!HuntIcon)
        {
            Object.Destroy(HuntRoot);
            HuntRoot = null;
            return false;
        }

        // Borrow the layout and image references, but do not let the native
        // StatusEffectIcon.Update expect a fake StatusEffect object.
        HuntIcon.enabled = false;

        UnityEngine.UI.Button button;
        if (HuntRoot.TryGetComponent<UnityEngine.UI.Button>(out button))
            button.enabled = false;

        HuntBar = bar;
        return true;
    }

    private static void DestroyHuntHud()
    {
        if (HuntRoot)
            Object.Destroy(HuntRoot);

        HuntRoot = null;
        HuntIcon = null;
        HuntBar = null;
    }
}

[HarmonyPatch(typeof(HUDShipBar), "Update")]
internal static class LeviathanPredatorHuntHudPatch
{
    public static void Postfix(HUDShipBar __instance)
    {
        LeviathanPredatorPresentation.UpdateHuntHud(__instance);
    }
}
