using HarmonyLib;
using StarVortex;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Profile = OrrerySpellCompendium.ArcResonance.Profile;
using Snapshot = OrreryArcResonance.PresentationSnapshot;

/// <summary>
/// Arc Resonance's presentation adapter. One static typed channel, sampled only
/// by the Orrery send pass. The owner publishes real strikes; this code NEVER
/// advances the owner scheduler or routes damage. Missing optional groups do not
/// cancel a cast, clear the strike ledger, or restart a sound.
/// </summary>
public static class OrreryArcResonancePresentation
{
    // One 19-byte payload / one physical bank record. Codec 7 belongs to the
    // parallel Accretion Disk work; Arc owns codec 8. Generation is in the bank
    // leader. Bytes: recipe, strike, active, uint target, Vector2 end, float age.
    internal struct WireState
    {
        public byte RecipeSize, StrikeIndex;
        public bool Active;
        public uint TargetNetId;
        public Vector2 TargetPosition;
        public float StrikeAgeSeconds;
    }

    private const int MaximumOwners = 32;
    private const float MaximumReplayAgeSeconds = 1f;
    private const float ResolveRetrySeconds = 0.1f;
    private static readonly OrreryNetwork.Channel<WireState> channel =
        new OrreryNetwork.Channel<WireState>(OrreryPresentationNetwork.CodecArcResonance, Wire);
    private static readonly FieldInfo bridgeField = AccessTools.Field(typeof(NetSession), "activeBridge");
    private static readonly Dictionary<GameShip, View> views = new Dictionary<GameShip, View>(4);
    private static readonly List<GameShip> deadOwners = new List<GameShip>(MaximumOwners);
    private static GameShip previousLocalOwner;

    private sealed class View
    {
        public uint Generation, TargetNetId;
        public byte StrikeIndex, RecipeSize;
        public bool Initialized, Ended, TargetBound, TargetLost, Visible;
        public GameShip Target;
        public CoreCombat.CombatEntityKey TargetKey;
        public Vector2 End;
        public float VisibleUntil, NextResolveAt, LeaseUntil;
        public Profile Profile;
        public readonly OrreryArcResonanceVisual Bolt = new OrreryArcResonanceVisual();
    }

    internal static void Wire(ref CoreWire wire, ref WireState state)
    {
        wire.Byte(ref state.RecipeSize);
        wire.Byte(ref state.StrikeIndex);
        wire.Flags(ref state.Active);
        wire.UInt32(ref state.TargetNetId);
        wire.Position(ref state.TargetPosition);
        wire.Float(ref state.StrikeAgeSeconds);
        if (!wire.Ok || (state.RecipeSize != 2 && state.RecipeSize != 3) ||
            state.StrikeIndex > OrreryLightningRodState<GameShip>.MaximumStrikeCount ||
            !OrreryNetwork.IsFinite(state.StrikeAgeSeconds) || state.StrikeAgeSeconds < 0f ||
            state.StrikeAgeSeconds > OrreryLightningRodState<GameShip>.MaximumDurationSeconds +
                OrreryArcResonance.MaximumBoltLifetimeSeconds)
            wire.Fail();
    }

    public static void Publish(GameShip owner)
    {
        Snapshot snapshot;
        if (!OrreryArcResonance.TryGetPresentation(owner, out snapshot)) return;
        WireState state = Convert(snapshot);
        channel.Publish(snapshot.Generation, ref state);
    }

    private static WireState Convert(Snapshot s)
    {
        return new WireState { RecipeSize = s.RecipeSize, StrikeIndex = s.StrikeIndex,
            Active = s.Active, TargetNetId = s.TargetNetId, TargetPosition = s.TargetPosition,
            StrikeAgeSeconds = s.StrikeAgeSeconds };
    }

    public static void Render(GameShip owner, float deltaTime)
    {
        if (owner == null || owner.health <= 0f || IsLocal(owner)) return;
        WireState state = default(WireState);
        uint generation;
        if (channel.TryRead(owner, ref state, out generation)) Apply(owner, generation, state, false);
        // No else/Forget: optional-group omission is not a gameplay transition.
        View view;
        if (views.TryGetValue(owner, out view)) Draw(owner, view, false);
    }

    public static void Update(float deltaTime)
    {
        CoreOwnerContext context = CoreClassRuntime.CurrentContext;
        GameShip owner = context != null && context.IsValid && context.ClassId == CoreClassId.Orrery
            ? context.Ship : null;
        if (!object.ReferenceEquals(previousLocalOwner, owner))
        {
            if (!object.ReferenceEquals(previousLocalOwner, null)) Forget(previousLocalOwner);
            previousLocalOwner = owner;
        }
        Snapshot snapshot;
        if (owner != null && OrreryArcResonance.TryGetPresentation(owner, out snapshot))
            Apply(owner, snapshot.Generation, Convert(snapshot), true);
        deadOwners.Clear();
        foreach (KeyValuePair<GameShip, View> entry in views)
        {
            if (entry.Key == null || entry.Key.health <= 0f)
                deadOwners.Add(entry.Key);
            else Draw(entry.Key, entry.Value, object.ReferenceEquals(entry.Key, owner));
        }
        for (int i = 0; i < deadOwners.Count; i++) Forget(deadOwners[i]);
        deadOwners.Clear();
    }

    private static bool IsLocal(GameShip owner)
    {
        CoreOwnerContext context = CoreClassRuntime.CurrentContext;
        return context != null && context.IsValid && object.ReferenceEquals(context.Ship, owner);
    }

    private static void Apply(GameShip owner, uint generation, WireState s, bool local)
    {
        if (generation == 0u) return;
        View v;
        if (!views.TryGetValue(owner, out v))
        {
            if (views.Count >= MaximumOwners) return;
            v = new View();
            views.Add(owner, v);
        }
        if (v.Initialized && generation != v.Generation && unchecked((int)(generation - v.Generation)) <= 0)
            return;
        if (!v.Initialized || generation != v.Generation)
        {
            v.Bolt.Hide();
            v.Initialized = true;
            v.Generation = generation;
            v.StrikeIndex = 0;
            v.RecipeSize = s.RecipeSize;
            v.TargetNetId = s.TargetNetId;
            v.Target = null;
            v.TargetKey = default(CoreCombat.CombatEntityKey);
            v.TargetBound = v.TargetLost = v.Ended = v.Visible = false;
            v.NextResolveAt = 0f;
            v.LeaseUntil = Time.time + (float)OrreryLightningRodState<GameShip>.MaximumDurationSeconds +
                OrreryArcResonance.MaximumBoltLifetimeSeconds;
            v.Profile = OrreryArcResonance.GetProfile(s.RecipeSize);
        }
        if (v.RecipeSize != s.RecipeSize || v.TargetNetId != s.TargetNetId ||
            s.StrikeIndex < v.StrikeIndex || (v.Ended && s.Active)) return;
        if (Time.time >= v.LeaseUntil) return;
        v.End = s.TargetPosition;
        v.Ended |= !s.Active;
        if (!local && s.Active) ResolveTarget(v);
        if (s.StrikeIndex == v.StrikeIndex) return;

        // Store identity BEFORE rendering/audio. A failed effect does not get
        // replayed every packet. Lost intermediate strikes are not backfilled.
        v.StrikeIndex = s.StrikeIndex;
        float remaining = Mathf.Max(0f, OrreryArcResonance.BoltLifetime(v.Profile) - s.StrikeAgeSeconds);
        v.VisibleUntil = Time.time + remaining;
        v.Visible = remaining > 0f;
        if (v.Visible) v.Bolt.Show(owner, v.Profile);
        else v.Bolt.Hide();
        if (s.StrikeAgeSeconds <= MaximumReplayAgeSeconds)
        {
            Vector2 position = Vector2.Lerp(OrreryArcResonance.CasterPoint(owner, v.Profile), v.End,
                Mathf.Clamp01(FiniteOr(v.Profile.ThunderTargetPositionBlend, 0f)));
            CoreAudioRuntime.PlayPositionalOneShot(OrrerySpellCompendium.ArcResonance.ThunderClipName,
                position, v.Profile.ThunderVolume, "Orrery Arc Resonance Thunder", true,
                v.Profile.ThunderSpatialBlend, AudioDistance(v.Profile.ThunderMinDistanceMeters),
                AudioDistance(v.Profile.ThunderMaxDistanceMeters),
                v.Profile.ThunderPlaybackDurationSeconds, v.Profile.ThunderFadeOutStartSeconds);
        }
    }

    private static void Draw(GameShip owner, View v, bool local)
    {
        if (Time.time >= v.LeaseUntil)
        {
            v.Ended = true;
            v.Visible = false;
            v.Bolt.Hide();
        }
        if (!v.Visible)
        {
            if (v.Ended) { v.TargetLost = true; v.Target = null; }
            return;
        }
        float remaining = v.VisibleUntil - Time.time;
        if (remaining <= 0f)
        {
            v.Visible = false;
            v.Bolt.Hide();
            if (v.Ended) { v.TargetLost = true; v.Target = null; }
            return;
        }
        if (!local)
        {
            ResolveTarget(v);
            CoreCombat.CombatEntityKey key;
            if (v.TargetBound && !v.TargetLost)
            {
                if (v.Target == null || v.Target.health <= 0f || v.Target.netId != v.TargetNetId ||
                    !CoreCombat.TryGetEntityKey(v.Target, out key) || !key.Equals(v.TargetKey))
                { v.TargetLost = true; v.Target = null; }
                else v.End = OrreryArcResonance.TargetPoint(v.Target, v.Profile);
            }
        }
        float fade = Mathf.Max(0f, FiniteOr(v.Profile.BoltFadeOutSeconds, 0f));
        v.Bolt.Draw(OrreryArcResonance.CasterPoint(owner, v.Profile), v.End, v.Profile,
            fade > 0f ? Mathf.Clamp01(remaining / fade) : 1f);
    }

    private static void ResolveTarget(View v)
    {
        if (v.TargetBound || v.TargetLost || v.Ended || Time.time >= v.LeaseUntil ||
            v.TargetNetId == 0u || Time.time < v.NextResolveAt) return;
        v.NextResolveAt = Time.time + ResolveRetrySeconds;
        NetWorldBridge bridge = NetSession.instance == null || bridgeField == null ? null :
            bridgeField.GetValue(NetSession.instance) as NetWorldBridge;
        if (bridge == null) return;
        GameShip target = bridge.ResolveNetTarget(v.TargetNetId) as GameShip;
        CoreCombat.CombatEntityKey key;
        if (target == null || target.health <= 0f || !CoreCombat.TryGetEntityKey(target, out key) || !key.IsValid)
            return;
        v.Target = target;
        v.TargetKey = key;
        v.TargetBound = true;
    }

    public static void ForgetShip(GameShip ship)
    {
        if (object.ReferenceEquals(ship, null)) return;
        foreach (View view in views.Values)
        {
            if (object.ReferenceEquals(view.Target, ship) || (view.TargetNetId != 0u && view.TargetNetId == ship.netId))
            { view.TargetLost = true; view.Target = null; }
        }
        Forget(ship);
    }

    private static void Forget(GameShip owner)
    {
        if (object.ReferenceEquals(owner, null)) return;
        View v;
        if (views.TryGetValue(owner, out v)) v.Bolt.Dispose();
        views.Remove(owner);
    }

    public static void Reset()
    {
        foreach (View view in views.Values) view.Bolt.Dispose();
        views.Clear();
        deadOwners.Clear();
        previousLocalOwner = null;
        OrreryArcResonanceVisual.ResetAssetCache();
    }

    private static float AudioDistance(float meters)
    { return meters < 0f ? CoreAudioRuntime.UseNativeDistance : OrreryUnits.MetersToWorld(meters); }
    private static float FiniteOr(float value, float fallback)
    { return OrreryNetwork.IsFinite(value) ? value : fallback; }
}
