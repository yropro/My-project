using StarVortex;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Bounded native spell execution for Orrery V0.
///
/// Each owner keeps at most one hidden native reference weapon per implemented
/// pure-element spell and at most one active invocation. The reference weapons
/// preserve Star Vortex projectile/beam/status/network behavior; Orrery only
/// normalizes their local expected output against OrrerySpellPower.
/// </summary>
public static class OrrerySpellRuntime
{
    public static class Tuning
    {
        public const float MagmaIntegratedReferenceSeconds = 1f;
        public const float MagmaDamageMultiplier = 1f;
        public const float CryoOutputSeconds = 1f;
        public const float CryoDpsMultiplier = 1f;
        public const float TeslaOutputSeconds = 1f;
        public const float TeslaDpsMultiplier = 1f;
    }

    private const string MagmaGunPath = "Base/Items/PrimaryWeapon/Magma Gun";
    private const string CryoGunPath = "Base/Items/PrimaryWeapon/Cryo Gun";
    private const string TeslaCoilPath = "Base/Items/PrimaryWeapon/Tesla Coil";

    private sealed class VirtualWeapon
    {
        public Activatable Weapon;
        public Activatable FocusSource;
        public int FocusSlotIndex;
        public int EffectiveItemLevel;
        public OrreryElement Element;
    }

    private sealed class ActiveCast
    {
        public OrreryCastInvocation Invocation;
        public VirtualWeapon VirtualWeapon;
        public float RemainingSeconds;
        public bool CompleteAfterFirstFixedTick;
    }

    private sealed class OwnerState
    {
        public VirtualWeapon Magma;
        public VirtualWeapon Cryo;
        public VirtualWeapon Tesla;
        public ActiveCast Active;
    }

    private static readonly Dictionary<GameShip, OwnerState> owners =
        new Dictionary<GameShip, OwnerState>(4);

    public static bool ExecuteMagma(
        GameShip owner,
        OrreryCastInvocation invocation,
        OrrerySpellRegistry.SpellDefinition spell)
    {
        return TryStart(
            owner,
            invocation,
            OrreryElement.Fire,
            spell,
            MagmaGunPath,
            true,
            0f);
    }

    public static bool ExecuteCryo(
        GameShip owner,
        OrreryCastInvocation invocation,
        OrrerySpellRegistry.SpellDefinition spell)
    {
        return TryStart(
            owner,
            invocation,
            OrreryElement.Ice,
            spell,
            CryoGunPath,
            false,
            Tuning.CryoOutputSeconds);
    }

    public static bool ExecuteTesla(
        GameShip owner,
        OrreryCastInvocation invocation,
        OrrerySpellRegistry.SpellDefinition spell)
    {
        return TryStart(
            owner,
            invocation,
            OrreryElement.Lightning,
            spell,
            TeslaCoilPath,
            false,
            Tuning.TeslaOutputSeconds);
    }

    public static void FixedTick(GameShip owner, float deltaTime)
    {
        OwnerState state;
        if (owner == null || !owners.TryGetValue(owner, out state) || state == null)
            return;

        if (!OrreryRuntime.IsActive(owner))
        {
            Forget(owner);
            return;
        }

        ActiveCast active = state.Active;
        if (active != null && active.VirtualWeapon != null)
            AimFromFocus(owner, active.VirtualWeapon);

        TickWeapon(state.Magma);
        TickWeapon(state.Cryo);
        TickWeapon(state.Tesla);

        active = state.Active;
        if (active == null)
            return;

        if (active.Invocation.Execution == null ||
            !active.Invocation.Execution.IsValid)
        {
            Deactivate(active.VirtualWeapon);
            state.Active = null;
            return;
        }

        if (active.CompleteAfterFirstFixedTick)
        {
            Complete(owner, state, active);
            return;
        }

        active.RemainingSeconds -= Mathf.Max(0f, deltaTime);
        if (active.RemainingSeconds <= 0f)
            Complete(owner, state, active);
    }

    public static void LateTick(GameShip owner)
    {
        OwnerState state;
        if (owner == null || !owners.TryGetValue(owner, out state) || state == null)
            return;

        BeamWeapon beam = state.Tesla == null
            ? null
            : state.Tesla.Weapon as BeamWeapon;
        if (beam != null)
            beam.LateUpdate();
    }

    public static void Forget(GameShip owner)
    {
        if (owner == null)
            return;

        OwnerState state;
        if (!owners.TryGetValue(owner, out state) || state == null)
            return;

        if (state.Active != null)
            Deactivate(state.Active.VirtualWeapon);

        Dispose(state.Magma);
        Dispose(state.Cryo);
        Dispose(state.Tesla);
        owners.Remove(owner);
    }

    public static void Reset()
    {
        GameShip[] keys = new GameShip[owners.Count];
        owners.Keys.CopyTo(keys, 0);
        for (int i = 0; i < keys.Length; i++)
            Forget(keys[i]);
        owners.Clear();
    }

    private static bool TryStart(
        GameShip owner,
        OrreryCastInvocation invocation,
        OrreryElement element,
        OrrerySpellRegistry.SpellDefinition spell,
        string resourcePath,
        bool instant,
        float outputSeconds)
    {
        if (owner == null || spell == null || invocation.Execution == null ||
            !invocation.Execution.IsValid || !OrreryRuntime.IsActive(owner))
        {
            return false;
        }

        OrreryFocusResolver.Focus focus;
        if (!OrreryFocusResolver.TryResolve(owner, element, out focus) ||
            !focus.IsValid)
        {
            Debug.LogWarning("[Orrery] " + spell.Name +
                " requires an equipped " + element + " damage focus.");
            return false;
        }

        OwnerState state;
        if (!owners.TryGetValue(owner, out state) || state == null)
        {
            state = new OwnerState();
            owners[owner] = state;
        }

        if (state.Active != null)
            return false;

        VirtualWeapon virtualWeapon;
        if (!TryEnsureVirtualWeapon(
                owner,
                state,
                spell.Id,
                element,
                focus,
                resourcePath,
                out virtualWeapon))
        {
            return false;
        }

        if (virtualWeapon.Weapon == null || !virtualWeapon.Weapon.CanActivate())
            return false;

        AimFromFocus(owner, virtualWeapon);
        virtualWeapon.Weapon.Activate();
        state.Active = new ActiveCast
        {
            Invocation = invocation,
            VirtualWeapon = virtualWeapon,
            RemainingSeconds = Mathf.Max(0f, outputSeconds),
            CompleteAfterFirstFixedTick = instant
        };
        return true;
    }

    private static bool TryEnsureVirtualWeapon(
        GameShip owner,
        OwnerState state,
        ushort spellId,
        OrreryElement element,
        OrreryFocusResolver.Focus focus,
        string resourcePath,
        out VirtualWeapon result)
    {
        result = GetVirtualWeapon(state, spellId);
        if (Matches(result, focus, element))
            return true;

        Dispose(result);
        result = null;

        ItemBase itemBase = Resources.Load<ItemBase>(resourcePath);
        if (itemBase == null)
        {
            Debug.LogError("[Orrery] Native spell reference not found: " + resourcePath);
            SetVirtualWeapon(state, spellId, null);
            return false;
        }

        Activatable weapon = itemBase.GetItem(Item.Rarity.Common, 1, 0) as Activatable;
        if (weapon == null)
        {
            Debug.LogError("[Orrery] Native spell reference is not Activatable: " + resourcePath);
            SetVirtualWeapon(state, spellId, null);
            return false;
        }

        weapon.heatPerSecond = 0f;
        weapon.SetFaction(owner.faction);
        weapon.Equip(owner, Placement.zero, focus.SlotIndex, false, false);
        weapon.ToggleVisbility(false);

        result = new VirtualWeapon
        {
            Weapon = weapon,
            FocusSource = focus.Source,
            FocusSlotIndex = focus.SlotIndex,
            EffectiveItemLevel = focus.EffectiveItemLevel,
            Element = element
        };

        if (!ConfigureOutput(spellId, result, focus))
        {
            Dispose(result);
            result = null;
            SetVirtualWeapon(state, spellId, null);
            return false;
        }

        // Populate native pools once per virtual weapon, never on the hot cast
        // path after the focus/version remains stable.
        weapon.PopulatePool();
        SetVirtualWeapon(state, spellId, result);
        return true;
    }

    private static bool ConfigureOutput(
        ushort spellId,
        VirtualWeapon virtualWeapon,
        OrreryFocusResolver.Focus focus)
    {
        float referenceDps = OrrerySpellPower.GetReferenceDps(
            focus.EffectiveItemLevel,
            OrrerySpellPower.ReferenceMode.Mean);

        if (spellId == 1)
        {
            Launcher launcher = virtualWeapon.Weapon as Launcher;
            if (launcher == null)
                return false;

            float expectedLocalHit = launcher.LocalDamage *
                Mathf.Max(1, launcher.LocalShotCount) *
                (1f + launcher.LocalCritChance * launcher.LocalCritModifier);
            if (expectedLocalHit <= 0f)
                return false;

            float targetIntegratedDamage = referenceDps *
                Tuning.MagmaIntegratedReferenceSeconds *
                Tuning.MagmaDamageMultiplier;
            launcher.ScaleDamage(targetIntegratedDamage / expectedLocalHit);
            return true;
        }

        if (spellId == 2)
        {
            BeamWeapon beam = virtualWeapon.Weapon as BeamWeapon;
            if (beam == null)
                return false;

            float localDps = beam.CalculateDPS(Activatable.Modified.Local);
            if (localDps <= 0f)
                return false;

            beam.ScaleDamage(referenceDps * Tuning.TeslaDpsMultiplier / localDps);
            return true;
        }

        if (spellId == 3)
        {
            ChargingLauncher cryo = virtualWeapon.Weapon as ChargingLauncher;
            if (cryo == null)
                return false;

            // Baseline Cryo is immediate full-output for its one-second stream;
            // retain native projectile cadence/status behavior but skip the
            // source weapon's normal rate-of-fire charge ramp.
            cryo.unchargedMulitplier = 1f;
            float localDps = cryo.CalculateDPS(Activatable.Modified.Local);
            if (localDps <= 0f)
                return false;

            cryo.ScaleDamage(referenceDps * Tuning.CryoDpsMultiplier / localDps);
            return true;
        }

        return false;
    }

    private static void Complete(
        GameShip owner,
        OwnerState state,
        ActiveCast active)
    {
        Deactivate(active.VirtualWeapon);
        state.Active = null;
        OrreryCasting.CompleteInvocation(owner, active.Invocation.Execution, 0);
        OrreryNetwork.PublishLocal(owner);
    }

    private static void TickWeapon(VirtualWeapon virtualWeapon)
    {
        if (virtualWeapon != null && virtualWeapon.Weapon != null)
            virtualWeapon.Weapon.FixedUpdate();
    }

    private static void AimFromFocus(GameShip owner, VirtualWeapon virtualWeapon)
    {
        if (owner == null || virtualWeapon == null || virtualWeapon.Weapon == null)
            return;

        Quaternion rotation = owner.transform.rotation;
        if (virtualWeapon.FocusSource != null &&
            virtualWeapon.FocusSource.gameObject != null)
        {
            rotation = virtualWeapon.FocusSource.gameObject.transform.rotation;
        }

        virtualWeapon.Weapon.AimAt(rotation);
    }

    private static void Deactivate(VirtualWeapon virtualWeapon)
    {
        if (virtualWeapon != null && virtualWeapon.Weapon != null)
            virtualWeapon.Weapon.Deactivate();
    }

    private static void Dispose(VirtualWeapon virtualWeapon)
    {
        if (virtualWeapon == null || virtualWeapon.Weapon == null)
            return;

        try
        {
            virtualWeapon.Weapon.Deactivate();
            virtualWeapon.Weapon.Unequip();
        }
        catch (System.Exception ex)
        {
            Debug.LogError("[Orrery] Failed to dispose virtual spell weapon: " + ex);
        }
        virtualWeapon.Weapon = null;
    }

    private static bool Matches(
        VirtualWeapon virtualWeapon,
        OrreryFocusResolver.Focus focus,
        OrreryElement element)
    {
        return virtualWeapon != null && virtualWeapon.Weapon != null &&
            virtualWeapon.Element == element &&
            object.ReferenceEquals(virtualWeapon.FocusSource, focus.Source) &&
            virtualWeapon.FocusSlotIndex == focus.SlotIndex &&
            virtualWeapon.EffectiveItemLevel == focus.EffectiveItemLevel;
    }

    private static VirtualWeapon GetVirtualWeapon(OwnerState state, ushort spellId)
    {
        if (spellId == 1) return state.Magma;
        if (spellId == 2) return state.Tesla;
        if (spellId == 3) return state.Cryo;
        return null;
    }

    private static void SetVirtualWeapon(
        OwnerState state,
        ushort spellId,
        VirtualWeapon virtualWeapon)
    {
        if (spellId == 1) state.Magma = virtualWeapon;
        else if (spellId == 2) state.Tesla = virtualWeapon;
        else if (spellId == 3) state.Cryo = virtualWeapon;
    }
}
