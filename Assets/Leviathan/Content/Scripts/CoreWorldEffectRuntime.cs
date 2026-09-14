using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Shared fixed-step/lifecycle boundary for host-authoritative mod world effects.
///
/// Effect implementations own their state and mechanics. This service only owns
/// WHEN registered runtimes tick/reset, preventing every field/wave spell from
/// adding another WorldController hot-path Harmony patch.
/// </summary>
public static class CoreWorldEffectRuntime
{
    private sealed class Entry
    {
        public string Name;
        public Action<float> Tick;
        public Action Reset;
        public bool Warned;
    }

    private static readonly List<Entry> entries = new List<Entry>(8);

    public static void Register(
        string name,
        Action<float> tick,
        Action reset = null)
    {
        if (string.IsNullOrWhiteSpace(name) || tick == null)
            throw new ArgumentException("World-effect runtime name/tick required.");

        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Name == name)
                throw new InvalidOperationException(
                    "Duplicate world-effect runtime: " + name);
        }

        entries.Add(new Entry
        {
            Name = name,
            Tick = tick,
            Reset = reset
        });
    }

    public static void FixedTick(float deltaTime)
    {
        float dt = Mathf.Max(0f, deltaTime);
        for (int i = 0; i < entries.Count; i++)
        {
            Entry entry = entries[i];
            try
            {
                entry.Tick(dt);
            }
            catch (Exception ex)
            {
                Warn(entry, ex);
            }
        }
    }

    public static void ResetWorld()
    {
        for (int i = 0; i < entries.Count; i++)
        {
            Entry entry = entries[i];
            try
            {
                if (entry.Reset != null)
                    entry.Reset();
            }
            catch (Exception ex)
            {
                Warn(entry, ex);
            }
            entry.Warned = false;
        }
    }

    private static void Warn(Entry entry, Exception error)
    {
        if (entry == null || entry.Warned)
            return;
        entry.Warned = true;
        Debug.LogWarning(
            "[CoreWorldEffectRuntime] " + entry.Name + " failed: " + error);
    }
}

[HarmonyPatch(typeof(WorldController), nameof(WorldController.FixedUpdate))]
public static class CoreWorldEffectRuntimeFixedPatch
{
    public static void Postfix()
    {
        CoreWorldEffectRuntime.FixedTick(Time.fixedDeltaTime);
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class CoreWorldEffectRuntimeWorldDestroyedPatch
{
    public static void Prefix()
    {
        CoreWorldEffectRuntime.ResetWorld();
    }
}
