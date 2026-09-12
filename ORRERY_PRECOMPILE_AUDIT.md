# Orrery V0 Pre-Compile Audit

**Branch:** `skill-trees`  
**Date:** 2026-09-12  
**Scope:** static/source + supplied Star Vortex decompile/item-data review before the first full Unity/mod compile of the current Orrery vertical slice.

> **Verdict:** **COMPILE THIS SLICE.** I found no remaining known source/API blocker that justifies adding more gameplay code before compilation. This is not a claim that the branch is compiler-clean or runtime-verified; the next useful evidence is the actual Unity/mod compiler and a local smoke test.

---

## 1. Source-verified V0 behavior

### Formula / controls

- Two active formula satellites and two-rune baseline.
- LMB / native `FirePrimary` locks the next valid formula satellite.
- RMB / native `ActivateActivatable` invokes a completed formula.
- RMB release is forwarded to committed live spells.
- `FF`, `II`, `LL` execute; `FI`, `FL`, `IL` are stable recognized identities but intentionally unimplemented.
- Successful casts, invalid/unimplemented formulas, and explicit reset use the shared physical shuffle/rearm path.

### Shuffle / orbit

- Baseline orbit lanes counter-rotate.
- Shuffle duration baseline: `0.50 s`.
- Target orbit phases are full-random `[0, 360)` with no minimum displacement.
- Satellites travel directly to owner-relative targets while normal orbit advancement is paused.
- Cached satellite colliders are disabled during crossing and restored afterward.
- Formula locking remains unavailable until shuffle completes.

### Conventional weapons

- Actual equipped Primary / Secondary / Special / AutoSpecial items are suppressed while Orrery is active.
- Items remain equipped and available as focus/stat sources.
- Utility activation remains untouched.
- Hidden Orrery native adapters borrow a focus slot without being `slot.equippable`; they are not suppressed.

---

## 2. FF — guided explosive fireball

### Current authored tuning

```text
FireballIntegratedReferenceSeconds       = 1.00
FireballDamageMultiplier                  = 1.00
FireballVelocityMultiplier                = 0.65
FireballTurnDegreesPerSecond              = 120
FireballProjectileScaleMultiplier         = 2.00
FireballExplosionRadiusMeters             = 40
FireballLifetimeSeconds                   = 2.00
```

### Native implementation

- Hidden delivery adapter: native **Inferno Cannon** launcher family.
- Supplied item/decompile review confirmed Inferno is an `ExplosiveProjectile` family, whereas Magma Gun itself has no explosive radius.
- Orrery steers the spawned projectile with bounded `Mathf.MoveTowardsAngle` heading changes.
- RMB release uses native explosive projectile destruction/detonation.
- Natural expiry also uses native explosive behavior.
- Direct hits intentionally receive the native direct component plus Orrery's additional explosion-equivalent packet because native `ExplosiveProjectile` excludes the directly-hit object from its AoE loop.

### 40 m boom

The **40 m radius is authoritative at launch**. `OrreryUnits.MetersToWorld(40)` is written into the hidden Inferno launcher's `BaseExplosiveRadius` before native projectile spawning.

The supplied decompile confirms `ExplosiveProjectile.Explode()` reads the same `parentLauncher.ExplosiveRadius` for both its mechanical overlap and `ExplosiveArea` visual scale. Therefore the native explosion VFX and gameplay radius remain coupled by construction.

### Chunky projectile body

The FF projectile root transform is scaled to `2.0x` before the native `Projectile.Init` body performs collision work and the original pooled scale is restored before reuse.

**Important:** native projectile collision radius uses root projectile transform scale. The 2x FF knob therefore makes the missile physically chunky as well as visually chunky. This is intentional for V0 and must be validated in-game.

### Reflection contract — fixed

Shield Ward reflection **severs Orrery control**:

- native reflection transfers `parentShip`, reflected trajectory/faction context, and resets projectile lifetime;
- Orrery detaches the projectile from the original hidden Inferno launcher's active-projectile list;
- the original cast execution is cancelled;
- original-owner RMB release is blocked during the short reflection -> next-fixed-tick bridge;
- the next spell-runtime fixed tick clears original private active-cast state and starts normal shuffle;
- no further original-owner cursor steering occurs;
- the reflected projectile continues under native reflected ownership, trajectory, and remaining lifetime;
- Orrery's extra same-target explosion packet is suppressed after ownership changes so it cannot be misattributed to the original owner.

### Non-gameplay cleanup — fixed

Class switch, cast invalidation, and world teardown use `Projectile.CaptureDestroy()` for still-owned FF projectiles instead of `TimedDestroy()`. This keeps native removal/despawn/pool cleanup without authoring an explosion during cleanup.

Detached/reflected FF is excluded from original-owner and hidden-launcher cleanup.

---

## 3. II — Cone of Cold

### Current authored tuning

```text
CryoIntegratedReferenceSeconds       = 1.00
CryoDamageMultiplier                 = 1.00
CryoConeRangeMeters                  = 7.50
CryoConeAngleDegrees                 = 70
CryoFreezeChanceAdditive             = +0.50 probability / +50 percentage points
CryoVisualProjectileCount            = 9
CryoVisualVelocityMultiplier         = 1.30
CryoVisualRangeMultiplier            = 1.00
CryoVisualAngleMultiplier            = 1.00
CryoVisualProjectileScaleMultiplier  = 1.00
```

### Mechanical behavior

- One mechanical cone query / hit per valid target per cast.
- Damage is normalized to one second of Orrery mean reference DPS.
- Freeze/status chance receives flat +50 percentage points and is clamped to 100%.
- Mechanical packets route through native `NetCombat.RouteDamage` instead of directly mutating targets.

### Presentation geometry

- 9 native Cryo projectiles are emitted as presentation only.
- Visual fan angle derives from the mechanical cone angle: `70 deg * CryoVisualAngleMultiplier`, so the current baseline is also 70 degrees.
- Visual projectile lifetime is derived from current projectile velocity and `CryoConeRangeMeters * CryoVisualRangeMultiplier`, so nominal visual travel range matches the mechanical 7.5 m cone by default.
- Visual projectile body scale has its own presentation-only multiplier and is pool-safe.

### Presentation safety — fixed

Presentation-only Cryo is tagged in a `Projectile.Init` **prefix**, before native spawn-frame collision can execute. The classifier uses the authoritative derived visual angle rather than the obsolete 60-degree prototype spread.

Tagged visuals are denied:

- ordinary projectile mechanical hits;
- Shield Ward reflection;
- projectile damageability / PDL or Gravity-Cannon-style interception targeting;
- fuzzy-projectile Burning Space secondary effects;
- explosive AoE if a future Cryo prefab is explosive.

Native movement, lifetime, launcher removal, despawn, and pooling remain intact. Visual sizing is owned separately by `OrrerySpellSizing`, and scale is restored before pool reuse.

### Tuning watch item

Under the Orrery unit contract, 7.5 m is only 0.375 Unity world units. With the current 1.30x Cryo velocity, the derived visual lifetime may be very short. That is **not** a static correctness blocker, but II range/visual readability is a priority first-smoke-test tuning check.

---

## 4. LL — held Tesla / Chain Lightning channel

### Current authored tuning

```text
TeslaInitialDpsMultiplier       = 2.00
TeslaMinimumDpsMultiplier       = 1.00
TeslaFadeSeconds                = 1.00

TeslaRangeMeters                = 195.0
TeslaChainCount                 = 1
TeslaChainRangeMeters           = 97.5
TeslaChainDamageMultiplier      = 0.50
TeslaBeamVisualWidthMultiplier  = 1.00
```

### Behavior

- LL retains native Tesla beam and chain machinery.
- Channel begins at 200% reference DPS and linearly fades to 100% over one second.
- It may remain held indefinitely at 100%.
- RMB release terminates the channel and starts shuffle.
- Hidden Tesla damage scaling is restored to neutral after completion so repeated channels cannot compound.

### Size/range knobs

- `TeslaRangeMeters` writes native `BeamWeapon.BaseMaxRange` after meters -> world conversion.
- `TeslaChainCount` writes native `BaseChainTargets` and clamps at zero.
- `TeslaChainRangeMeters` is converted into the native chain-range ratio against current primary range.
- `TeslaChainDamageMultiplier` writes native `BaseChainDamage`.
- `TeslaBeamVisualWidthMultiplier` scales Beam's cached `maxWidth`, `maxEndWidth`, and `additionalMaxWidth` against a captured baseline.
- Native beam visual **length already follows mechanical MaxRange**, so no independent visual-length knob is provided; this prevents a beam from visually claiming a different range than it can hit.
- Native Tesla width is presentation-only; a wider rendered arc does not widen the mechanical hit ray.

The 195 m / 97.5 m / 1 chain / 0.50 chain-damage defaults intentionally reproduce the native Tesla asset's current geometry in explicit Orrery units rather than hiding them behind ratios.

---

## 5. Native API / Harmony boundary checks completed

The supplied game decompile was checked for the critical native members used by this slice, including:

- `Projectile.Init(float, bool, bool, Launcher, GameShip, List<GameObject>, Action<Projectile,GameObject,Vector2>)`
- protected `Projectile.TryReflectOffShieldWard(GameObject, Vector2)`
- `Projectile.GetParentShip()`
- `Projectile.CaptureDestroy()`
- `Projectile.TimedDestroy()` / `ScheduleDestroy()` / `PoolDestroy()`
- `Launcher.ShootProjectile(...)`
- `Launcher.AddProjectile(...)`
- `Launcher.RemoveProjectile(...)`
- `Launcher.BaseExplosiveRadius`
- `ExplosiveProjectile.Explode()` and native `ExplosiveArea.SetScale(...)`
- `BeamWeapon.BaseMaxRange`
- `BeamWeapon.BaseChainTargets`
- `BeamWeapon.BaseChainRange`
- `BeamWeapon.BaseChainDamage`
- `Beam.Init(BeamWeapon, GameShip, bool, float, int, float, bool, bool, bool, bool, bool, GameObject)`
- `Beam.OnDestroy()`
- protected Beam cached width fields `maxWidth`, `maxEndWidth`, `additionalMaxWidth`
- `Activatable.Equip(...)` / borrowed `equippedSlot` behavior
- exact `GameShip.Damage(DamageType, DamageData[], float, bool, Vector2, GameShip, bool)` overload used by the satellite patch.

No new Harmony target in this final cleanup pass is based on an invented API name.

---

## 6. Correctness issues found during the pre-compile review and fixed

- Cryo presentation tagging previously happened too late for spawn-frame collision.
- Presentation projectile cleanup previously risked bypassing native teardown/network bookkeeping.
- Presentation Cryo could otherwise become a mechanical PDL/Gravity-Cannon decoy.
- Cryo safety classification still depended on the obsolete 60-degree visual spread after visual geometry was changed to derive from the 70-degree cone.
- Cryo presentation safety and visual sizing both attempted to own pooled transform scale; scale ownership is now separated into sizing only.
- Two stale cross-file sizing symbol references were found during audit (`FireballProjectileVisualScale` / `CryoVisualProjectileScale`) and removed/replaced with the authoritative sizing controls.
- FF could explode during class/world cleanup through hidden-launcher teardown.
- Invalidated FF could remain flying after the authored cast was gone.
- Reflected FF's extra same-target explosion packet could be attributed to the original owner.
- Reflected FF remained steerable/detonatable by the original Orrery.
- Reflected FF could remain owned by the hidden launcher's cleanup list.
- Baseline satellite lanes had accidentally become same-direction rather than counter-rotating.
- Orrery class exit had a small stale hidden-spell-runtime window before the controller's next physics tick.
- FF explosion radius was still the obsolete 3 m prototype behavior at launch; launch-time authoritative radius is now 40 m.
- FF / II / LL lacked coherent explicit geometry controls; the current source now has meters/counts for mechanical geometry and clearly named presentation-only multipliers where appropriate.

---

## 7. Deliberately still TBD — not a reason to delay first compile

### Verification

- Full Unity/mod compiler result.
- Warnings introduced by this slice.
- Local in-game FF / II / LL smoke test.
- Packaged Inferno projectile/prefab behavior.
- Reflective `NetCombat.RouteDamage` lookup against the packaged assembly.
- Warp/world-transition test.
- Spec/un-spec / Orrery <-> other-class transition test.
- Host-owned and client-owned co-op test.
- Performance profiling under real combat load.

### Feature work

- Full focus modifier inheritance from the actual elemental focus.
- Satellite disable / intangible / half-opacity / repair lifecycle.
- Halo sector wheel rendering.
- Locked/firing satellite self-spin.
- Player-authored satellite save/load.
- Third/fourth/fifth satellite progression and higher-arity recipes.
- Mixed-pair spell content.
- Remote Orrery presentation work if native replication is insufficient.
- F10 Orrery debug panel.

### Tuning

- Whether the 2x physical FF projectile body feels appropriately chunky.
- FF speed/turn/lifetime after seeing the 40 m explosion in-game.
- Whether II's current 7.5 m cone is too short to read well visually/gameplay-wise.
- II angle/projectile count/visual scale after seeing it.
- LL range/width/chain range/count/damage after seeing native chain behavior in the Orrery context.
- FF direct-hit + explosion total damage budget.

---

## 8. First compile / smoke-test order

Do not add another feature layer before this sequence:

1. Compile/package the exact current `skill-trees` branch.
2. Fix every compiler error attributable to this slice before runtime testing.
3. Enter world as Orrery and confirm two satellites spawn and counter-rotate.
4. Equip one Thermal, Cold, and Electric focus.
5. Verify conventional weapons cannot fire while Orrery is active.
6. FF: lock FF -> launch -> steer -> RMB-release detonate -> inspect 40 m VFX/hit agreement -> direct-hit test.
7. FF: reflect from Shield Ward -> confirm steering and original RMB control stop while reflected projectile continues.
8. II: confirm one cone damage event per target, visual fan follows the cone, and Cryo shards cause no mechanical interactions.
9. LL: hold >1 s -> confirm chain behavior persists and damage envelope settles -> release; inspect 195 m / 97.5 m geometry and width.
10. Invoke `FI`, `FL`, or `IL` and verify clean shuffle without stuck cast state.
11. Warp / change ship / spec out of Orrery with and without a live FF/LL.
12. Only after local correctness, repeat host-owned and client-owned in co-op.

If compile/runtime results disagree with this document, trust the actual packaged-game evidence and update the document rather than adding compensating complexity blindly.
