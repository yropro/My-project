using System;
using System.Collections.Generic;
using HarmonyLib;
using StarVortex;
using UnityEngine;

/// <summary>Shared engine lifecycle for mod presentation. Register once at mod
/// initialization; registrations survive world teardown. Callbacks must only
/// present state, never apply damage or grants. Failure is isolated per entry.</summary>
public static class CoreNetworkPresentation
{
    private sealed class Entry
    {
        public string Name;
        public Action Publish;
        public Action<GameShip, float> Render;
        public Action<GameShip> Forget;
        public Action<GameShip> Died;
        public Action<float> Update;
        public Action Reset;
        public bool Warned;
    }
    private static readonly List<Entry> entries = new List<Entry>();

    public static void Register(string name, Action publish = null,
        Action<GameShip, float> render = null, Action<GameShip> forget = null,
        Action reset = null, Action<float> update = null, Action<GameShip> died = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Presentation name required.");
        foreach (Entry entry in entries)
            if (entry.Name == name) throw new InvalidOperationException("Duplicate presentation: " + name);
        entries.Add(new Entry { Name = name, Publish = publish, Render = render,
            Forget = forget, Reset = reset, Update = update, Died = died });
    }

    public static bool Publish()
    {
        foreach (Entry entry in entries)
        {
            try { entry.Publish?.Invoke(); }
            catch (Exception ex)
            {
                // A publisher may have written half a group. Discard this
                // sample before native serialization, never send partial state.
                CoreNetwork.ClearLocalSlots();
                Warn(entry, ex);
                return false; // Suppress the extension; native serialization continues.
            }
        }
        return true;
    }

    public static void Render(GameShip owner, float deltaTime)
    {
        if (owner == null || !owner.IsRemotePlayer()) return;
        foreach (Entry entry in entries)
        {
            try { entry.Render?.Invoke(owner, deltaTime); }
            catch (Exception ex) { Warn(entry, ex); }
        }
    }

    public static void Forget(GameShip owner)
    {
        if (ReferenceEquals(owner, null)) return;
        foreach (Entry entry in entries)
        {
            try { entry.Forget?.Invoke(owner); }
            catch (Exception ex) { Warn(entry, ex); }
        }
    }

    public static void Update(float deltaTime)
    {
        foreach (Entry entry in entries)
        {
            try { entry.Update?.Invoke(deltaTime); }
            catch (Exception ex) { Warn(entry, ex); }
        }
    }

    public static void Died(GameShip owner)
    {
        foreach (Entry entry in entries)
        {
            try { entry.Died?.Invoke(owner); }
            catch (Exception ex) { Warn(entry, ex); }
        }
    }

    public static void Reset()
    {
        foreach (Entry entry in entries)
        {
            try { entry.Reset?.Invoke(); }
            catch (Exception ex) { Warn(entry, ex); }
            entry.Warned = false;
        }
    }

    private static void Warn(Entry entry, Exception error)
    {
        if (entry.Warned) return;
        entry.Warned = true;
        Debug.LogWarning("[CoreNetwork] " + entry.Name + " presentation failed: " + error);
    }
}

[HarmonyPatch(typeof(RemoteShipDriver), "Render")]
public static class CoreNetworkPresentationRenderPatch
{
    public static void Postfix(RemoteShipDriver __instance)
    {
        CoreNetworkPresentation.Render(__instance == null ? null : __instance.gameShip, Time.deltaTime);
    }
}

[HarmonyPatch(typeof(GameShip), "OnDestroy")]
public static class CoreNetworkPresentationDestroyPatch
{
    public static void Prefix(GameShip __instance) { CoreNetworkPresentation.Forget(__instance); }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class CoreNetworkPresentationWorldPatch
{
    public static void Prefix() { CoreNetworkPresentation.Reset(); }
}

[HarmonyPatch(typeof(WorldController), "Update")]
public static class CoreNetworkPresentationUpdatePatch
{
    public static void Postfix() { CoreNetworkPresentation.Update(Time.deltaTime); }
}

[HarmonyPatch(typeof(GameShip), "Destroyed")]
public static class CoreNetworkPresentationDeathPatch
{
    public static void Prefix(GameShip __instance) { CoreNetworkPresentation.Died(__instance); }
}
