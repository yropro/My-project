# Orrery Spell Lifetime / Runtime Orchestration Agent

**Project:** Star Vortex — Orrery / Celestial Mage / Sphereweaver  
**Document purpose:** Implementation-grade design and migration handoff for centralizing Orrery spell lifetime orchestration without building a generic spell engine.  
**Target branch:** `skill-trees`  
**Source snapshot reviewed:** `2e84443e50c4c2116d3e4d3740b1e5a46aaaf55e` (`needs review`)  
**Primary live examples:** `OrreryController`, `OrrerySpellRuntime`, `OrreryShatterbolt`, `OrreryPlasmaBolt`  
**Status:** Design/implementation plan only. This document does not itself authorize unrelated rebalance, networking redesign, focus-system redesign, or spell-mechanics changes.

---

# 0. Executive Summary

The Orrery now has enough real persistent spell behavior to prove that spell lifetime orchestration should become a small shared class-level service.

This is **not** a proposal for a universal spell engine.

This is **not** a proposal to move Shatterbolt chaining, Frost Burst logic, Plasma Burn propagation, target selection, damage packets, focus mapping, networking codecs, or presentation state into one giant abstraction.

The problem is much narrower:

```text
Today:
    OrreryController owns one FixedUpdate
        +
    legacy OrrerySpellRuntime is ticked directly
        +
    Shatterbolt patches OrreryController.FixedUpdate again
        +
    Plasma Bolt patches OrreryController.FixedUpdate again
        +
    each explicit spell separately discovers owner changes
        +
    each explicit spell separately patches world teardown
        +
    each explicit spell separately patches ship destruction
        +
    OrreryController separately knows every spell's Forget method

Desired:
    one Orrery spell-lifetime integration boundary
        ↓
    one local-owner resolution path
        ↓
    one fixed-step dispatch path
        ↓
    one owner/class teardown path
        ↓
    one world teardown path
        ↓
    one ship-destruction notification path
        ↓
    explicit per-spell runtime methods still own all spell behavior
```

The first implementation should remain deliberately boring and explicit.

Recommended initial shape:

```text
OrrerySpellLifetime
    FixedTickLocal(dt)
    ForgetOwner(owner)
    NotifyShipDestroyed(ship)
    ResetWorld()

FixedTickLocal(dt)
    resolve current local Orrery owner once
    handle owner replacement once
    call OrreryShatterbolt.FixedTick(owner, dt)
    call OrreryPlasmaBolt.FixedTick(owner, dt)
    future explicit spell runtimes are added here

ForgetOwner(owner)
    call each runtime's existing idempotent Forget(owner)
    shared class-owned cleanup occurs once

ResetWorld()
    reset each runtime once
    reset shared Orrery runtime services once
```

The most important architectural rule is:

> **The lifetime layer owns when a spell runtime is ticked or torn down. The spell owns what happens while it is alive.**

That line is the boundary. Do not blur it.

---

# 1. Why This Exists Now

The Orrery proof-of-concept stage was correct to build a polished, difficult spell before extracting shared infrastructure.

Before Shatterbolt and Plasma Bolt existed, a centralized spell-lifetime design would have been speculative. We did not yet know what a real mixed-element Orrery spell would need to survive across frames.

We now have two strong examples with substantially different lifetime shapes.

## 1.1 Shatterbolt proves travel + child-effect + presentation-tail lifetime

Shatterbolt has all of the following:

```text
cast accepted
    ↓
traveling owner-authoritative orb
    ↓
possible target invalidation / reacquisition
    ↓
multiple impacts
    ↓
multiple independently expanding Frost Bursts
    ↓
cast can mechanically finish while bursts still exist
    ↓
presentation history intentionally remains briefly available
    ↓
final cleanup
```

Important consequence:

**“Cast complete” does not mean “spell runtime can be forgotten.”**

The invocation may have completed and satellites may already be shuffling while Frost Bursts and/or a short presentation tail still need to exist.

Shatterbolt currently handles this correctly inside its own state. The shared lifetime layer must preserve that behavior rather than trying to decide that the spell is over merely because `OrreryCasting` is no longer invoking.

## 1.2 Plasma Bolt proves post-cast semantic-tail lifetime

Plasma Bolt has a different shape:

```text
cast accepted
    ↓
instant wide stroke
    ↓
initial native/Core combat outcome may confirm later
    ↓
formula completion is deferred out of TryCommit
    ↓
Plasma Burn can live for 5 seconds
    ↓
Burn can spread recursively
    ↓
reinfection history can remain meaningful for 10 seconds
    ↓
presentation can outlive mechanical burn momentarily while VFX fades
```

Again:

**The cast itself is not the lifetime boundary.**

The initial bolt can be finished while the gameplay effect remains alive.

Plasma also proves that an explicit spell runtime may need to keep ticking because it is waiting for a meaningful CoreCombat outcome after the visual/direct cast action has already happened.

## 1.3 The duplication is now real, not hypothetical

Current live architecture contains several repeated responsibilities.

### `OrreryController`

`OrreryController.FixedUpdate()` currently:

1. synchronizes the local Orrery owner,
2. directly ticks the old monolithic `OrrerySpellRuntime`,
3. updates shuffle/orbit behavior.

`OrreryController.TearDownCurrentBuild()` currently explicitly knows about:

```text
OrreryShatterbolt.Forget(owner)
OrreryPlasmaBolt.Forget(owner)
OrrerySpellRuntime.Forget(owner)
```

That list will grow one line per spell unless the boundary changes.

### `OrreryShatterbolt`

Shatterbolt currently owns:

```text
private static Dictionary<GameShip, OwnerState> owners
private static GameShip lastTickOwner

TickLocal(dt)
FixedTick(owner, dt)
Forget(owner)
Reset()

Harmony patch: OrreryController.FixedUpdate postfix
Harmony patch: WorldController.OnDestroy
Harmony patch: GameShip.OnDestroy
```

### `OrreryPlasmaBolt`

Plasma Bolt currently owns a nearly parallel set:

```text
private static Dictionary<GameShip, OwnerState> owners
private static GameShip lastTickOwner

TickLocal(dt)
FixedTick(owner, dt)
Forget(owner)
Reset()
ForgetTarget(target)

Harmony patch: OrreryController.FixedUpdate postfix
Harmony patch: WorldController.OnDestroy
Harmony patch: GameShip.OnDestroy
Harmony patch: GameShip.Destroyed
```

The spell-specific mechanics are different. The global orchestration is not.

That is exactly the point where extraction becomes useful.

---

# 2. The Problem We Are Solving

The shared lifetime layer exists to eliminate **repeated orchestration**, not repeated mechanics.

The repeated orchestration problems are:

1. multiple Harmony patches targeting the same fixed-step boundary,
2. multiple copies of local-owner discovery,
3. multiple copies of `lastTickOwner` replacement detection,
4. multiple world-destruction hooks,
5. multiple ship-destruction hooks,
6. a growing hard-coded list of spell cleanup calls inside `OrreryController`,
7. shared-service cleanup being owned accidentally by individual spells,
8. increasingly unclear tick ordering between spell runtimes,
9. increased chance that one future spell forgets one teardown path,
10. increased chance of accidental double ticking during migration.

The goal is to make these responsibilities class-owned.

---

# 3. Non-Goals

These are explicit non-goals for this work.

Do **not** use this task as permission to redesign unrelated systems.

## 3.1 Do not build a universal effect graph

Do not create a system where spells are defined as generic nodes such as:

```text
ProjectileNode
ChainNode
ExplosionNode
DotNode
SpreadNode
PresentationNode
```

Shatterbolt and Plasma Bolt should remain ordinary explicit C# spell implementations.

Their weirdness is a feature.

## 3.2 Do not move spell-specific state into a giant common struct

Do not create:

```text
OrreryUniversalSpellState
    CurrentTarget
    SecondTarget
    ProjectilePosition
    ExplosionRadius
    BurnBudget
    InfectionCount
    ChainCount
    BeamWidth
    ...
```

That would become a union of every spell ever created.

Each spell remains responsible for its own runtime state.

## 3.3 Do not make lifetime own damage

The lifetime layer must not:

- calculate spell damage,
- roll crit,
- roll status,
- call `OrreryDamageRouter` on behalf of arbitrary spells,
- choose CoreCombat semantics,
- choose contributors,
- resolve elemental focus,
- apply tree bonuses,
- decide chain damage,
- decide DOT amounts.

Those remain spell responsibilities.

## 3.4 Do not make lifetime own targeting

The lifetime layer must not:

- perform overlap queries,
- choose enemies,
- reacquire targets,
- store hit history,
- resolve cursor aim,
- implement cone/circle/beam geometry.

## 3.5 Do not make lifetime own networking codecs

The lifetime layer should not become `OrreryNetwork`.

A spell may still call `OrreryNetwork.PublishLocal(owner)` when its own presentation state changes.

A future network aggregation pass can be designed separately.

## 3.6 Do not rebalance anything

No coefficient, duration, radius, speed, chain count, burn tick count, or visual duration should change because of this migration.

A lifecycle refactor is not a balance pass.

## 3.7 Do not change focus inheritance

`OrreryFocusProfile` and spell-specific focus mapping are outside the scope of this work.

## 3.8 Do not redesign CoreCombat

CoreCombat provenance/state/history remain separate shared infrastructure.

The lifetime layer is only an Orrery class runtime boundary.

---

# 4. Core Design Principle

The lifetime service should be understood as a **scheduler + teardown coordinator**.

It is not a mechanic engine.

The clean responsibility split is:

```text
OrreryCasting
    owns formula invocation transaction / execution validity

OrrerySpellRegistry
    owns recipe -> spell executor identity

OrreryFocusProfile
    owns focus/donor interpretation

OrrerySpellLifetime
    owns shared runtime orchestration
        - local owner resolution
        - fixed-step dispatch
        - centralized owner teardown
        - centralized world teardown
        - centralized ship-destruction notification

OrreryShatterbolt
    owns Shatterbolt behavior/state

OrreryPlasmaBolt
    owns Plasma Bolt/Plasma Burn behavior/state

OrreryNetwork
    owns replicated Orrery presentation transport

CoreCombat / CoreCombatState / CoreCombatHistory
    own shared combat meaning/history/state
```

One-sentence contract:

> **Lifetime decides when runtime code gets a chance to run and when it must be destroyed; spell code decides every gameplay consequence of that run.**

---

# 5. The Most Important Lifetime Distinction

A future implementation agent must not collapse all spell lifetime into one boolean called `Active`.

There are several distinct lifetimes.

## 5.1 Invocation lifetime

This is the lifetime of the cast transaction represented by `OrreryCastInvocation` / its execution token.

Example:

```text
RMB cast commits
    ↓
Execution is valid
    ↓
spell accepts cast
    ↓
spell eventually calls CompleteInvocation or Cancel
```

This lifetime is owned by `OrreryCasting` / Core ability execution semantics.

The shared lifetime service should not automatically complete it.

## 5.2 Primary mechanical lifetime

This is the main active behavior of the spell.

Examples:

- Shatterbolt orb is still traveling/chaining.
- Tesla is still channeling.
- Magma projectile is still traveling.

The invocation often remains relevant during this period.

## 5.3 Gameplay-tail lifetime

Gameplay may continue after the cast transaction itself is complete.

Examples:

- Shatterbolt Frost Bursts are still expanding after the orb finishes.
- Plasma Burn infections continue dealing damage/spreading after the bolt cast completes.
- a future gravity field may remain for 8 seconds after the casting gesture ends.
- a future delayed mine may wait for detonation after satellites have already rearmed.

This is the single most important reason the central driver must tick **runtime state**, not merely “the currently invoking spell.”

## 5.4 Presentation-tail lifetime

Presentation may intentionally remain after gameplay ends.

Examples:

- Shatterbolt keeps completed impact history available briefly so remote clients do not miss the final burst path.
- Plasma Burn status visual can fade after the active burn ends.
- a beam afterimage may fade for 0.25 seconds without gameplay.

Presentation tails must not accidentally prolong gameplay.

## 5.5 Semantic-history lifetime

Some meaning belongs in Core state/history rather than in the active spell runtime.

Example:

`PlasmaBurnRecent` exists to prevent reinfection for the defined lockout window.

The lifetime service should not try to duplicate that history in a generic runtime timer.

---

# 6. Recommended Initial Architecture

The initial implementation should favor explicit calls over a dynamic plugin framework.

Recommended file:

```text
Assets/Leviathan/Content/Scripts/OrrerySpellLifetime.cs
```

Recommended shape:

```csharp
public static class OrrerySpellLifetime
{
    private static GameShip lastLocalOwner;

    public static void FixedTickLocal(float deltaTime)
    {
        // Resolve local Orrery owner once.
        // Handle owner transition once.
        // Dispatch explicit spell runtimes once.
    }

    public static void ForgetOwner(GameShip owner)
    {
        // Explicitly forward teardown to known runtimes.
    }

    public static void NotifyShipDestroyed(GameShip ship)
    {
        // Tell runtimes that care about destroyed targets.
        // Then forget this ship if it was an Orrery owner.
    }

    public static void ResetWorld()
    {
        // Reset every runtime and shared Orrery spell service exactly once.
    }
}
```

This deliberately does **not** require:

- `IOrrerySpell` interfaces,
- reflection,
- runtime assembly scanning,
- delegate registration,
- per-cast closures,
- generic state machines,
- generic effect objects,
- a service locator,
- dependency injection.

For the current project, direct static dispatch is easier to inspect and harder to misuse.

---

# 7. Recommended Fixed-Step Flow

## 7.1 Resolve the local owner once

Current Shatterbolt and Plasma each do essentially the same work:

```text
CoreClassRuntime.CurrentContext
    ↓
context valid?
    ↓
ClassId == Orrery?
    ↓
owner = context.Ship
```

The lifetime layer should do this once per fixed step.

Conceptually:

```csharp
CoreOwnerContext context = CoreClassRuntime.CurrentContext;
GameShip owner = context != null && context.IsValid &&
    context.ClassId == CoreClassId.Orrery
        ? context.Ship
        : null;
```

Do not independently repeat this in every new spell.

## 7.2 Detect owner transitions once

Current explicit spells each have their own `lastTickOwner`.

That should become one lifetime-owned value.

Conceptually:

```csharp
if (!ReferenceEquals(owner, lastLocalOwner))
{
    if (lastLocalOwner != null)
        ForgetOwner(lastLocalOwner);

    lastLocalOwner = owner;
}
```

This is defensive even though `OrreryController.SyncOwner()` also tears down its current build.

The cleanup methods must remain idempotent because owner transition can be observed through more than one legitimate path.

## 7.3 Dispatch spell runtimes explicitly

For the first version:

```csharp
if (owner == null)
    return;

OrreryShatterbolt.FixedTick(owner, deltaTime);
OrreryPlasmaBolt.FixedTick(owner, deltaTime);
```

Add new explicit runtimes here as they are created.

This looks almost too simple. That is intentional.

The value comes from having one authoritative integration boundary, not from inventing a complicated dispatcher.

## 7.4 Do not require an active invocation

Never write the driver like this:

```csharp
if (!OrreryCasting.IsInvoking(owner))
    return;
```

That would break both major proof-of-concept lifetime patterns.

Shatterbolt may still have active Frost Bursts or a presentation tail after invocation completion.

Plasma Bolt may still have pending CoreCombat confirmations and active infections long after formula completion.

Each spell's `FixedTick` should remain the authority for whether that spell currently has anything to do.

---

# 8. Where the Fixed-Step Hook Should Live

There are two technically viable placements.

The first migration should choose the one that changes the least behavior.

## 8.1 Recommended first migration: one shared Harmony postfix

Current explicit spells already run from postfixes on:

```text
OrreryController.FixedUpdate
```

The safest first refactor is therefore:

```text
remove:
    OrreryShatterboltFixedTickPatch
    OrreryPlasmaBoltFixedTickPatch

add:
    OrrerySpellLifetimeFixedTickPatch
        Postfix -> OrrerySpellLifetime.FixedTickLocal(Time.fixedDeltaTime)
```

Why this is the safest first step:

- it preserves the broad “explicit spell runtimes tick after `OrreryController.FixedUpdate`” boundary,
- it avoids subtly moving Shatterbolt/Plasma before orbit/shuffle processing during the same fixed step,
- it avoids rewriting `OrreryController.FixedUpdate` control flow merely to centralize patches,
- it makes double-tick auditing straightforward,
- it can later be moved into the controller directly after the migration is proven.

## 8.2 Later option: direct controller call

Once the old monolithic runtime is migrated and tick ordering is deliberately chosen, the final shape can be:

```text
OrreryController.FixedUpdate
    SyncOwner
    lifetime fixed tick
    shuffle/orbit
```

or another explicitly documented order.

Do not make that ordering change accidentally during the first extraction.

The first goal is **one hook**, not “no Harmony at any cost.”

---

# 9. Tick Ordering

With separate Harmony postfixes, ordering between independent explicit spell patches is less obvious than it should be.

A shared dispatcher makes ordering deterministic.

Recommended initial explicit order:

```text
1. Shatterbolt
2. Plasma Bolt
3. future explicit spell runtimes in stable SpellId/order unless a real dependency requires otherwise
```

No current gameplay rule should depend on Shatterbolt ticking before Plasma or vice versa.

If a future spell genuinely depends on another runtime's same-frame result, that dependency should be documented rather than hidden in registration order.

Do not use tick ordering as an implicit cross-spell communication mechanism.

Cross-spell meaning should go through deliberate shared state such as CoreCombatState, Core history, or explicit Orrery class state.

---

# 10. Delta Time and Time Sources

The shared driver receives:

```text
Time.fixedDeltaTime
```

and passes it unchanged to spell runtimes.

The driver should not rescale, clamp, or accumulate time globally.

Individual spells may still legitimately use:

```text
Time.time
Time.unscaledTime
```

for semantics that already require absolute timestamps.

Examples:

- Shatterbolt uses absolute time for presentation-tail expiry.
- Plasma Bolt uses unscaled time for pending outcome timeouts and normal time for authored burn/spread/presentation timing.

Do not “standardize” these into one global clock as part of this refactor.

That would be a behavior change.

---

# 11. Cast Completion Must Remain Spell-Owned

The lifetime service must **not** decide when to call:

```text
OrreryCasting.CompleteInvocation
OrreryCasting.Cancel
OrreryController.StartShuffle
```

Why:

Different spells have different completion semantics.

## 11.1 Shatterbolt

Shatterbolt completes when its traveling/chaining primary action ends, then may retain active Frost Bursts/presentation history.

## 11.2 Plasma Bolt

Plasma Bolt intentionally defers formula completion out of `SpellRegistry.TryCommit()` because ending the Core ability execution while `TryCommit` is still on the stack can make the commit report failure.

That is a spell/casting transactional detail.

A generic lifetime service should not need to understand it.

## 11.3 Future channels

A channel may complete only on release.

## 11.4 Future delayed confirmation spells

A spell may complete formula use immediately but leave persistent world effects.

Therefore:

> **Lifetime dispatch must never equate “this spell has runtime state” with “the formula invocation is still open.”**

---

# 12. Shuffle Interaction

Existing behavior permits gameplay tails to continue while satellites shuffle/rearm.

Examples:

- Shatterbolt can call `StartShuffle` after primary cast completion while Frost Bursts are still resolving.
- Plasma Burn continues after the bolt cast has completed and the formula has rearmed.

Therefore the shared lifetime driver must not do:

```csharp
if (OrreryController.IsShuffling(owner))
    return;
```

That check belongs at **cast acceptance** for spells that should not start during shuffle.

It is not a valid global rule for already-existing effects.

---

# 13. Cancellation and Execution Invalidation

Invocation cancellation remains a spell concern because only the spell knows what cancellation should destroy.

For example, current Shatterbolt checks execution validity while the primary cast is active and aborts before another burst can deal damage.

The lifetime layer should simply continue calling:

```text
OrreryShatterbolt.FixedTick(owner, dt)
```

Shatterbolt remains responsible for:

- checking execution validity,
- choosing abort vs complete behavior,
- cleaning active orb/bursts,
- publishing presentation clear state,
- cancelling casting state,
- starting shuffle if appropriate.

Do not create a generic “if invocation invalid then dispose everything” rule in the lifetime service.

A persistent effect may intentionally survive after its originating invocation is no longer active.

---

# 14. Owner Teardown

The central owner teardown path is one of the highest-value pieces of this refactor.

## 14.1 Current problem

`OrreryController.TearDownCurrentBuild()` currently knows every runtime individually.

That means every new spell can require editing class controller teardown.

The class controller should know only the class-level service.

## 14.2 Desired controller boundary

Replace spell-specific cleanup lines with one call:

```csharp
OrrerySpellLifetime.ForgetOwner(owner);
```

This call should occur **before** casting/satellite teardown, preserving the current broad ordering.

Recommended order remains conceptually:

```text
Stop shuffle
    ↓
OrrerySpellLifetime.ForgetOwner(owner)
    ↓
OrreryCasting.Cancel(owner)
    ↓
invalidate satellite live state / intent
    ↓
orbit cleanup
    ↓
native satellite destruction
```

Why spell cleanup should happen early:

- spell runtime may still hold references to owner/satellites/native adapters,
- pooled presentation objects should be returned while their native environment is still intact,
- a spell may need to clear Core state associated with its owner,
- hidden implementation sources should be unequipped before owner teardown progresses.

## 14.3 ForgetOwner must be idempotent

It may be reached through:

- `OrreryController.SyncOwner()` owner replacement,
- class exit,
- controller destruction,
- `GameShip.Destroyed`,
- `GameShip.OnDestroy`,
- world teardown.

Calling it twice must be harmless.

Every underlying spell `Forget(owner)` must therefore remain safe when no state exists.

---

# 15. Ship Destruction Is Broader Than Owner Destruction

A central ship-destruction notification is useful because a destroyed ship may be:

1. the local Orrery owner,
2. a Shatterbolt target,
3. a Plasma Burn target,
4. a remote Orrery owner whose presentation exists locally,
5. a future persistent-effect target.

Do not treat every destroyed `GameShip` as merely “maybe the caster.”

Recommended class-level entry point:

```csharp
public static void NotifyShipDestroyed(GameShip ship)
```

It can explicitly forward to the runtimes/presentation systems that need target cleanup.

Example conceptual order:

```text
NotifyShipDestroyed(ship)
    ↓
spell-specific target detach/cleanup
    ↓
remote presentation target detach/cleanup where needed
    ↓
ForgetOwner(ship) if this ship owns Orrery runtime state
```

The lifetime service does not need to know what an `Infection` is.

It merely knows that Plasma Bolt has a target-destruction notification entry point.

---

# 16. `Destroyed` vs `OnDestroy`

Current Plasma Bolt correctly has a special reason for observing native `GameShip.Destroyed` in addition to Unity `OnDestroy`:

> native `Destroyed` returns/disowns pooled children before Unity `OnDestroy`, so Plasma needs to release its unassigned status layer before that native sweep.

This timing requirement must be preserved.

Recommended shared hooks:

```text
Harmony patch GameShip.Destroyed Prefix
    -> OrrerySpellLifetime.NotifyShipDestroyed(__instance)

Harmony patch GameShip.OnDestroy Prefix
    -> OrrerySpellLifetime.NotifyShipDestroyed(__instance)
```

Yes, this can notify twice.

That is preferable to missing one destruction path.

The notification path must therefore be idempotent.

Do not remove the earlier `Destroyed` hook merely because `OnDestroy` also exists.

---

# 17. World Teardown

Current explicit spells each patch `WorldController.OnDestroy` separately.

Replace those with one shared world teardown hook.

Conceptually:

```text
Harmony patch WorldController.OnDestroy Prefix
    -> OrrerySpellLifetime.ResetWorld()
```

`ResetWorld()` should:

1. clean every owner/spell runtime,
2. release pooled/local presentation owned by those runtimes,
3. clear lifetime owner tracking,
4. reset shared spell-runtime services once,
5. leave unrelated class systems to their own owners.

Do not rely solely on ordinary per-owner teardown during world destruction. Unity/native destruction ordering can make references disappear in inconvenient orders.

A dedicated world reset gives the class one deterministic emergency drain.

---

# 18. Shared Service Ownership

Centralization exposes one existing smell: a shared service should not be reset because one particular spell decided to reset.

Current Shatterbolt `Reset()` resets `OrreryDamageRouter`.

Both Shatterbolt and Plasma call `OrreryDamageRouter.Forget(owner)` even though the router's per-owner `Forget` is intentionally a no-op today.

That is survivable now, but ownership is backwards.

Target rule:

```text
spell reset
    resets spell-owned state only

OrrerySpellLifetime.ResetWorld
    resets shared Orrery spell services once
```

Therefore, after migration is proven:

```text
remove OrreryDamageRouter.Reset() from Shatterbolt.Reset()

OrrerySpellLifetime.ResetWorld()
    OrreryShatterbolt.Reset()
    OrreryPlasmaBolt.Reset()
    OrrerySpellRuntime.Reset()
    OrreryDamageRouter.Reset()
```

Likewise, a future shared native-effect cache or shared presentation bank should be reset by the class-level owner, not arbitrarily by one spell.

---

# 19. What Each Spell Must Expose

Do not force an interface in the first implementation.

Use a small naming convention.

For persistent explicit spell runtimes, prefer these methods where relevant:

```text
Execute(...)
FixedTick(GameShip owner, float deltaTime)
Forget(GameShip owner)
Reset()
```

Optional methods only when required:

```text
LateTick(GameShip owner)
ReleaseInvoke(GameShip owner)
NotifyTargetDestroyed(GameShip target)
TryGetPresentation(...)
```

A spell does not need empty implementations for hooks it does not use.

The lifetime dispatcher can make explicit calls only to the spells that need a given hook.

That is simpler than forcing every spell through an interface with mostly unused methods.

---

# 20. What New Spells Must NOT Add After This Migration

Once `OrrerySpellLifetime` is live, new spell implementations should not add their own copies of class-global lifecycle glue.

Specifically, a new explicit spell should not normally add:

```text
HarmonyPatch(OrreryController, FixedUpdate)
HarmonyPatch(WorldController, OnDestroy)
private static GameShip lastTickOwner
its own class-owner replacement detector
its own global world-reset integration point
```

If a spell requires a new kind of lifecycle event, add that event at the class lifetime boundary once and forward it explicitly.

Example:

If a future spell genuinely requires `Update` rather than `FixedUpdate`, add a shared `OrrerySpellLifetime.UpdateLocal()` boundary rather than giving ten spells ten independent Update patches.

---

# 21. Per-Spell Owner Dictionaries: Transitional Rule

This deserves a precise rule because there are two competing risks:

- leaving every spell with permanent global owner storage forever,
- prematurely inventing a generic state container that is harder to understand than the problem.

## 21.1 First migration

For the first lifetime refactor, **do not rewrite Shatterbolt and Plasma state ownership merely to remove their dictionaries.**

Keep:

```text
Dictionary<GameShip, Shatterbolt.OwnerState>
Dictionary<GameShip, PlasmaBolt.OwnerState>
```

while moving orchestration out.

Why:

- it isolates the refactor to integration/lifetime plumbing,
- it minimizes gameplay regression risk,
- it makes before/after behavior easy to compare,
- the dictionaries are not currently a meaningful hot-path performance problem,
- each spell already removes its state when idle/forgotten.

## 21.2 But they are no longer lifecycle authority

After the refactor, these dictionaries are merely private state lookup tables.

They do not decide:

- who the local Orrery owner is,
- when world teardown happens,
- when class exit happens,
- whether their FixedUpdate hook runs.

That authority belongs to `OrrerySpellLifetime`.

## 21.3 Future extraction trigger

Do not create a central generic spell-state store until repeated evidence justifies it.

A good trigger is when several additional explicit spell files all repeat the same pattern:

```text
Dictionary<GameShip, OwnerState>
GetOrCreateState(owner)
remove on idle
remove on owner teardown
```

At that point a narrow class-owned owner-state container may be worth designing.

That is **Phase 2**, not required to gain the main lifetime benefit.

## 21.4 New-spell guidance during the transition

A new spell may temporarily use a private bounded owner-state dictionary if needed, but it must:

- be local-owner scoped,
- self-empty when no runtime state remains,
- have bounded child arrays/collections,
- integrate through `OrrerySpellLifetime`,
- not add its own Harmony lifetime patches,
- not add its own `lastTickOwner`,
- not become the owner of shared Orrery services.

---

# 22. Why Not `IOrrerySpellRuntime` Yet?

An interface could eventually be reasonable, but it is not required for the first useful version.

Possible interface:

```csharp
interface IOrrerySpellRuntime
{
    void FixedTick(GameShip owner, float deltaTime);
    void Forget(GameShip owner);
    void Reset();
}
```

Problems today:

- our runtimes are static classes,
- optional lifecycle methods differ,
- we would need adapter singleton objects or convert each runtime to instances,
- registration/order becomes another system to debug,
- it does not materially improve two or three explicit dispatch calls.

Direct calls are more transparent:

```csharp
OrreryShatterbolt.FixedTick(owner, dt);
OrreryPlasmaBolt.FixedTick(owner, dt);
```

If later there are dozens of runtimes and the dispatch list itself becomes maintenance pain, introduce an interface/registration design then.

Do not solve that hypothetical problem now.

---

# 23. Why Not One MonoBehaviour Per Spell?

Do not solve lifetime by attaching a new Unity `MonoBehaviour` for every spell family.

That would create:

- many independent Unity update entry points,
- more hidden lifetime/order behavior,
- more object/component teardown paths,
- harder world-reset auditing,
- harder deterministic ordering,
- more opportunities for one spell to continue ticking after class exit.

The Orrery already has a class controller. A centralized dispatcher is simpler.

---

# 24. Why Not Coroutines?

Coroutines are tempting for:

- burns,
- delayed explosions,
- presentation fades,
- channels.

They are not the preferred class-wide lifetime model here.

Reasons:

1. cancellation/class exit requires tracking every coroutine anyway,
2. fixed-step gameplay timing becomes less explicit,
3. world teardown becomes harder to audit,
4. owner replacement can leave hidden work alive if not stopped perfectly,
5. multiplayer authority remains easier to reason about in one explicit tick path,
6. bounded per-tick work is easier to inspect in normal loops.

A presentation-only coroutine can still be acceptable in a narrow case, but it should not become the core spell-runtime architecture.

---

# 25. Why Not a Generic Active-Instance Scheduler Yet?

Another plausible design is:

```text
RegisterSpellInstance(instance)
UnregisterSpellInstance(instance)
foreach active instance -> Tick()
```

That can be useful in a different project, but it is not yet clearly better here.

Potential costs:

- per-cast object instances,
- registration/removal mutation during iteration,
- delegate/closure allocations if implemented casually,
- need for stable ordering,
- need for pooled instance objects under heavy use,
- harder mapping to spells like Plasma Bolt that maintain a population of infections across casts.

The Orrery currently needs a central **integration boundary**, not a miniature ECS.

Use explicit spell runtimes first.

---

# 26. Legacy `OrrerySpellRuntime`

The old FF/II/LL runtime is still monolithic and is already directly ticked by `OrreryController.FixedUpdate`.

Do not force its migration into this same first patch unless necessary.

Recommended staged approach:

## Stage A

Centralize the **new explicit spell runtimes** first:

```text
Shatterbolt
Plasma Bolt
future explicit spells
```

Keep the existing direct:

```csharp
OrrerySpellRuntime.FixedTick(currentOwner, Time.fixedDeltaTime);
```

until FF/II/LL are migrated.

## Stage B

When FF/II/LL are converted to per-spell implementations or otherwise cleaned up, integrate them through the same lifetime boundary.

## Stage C

Once every Orrery spell runtime is class-dispatched, remove the special legacy direct tick and choose one final stable ordering in `OrreryController`/lifetime.

This prevents the lifetime refactor from becoming entangled with the separate old-spell focus/damage-semantics migration.

---

# 27. `LateUpdate` / Late Tick

The legacy runtime currently has `LateTick(owner)` because native beam presentation may need late-frame work.

The lifetime architecture should support a shared late-tick boundary **only if explicit spells need it**.

Do not create empty LateUpdate machinery for every spell immediately.

If/when needed:

```csharp
OrrerySpellLifetime.LateTickLocal()
```

can resolve the current owner once and explicitly call the runtimes that require late presentation work.

Until then, leave the existing legacy path stable.

---

# 28. Presentation Ownership

The lifetime layer should coordinate teardown notifications but not become the presentation implementation.

Important distinction:

```text
owner gameplay runtime
    may own local presentation objects directly

remote presentation runtime
    may own remote-only VFX state

OrrerySpellLifetime
    can notify both on ship/world teardown
    but does not encode/render their effects
```

For example, Plasma currently has separate remote presentation cleanup concerns.

The shared ship-destruction notification may forward to both:

```text
OrreryPlasmaBolt.NotifyTargetDestroyed(ship)
OrreryPlasmaBoltPresentation.ForgetTarget(ship)
```

but the lifetime class should not know how a burn visual is built.

---

# 29. Networking Semantics

Central lifetime orchestration must not alter gameplay authority.

Keep the invariant:

```text
source owner
    runs gameplay state
    decides damage/targets/spread/chains
    publishes presentation state

remote peers
    consume presentation only
    never apply spell gameplay
```

The lifetime layer itself should not send new network messages merely because it ticked.

Spells should continue publishing when their own state changes or when their existing presentation contract requires periodic publication.

A future network presentation bank is a separate architecture task.

---

# 30. Performance Requirements

The shared lifetime layer should be effectively allocation-free in steady state.

## 30.1 Fixed-step requirements

Do not allocate per fixed tick.

Avoid:

- LINQ,
- `foreach` over allocation-prone abstractions,
- temporary lists,
- new delegates per frame,
- closures,
- reflection,
- string formatting/logging in normal success paths.

## 30.2 Direct dispatch cost

Calling a handful of static `FixedTick` methods is extremely cheap.

Each inactive runtime should fail fast on owner-state lookup.

Example:

```csharp
OwnerState state;
if (!owners.TryGetValue(owner, out state) || state == null)
    return;
```

Even if the class eventually has many spells, one local Orrery owner means this overhead is modest.

Do not prematurely replace transparent calls with a complex scheduler before profiling shows the explicit approach matters.

## 30.3 Bound all spell-owned populations

The lifetime driver does not remove the existing requirement that spell runtimes remain bounded.

Examples already worth preserving:

- Shatterbolt maximum impact history,
- Shatterbolt bounded Frost Burst target history,
- Plasma bounded pending impacts,
- Plasma bounded infection pool.

The driver must never introduce an unbounded global “active effects list.”

---

# 31. Error Isolation

Do not wrap every spell tick in broad `try/catch` by default.

Why:

- swallowing exceptions can leave half-mutated gameplay state alive,
- errors become harder to reproduce,
- this mod is version-controlled and distributed as an exact build to co-op peers,
- programming errors should be visible during development.

If a particular native API boundary is known to throw and already has defensive handling, keep that handling near the boundary.

The lifetime dispatcher should remain simple.

---

# 32. Determinism and Co-op

The lifetime refactor should improve determinism by reducing duplicate/ambiguous local scheduling.

Rules:

1. gameplay remains owner-authoritative,
2. one local fixed-step dispatcher calls each explicit runtime at most once per fixed step,
3. dispatch order is stable,
4. no spell gains an extra tick because two Harmony patches coexist,
5. no remote peer runs owner gameplay merely because presentation exists,
6. class/owner teardown stops all local authoritative effects deterministically.

The largest migration risk is **double ticking**.

If the shared patch is added before old per-spell postfixes are removed, burns/explosions/travel can advance twice per fixed step.

Treat that as a hard blocker.

---

# 33. Migration Plan — Small Safe Chunks

This project is being modified concurrently at times. Before every write, refetch exact live source and SHA.

Do not assume the source snapshot in this document is still current.

Recommended migration sequence follows the project preference: one small semantic chunk, verify, then continue.

## Chunk 1 — Add the class lifetime service without changing behavior

Add:

```text
OrrerySpellLifetime.cs
```

Initially include only explicit forwarding methods and no active Harmony patch if that makes the change easier to review.

Suggested methods:

```text
FixedTickLocal(dt)
ForgetOwner(owner)
NotifyShipDestroyed(ship)
ResetWorld()
```

The dispatcher should know Shatterbolt and Plasma Bolt explicitly.

No spell mechanics change.

## Chunk 2 — Centralize fixed-step integration atomically

In one coherent change:

1. add the single lifetime postfix on `OrreryController.FixedUpdate`,
2. remove `OrreryShatterboltFixedTickPatch`,
3. remove `OrreryPlasmaBoltFixedTickPatch`,
4. remove spell-local `TickLocal` methods if no longer used,
5. remove spell-local `lastTickOwner` fields,
6. keep `FixedTick(owner, dt)` behavior unchanged.

Do not leave both old and new fixed-tick paths active even temporarily in a shipped commit.

## Chunk 3 — Centralize owner teardown

Change `OrreryController.TearDownCurrentBuild()` from multiple spell-specific calls to:

```csharp
OrrerySpellLifetime.ForgetOwner(owner);
```

Preserve teardown ordering around casting/satellite cleanup.

## Chunk 4 — Centralize world teardown

Replace separate Shatterbolt/Plasma world-destroy patches with one lifetime patch.

Keep each spell's `Reset()` implementation, but invoke them through the class owner.

Move shared-service reset ownership to the lifetime layer where appropriate.

## Chunk 5 — Centralize ship destruction notifications

Replace per-spell `GameShip.OnDestroy` / `GameShip.Destroyed` lifecycle patches with shared lifetime hooks.

Forward explicitly to:

- owner cleanup,
- Plasma target cleanup,
- remote presentation target cleanup where required,
- future spell target cleanup only when actually needed.

Preserve the early `Destroyed` notification required for pooled status-layer cleanup.

## Chunk 6 — Remove redundant shared cleanup ownership

After behavior is stable:

- remove `OrreryDamageRouter.Reset()` ownership from Shatterbolt,
- invoke it once from `OrrerySpellLifetime.ResetWorld()`,
- reconsider redundant per-spell `OrreryDamageRouter.Forget(owner)` calls if still no-op.

Do this only after the class-level reset path is proven.

## Chunk 7 — Document the standard

Update the canonical Orrery class/implementation standards so future agents know:

- new persistent spells integrate through the lifetime service,
- no per-spell FixedUpdate/world-owner lifecycle patches,
- spells own mechanics and internal state,
- gameplay/presentation tails may outlive invocation completion,
- the lifetime service is not a generic effect engine.

---

# 34. Suggested Concrete Skeleton

This is illustrative pseudocode, not a blind copy/paste mandate. Refetch live APIs first.

```csharp
using HarmonyLib;
using StarVortex;
using UnityEngine;

public static class OrrerySpellLifetime
{
    private static GameShip lastLocalOwner;

    public static void FixedTickLocal(float deltaTime)
    {
        CoreOwnerContext context = CoreClassRuntime.CurrentContext;
        GameShip owner = context != null && context.IsValid &&
            context.ClassId == CoreClassId.Orrery
                ? context.Ship
                : null;

        if (!object.ReferenceEquals(owner, lastLocalOwner))
        {
            if (!object.ReferenceEquals(lastLocalOwner, null))
                ForgetOwner(lastLocalOwner);

            lastLocalOwner = owner;
        }

        if (owner == null)
            return;

        // Explicit, deterministic, allocation-free dispatch.
        OrreryShatterbolt.FixedTick(owner, deltaTime);
        OrreryPlasmaBolt.FixedTick(owner, deltaTime);
    }

    public static void ForgetOwner(GameShip owner)
    {
        if (object.ReferenceEquals(owner, null))
            return;

        OrreryShatterbolt.Forget(owner);
        OrreryPlasmaBolt.Forget(owner);
        OrrerySpellRuntime.Forget(owner); // while legacy runtime remains

        if (object.ReferenceEquals(lastLocalOwner, owner))
            lastLocalOwner = null;
    }

    public static void NotifyShipDestroyed(GameShip ship)
    {
        if (object.ReferenceEquals(ship, null))
            return;

        // Only runtimes with target-attached state need explicit target cleanup.
        OrreryPlasmaBolt.ForgetTarget(ship);
        OrreryPlasmaBoltPresentation.ForgetTarget(ship);

        // Harmless no-op if this ship was not an Orrery owner.
        ForgetOwner(ship);
    }

    public static void ResetWorld()
    {
        OrreryShatterbolt.Reset();
        OrreryPlasmaBolt.Reset();
        OrrerySpellRuntime.Reset();

        // Shared spell service ownership belongs here, not in one spell.
        OrreryDamageRouter.Reset();

        lastLocalOwner = null;
    }
}
```

Shared patches conceptually:

```csharp
[HarmonyPatch(typeof(OrreryController), "FixedUpdate")]
public static class OrrerySpellLifetimeFixedTickPatch
{
    public static void Postfix()
    {
        OrrerySpellLifetime.FixedTickLocal(Time.fixedDeltaTime);
    }
}

[HarmonyPatch(typeof(WorldController), "OnDestroy")]
public static class OrrerySpellLifetimeWorldDestroyedPatch
{
    public static void Prefix()
    {
        OrrerySpellLifetime.ResetWorld();
    }
}

[HarmonyPatch(typeof(GameShip), "Destroyed")]
public static class OrrerySpellLifetimeShipDyingPatch
{
    public static void Prefix(GameShip __instance)
    {
        OrrerySpellLifetime.NotifyShipDestroyed(__instance);
    }
}

[HarmonyPatch(typeof(GameShip), "OnDestroy")]
public static class OrrerySpellLifetimeShipDestroyedPatch
{
    public static void Prefix(GameShip __instance)
    {
        OrrerySpellLifetime.NotifyShipDestroyed(__instance);
    }
}
```

Again: verify exact current methods/signatures before implementing.

---

# 35. One Important Detail: Avoid Recursive/Conflicting Cleanup

When `OrreryController.TearDownCurrentBuild()` calls `OrrerySpellLifetime.ForgetOwner(owner)`, and a later `GameShip.OnDestroy` also calls `NotifyShipDestroyed(owner)`, cleanup will run more than once.

That is expected.

Do not add fragile “cleanup only once ever” flags unless needed.

Prefer idempotent cleanup:

```text
lookup state
if absent -> return
if visual exists -> return/destroy it
if adapter exists -> unequip it
clear/remove state
```

This is more robust against Unity/native destruction order than assuming exactly one path always fires.

---

# 36. One Important Detail: Do Not Remove Runtime State at Cast Completion Automatically

The lifetime service should never do something like:

```csharp
if (!state.Invocation.Execution.IsValid)
    RemoveSpellState(spellId);
```

That would be wrong for persistent tails.

Only the spell can know when all of these are done:

```text
primary cast
child gameplay effects
pending authoritative confirmations
presentation tail
pooled visual retirement
```

A runtime may remove its own private owner state when truly idle.

For example:

- Shatterbolt already has `CleanupIdleState` conditions.
- Plasma retains owner state while burns/pending outcomes/presentation require it.

Keep that knowledge local.

---

# 37. One Important Detail: Target Destruction Must Not Become Generic “Cancel Spell”

If a target dies:

- Shatterbolt may reacquire another target,
- Plasma may spread from the captured death position if confirmed destroyed,
- an infection target should clean up its visual/state,
- a future homing projectile may seek a replacement,
- a future tether may end.

Therefore the lifetime layer should only **notify**.

It should not impose a universal response.

---

# 38. One Important Detail: Current Shatterbolt and Plasma Have Different Idle Semantics

Do not force them to share one “active” predicate.

Shatterbolt can be alive because:

```text
CastActive
OR active explosions
OR presentation tail not expired
```

Plasma can be alive because:

```text
completion is pending
OR direct-hit outcome confirmation pending
OR one or more infections active
OR one or more infection visuals retiring
OR bolt presentation still needs local/replicated state
```

Those predicates belong in the spells.

The lifetime driver simply gives the spell a tick if its owner state exists.

---

# 39. Future Spell Examples and How They Fit

The architecture should remain useful for very different spells.

## 39.1 Instant spell with no tail

```text
Execute
    apply effect immediately
    complete invocation
    no runtime state created
```

It may not need `FixedTick` at all.

Do not force every spell into lifetime state.

## 39.2 Traveling projectile

```text
Execute
    create state

FixedTick
    move / hit / timeout

Forget
    kill projectile/presentation
```

## 39.3 Held channel

```text
Execute
    create channel state

FixedTick
    apply channel behavior

ReleaseInvoke
    complete

Forget
    hard cancel
```

## 39.4 Persistent area after cast

```text
Execute
    spawn area state
    complete invocation

FixedTick
    area continues independently

state removes itself on duration expiry
```

## 39.5 Target-attached debuff

```text
Execute
    apply target state
    complete invocation

FixedTick
    tick semantic effect

NotifyShipDestroyed
    detach early if target disappears
```

## 39.6 Presentation-only tail

```text
mechanics finish
presentation timer remains
FixedTick continues only presentation cleanup/publication
then remove state
```

All of these fit without a universal mechanic engine.

---

# 40. Interaction With Spell Registry

`OrrerySpellRegistry` should remain responsible for initial execution dispatch.

Flow stays:

```text
recipe resolves
    ↓
SpellDefinition.Executor(owner, invocation, spell)
    ↓
spell creates/updates its own runtime state if needed
    ↓
future fixed steps are supplied by OrrerySpellLifetime
```

The lifetime service does not replace `OrrerySpellRegistry`.

Registry answers:

> What spell should begin?

Lifetime answers:

> Which Orrery runtime code receives ongoing class lifecycle ticks/teardown?

Those are different questions.

---

# 41. Interaction With `OrreryCastInvocation.Execution`

The execution object remains the spell's transactional link back to casting.

The lifetime service may carry the owner, but it should not store one global “current execution” and assume all persistent effects belong to it.

Why:

- gameplay tails can overlap a later invocation,
- future persistent effects may outlive multiple casts,
- Plasma Burn descendants retain originating cast identity for provenance but do not keep the original invocation open.

Keep per-spell/per-effect cast IDs where mechanics/provenance require them.

---

# 42. Interaction With Combat Provenance

No change to the existing rule:

Spell-authored damage should continue using Core combat provenance where supported.

The lifetime service should not begin or end generic damage scopes.

Each spell still knows:

- semantic key,
- contributor,
- cast/attack instance ID,
- acknowledgement mode,
- tracking flags,
- physical/native source context.

This keeps lifecycle architecture independent of damage architecture.

---

# 43. Interaction With CoreCombatState

Core semantic state should continue to own semantic persistence that is not merely local runtime bookkeeping.

Example:

```text
PlasmaBurn
PlasmaBurnRecent
```

The lifetime service should not duplicate those as class-global timers.

When class/owner teardown occurs, spell-specific cleanup may remove active semantic state if required. Long-lived semantic history should follow Core's established ownership/expiry model.

---

# 44. Interaction With Native-Backed Adapters

Hidden launchers/native adapters remain spell implementation details for now.

`ForgetOwner` gives every spell one reliable place to dispose them.

A future shared native-asset/adapter cache may reduce repeated `Resources.Load` and item construction, but that is a separate extraction.

Do not block the lifetime refactor on adapter caching.

---

# 45. Logging / Diagnostics

The lifetime service should have very little logging in normal operation.

Useful development-only warnings could include:

- duplicate registration if a future registry is introduced,
- impossible owner state,
- a runtime remaining present after hard class teardown.

Do not log every tick or every inactive runtime.

If F10 debug tooling later gets a class-runtime panel, useful fields could be:

```text
Current local Orrery owner
Lifetime lastLocalOwner
Shatterbolt owner state present?
Plasma owner state present?
active infection count
active Shatterbolt burst count
pending Plasma confirmation count
```

But debug UI is not part of this migration.

---

# 46. Validation Checklist

The implementation is not complete merely because it compiles.

The following behavior should be validated.

## 46.1 Structural validation

- exactly one explicit-spell fixed-step hook remains,
- no Shatterbolt-specific FixedUpdate Harmony patch remains,
- no Plasma-specific FixedUpdate Harmony patch remains,
- no Shatterbolt-specific world-destroy patch remains,
- no Plasma-specific world-destroy patch remains,
- controller teardown calls one class spell-lifetime boundary,
- no duplicate `lastTickOwner` remains in explicit spell runtimes,
- shared service reset ownership is class-level where migrated.

## 46.2 Shatterbolt gameplay

Test:

- ordinary 4-impact baseline behavior,
- inherited extra chains,
- target death/reacquisition,
- no double travel speed,
- no double Frost Burst expansion,
- each burst still damages a target only once,
- final burst still occurs,
- cast completion still triggers shuffle,
- Frost Bursts can finish after primary cast completion,
- presentation tail clears correctly,
- class exit immediately cleans orb/bursts/hidden sources.

Double-tick symptoms to watch for:

- projectile moving roughly twice as fast,
- explosions expanding twice as fast,
- shorter-than-expected presentation tail,
- duplicated network publication/damage.

## 46.3 Plasma Bolt gameplay

Test:

- initial bolt fires once,
- formula completion still succeeds (no `TryCommit` regression),
- confirmed actual damage still becomes the burn budget,
- burn total remains exact,
- burn lasts expected duration,
- ticks do not accelerate,
- spread scans do not run twice as often,
- reinfection lockout remains correct,
- destroyed target cleanup still releases faux-burning VFX before native pooled-child teardown,
- burn can remain after initial cast is complete,
- owner/class exit kills authoritative infections cleanly.

Double-tick symptoms to watch for:

- burn finishing in roughly half duration,
- spread happening too rapidly,
- pending confirmations being consumed/pruned oddly,
- visual fade accelerated.

## 46.4 Legacy spells

Because initial migration should not change legacy `OrrerySpellRuntime` scheduling, verify:

- Magma Cannon still runs,
- Cone of Cold still runs,
- Tesla channel still runs/releases,
- no additional tick is accidentally added to legacy runtime.

## 46.5 Owner transition

Test:

```text
enter Orrery
cast persistent effect
leave Orrery / change class / replace owner
```

Expected:

- all local authoritative spell state is cleaned,
- hidden adapters are disposed,
- local pooled VFX are returned/destroyed correctly,
- no old-owner effect resumes if Orrery is later re-entered.

## 46.6 World transition

Test world unload/reload with:

- Shatterbolt orb active,
- Shatterbolt burst active,
- Plasma pending confirmation,
- multiple active Plasma Burns,
- retiring Plasma visual.

Expected:

- no leaked state,
- no stale owner references,
- no VFX carried into next world,
- next Orrery build works normally.

## 46.7 Ship destruction

Test destruction of:

- Orrery owner,
- Shatterbolt current target,
- Plasma Burn target,
- remote Orrery player where applicable.

Expected behavior must match existing spell rules, with no exceptions from stale references.

---

# 47. Static Review Checklist for the Agent

Before committing each migration chunk:

1. refetch branch HEAD,
2. refetch every file being modified,
3. verify no other agent changed the same area,
4. search for all `HarmonyPatch(typeof(OrreryController), "FixedUpdate")` entries,
5. search for all Orrery `WorldController.OnDestroy` patches,
6. search for all Orrery `GameShip.OnDestroy` / `GameShip.Destroyed` patches,
7. search for all `lastTickOwner` fields,
8. search for direct `OrreryShatterbolt.Forget` / `OrreryPlasmaBolt.Forget` calls outside the lifetime layer,
9. verify there is exactly one ongoing tick path per spell,
10. inspect the final commit diff for accidental tuning/mechanics changes.

Do not trust an old handoff over current source.

---

# 48. Concurrency / Multi-Agent Repository Rule

This repo may be edited by another agent at the same time.

Therefore:

- never patch from a stale local copy,
- never assume the SHAs in this document are still current,
- fetch live branch/source immediately before each write,
- keep migration chunks small,
- do not overwrite unrelated new spell work,
- if another agent has changed lifecycle code, reassess before applying the plan.

This document describes architectural intent, not permission to clobber newer code.

---

# 49. Acceptance Criteria

The first lifetime centralization pass is successful when all of the following are true:

```text
[ ] One shared explicit-spell fixed-step integration point exists.
[ ] Shatterbolt no longer owns its own controller FixedUpdate patch.
[ ] Plasma Bolt no longer owns its own controller FixedUpdate patch.
[ ] Local Orrery owner resolution/last-owner tracking is centralized.
[ ] OrreryController teardown calls one spell-lifetime owner cleanup method.
[ ] World teardown is centralized for explicit Orrery spell runtimes.
[ ] Ship destruction notification is centralized without losing Plasma's early Destroyed cleanup need.
[ ] Shatterbolt mechanics are unchanged.
[ ] Plasma Bolt mechanics are unchanged.
[ ] Legacy FF/II/LL scheduling is unchanged during the first migration.
[ ] No spell ticks twice per fixed step.
[ ] Gameplay tails continue after invocation completion when designed to do so.
[ ] Presentation tails do not prolong gameplay.
[ ] Class exit/owner replacement/world teardown clean every persistent effect.
[ ] Shared Orrery spell services are not reset by an arbitrary individual spell once migrated.
[ ] No generic spell-effect engine was introduced.
[ ] No balance values changed.
```

---

# 50. What “Good” Looks Like Afterward

After this migration, implementing a new persistent spell should feel like this:

```text
1. Add spell-specific file.
2. Implement Execute.
3. If it has ongoing state, implement FixedTick(owner, dt).
4. Implement Forget(owner) / Reset if it owns persistent resources.
5. Add one explicit dispatch line to OrrerySpellLifetime.
6. Add target-destruction forwarding only if the mechanic genuinely needs it.
7. Do NOT add another FixedUpdate patch.
8. Do NOT add another world-destroy patch.
9. Do NOT add another lastTickOwner.
10. Keep all spell-specific mechanics in the spell.
```

That is the practical payoff.

Instead of every spell solving Unity/class lifetime from scratch, every spell gets a reliable host boundary and can spend its complexity budget on the mechanic itself.

---

# 51. What This Architecture Does Not Promise

This change does not magically solve every future lifetime problem.

Future needs may include:

- multiple simultaneously active persistent spell instances,
- bounded central runtime slots,
- pooled spell-instance objects,
- active-only dispatch rather than explicit all-runtime dispatch,
- separate fixed/update/late-update phases,
- shared remote presentation bank,
- save/load or reconnect semantics,
- debug inspection.

Do not prebuild those now.

The architecture is intentionally evolutionary:

```text
Step 1:
    centralize the lifecycle seams we have proven are repeated

Step 2:
    build more spells

Step 3:
    extract the next repeated seam only when real spells prove it exists
```

This is the same strategy that produced `OrreryFocusProfile`: build enough real behavior to discover the correct shared boundary, then extract that boundary narrowly.

---

# 52. Final Recommendation

Implement this.

It is not wishful-thinking architecture and it is not abstraction for abstraction's sake.

The repeated lifecycle responsibilities already exist in live code, and two materially different spells already need them.

The correct scope is deliberately small:

> **One Orrery-owned runtime orchestration boundary for ticking, owner replacement, class teardown, ship destruction notification, and world teardown.**

Keep spell mechanics explicit.

Keep spell state spell-owned during the first migration.

Keep gameplay authority owner-local.

Keep tails alive for as long as their spell says they are alive, not as long as the cast transaction happens to be open.

Keep the first dispatcher boring.

The architecture should make the tenth persistent spell cheaper and safer to implement than the second one without making the second one harder to understand.
