# Orrery Status — D / TBD

**Branch:** `skill-trees`  
**Date:** 2026-09-11  
**Meaning of D:** implemented in source. D does **not** imply compiler, in-game, or coop verification unless explicitly stated.

This is the short current-status companion to `ORRERY_IMPLEMENTATION_HANDOFF.md`. Where the older handoff describes deferred input, conventional-weapon suppression, proof-only spell behavior, or logical-only shuffle, this file supersedes it.

## D — implemented in source

### Class / progression foundation

- [D] Standalone Orrery class registration through shared Core class lifecycle.
- [D] Orrery root/progression infrastructure.
- [D] Progression currency named **Knowledge Points**.
- [D] Removed invented placeholder `Arcane Focus` / `+10% spell power` talent.
- [D] Baseline 2 active satellites.
- [D] Baseline 2-rune formula capacity.
- [D] Five persisted satellite design slots reserved for later progression.

### Formula / casting model

- [D] Fire / Ice / Lightning element identities.
- [D] Canonical unordered recipe keys.
- [D] Stable 2-rune recipe identities: `FF`, `II`, `LL`, `FI`, `FL`, `IL`.
- [D] Pure recipe IDs/executors reserved as FF / II / LL V0 spell families.
- [D] Mixed recipes registered but intentionally have no executors yet.
- [D] Semantic lock / invoke / invoke-release API separated from physical input bindings.
- [D] Complete-formula invocation requirement.
- [D] Unimplemented mixed recipe invocation exits cleanly rather than trapping cast state.

### Player input

- [D] Native `InputController.UpdateFirePrimary` integration.
- [D] `FirePrimary` is Orrery's formula-lock input while Orrery is active.
- [D] Native `InputController.UpdateActivateActivatable` integration.
- [D] `ActivateActivatable` button-down is Orrery's invoke input while Orrery is active.
- [D] `ActivateActivatable` button-up is forwarded to the committed live spell for FF detonation / LL channel release.
- [D] Lock selection uses the live satellite snapshot's existing order and chooses the first valid unlocked formula satellite. No extra Orrery sorting layer was invented.
- [D] Input integration runs inside Star Vortex's normal input-update path rather than global mouse polling, preserving native pause/menu/chat/fire gating.
- [D] Formula locking is blocked during physical shuffle/rearm.
- [D] Concise event logging for lock success/failure, captured element, angle, recipe, invoke result, and handled release.
- [D] `PrimaryAndActivatable` is deliberately not overloaded for Orrery because the class requires two distinct semantic inputs.

### Conventional weapon suppression

- [D] Normal `PrimaryWeapon` activation suppressed while local Orrery is active.
- [D] Normal `SecondaryWeapon` activation suppressed while local Orrery is active.
- [D] Normal `Special` activation suppressed while local Orrery is active.
- [D] Normal `AutoSpecial` activation suppressed while local Orrery is active.
- [D] Dedicated Utility inputs remain untouched.
- [D] Suppression does not unequip or mutate the loot items; they remain available as elemental focus/stat sources.
- [D] Manual activation is blocked at both native `GameShip.StartActivating` overloads.
- [D] Autonomous/native weapon activation is blocked through `Activatable.CanActivate` for actual equipped weapon items.
- [D] Hidden Orrery virtual spell weapons are exempt because they point at a focus slot but are not the slot's actual equipped object.
- [D] Native launcher-family `CanActivate` overrides were checked against the supplied decompile and route through `base.CanActivate()`, so the suppression gate remains effective there.

### Satellites / orbit

- [D] Physical local `GameShip` satellites spawned from a private runtime squadron copy.
- [D] Exact temporary spawn source resolved as `Base/Squadrons/Skeran/LeviathanTest`.
- [D] Semantic satellite IDs published separately from construction order assumptions.
- [D] Baseline concentric orbit lanes.
- [D] Baseline orbit radius / lane spacing / angular-speed knobs.
- [D] Locked satellites stop angular progression.
- [D] Satellites follow current owner position through class-owned desired poses.
- [D] Ordinary target selection rejects semantic Orrery satellites while physical collision/hits remain native.
- [D] Satellite autonomous `OrbitAIShip.RunGambits` suppressed.
- [D] Safe direct-object teardown rather than gameplay `Destroyed()` during class/world cleanup.

### Physical shuffle / rearm

- [D] Successful FF / II / LL completion uses a physical post-cast shuffle.
- [D] Unimplemented mixed-recipe invocation uses the same physical shuffle rather than logical reset only.
- [D] Explicit local formula cancellation through `OrreryControl.Shuffle` uses the physical shuffle.
- [D] All live, enabled formula satellites participate in baseline shuffle.
- [D] Each participant rolls a fully random target orbital phase in `[0, 360)` with no minimum displacement.
- [D] Baseline shuffle duration = `0.50 s`.
- [D] Satellites travel on direct straight owner-relative paths rather than circular orbit paths.
- [D] Start and target positions remain owner-relative while the owner moves during shuffle.
- [D] Normal orbit advancement/correction is suspended for the entire shuffle fixed step interval.
- [D] Satellite colliders are cached at build time, disabled during shuffle, and restored to their previous enabled state after landing.
- [D] Canonical orbit phase is committed to the rolled target when each shuffle finishes.
- [D] Lock input is unavailable until shuffle/rearm finishes.

### Satellite durability

- [D] Incoming satellite damage multiplier applied at native `GameShip.Damage` boundary.
- [D] Baseline incoming multiplier = `0.50`.

### Focus / loot foundation

- [D] First matching equipped elemental focus resolved in native slot order.
- [D] Fire focus = Thermal.
- [D] Ice focus = Cold.
- [D] Lightning focus = Electric.
- [D] Effective item level uses native `BaseRequiredLevel` / stat level.
- [D] This correctly preserves the intended rule where e.g. displayed level 2 with `-2 Level Requirement` has effective/stat level 4.
- [D] Native weapon damage is not used as Orrery spell baseline damage.

### Spell power

- [D] Mean reference DPS helper: `726 * (1 + 0.02 * (effectiveItemLevel - 1))`.
- [D] Median reference DPS helper retained: `546 * (1 + 0.02 * (effectiveItemLevel - 1))`.
- [D] Mean is current default.

### FF — guided explosive fireball

- [D] FF keeps stable spell id/effect identity `Magma Cannon`, but its hidden native delivery adapter is now **Inferno Cannon**, because supplied item data verifies Magma Gun has `explosiveRadius = 0` while Inferno is the native thermal explosive-projectile family.
- [D] Native Inferno projectile spawning / AoE lifecycle is reused rather than rebuilding explosion behavior.
- [D] FF direct packet is normalized against one second of Orrery mean reference DPS at neutral `1.0x` tuning.
- [D] Fireball launch velocity baseline = `0.65x` native Inferno velocity.
- [D] Fireball cursor guidance uses bounded heading change at `120 deg/s`.
- [D] Fireball explosion radius baseline = `3 m`.
- [D] Fireball lifetime baseline = `2 s`.
- [D] RMB release detonates the current live fireball through native projectile expiry/detonation.
- [D] Native `ExplosiveProjectile` deliberately skips the directly struck object in its AoE loop; Orrery adds exactly one explosion-equivalent native-routed packet to that direct target so it can receive both direct and explosion components.
- [D] Native projectile capture occurs after `Launcher.AddProjectile`; Orrery does not duplicate native projectile construction.

### II — Cone of Cold

- [D] II is registered as **Cone of Cold** / custom execution rather than a one-second mechanical Cryo stream.
- [D] II performs one mechanical Cold cone hit per cast.
- [D] Cone range baseline = `7.5 m`.
- [D] Cone angle baseline = `70 deg` total.
- [D] II adds flat `+50 percentage points` to the captured baseline Freeze/status chance, clamped to 100%.
- [D] Cone damage is normalized to one second of mean reference DPS at neutral `1.0x` tuning, with crit expectation normalized before the per-target crit roll.
- [D] Custom cone packets route through Star Vortex's native `NetCombat.RouteDamage` boundary; the internal overload is resolved once and cached rather than reimplementing multiplayer damage routing.
- [D] Native Cryo projectiles are presentation-only: their hidden launcher has zero authored damage/status/crit.
- [D] Visual burst = 9 Cryo projectiles over a 60-degree spread at `1.30x` native Cryo velocity.
- [D] Cryo native charge penalty is neutralized with `unchargedMulitplier = 1` for the visual emission.
- [D] Presentation-only Cryo projectiles are explicitly tracked and denied native `Projectile.HitObject` mechanics, fuzzy-projectile secondary effects, and explosive expiry effects, so owner/global modifiers cannot accidentally make the visual burst damaging.
- [D] Presentation projectile tracking is cleared on native pool return and world teardown.

### LL — held Tesla channel

- [D] LL reuses hidden native Tesla Coil beam/chain machinery.
- [D] Neutral cached Tesla output is normalized to Orrery mean reference DPS.
- [D] LL begins at `2.0x` reference DPS.
- [D] LL fades to `1.0x` reference DPS over `1.0 s`.
- [D] LL can remain held indefinitely after reaching its minimum multiplier.
- [D] RMB release ends the channel and starts shuffle/rearm.
- [D] Explicit tuning knobs exist for initial multiplier, minimum multiplier, and fade duration.
- [D] The cached hidden Tesla adapter is restored to neutral `1.0x` when the channel ends so repeated casts do not compound scaling.

### Spell runtime bounds

- [D] Hidden native spell weapons generate zero weapon heat and are cached per owner.
- [D] At most one active Orrery invocation per owner.
- [D] Custom cone target dedupe is bounded to one cast and reused per owner.
- [D] Native damage reflection metadata and argument storage are cached/reused rather than rediscovered per hit.
- [D] Presentation-only Cryo projectile tracking is bounded by the small native projectile pool/lifetime and removes entries when projectiles return to the pool.

### Network / shared architecture

- [D] Orrery remains owner-authoritative through shared Core class/execution/network infrastructure.
- [D] Cast presentation state has stable compact network representation.
- [D] Native FF projectile / LL beam damage paths retain native Star Vortex attack families.
- [D] II custom damage explicitly enters native `NetCombat.RouteDamage` rather than directly mutating remote targets.
- [D] Shared combat provenance / semantic contributor infrastructure remains reused rather than duplicated.

## TBD — not implemented yet

### Spell tuning / visual polish

- [TBD] In-game balance validation of FF `0.65x` velocity, `120 deg/s` turning, `3 m` explosion radius, and `2 s` lifetime.
- [TBD] FF bespoke large-boom visual treatment beyond the native Inferno explosion presentation.
- [TBD] In-game validation/tuning of II `7.5 m / 70 deg / 9 projectile / 60 deg visual fan` baselines.
- [TBD] In-game validation that the II visual burst reads as dense enough without unnecessary extra projectiles.
- [TBD] In-game validation of LL `2.0 -> 1.0` envelope and native chain behavior during an indefinite hold.

### Formula reset / presentation

- [TBD] Locked slow satellite self-spin.
- [TBD] Firing fast satellite self-spin.
- [TBD] Physical shuffle triggered specifically when a participating satellite becomes unavailable/disabled mid-formula.
- [TBD] Decide whether any future non-formula satellite kinds should also participate in whole-Orrery shuffle; baseline currently shuffles enabled formula satellites only.

### Wheel / visuals

- [TBD] Halo-sector wheel rendering.
- [TBD] Two-layer transparent Halo-sector visual experiment.
- [TBD] Wheel rotation/follow behavior beyond current fixed baseline data.
- [TBD] Later predictable fixed-degrees-per-second wheel follow.

### Full focus modifier inheritance

- [TBD] Broad resolved focus modifier snapshot.
- [TBD] Crit chance inheritance from the actual elemental focus for all spells.
- [TBD] Crit damage inheritance from the actual elemental focus.
- [TBD] Status/debuff chance inheritance from the actual elemental focus.
- [TBD] Range / projectile speed / pierce / chain / duration / other native roll inheritance.
- [TBD] Per-spell flat/additive overrides on top of inherited values.
- [TBD] Exact handling for modifiers whose mechanical concept does not exist on a particular spell delivery type.
- [TBD] Until this layer exists, II's captured crit/status baseline comes from its hidden native reference adapter, not the player's full focus modifier package.

### Satellite durability / recovery

- [TBD] Prevent native destruction at disable threshold.
- [TBD] Disabled/restoring satellite state.
- [TBD] Half-opacity while restoring.
- [TBD] Intangible/non-colliding while restoring.
- [TBD] Repair rate knobs.
- [TBD] Restore functionality only at full hull.
- [TBD] Formula reset + physical shuffle when a participating satellite becomes unavailable.

### Player-authored satellites / progression

- [TBD] Satellite editor/save/load flow.
- [TBD] Persistent designs for inactive satellite slots.
- [TBD] Third active satellite progression breakpoint.
- [TBD] 3-rune recipes and unlock nodes.
- [TBD] Fourth/fifth active satellites.
- [TBD] Mixed-pair spell content.
- [TBD] Later higher-arity spell content.

### Multiplayer / remote presentation

- [TBD] Remote physical satellite visibility/motion validation.
- [TBD] Remote lock/spin/shuffle presentation.
- [TBD] Host-owned Orrery coop test.
- [TBD] Client-owned Orrery coop test.
- [TBD] Validate whether native projectile replication is sufficient for continuously steered FF; add compact steering presentation data only if required.
- [TBD] LL held-channel start/release remote presentation validation.
- [TBD] II custom cone native-route validation against both host-owned and client-owned targets.

### Input / UX follow-ups

- [TBD] Controller UX for the native combined `PrimaryAndActivatable` action; it currently does nothing for Orrery by design because Orrery needs two distinct inputs.
- [TBD] Player-facing feedback for missing elemental focus.
- [TBD] Player-facing feedback for unimplemented mixed recipe beyond logs.
- [TBD] F10 Orrery debug panel/readout. Current implementation provides log-level visibility only.

### Verification

- [TBD] Unity/mod compiler pass on the complete current branch.
- [TBD] Local in-game smoke test.
- [TBD] Verify FF's runtime Inferno projectile is the expected `ExplosiveProjectile` prefab family in the packaged game.
- [TBD] Verify cached reflective `NetCombat.RouteDamage` resolution against the actual packaged game assembly.
- [TBD] Warp/world-transition test.
- [TBD] Class spec/un-spec cleanup test.
- [TBD] Coop host/client test.
- [TBD] Performance profiling under combat load.

## Current recommended next chunk

Stop adding gameplay features until this slice compiles and runs. The next highest-value chunk is **verification + correction**:

1. Unity/mod compiler pass and fix every error/warning caused by this slice.
2. Local smoke: FF launch/steer/release/direct-hit; II cone + visual-only burst; LL hold/release; bad mixed recipe; shuffle collision restoration.
3. Warp and class spec/un-spec cleanup tests.
4. Host-owned and client-owned coop smoke, with special attention to FF steering, II routed cone damage, LL release, and remote shuffle presentation.
5. Only after that, choose between full focus-modifier inheritance and satellite disable/repair as the next feature chunk.
