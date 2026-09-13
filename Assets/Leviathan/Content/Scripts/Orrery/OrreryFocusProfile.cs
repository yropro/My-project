using StarVortex;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Shared resolved elemental-focus contract for Orrery spells.
///
/// The equipped matching weapon contributes its own baseline crit/status behavior
/// plus compatible item rolls. Raw donor damage is never inherited. When no
/// matching focus exists, this profile materializes Orrery's explicit fallback
/// combat baselines instead of borrowing stats from a hidden implementation item.
/// </summary>
public static class OrreryFocusProfile
{
    public struct Resolved
    {
        public OrreryFocusResolver.Focus Focus;
        public Activatable Donor;

        public float CritChance;
        public float CritModifier;
        public float StatusChance;

        // Compatible donor roll translations. Percentage values are represented
        // as decimal bonuses: 0.15f == +15%.
        public float SpellDamageBonus;
        public float RangeBonus;
        public float ChainRangeBonus;
        public float ChainDamageBonus;
        public float ProjectileVelocityBonus;
        public float DurationBonus;
        public int AdditionalProjectileOrChainCount;
        public bool BypassDamageLimit;

        public bool IsValid
        {
            get { return Focus.IsValid; }
        }

        public bool HasFocus
        {
            get { return Focus.HasFocus && Donor != null; }
        }

        public int EffectiveItemLevel
        {
            get { return Focus.EffectiveItemLevel; }
        }

        public float GetReferenceDps(
            OrrerySpellPower.ReferenceMode mode = OrrerySpellPower.ReferenceMode.Mean)
        {
            return IsValid
                ? OrrerySpellPower.GetReferenceDps(EffectiveItemLevel, mode)
                : 0f;
        }

        public float ApplySpellDamageBonus(float value)
        {
            return value * Mathf.Max(0f, 1f + SpellDamageBonus);
        }

        public float ApplyRangeBonus(float value)
        {
            return value * Mathf.Max(0f, 1f + RangeBonus);
        }

        public float ApplyChainRangeBonus(float value)
        {
            return value * Mathf.Max(0f, 1f + ChainRangeBonus);
        }

        public float ApplyChainDamageBonus(float value)
        {
            return value * Mathf.Max(0f, 1f + ChainDamageBonus);
        }

        public float ApplyProjectileVelocityBonus(float value)
        {
            return value * Mathf.Max(0f, 1f + ProjectileVelocityBonus);
        }

        public float ApplyDurationBonus(float value)
        {
            return value * Mathf.Max(0f, 1f + DurationBonus);
        }
    }

    private sealed class StatAccessors
    {
        public PropertyInfo LocalCritChance;
        public PropertyInfo LocalCritModifier;
        public PropertyInfo LocalStatusEffectChance;
    }

    private static readonly Dictionary<Type, StatAccessors> accessorsByType =
        new Dictionary<Type, StatAccessors>();

    public static bool TryResolve(
        GameShip owner,
        OrreryElement element,
        out Resolved resolved)
    {
        resolved = default(Resolved);

        OrreryFocusResolver.Focus focus;
        if (!OrreryFocusResolver.TryResolve(owner, element, out focus) ||
            !focus.IsValid)
        {
            return false;
        }

        resolved.Focus = focus;
        resolved.Donor = focus.Source;

        if (!focus.HasFocus || focus.Source == null)
        {
            resolved.CritChance = OrrerySpellPower.UnfocusedCritChance;
            resolved.CritModifier = OrrerySpellPower.UnfocusedCritModifier;
            resolved.StatusChance = OrrerySpellPower.UnfocusedStatusEffectChance;
            return true;
        }

        Activatable donor = focus.Source;
        StatAccessors accessors = GetAccessors(donor.GetType());

        float value;
        resolved.CritChance = TryReadFloat(
            accessors.LocalCritChance,
            donor,
            out value)
                ? Mathf.Clamp01(value)
                : OrrerySpellPower.UnfocusedCritChance;
        resolved.CritModifier = TryReadFloat(
            accessors.LocalCritModifier,
            donor,
            out value)
                ? Mathf.Max(0f, value)
                : OrrerySpellPower.UnfocusedCritModifier;
        resolved.StatusChance = TryReadFloat(
            accessors.LocalStatusEffectChance,
            donor,
            out value)
                ? Mathf.Clamp01(value)
                : OrrerySpellPower.UnfocusedStatusEffectChance;

        ReadCompatibleModifierRolls(donor, ref resolved);
        resolved.BypassDamageLimit =
            donor.HasCustomizer(Customizer.Type.BypassDamageLimit);
        return true;
    }

    private static StatAccessors GetAccessors(Type type)
    {
        StatAccessors accessors;
        if (accessorsByType.TryGetValue(type, out accessors))
            return accessors;

        accessors = new StatAccessors();
        accessors.LocalCritChance = FindFloatProperty(type, "LocalCritChance");
        accessors.LocalCritModifier = FindFloatProperty(type, "LocalCritModifier");
        accessors.LocalStatusEffectChance =
            FindFloatProperty(type, "LocalStatusEffectChance");
        accessorsByType[type] = accessors;
        return accessors;
    }

    private static PropertyInfo FindFloatProperty(Type type, string name)
    {
        Type current = type;
        while (current != null)
        {
            PropertyInfo property = current.GetProperty(
                name,
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);
            if (property != null && property.PropertyType == typeof(float) &&
                property.GetIndexParameters().Length == 0)
            {
                return property;
            }
            current = current.BaseType;
        }
        return null;
    }

    private static bool TryReadFloat(
        PropertyInfo property,
        object instance,
        out float value)
    {
        value = 0f;
        if (property == null || instance == null)
            return false;

        try
        {
            object raw = property.GetValue(instance, null);
            if (!(raw is float))
                return false;
            value = (float)raw;
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                "[Orrery] Could not read focus stat " + property.Name + ": " + ex);
            return false;
        }
    }

    private static void ReadCompatibleModifierRolls(
        Activatable donor,
        ref Resolved resolved)
    {
        Modifier[] modifiers = donor == null ? null : donor.modifiers;
        if (modifiers == null)
            return;

        int countBonus = 0;
        for (int i = 0; i < modifiers.Length; i++)
        {
            Modifier modifier = modifiers[i];
            if (modifier == null)
                continue;

            float value = modifier.GetValue();
            switch (modifier.type)
            {
                // Star Vortex exposes launcher ROF rolls as -Reload Time. Orrery
                // transfers the displayed roll percentage directly to spell
                // damage rather than recalculating the reciprocal cadence change.
                case Modifier.Type.ReloadTime:
                case Modifier.Type.BeamTickRate:
                    resolved.SpellDamageBonus += value;
                    break;

                case Modifier.Type.MaxRange:
                    resolved.RangeBonus += value;
                    break;

                // Orrery standard: Extra Shots and Extra Chains are one shared
                // transferable count bonus. A spell that supports either supports
                // both at 1:1 value.
                case Modifier.Type.ShotCount:
                case Modifier.Type.ChainCount:
                    countBonus += Mathf.RoundToInt(value);
                    break;

                case Modifier.Type.ChainRange:
                    resolved.ChainRangeBonus += value;
                    break;

                case Modifier.Type.ChainDamage:
                    resolved.ChainDamageBonus += value;
                    break;

                case Modifier.Type.Velocity:
                    resolved.ProjectileVelocityBonus += value;
                    break;

                case Modifier.Type.EffectDuration:
                    resolved.DurationBonus += value;
                    break;
            }
        }

        resolved.AdditionalProjectileOrChainCount = Mathf.Max(0, countBonus);
    }
}
