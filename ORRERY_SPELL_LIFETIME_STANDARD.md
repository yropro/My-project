# Orrery Spell Lifetime Standard

**Project:** Star Vortex — Orrery / Celestial Mage / Sphereweaver  
**Status:** CURRENT — authoritative gameplay lifetime only; scope reviewed 2026-09-14  
**Applies to:** all new Orrery spells and all maintenance/refactors touching Orrery persistent runtime behavior  
**Established from live implementation:** `skill-trees` through `6d6e56e52d0d2389e459c359fb1502cc485e5758`

---

See [DOCUMENTATION.md](DOCUMENTATION.md) for reference scopes.
Remote presentation lifecycle and network exception containment are governed by
[Networking for skill authors](Assets/Leviathan/Content/Scripts/NETWORKING.md). Shared presentation callbacks do not
replace or duplicate this authoritative gameplay fixed-step dispatcher.

# 1. Purpose

This document records the lifetime architecture that is now implemented so future spell work does not recreate the lifecycle duplication that Shatterbolt and Plasma Bolt required us to remove.

This is a **standard**, not a proposal.

The governing rule is:

> **`OrrerySpellLifetime` owns WHEN persistent Orrery spell runtimes are ticked or torn down. Each spell owns WHAT its runtime does while alive.**

Do not refactor away from this boundary merely because a new spell has unusual mechanics. Extend the existing lifetime boundary narrowly when a genuinely new lifecycle phase is required.

---

# 2. Current Authoritative Architecture

The live persistent-spell path is intentionally explicit:

```text
OrreryController.FixedUpdate
    ↓ Harmony Postfix
OrrerySpellLifetime.FixedTickLocal(Time.fixedDeltaTime)
    ↓
resolve current local Orrery owner once
    ↓
handle owner replacement once
    ↓
explicit stable dispatch
    ├─ OrreryShatterbolt.FixedTick(owner, dt)
    └─ OrreryPlasmaBolt.FixedTick(owner, dt)
```

Owner teardown is likewise centralized:

```text
OrreryRuntime.Deactivate(owner)
    -> OrrerySpellLifetime.ForgetOwner(owner)

OrreryController.TearDownCurrentBuild()
    -> OrrerySpellLifetime.ForgetOwner(owner)
```

Ship destruction is centralized:

```text
GameShip.Destroyed Prefix
GameShip.OnDestroy Prefix
    ↓
OrrerySpellLifetime.ForgetShip(ship)
```

World teardown is centralized:

```text
WorldController.OnDestroy Prefix
    ↓
OrrerySpellLifetime.ResetWorld()
```

The two ship-destruction hooks are intentional. `Destroyed` occurs early enough to release spell-owned/presentation objects before native pooled-child teardown; `OnDestroy` remains the defensive Unity destruction path. Cleanup must therefore remain idempotent.

---

# 3. Mandatory Rules for New Persistent Spells

A new persistent Orrery spell **MUST**:

1. Keep its mechanic-specific runtime state in its own spell implementation.
2. Expose only the narrow lifecycle methods it actually needs, normally `FixedTick(GameShip owner, float deltaTime)`, `Forget(GameShip owner)`, and `Reset()`.
3. Add its ongoing fixed-step dispatch to `OrrerySpellLifetime.FixedTickOwner`.
4. Add target/ship teardown forwarding to `OrrerySpellLifetime.ForgetShip` only if the spell genuinely owns target-attached state that must react to destruction.
5. Make `Forget` and `Reset` safe to call after partial cleanup or more than once.
6. Keep gameplay owner-authoritative. Remote presentation must never become a second gameplay simulation.
7. Keep hot-path state bounded and allocation-stable.
8. Preserve separate invocation, gameplay-tail, presentation-tail, and semantic-history lifetimes when the mechanic needs them.

A new persistent Orrery spell **MUST NOT** normally add:

```text
HarmonyPatch(typeof(OrreryController), "FixedUpdate")
HarmonyPatch(typeof(WorldController), "OnDestroy")
HarmonyPatch(typeof(GameShip), "OnDestroy")
HarmonyPatch(typeof(GameShip), "Destroyed")
private static GameShip lastTickOwner
its own local Orrery owner discovery loop
its own owner-replacement detector
its own class-exit integration path
its own global shared-service reset ownership
```

If a proposed spell appears to require one of those, treat that as an architecture review point before implementing it. The default solution is to extend `OrrerySpellLifetime` once, not to create a parallel lifecycle system inside the spell.

---

# 4. What Belongs in `OrrerySpellLifetime`

`OrrerySpellLifetime` is a scheduler and teardown coordinator.

It may own:

- resolving the current local Orrery owner,
- owner replacement tracking,
- stable explicit runtime dispatch,
- owner/class teardown forwarding,
- world teardown forwarding,
- ship-destruction forwarding,
- reset of genuinely shared Orrery spell-runtime services.

It should remain boring, explicit, and easy to audit.

Direct calls such as:

```csharp
OrreryShatterbolt.FixedTick(owner, deltaTime);
OrreryPlasmaBolt.FixedTick(owner, deltaTime);
```

are preferred over reflection, registration frameworks, generic effect graphs, per-cast delegates, or a universal spell engine until actual repeated pressure proves a more complicated dispatcher is necessary.

---

# 5. What MUST Remain Spell-Owned

Do not move these into `OrrerySpellLifetime` merely because multiple spells use conceptually similar ideas:

- damage calculation,
- crit/status rolls,
- elemental focus inheritance,
- CoreCombat semantic/contributor selection,
- target acquisition/reacquisition,
- chains,
- explosions,
- burns/DOTs,
- spread/contagion,
- projectile movement,
- beam/cone/circle geometry,
- per-spell timers,
- cast completion rules,
- release/channel rules,
- `OrreryCasting.CompleteInvocation`,
- `OrreryCasting.Cancel`,
- `OrreryController.StartShuffle`,
- the semantic state a skill exposes to its networking adapter (byte layouts remain in that adapter),
- spell-specific presentation state.

The lifetime layer may tell a spell **when** a target or owner is disappearing. The spell decides **what that means** for its mechanic.

---

# 6. Cast Completion Is NOT Runtime Death

This rule is non-negotiable because both existing persistent spells prove it.

Do not globally stop ticking a spell because:

```text
OrreryCasting.Phase != Invoking
execution completed
satellites started shuffling
primary projectile disappeared
```

Those facts do not prove the runtime is dead.

Examples:

### Shatterbolt

The traveling/chaining cast may complete while Frost Bursts are still expanding. Completed impact history also remains briefly available for presentation reliability.

### Plasma Bolt

The bolt/formula can complete while the runtime is still waiting for a confirmed CoreCombat outcome, while Plasma Burns remain active for five seconds, while burns spread, and while visuals retire.

Future spells may leave fields, mines, summons, delayed detonations, debuffs, afterimages, or other tails after the invocation transaction ends.

Only the spell knows when all of its own runtime state is truly idle.

---

# 7. Shuffle Is NOT a Global Runtime Gate

`OrreryController.IsShuffling(owner)` may reject **starting** a new spell where appropriate.

It must not become a lifetime-level rule that prevents existing effects from ticking.

Persistent gameplay and presentation tails are allowed to continue while satellites shuffle/rearm.

Therefore do not add this to the lifetime dispatcher:

```csharp
if (OrreryController.IsShuffling(owner))
    return;
```

---

# 8. Private Per-Spell State Is Allowed

Private bounded state containers are not an architectural violation.

Shatterbolt and Plasma Bolt may retain private owner-state dictionaries. A future spell may do the same when appropriate.

Those dictionaries are **state lookup**, not lifecycle authority.

They must not independently decide:

- who the local Orrery owner is,
- when their Unity tick hook runs,
- when class exit occurs,
- when world teardown occurs.

Do not build a generic Orrery universal state object simply to eliminate private dictionaries. Extract a shared state container only after several real spells demonstrate a genuinely repeated state-management problem.

---

# 9. Shared Service Ownership

Shared Orrery services belong to the class/lifetime boundary, not to whichever spell happened to use them first.

Current example:

```text
OrreryDamageRouter.Reset()
```

is owned by `OrrerySpellLifetime.ResetWorld()`.

Shatterbolt and Plasma Bolt do not reset the router themselves. They also do not own per-owner router cleanup.

Future shared caches, adapter banks, or runtime services should follow the same rule:

```text
spell Forget/Reset
    -> spell-owned state/resources only

OrrerySpellLifetime / class boundary
    -> genuinely shared service teardown once
```

Do not make an arbitrary spell the global reset owner for a shared subsystem.

---

# 10. Destruction and Idempotence

A ship can be observed through multiple legitimate teardown paths:

- class exit,
- owner replacement,
- controller rebuild,
- `GameShip.Destroyed`,
- `GameShip.OnDestroy`,
- world teardown.

This is expected.

Prefer idempotent cleanup over fragile "cleanup exactly once" flags.

Typical spell cleanup should behave like:

```text
if owner/state absent -> return
release spell-owned pooled/native resources if present
clear target-attached state if present
remove private owner state
```

Target destruction must not be interpreted generically as "cancel the spell." Shatterbolt may reacquire; Plasma may use captured impact/death information; a future tether may end. The lifetime layer forwards the event and the spell owns the response.

---

# 11. Performance and Co-op Requirements

The lifetime dispatcher must remain effectively allocation-free in steady state.

Do not add per-tick:

- LINQ,
- temporary lists,
- closures,
- delegate construction,
- reflection,
- logging/string formatting on normal success paths.

Persistent spell populations must remain bounded. Prefer fixed-capacity arrays/pools or otherwise explicitly bounded storage for high-frequency child effects.

Gameplay remains source-owner authoritative. The lifetime refactor must never cause remote peers to run authoritative damage, target selection, spread, chain, or projectile gameplay merely because they render presentation.

Each persistent runtime must receive at most one authoritative fixed tick per fixed step.

Double ticking is a hard regression.

---

# 12. Time Sources Are Semantic

The lifetime boundary supplies `Time.fixedDeltaTime` to fixed-step spell runtimes and must pass it through unchanged.

Do not use a lifecycle refactor as an excuse to normalize every timer onto one clock. Existing and future spells may legitimately use different Unity time sources for different semantics:

- `Time.fixedDeltaTime` for fixed-step integration,
- `Time.time` for authored gameplay/presentation timestamps,
- `Time.unscaledTime` for confirmation deadlines or other semantics that intentionally survive time scaling.

For example, Shatterbolt presentation-tail expiry and Plasma's pending-outcome/burn timing do not become interchangeable merely because both are timers.

Changing a spell's time source is a behavior change and requires its own explicit review/testing. It is not lifetime cleanup.

---

# 13. Error Isolation: Do Not Blanket-Catch Runtime Ticks

Do not wrap every spell dispatch in a broad `try/catch` merely to keep the lifetime loop running.

A swallowed exception can leave half-mutated gameplay state, native adapters, pooled presentation objects, or semantic state alive while hiding the programming error that caused it.

This restriction applies to authoritative gameplay ticks. Core's presentation,
serialization and network-dispatch boundaries intentionally contain exceptions
so a visual error cannot interrupt native networking or become a peer strike.
Use the shared boundaries; do not copy their catches around gameplay mutation.
Defensive handling around a specific native/API boundary should remain near that
boundary and leave failures observable.

The normal lifetime dispatcher should stay transparent: if a spell runtime has a programming error during development, it should be visible rather than silently converted into corrupted persistent state.

---

# 14. Legacy `OrrerySpellRuntime`

The existing FF/II/LL `OrrerySpellRuntime` remains a separate legacy path for now.

Do not use that fact as precedent for new spells to bypass `OrrerySpellLifetime`.

New persistent explicit spells integrate through `OrrerySpellLifetime`.

A future migration of legacy FF/II/LL into the same boundary should be treated as its own reviewed architecture change because `OrrerySpellRuntime` also has `LateTick`/native virtual-weapon behavior. Do not casually move or double-schedule it while implementing an unrelated spell.

---

# 15. When a New Lifecycle Phase Is Actually Needed

If a future mechanic genuinely needs a lifecycle phase not currently supplied—for example a shared `LateTick` or non-fixed presentation update—do **not** give that spell an isolated Harmony patch by default.

For remote/non-gameplay presentation, first use the existing `CoreNetworkPresentation` render/update callbacks registered by `OrreryNetwork.Initialize`. If a new authoritative gameplay phase is needed, review whether the class boundary should gain one narrow shared phase:

```text
OrrerySpellLifetime.LateTickLocal()
OrrerySpellLifetime.UpdateLocal()
```

Then explicitly dispatch only the runtimes that need it.

Do not add empty interfaces or force every spell to implement every phase.

---

# 16. New Spell Implementation Checklist

Before considering a persistent spell implementation complete, verify:

```text
[ ] Spell mechanics/state live in the spell file.
[ ] Ongoing fixed-step work is dispatched by OrrerySpellLifetime.
[ ] No spell-local OrreryController.FixedUpdate patch was added.
[ ] No spell-local world teardown patch was added.
[ ] No spell-local owner replacement tracker was added.
[ ] No spell-local class exit integration was added.
[ ] Target destruction forwarding is added centrally only if required.
[ ] Forget(owner) is idempotent.
[ ] Reset() releases all spell-owned persistent resources.
[ ] Shared services are not reset by the spell.
[ ] Cast completion does not accidentally kill intended gameplay/presentation tails.
[ ] Shuffle does not freeze existing tails.
[ ] Runtime state is bounded.
[ ] No steady-state per-tick allocations were introduced by lifecycle plumbing.
[ ] Existing time-source semantics were preserved unless deliberately changed/tested.
[ ] Gameplay remains owner-authoritative.
[ ] Remote presentation cannot apply gameplay.
[ ] Exactly one authoritative tick path exists.
```

---

# 17. Regression Validation

Architecture changes and new persistent spells must be tested for timing and teardown regressions, not merely compilation.

## Shatterbolt reference checks

Verify ordinary and inherited-chain behavior, target death/reacquisition, final burst behavior, shuffle on cast completion, post-cast Frost Burst completion, presentation-tail retirement, and immediate class-exit cleanup of orb/bursts/hidden sources.

Strong double-tick symptoms include:

- projectile travel appearing roughly twice as fast,
- Frost Bursts expanding roughly twice as fast,
- shortened presentation tail,
- duplicated damage or presentation publication.

## Plasma Bolt reference checks

Verify the initial bolt occurs once, formula completion still succeeds, confirmed actual initial damage remains the burn budget, burn duration/total remain exact, spread cadence and reinfection lockout remain correct, target destruction releases faux-burning presentation before native pooled-child teardown, and burns survive initial cast completion but die on owner/class teardown.

Strong double-tick/lifetime symptoms include:

- burn completing in roughly half its intended duration,
- spread scanning/propagating too rapidly,
- pending combat confirmations being consumed or pruned unexpectedly,
- accelerated visual retirement.

## Owner/class transition checks

With a persistent effect alive, leave Orrery, replace the local owner, or otherwise trigger class teardown. Expected result: authoritative runtime state and spell-owned native/pooled resources are cleaned immediately, and old-owner effects do not resume if Orrery is entered again.

## World transition checks

Unload/reload while representative persistent states are alive: traveling projectile, expanding child effect, pending combat confirmation, active target-attached effect, and retiring presentation. Expected result: no stale owner references, leaked gameplay state, or VFX carried into the next world; the next Orrery activation works normally.

## Ship destruction checks

Destroy the Orrery owner and representative spell targets. Both `GameShip.Destroyed` and `OnDestroy` may notify cleanup; behavior must remain correct and idempotent.

## Legacy regression check

Until legacy FF/II/LL are deliberately migrated, verify Magma Cannon, Cone of Cold, and Tesla still use their existing scheduling and have not acquired an extra tick path.

---

# 18. Refactor Guardrails

Before changing this architecture, require a concrete problem demonstrated by live spell implementations.

Do not refactor it merely to obtain:

- fewer static classes,
- fewer explicit dispatch lines,
- an interface for aesthetic consistency,
- a generic spell engine,
- a generic effect graph,
- a service locator,
- dependency injection,
- reflection-based discovery,
- one universal `Active` state,
- one universal spell-state struct.

A replacement architecture must demonstrate that it preserves or improves all of these properties:

1. one authoritative local owner-resolution path,
2. one fixed-step integration boundary for explicit persistent spells,
3. stable deterministic dispatch,
4. no double ticking,
5. class exit/owner replacement cleanup,
6. early native `Destroyed` cleanup where required,
7. Unity `OnDestroy` fallback cleanup,
8. world teardown,
9. gameplay tails independent from invocation lifetime,
10. presentation tails independent from gameplay lifetime,
11. spell-specific mechanics remain spell-owned,
12. bounded/allocation-stable hot paths,
13. owner-authoritative co-op gameplay.

If a proposed abstraction cannot explain how it preserves those properties, do not merge it.

---

# 19. Review Rule for Future Agents

When implementing or reviewing an Orrery spell, search the new code for these warning signs:

```text
HarmonyPatch + FixedUpdate
HarmonyPatch + WorldController.OnDestroy
HarmonyPatch + GameShip.OnDestroy
HarmonyPatch + GameShip.Destroyed
lastTickOwner
CoreClassRuntime.CurrentContext inside a persistent spell tick wrapper
OrreryDamageRouter.Reset inside a spell
```

Any match requires an explicit justification.

Also inspect `OrrerySpellLifetime` before inventing new lifecycle plumbing. If the required event already exists there, use it.

The desired maintenance pattern is deliberately mundane:

```text
new spell mechanic
    + narrow spell-owned runtime
    + one explicit lifetime dispatch line
    + optional central destruction forwarding
```

That is the standard.

---

# 20. Documentation Ownership

This file is the maintained authoritative gameplay-lifetime reference. It does not govern presentation transport or override the shared presentation callback registry.

Historical migration plans are intentionally not retained as competing implementation guidance after their durable rules and regression checks have been incorporated here.

Always treat current live source as authoritative for exact method names/signatures. If future implementation materially changes the lifetime architecture after an explicit architecture review, update this standard in the same change. Do not allow code and lifetime documentation to drift apart.
