# Orrery Implementation Handoff

> **Historical snapshot (2026-09-11); not current instructions or status.**
> Labeled on 2026-09-14. Preserve the findings below as evidence of that snapshot;
> do not copy its tuning, native signatures, missing-feature list or next-step plan
> into current work without checking the source. Start with
> [DOCUMENTATION.md](DOCUMENTATION.md) and [current networking](Assets/Leviathan/Content/Scripts/NETWORKING.md).

**Branch:** `skill-trees`  
**State:** runtime-foundation chunk implemented; not yet claimed compile/playtest/coop verified  
**Date:** 2026-09-11

This document records the Orrery implementation work completed so far, the contracts it establishes, and the intentional stopping point before the next chunk.

---

## 1. Current class contract

Orrery is a standalone class using the shared Core class/combat/network architecture. It does not depend on Leviathan anatomy semantics.

Current baseline:

- 2 active formula satellites.
- 2-rune formula capacity.
- Fire / Ice / Lightning elements.
- Canonical unordered pair recipes:
  - `FF` -> Magma Cannon
  - `II` -> Cryo Gun
  - `LL` -> Tesla Coil
  - `FI` -> registered identity, not implemented yet
  - `FL` -> registered identity, not implemented yet
  - `IL` -> registered identity, not implemented yet
- Five satellite design slots are reserved in the runtime contract for later progression even though only two are active at baseline.
- Orrery progression currency is **Knowledge Points**.

Recipe identity is count-based and unordered. Lock order does not change formula identity.

---

## 2. Runtime files added / materially changed

### `OrreryRuntime.cs`

Baseline resolved state now uses:

- `BaseSatelliteCount = 2`
- `BaseFormulaSatelliteCount = 2`
- 5 reserved design slots
- 50% incoming satellite damage multiplier
- equal 120 degree Fire/Ice/Lightning sectors
- resolved orbit radius / lane spacing / angular-speed values

The runtime continues to separate stable resolved build state from live cast/orbit state.

### `OrrerySpellRegistry.cs`

The registry now contains stable IDs for all six 2-rune recipes.

Pure recipes are executable:

- spell 1: Magma Cannon
- spell 2: Tesla Coil
- spell 3: Cryo Gun

Mixed recipes have stable IDs but null executors. This is intentional so later content does not change recipe or network identity.

### `OrreryOrbit.cs`

Added class-local orbit state for up to the current bounded satellite maximum.

Responsibilities:

- canonical orbital angle per semantic satellite ID
- resolved radius per satellite lane
- locked satellites freeze orbital progression
- desired world pose calculation
- no spell/input semantics

### `OrreryController.cs`

Added the physical Orrery runtime coordinator.

Responsibilities:

- build physical satellite `GameShip`s for the local Orrery owner
- publish satellite intent before physical construction
- publish the final live semantic satellite set only after a successful build
- drive class-owned orbital movement
- suppress autonomous `OrbitAIShip.RunGambits` for semantic Orrery satellites
- make satellites untargetable through normal `GameShip.CanBeTargetedBy` selection while leaving them physically hittable
- clean up satellites on class/world teardown without invoking normal gameplay destruction
- tick `OrrerySpellRuntime`

The temporary spawn source is the existing mod asset:

`Base/Squadrons/Skeran/LeviathanTest`

The runtime now resolves that exact path/name pair. It first attempts `Resources.Load<SquadronBase>()` by the full verified resource path and only then falls back to loaded-object discovery requiring both `filename == LeviathanTest` and `resourcePath == Base/Squadrons/Skeran`.

The carrier is only a source of private runtime NPC/Ship definitions. The returned `Squadron` is trimmed to the owner plus requested satellites and follower definitions are converted to native `OrbitInner` AI before spawning.

### `OrrerySatelliteDamage.cs`

Added the baseline incoming satellite damage multiplier at the native `GameShip.Damage` boundary.

Current behavior:

- semantic Orrery satellites receive `ResolvedState.SatelliteIncomingDamageMultiplier`
- baseline multiplier is `0.50`
- damage data is modified in place
- no second/custom damage route is created

Satellite death interception / 1-HP disable behavior is deliberately **not** implemented yet. That needs to be designed together with disabled collider/blocking behavior so an immortal disabled satellite cannot become a projectile shield.

### `OrreryControl.cs`

Added semantic control operations independent from actual input bindings:

- `TryLock(owner, satelliteId, ...)`
- `TryInvoke(owner)`
- `Shuffle(owner)`
- captured-element query

Important current behavior:

- invocation requires the current formula capacity to be complete
- invoking a registered-but-unimplemented mixed recipe consumes/releases the formula cleanly instead of trapping the cast in `Invoking`
- actual mouse/Rewired bindings are intentionally deferred

### `OrreryFocusResolver.cs`

Added first-equipped elemental focus resolution.

Element mapping:

- Fire -> Thermal
- Ice -> Cold
- Lightning -> Electric

The resolver scans equipped slots in slot order and chooses the first supported `Activatable` carrying the matching native damage type.

Supported native activatable families currently include launcher, beam, torch, pulse, assault, conduit, laser spinner, halo, and drone dropper families where a direct native `damageType` exists.

The focus API returns:

- owner
- element
- native damage type
- source `Activatable`
- slot index
- effective item level

### Effective item level verification

Star Vortex already stores the item's higher stat level in `Equippable.level` / `BaseRequiredLevel`.

A negative level-requirement modifier lowers the displayed/required level but does not lower this stat level. Therefore an item whose displayed requirement is 10 because of `-5 Level Requirement` can already expose effective/stat level 15 through `BaseRequiredLevel`.

Orrery therefore uses `BaseRequiredLevel` directly rather than reverse-engineering level-requirement modifiers.

### `OrrerySpellPower.cs`

Added the shared spell-output reference curve established during weapon-DPS analysis:

```text
Mean reference DPS   = 726 * (1 + 0.02 * (effectiveItemLevel - 1))
Median reference DPS = 546 * (1 + 0.02 * (effectiveItemLevel - 1))
```

Mean is the current default reference mode.

The helper intentionally does not choose the focus or author spell behavior. It only converts an already-resolved effective item level into the shared reference output.

### `OrrerySpellRuntime.cs`

Added bounded native-backed execution for the three pure formulas.

Per owner it holds at most:

- one cached virtual Magma reference weapon
- one cached virtual Cryo reference weapon
- one cached virtual Tesla reference weapon
- one active invocation

The virtual weapons use real Star Vortex native attack implementations rather than reimplementing projectile/beam logic.

Native reference assets:

- `Base/Items/PrimaryWeapon/Magma Gun`
- `Base/Items/PrimaryWeapon/Cryo Gun`
- `Base/Items/PrimaryWeapon/Tesla Coil`

Each virtual weapon is:

- created from the native `ItemBase`
- equipped virtually against the focus slot
- hidden visually as an equipped item
- set to zero heat generation
- pooled once when constructed
- reused while the focus identity/effective level remains unchanged

#### FF / Magma Cannon

Uses the native Magma Gun launcher/projectile family.

Current neutral tuning:

- integrated reference window: 1.0 s
- damage multiplier: 1.0

The projectile's expected local hit output is normalized to one second of Orrery mean reference DPS.

Magma's native `0.15 s` launcher reload is **not** allowed to become Orrery formula cadence. Pool construction deliberately sees the native reload first so `PopulatePool()` remains well-behaved; after the pool exists, the virtual Magma launcher's `BaseReloadTime` is set to `0`.

The cast still emits exactly one activation because the discrete invocation is deactivated after its first fixed-update execution tick.

Formula/rearm cadence will therefore be owned by Orrery casting/orbit behavior rather than hidden Magma Gun reload state.

#### II / Cryo Gun

Uses native Cryo Gun `ChargingLauncher` projectile-stream behavior.

Current neutral tuning:

- output duration: 1.0 s
- DPS multiplier: 1.0
- `unchargedMulitplier = 1.0`

The latter intentionally removes the native Cryo weapon's normal charge-output penalty because formula assembly is Orrery's preparation step. Native projectile cadence, projectile behavior, and status semantics remain native.

#### LL / Tesla Coil

Uses the native Tesla Coil `BeamWeapon` implementation.

Current neutral tuning:

- output duration: 1.0 s
- DPS multiplier: 1.0

The native beam and chain behavior remain intact. `LateUpdate()` is driven by the Orrery controller because the virtual weapon is not part of the owner's ordinary equipped-item update loop.

---

## 3. Native behavior intentionally reused

The current implementation deliberately leans on verified Star Vortex code instead of cloning it.

Verified examples used by this chunk:

- `SquadronBase.GetSquadron()` produces a private runtime `Squadron` and builds `squadronBasePath` from `resourcePath + "/" + filename`.
- `IShippable.GetShip()` is used to obtain each private follower `Ship` definition before build.
- `Launcher.FixedUpdate()` performs activation, reload, muzzle, and charge updates.
- `Launcher.BaseReloadTime` is writable.
- `Launcher.PopulatePool()` sizes the projectile pool from native projectile lifetime / rate of fire, therefore cadence normalization is done only **after** pooling for Magma.
- `ChargingLauncher` keeps native launcher mechanics while exposing the charge-output multiplier used to bypass Cryo's preparation penalty.
- `BeamWeapon.LateUpdate()` remains necessary for the native beam path.
- `Activatable.CanActivate()` still enforces normal owner/slot/disabled/cooldown safety around the virtual weapon.

---

## 4. Progression cleanup

The old scaffold-only `Arcane Focus` node and invented `+10% spell power` effect were removed.

Current Orrery tree content is intentionally just the class/root infrastructure until actual progression nodes are designed.

The point-currency display is now **Knowledge Points** rather than the stale `Orrery Points` placeholder.

---

## 5. Multiplayer / authority state

Current intent remains owner-authoritative:

- local Orrery owner samples satellite angle/element at lock time
- owner authors formula/invocation
- native projectile/beam machinery handles its existing combat/network path
- existing `OrreryNetwork` publishes irreducible cast presentation state

No claim is made yet that physical satellite visibility/motion and the three spells are fully verified from both host-owned and client-owned Orrery perspectives. That is still a required coop test phase.

---

## 6. Deliberately deferred from this chunk

The following are **not implemented yet** and should not be inferred from the current files:

1. Actual player input bindings.
   - `OrreryControl` exists, but no LMB/RMB/Rewired adapter has been installed.
   - Final lock-order UX remains open.

2. Conventional weapon firing suppression while Orrery is active.

3. Full focus stat inheritance.
   - current focus resolution establishes source identity + effective item level
   - broad non-damage roll capture/inheritance remains to be implemented
   - native weapon damage itself must remain excluded from focus inheritance

4. Formula wheel / halo-sector rendering.

5. Locked/firing satellite self-spin presentation.

6. Post-cast straight-line shuffle/rearm animation.

7. Disabled satellite state / repair / collider policy.

8. Satellite player-design editor/save flow.

9. Third/fourth/fifth active satellite progression.

10. Mixed-pair spell executors.

11. Full remote presentation of dynamic Orrery casting state.

12. Compile + in-game + coop verification.

---

## 7. Important non-decisions preserved

Do not silently resolve these while doing unrelated implementation work:

- exact LMB lock selection/order policy
- whether wheel frame is world-fixed, aim-following, or lagged
- disabled satellite collider/blocking behavior
- missing-focus UX beyond clean execution rejection
- exact non-damage focus-roll whitelist/application semantics per spell
- satellite self-spin rates
- post-cast shuffle duration/minimum angular displacement/easing
- eventual spell-tree unlock topology

The architecture already has safe places for these decisions; none require replacing the current recipe/orbit/focus/runtime foundation.

---

## 8. Recommended next chunk

Keep the next chunk player-control focused rather than mixing in progression or visuals.

Suggested scope:

1. Add Orrery input adapter over `OrreryControl`.
2. Suppress normal weapon activation while Orrery class is active without disabling the equipped items themselves.
3. Decide and implement baseline lock-order behavior for the two active satellites.
4. Add concise debug logging / F10 readout for:
   - satellite IDs and live angles
   - captured elements
   - current canonical recipe
   - focus item + effective item level per element
   - invocation result
5. Compile and do first local functional pass:
   - class activation
   - two satellite spawn/orbit
   - lock FF / II / LL
   - invoke each pure spell
   - mixed recipe clean reset
   - class removal/world transition cleanup

After that works locally, the following chunk should be coop replication/presentation before expanding spell content.

---

## 9. Current tuning values to keep visible

```text
Base active satellites                  2
Base formula capacity                   2
Reserved satellite design slots         5
Satellite incoming damage multiplier    0.50
Element sectors                         3 x 120 degrees

Spell reference mode                    Mean
Mean DPS level 1                        726
Median DPS level 1                      546
DPS growth per effective item level     2%

Magma integrated reference seconds      1.0
Magma damage multiplier                 1.0
Cryo output seconds                     1.0
Cryo DPS multiplier                     1.0
Tesla output seconds                    1.0
Tesla DPS multiplier                    1.0
```

These are implementation knobs / neutral starting values, not final balance claims.

---

## 10. Current verification status

Source/API verification has been performed against the provided decompiled Star Vortex `Assembly-CSharp` and item assets for the native members described above.

What has **not** yet been done:

- Unity/mod compiler pass on this complete Orrery slice
- runtime smoke test in Star Vortex
- host/client coop validation
- performance measurement under live combat load

Do not promote this handoff from "implemented foundation" to "playable verified" until those passes succeed.
