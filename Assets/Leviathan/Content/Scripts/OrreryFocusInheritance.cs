using StarVortex;
using System.Collections.Generic;

/// <summary>
/// Copies an elemental focus weapon's loot state onto Orrery's hidden native spell
/// adapter. The donor's direct damage family is deliberately excluded because
/// OrrerySpellPower owns spell damage; everything else is inherited mechanically.
///
/// This is intentionally Orrery-specific. It is not a generic equipment transform
/// layer and does not attempt to decide whether an inherited stat is "useful" for
/// a particular spell. Unsupported/inapplicable stats simply have no effect unless
/// the target native attack family consumes them.
/// </summary>
public static class OrreryFocusInheritance
{
    public static void Apply(Activatable donor, Activatable adapter)
    {
        if (donor == null || adapter == null)
            return;

        adapter.modifiers = CloneNonDamageModifiers(donor.modifiers);
        adapter.customizers = MergeCustomizers(adapter.customizers, donor.customizers);

        // Customizers are authored as one-time mutations against an Equippable.
        // Apply inherited donor customizers to the spell adapter, excluding only
        // direct base-damage rewriting. Spell-specific normalization/tuning runs
        // after this and therefore remains authoritative over the final attack.
        Customizer[] donorCustomizers = donor.customizers;
        if (donorCustomizers != null)
        {
            for (int i = 0; i < donorCustomizers.Length; i++)
            {
                Customizer customizer = donorCustomizers[i];
                if (customizer == null || IsDirectDamageCustomizer(customizer.type))
                    continue;

                new Customizer(customizer.type, customizer.value).Apply(adapter);
            }
        }

        adapter.ModifiersChanged();
    }

    private static Modifier[] CloneNonDamageModifiers(Modifier[] source)
    {
        if (source == null || source.Length == 0)
            return new Modifier[0];

        List<Modifier> inherited = new List<Modifier>(source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            Modifier modifier = source[i];
            if (modifier == null || IsDirectDamageModifier(modifier.type))
                continue;

            inherited.Add(modifier.Clone());
        }
        return inherited.ToArray();
    }

    private static Customizer[] MergeCustomizers(
        Customizer[] nativeCustomizers,
        Customizer[] donorCustomizers)
    {
        int nativeCount = nativeCustomizers == null ? 0 : nativeCustomizers.Length;
        int donorCount = donorCustomizers == null ? 0 : donorCustomizers.Length;
        if (donorCount == 0)
            return nativeCustomizers ?? new Customizer[0];

        List<Customizer> merged = new List<Customizer>(nativeCount + donorCount);
        for (int i = 0; i < nativeCount; i++)
        {
            Customizer customizer = nativeCustomizers[i];
            if (customizer != null)
                merged.Add(customizer);
        }

        for (int i = 0; i < donorCount; i++)
        {
            Customizer customizer = donorCustomizers[i];
            if (customizer == null || IsDirectDamageCustomizer(customizer.type))
                continue;

            merged.Add(new Customizer(customizer.type, customizer.value));
        }
        return merged.ToArray();
    }

    private static bool IsDirectDamageModifier(Modifier.Type type)
    {
        Modifier.Type[] damageTypes = Modifier.damageTypes;
        for (int i = 0; i < damageTypes.Length; i++)
        {
            if (damageTypes[i] == type)
                return true;
        }
        return false;
    }

    private static bool IsDirectDamageCustomizer(Customizer.Type type)
    {
        return type == Customizer.Type.AdjustDamage;
    }
}
