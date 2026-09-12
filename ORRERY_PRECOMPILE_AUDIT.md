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
- Hidden Orrery native adapters are distinguished by borrowing a focus slot without being `slot.equippable`; they are not suppressed.

---

## 2. FF — guided explosive fireball

### Current source tuning

```text
FireballIntegratedReferenceSeconds = 1.00
FireballDamageMultiplier            = 1.00
FireballVelocityMultiplier          = 0.65
FireballTurnDegreesPerSecond        = 120
FireballProjectileVisualScale       = 2.00
FireballExplosionRadiusMeters       = 40
FireballLifetimeSeconds             = 2.00
```

### Native implementation

- Hidden delivery adapter: native **Inferno Cannon** launcher family.
- Native item/decompile review confirmed Inferno is an `ExplosiveProjectile` family, whereas Magma Gun itself has no explosive radius.
- Orrery steers the spawned projectile with bounded `Mathf.MoveTowardsAngle` heading changes.
- RMB release uses native explosive projectile destruction/detonation.
- Natural expiry also uses native explosive behavior.
- Direct hits intentionally receive the native direct component plus Orrery's additional explosion-equivalent packet because native `ExplosiveProjectile` excludes the directly-hit object from its AoE loop.

### 40 m explosion / visual matching

The **40 m radius is authoritative**. `OrreryUnits.MetersToWorld(40)` is written into the hidden Inferno launcher's `BaseExplosiveRadius`.

The supplied decompile confirms `ExplosiveProjectile.Explode()` reads the same `parentLauncher.ExplosiveRadius` for:

1. the mechanical overlap radius, and
2. `ExplosiveArea.SetScale(new Vector3(radius * 2, radius * 2, radius * 2))`.

Therefore the native explosion prefab and gameplay hit radius remain coupled by construction. There is intentionally no independent default VFX radius that can drift away from the mechanical radius.

### Chunky projectile scale

`FireballProjectileVisualScale = 2.0` is applied before native projectile collision begins and restored before pooled reuse.

**Important:** Star Vortex uses root projectile transform scale when deriving its circle-cast collision radius. The current 2x scale therefore makes FF physically chunky as well as visually chunky. That is currently treated as consistent with the requested chunky-missile behavior, not as a cosmetic-only scale.

### Reflection contract — fixed

Shield Ward reflection now **severs Orrery control**:

- native reflection first transfers `parentShip`, trajectory, faction cache, and resets projectile lifetime;
- Orrery marks the projectile detached from the original cast;
- it is removed from the hidden Inferno launcher's active-projectile list so later adapter disposal cannot kill it;
- the original cast execution is cancelled;
- original-owner RMB release is blocked during the short reflection→next-fixed-tick transition;
- the next spell-runtime fixed tick clears the original private active cast and starts normal shuffle;
- no further Orrery cursor steering occurs;
- the reflected projectile continues with native reflected ownership, trajectory, and lifetime;
- Orrery's extra same-target explosion packet remains suppressed after ownership changes, preventing original-owner misattribution.

### Non-gameplay cleanup — fixed

Class switch, cast invalidation, and world teardown use `Projectile.CaptureDestroy()` for still-owned FF projectiles instead of `TimedDestroy()`. This preserves native removal/despawn/pool cleanup without creating an authored explosion during cleanup.

Detached/reflected FF is excluded from original-owner and hidden-launcher cleanup.

---

## 3. II — Cone of Cold

### Current source tuning

```text
CryoIntegratedReferenceSeconds = 1.00
CryoDamageMultiplier            = 1.00
CryoConeRangeMeters             = 7.50
CryoConeAngleDegrees            = 70
CryoFreezeChanceAdditive        = +0.50 percentage probability
CryoVisualProjectileCount       = 9
CryoVisualSpreadDegrees         = 60
CryoVisualVelocityMultiplier    = 1.30
CryoVisualProjectileScale       = 1.00
```

### Mechanical behavior

- One mechanical cone query / hit per valid target per cast.
- Damage is normalized to one second of Orrery mean reference DPS.
- Freeze/status chance receives flat +50 percentage points and is clamped to 100%.
- Mechanical packets route through native `NetCombat.RouteDamage` instead of directly mutating targets.

### Presentation burst safety

The 9 Cryo projectiles are presentation only.

They are tagged in a `Projectile.Init` prefix, before native spawn-frame collision can execute. Tagged visual projectiles are denied:

- ordinary projectile mechanical hits;
- Shield Ward reflection;
- projectile damageability / PDL or Gravity-Cannon-style interception targeting;
- fuzzy-projectile Burning Space secondary effects;
- explosive AoE if a future Cryo prefab is explosive.

Native movement, lifetime, launcher removal, despawn, and pooling remain intact.

`CryoVisualProjectileScale` is applied at registration and restored on pool return, so future non-1.0 tuning cannot compound across pooled reuse.

---

## 4. LL — held Tesla / Chain Lightning channel

### Current source tuning

```text
TeslaInitialDpsMultiplier = 2.00
TeslaMinimumDpsMultiplier = 1.00
TeslaFadeSeconds          = 1.00

TeslaRangeMultiplier      = 1.00
TeslaBeamWidthMultiplier  = 1.00
TeslaChainRangeMultiplier = 1.00
TeslaChainCountAdjustment = 0
```

### Behavior

- LL is both the held Tesla channel and the Chain Lightning spell identity; native Tesla chaining is retained.
- Channel begins at 200% reference DPS and linearly fades to 100% over one second.
- It may remain held indefinitely at 100%.
- RMB release terminates the channel and starts shuffle.
- Hidden Tesla damage scaling is restored to neutral after completion so repeated channels cannot compound.

### Size/range knobs

- `TeslaRangeMultiplier` scales native `BeamWeapon.BaseMaxRange`.
- `TeslaChainRangeMultiplier` scales native `BeamWeapon.BaseChainRange`.
- `TeslaChainCountAdjustment` adjusts native `BaseChainTargets` and clamps at zero.
- `TeslaBeamWidthMultiplier` scales the native Beam cached `maxWidth`, `maxEndWidth`, and `additionalMaxWidth` after `Beam.Init`.

The supplied decompile confirms Star Vortex derives later beam animation widths from those cached values, so this preserves native activation/fade/chain behavior rather than repeatedly mutating LineRenderers each frame.

All LL size/range defaults are currently neutral (`1x`, `+0`) until in-game tuning provides evidence to change them.

---

## 5. Native API / Harmony boundary checks completed

The supplied game decompile was checked for the new/critical native members used by this slice, including:

- `Projectile.Init(...)`
- `Projectile.TryReflectOffShieldWard(...)`
- `Projectile.GetParentShip()`
- `Projectile.CaptureDestroy()`
- `Projectile.TimedDestroy()` / `ScheduleDestroy()` / `PoolDestroy()`
- `Launcher.AddProjectile(...)`
- `Launcher.RemoveProjectile(...)`
- `Launcher.BaseExplosiveRadius`
- `ExplosiveProjectile.Explode()` and native `ExplosiveArea.SetScale(...)`
- `BeamWeapon.BaseMaxRange`
- `BeamWeapon.BaseChainTargets`
- `BeamWeapon.BaseChainRange`
- native `Beam.Init(BeamWeapon, ...)`
- protected Beam cached width fields `maxWidth`, `maxEndWidth`, `additionalMaxWidth`
- `Activatable.Equip(...)` / borrowed `equippedSlot` behavior
- `OrreryCasting.Cancel()` ending its `CoreAbilityExecution` as `Cancelled`.

No new Harmony target in this final cleanup pass is based on an invented API name.

---

## 6. Correctness issues found during the pre-compile review and fixed

- Cryo presentation tagging previously happened too late for spawn-frame collision.
- Presentation projectile cleanup previously risked bypassing native teardown/network bookkeeping.
- Presentation Cryo could otherwise become a mechanical PDL/Gravity-Cannon decoy.
- FF could explode during class/world cleanup through hidden-launcher teardown.
- Invalidated FF could remain flying after the authored cast was gone.
- Reflected FF's extra same-target explosion packet could be attributed to the original owner.
- Reflected FF remained steerable/detonatable by the original Orrery.
- Reflected FF could remain owned by the hidden launcher's cleanup list.
- Baseline satellite lanes had accidentally become same-direction rather than counter-rotating.
- Orrery class exit had a small stale hidden-spell-runtime window before the controller's next physics tick.
- FF explosion radius was still the obsolete 3 m prototype value.
- FF / II / LL did not expose the agreed presentation/range size knobs.

---

## 7. Deliberately still TBD — not a reason to delay first compile

### Verification

- Full Unity/mod compiler result.
- Warnings introduced by this slice.
- Local in-game FF / II / LL smoke test.
- Warp/world-transition test.
- Spec/un-spec / Orrery↔other-class transition test.
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

- Whether the 2x FF physical projectile body feels appropriately chunky.
- FF speed/turn/lifetime after seeing the 40 m explosion in-game.
- II range/angle/projectile count/visual scale.
- LL range/width/chain range/count.
- FF direct-hit + explosion total damage budget.

---

## 8. First compile / smoke-test order

Do not add another feature layer before this sequence:

1. Compile/package the exact current `skill-trees` branch.
2. Fix every compiler error attributable to this slice before runtime testing.
3. Enter world as Orrery and confirm two satellites spawn and counter-rotate.
4. Equip one Thermal, Cold, and Electric focus.
5. Verify conventional weapons cannot fire while Orrery is active.
6. FF: lock FF → launch → steer → RMB-release detonate → inspect 40 m VFX/hit agreement → direct-hit test.
7. FF: reflect from Shield Ward → confirm steering and original RMB control stop while reflected projectile continues.
8. II: confirm one cone damage event per target and visual Cryo shards cause no mechanical interactions.
9. LL: hold >1 s → confirm chain behavior persists and damage envelope settles → release.
10. Invoke `FI`, `FL`, or `IL` and verify clean shuffle without stuck cast state.
11. Warp / change ship / spec out of Orrery with and without a live FF/LL.
12. Only after local correctness, repeat host-owned and client-owned in co-op.

If compile/runtime results disagree with this document, trust the actual packaged-game evidence and update the document rather than adding compensating complexity blindly.
