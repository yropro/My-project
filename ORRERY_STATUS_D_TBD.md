# Orrery Status — D / TBD

**Branch:** `skill-trees`  
**Date:** 2026-09-11  
**Meaning of D:** implemented in source. D does **not** imply compiler, in-game, or coop verification unless explicitly stated.

This is the short current-status companion to `ORRERY_IMPLEMENTATION_HANDOFF.md`. Where the older handoff says input or conventional-weapon suppression was deferred, this file supersedes it.

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
- [D] Pure recipe IDs/executors reserved as Magma Cannon / Cryo Gun / Tesla Coil.
- [D] Mixed recipes registered but intentionally have no executors yet.
- [D] Semantic lock / invoke API separated from physical input bindings.
- [D] Complete-formula invocation requirement.
- [D] Unimplemented mixed recipe invocation exits cleanly rather than trapping cast state.

### Player input — this chunk

- [D] Native `InputController.UpdateFirePrimary` integration.
- [D] `FirePrimary` is Orrery's formula-lock input while Orrery is active.
- [D] Native `InputController.UpdateActivateActivatable` integration.
- [D] `ActivateActivatable` is Orrery's invoke input while Orrery is active.
- [D] Lock selection uses the live satellite snapshot's existing order and chooses the first valid unlocked formula satellite. No extra Orrery sorting layer was invented.
- [D] Input integration runs inside Star Vortex's normal input-update path rather than global mouse polling, preserving native pause/menu/chat/fire gating.
- [D] Concise event logging for lock success/failure, captured element, angle, recipe, and invoke result.
- [D] `PrimaryAndActivatable` is deliberately not overloaded for Orrery because the class requires two distinct semantic inputs.

### Conventional weapon suppression — this chunk

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

### Current native-backed spell prototypes

- [D] `FF` currently uses hidden native Magma Gun launcher/projectile machinery.
- [D] Magma output normalized against one second of Orrery mean reference DPS at neutral `1.0x` tuning.
- [D] Magma native 0.15 s reload no longer owns Orrery formula cadence.
- [D] `II` currently uses hidden native Cryo Gun `ChargingLauncher` machinery.
- [D] Native Cryo preparation penalty neutralized with `unchargedMulitplier = 1`.
- [D] `LL` currently uses hidden native Tesla Coil beam/chain machinery.
- [D] Hidden native spell weapons generate zero weapon heat and are bounded/cached per owner.
- [D] At most one active Orrery invocation per owner in this prototype runtime.

### Network / shared architecture

- [D] Orrery remains owner-authoritative through shared Core class/execution/network infrastructure.
- [D] Cast presentation state has stable compact network representation.
- [D] Shared combat provenance / semantic contributor infrastructure remains reused rather than duplicated.

## TBD — not implemented yet

### Spell behavior matching the latest design

- [TBD] **FF guided fireball:** slow chunky projectile that tracks the cursor with bounded turn rate.
- [TBD] **FF release detonation:** RMB release detonates the live fireball.
- [TBD] **FF direct-hit + explosion:** struck target can intentionally take both components.
- [TBD] FF large-boom/explosion-radius tuning and visual treatment.
- [TBD] **II final Cone of Cold behavior:** one mechanical cone hit rather than the current one-second native Cryo stream prototype.
- [TBD] II visual-only dense Cryo projectile burst at about `1.30x` native projectile velocity.
- [TBD] II flat **+50 percentage points Freeze chance**.
- [TBD] **LL final held channel:** starts at 200% reference DPS, fades to 100% over one second, then can be held indefinitely.
- [TBD] LL `InitialDpsMultiplier`, `MinimumDpsMultiplier`, and `FadeSeconds` knobs.
- [TBD] RMB-release plumbing for active FF/LL interactions.

### Formula reset / presentation

- [TBD] Physical post-cast shuffle animation.
- [TBD] Bad-input shuffle animation. Current mixed-recipe failure clears the logical formula but does not yet perform the designed physical shuffle.
- [TBD] Cancellation/death shuffle path.
- [TBD] Full-random target orbit phases after shuffle.
- [TBD] ~0.5 s straight-line owner-relative shuffle movement.
- [TBD] Temporary collision suppression during shuffle.
- [TBD] Locked slow self-spin.
- [TBD] Firing fast self-spin.

### Wheel / visuals

- [TBD] Halo-sector wheel rendering.
- [TBD] Two-layer transparent Halo-sector visual experiment.
- [TBD] Wheel rotation/follow behavior beyond current fixed baseline data.
- [TBD] Later predictable fixed-degrees-per-second wheel follow.

### Full focus modifier inheritance

- [TBD] Broad resolved focus modifier snapshot.
- [TBD] Crit chance inheritance for all applicable spells.
- [TBD] Crit damage inheritance.
- [TBD] Status/debuff chance inheritance.
- [TBD] Range / projectile speed / pierce / chain / duration / other native roll inheritance.
- [TBD] Per-spell flat/additive overrides on top of inherited values.
- [TBD] Exact handling for modifiers whose mechanical concept does not exist on a particular spell delivery type.

### Satellite durability / recovery

- [TBD] Prevent native destruction at disable threshold.
- [TBD] Disabled/restoring satellite state.
- [TBD] Half-opacity while restoring.
- [TBD] Intangible/non-colliding while restoring.
- [TBD] Repair rate knobs.
- [TBD] Restore functionality only at full hull.
- [TBD] Formula reset when a participating satellite becomes unavailable.

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
- [TBD] FF guided projectile steering replication strategy if native projectile sync is insufficient for continuous guidance.
- [TBD] LL held-channel start/release replication.

### Input / UX follow-ups

- [TBD] Controller UX for the native combined `PrimaryAndActivatable` action; it currently does nothing for Orrery by design because Orrery needs two distinct inputs.
- [TBD] Player-facing feedback for missing elemental focus.
- [TBD] Player-facing feedback for unimplemented mixed recipe beyond logs.
- [TBD] F10 Orrery debug panel/readout. Current chunk provides log-level visibility only.

### Verification

- [TBD] Unity/mod compiler pass on the complete current branch.
- [TBD] Local in-game smoke test.
- [TBD] Warp/world-transition test.
- [TBD] Class spec/un-spec cleanup test.
- [TBD] Coop host/client test.
- [TBD] Performance profiling under combat load.

## Current recommended next chunk

Do **not** expand the spell tree yet. The highest-value next chunk is to replace the three native proof-of-plumbing spell behaviors with the actual agreed V0 identities:

1. FF guided/release-detonated fireball.
2. II one-hit Cone of Cold + presentation-only Cryo burst + flat +50 freeze chance.
3. LL held channel with `2.0 -> 1.0` DPS fade over 1 second.
4. Add input-release routing needed by FF and LL.
5. Add real post-cast/bad-input shuffle after those spell lifetimes are authoritative.

After that, do a compile/local smoke pass before adding wheel visuals or progression content.
