using StarVortex;
using System;
using System.Collections.Generic;

public enum OrrerySpellExecutionKind : byte
{
    Custom = 0,
    DiscreteProjectile = 1,
    ContinuousBeam = 2,
    RapidProjectileStream = 3
}

public delegate bool OrrerySpellExecutor(
    GameShip owner,
    OrreryCastInvocation invocation,
    OrrerySpellRegistry.SpellDefinition spell);

/// <summary>
/// Small data-driven recipe registry. It maps canonical unordered compositions
/// to stable spell definitions without trying to become a universal effect graph.
/// Weird spells remain ordinary explicit C# executors.
/// </summary>
public static class OrrerySpellRegistry
{
    public sealed class SpellDefinition
    {
        public readonly ushort Id;
        public readonly string Name;
        public readonly OrreryRecipeKey Recipe;
        public readonly OrrerySpellExecutionKind ExecutionKind;
        public readonly byte CombatEffectId;
        public readonly OrrerySpellExecutor Executor;

        public bool Implemented { get { return Executor != null; } }

        public SpellDefinition(
            ushort id,
            string name,
            OrreryRecipeKey recipe,
            OrrerySpellExecutionKind executionKind,
            byte combatEffectId,
            OrrerySpellExecutor executor)
        {
            if (id == 0)
                throw new ArgumentException("Spell id 0 is reserved.", "id");
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("Spell name is required.", "name");
            if (!recipe.IsValid)
                throw new ArgumentException("Spell recipe is required.", "recipe");

            Id = id;
            Name = name;
            Recipe = recipe;
            ExecutionKind = executionKind;
            CombatEffectId = combatEffectId;
            Executor = executor;
        }
    }

    private static readonly Dictionary<OrreryRecipeKey, SpellDefinition> byRecipe =
        new Dictionary<OrreryRecipeKey, SpellDefinition>();
    private static readonly Dictionary<ushort, SpellDefinition> byId =
        new Dictionary<ushort, SpellDefinition>();
    private static bool defaultsRegistered;

    public static int Revision { get; private set; }

    public static void RegisterDefaults()
    {
        if (defaultsRegistered)
            return;

        // Baseline Orrery formulas use two satellites. Each formula keeps one
        // stable spell id; mechanically unusual spells remain explicit executors.
        Register(new SpellDefinition(
            1,
            "Magma Cannon",
            OrreryRecipeKey.Pure(OrreryElement.Fire, 2),
            OrrerySpellExecutionKind.DiscreteProjectile,
            OrreryCombat.EffectIds.MagmaCannon,
            OrrerySpellRuntime.ExecuteMagma));

        Register(new SpellDefinition(
            2,
            "Tesla Coil",
            OrreryRecipeKey.Pure(OrreryElement.Lightning, 2),
            OrrerySpellExecutionKind.ContinuousBeam,
            OrreryCombat.EffectIds.TeslaCoil,
            OrrerySpellRuntime.ExecuteTesla));

        Register(new SpellDefinition(
            3,
            "Cone of Cold",
            OrreryRecipeKey.Pure(OrreryElement.Ice, 2),
            OrrerySpellExecutionKind.Custom,
            OrreryCombat.EffectIds.CryoGun,
            OrrerySpellRuntime.ExecuteCryo));

        Register(new SpellDefinition(
            OrrerySpellCompendium.ColdFusion.Id,
            OrrerySpellCompendium.ColdFusion.Name,
            OrrerySpellCompendium.ColdFusion.Recipe,
            OrrerySpellExecutionKind.Custom,
            0,
            OrreryColdFusion.Execute));

        Register(new SpellDefinition(
            OrrerySpellCompendium.PlasmaBolt.Id,
            OrrerySpellCompendium.PlasmaBolt.Name,
            OrrerySpellCompendium.PlasmaBolt.Recipe,
            OrrerySpellExecutionKind.Custom,
            OrreryCombat.EffectIds.PlasmaBolt,
            OrreryPlasmaBolt.Execute));

        Register(new SpellDefinition(
            OrrerySpellCompendium.Shatterbolt.Id,
            OrrerySpellCompendium.Shatterbolt.Name,
            OrrerySpellCompendium.Shatterbolt.Recipe,
            OrrerySpellExecutionKind.Custom,
            OrreryCombat.EffectIds.Shatterbolt,
            OrreryShatterbolt.Execute));

        defaultsRegistered = true;
    }

    public static void Register(SpellDefinition definition)
    {
        if (definition == null)
            throw new ArgumentNullException("definition");

        SpellDefinition existingRecipe;
        if (byRecipe.TryGetValue(definition.Recipe, out existingRecipe) &&
            existingRecipe != null && existingRecipe.Id != definition.Id)
        {
            throw new InvalidOperationException(
                "Orrery recipe is already owned by spell " + existingRecipe.Id +
                " (" + existingRecipe.Name + ").");
        }

        SpellDefinition existingId;
        if (byId.TryGetValue(definition.Id, out existingId) && existingId != null &&
            !existingId.Recipe.Equals(definition.Recipe))
        {
            throw new InvalidOperationException(
                "Orrery spell id " + definition.Id + " is already assigned to " +
                existingId.Name + ".");
        }

        byRecipe[definition.Recipe] = definition;
        byId[definition.Id] = definition;
        Revision++;
    }

    public static bool TryResolve(
        OrreryRecipeKey recipe,
        out SpellDefinition definition)
    {
        RegisterDefaults();
        return byRecipe.TryGetValue(recipe, out definition) && definition != null;
    }

    public static bool IsKnownRecipe(OrreryRecipeKey recipe)
    {
        SpellDefinition definition;
        return TryResolve(recipe, out definition);
    }

    public static bool TryGet(ushort id, out SpellDefinition definition)
    {
        RegisterDefaults();
        return byId.TryGetValue(id, out definition) && definition != null;
    }

    public static bool TryExecute(GameShip owner, OrreryCastInvocation invocation)
    {
        SpellDefinition definition;
        if (!TryResolve(invocation.Recipe, out definition) ||
            definition == null || definition.Executor == null)
        {
            return false;
        }

        if (invocation.Execution == null || !invocation.Execution.IsValid ||
            !ReferenceEquals(invocation.Execution.Owner.Ship, owner)) return false;
        return invocation.Execution.TryCommit(() => definition.Executor(owner, invocation, definition));
    }

    public static CoreCombat.SemanticKey GetCombatSemantic(
        SpellDefinition definition)
    {
        return definition == null || definition.CombatEffectId == 0
            ? default(CoreCombat.SemanticKey)
            : OrreryCombat.Spell(definition.CombatEffectId);
    }

    private static OrreryRecipeKey Pair(OrreryElement first, OrreryElement second)
    {
        return default(OrreryRecipeKey).Add(first).Add(second);
    }
}
