# Orrery Status — D / TBD

**Branch:** `skill-trees`  
**Date:** 2026-09-12  
**Meaning of D:** implemented in source. D does **not** mean compiler-, in-game-, or co-op-verified unless explicitly stated.

For the detailed pre-compile correctness review, native API checks, and smoke-test order, see `ORRERY_PRECOMPILE_AUDIT.md`.

---

## D — implemented in source

### Class / progression / formula foundation

- [D] Standalone Orrery class through shared Core class lifecycle.
- [D] Knowledge Points progression naming/root infrastructure.
- [D] Baseline 2 active formula satellites and 2-rune formulas.
- [D] Five persisted satellite design slots reserved for progression.
- [D] Fire / Ice / Lightning identities.
- [D] Canonical unordered `FF`, `II`, `LL`, `FI`, `FL`, `IL` recipes.
- [D] `FF`, `II`, `LL` executors implemented.
- [D] Mixed pairs registered with stable identities but intentionally unimplemented.

### Input / conventional weapon suppression

- [D] LMB / native `FirePrimary` locks the next valid formula satellite.
- [D] RMB / native `ActivateActivatable` invokes a complete formula.
- [D] RMB release is forwarded to committed FF / LL interactions.
- [D] Input stays inside Star Vortex's native gameplay input-update path.
- [D] Primary / Secondary / Special / AutoSpecial firing is suppressed while Orrery is active.
- [D] Utilities remain usable.
- [D] Equipped loot remains equipped as elemental focus/stat sources.
- [D] Hidden Orrery adapters are distinguished from the actual `slot.equippable` item and remain usable.

### Satellites / orbit / shuffle

- [D] Two physical local `GameShip` satellites spawn from the exact runtime `Base/Squadrons/Skeran/LeviathanTest` carrier.
- [D] Semantic satellite IDs are separate from physical construction order.
- [D] Baseline concentric lanes counter-rotate.
- [D] Locked satellites stop orbital angular progression.
- [D] Ordinary target selection rejects Orrery satellites while physical collision/hits remain native.
- [D] Satellite autonomous `OrbitAIShip.RunGambits` is suppressed.
- [D] Incoming satellite damage baseline multiplier = `0.50`.
- [D] Physical shuffle/rearm baseline = `0.50 s`.
- [D] Shuffle destinations are fully random `[0,360)` with no minimum displacement.
- [D] Straight owner-relative travel during shuffle.
- [D] Satellite colliders are disabled during crossing and restored afterward.
- [D] Locking is unavailable until shuffle ends.
- [D] Successful pure spells, invalid/unimplemented mixed formulas, and explicit reset use the same shuffle/rearm path.

### Focus / spell power foundation

- [D] First matching equipped elemental focus selected in native slot order.
- [D] Thermal = Fire, Cold = Ice, Electric = Lightning.
- [D] Effective/stat item level uses native `BaseRequiredLevel`, so negative level-requirement rolls naturally expose their higher underlying stat level.
- [D] Native weapon damage is replaced by the shared Orrery spell reference-DPS model.
- [D] Mean reference DPS: `726 * (1 + 0.02 * (effectiveItemLevel - 1))`.
- [D] Median alternate retained: `546 * (1 + 0.02 * (effectiveItemLevel - 1))`.

### FF — guided explosive fireball

- [D] Hidden native delivery family = Inferno Cannon / `ExplosiveProjectile`.
- [D] One-second mean-reference direct damage baseline at neutral `1.0x` spell multiplier.
- [D] Velocity baseline = `0.65x` native Inferno.
- [D] Cursor guidance turn rate = `120 deg/s`.
- [D] Projectile body/visual scale knob = **`2.0x`** baseline.
- [D] Explosion radius knob = **`40 m`** baseline.
- [D] Lifetime baseline = `2 s`.
- [D] RMB release detonates the live fireball.
- [D] Direct target intentionally receives direct hit + one explosion-equivalent packet.
- [D] Native explosion VFX scale and mechanical overlap derive from the same native `ExplosiveRadius`, keeping the 40 m boom visually/mechanically coupled by default.
- [D] Non-gameplay cancellation/class/world cleanup uses native `Projectile.CaptureDestroy()` instead of authored explosion.
- [D] Shield Ward reflection **severs Orrery control**: original steering/release control ends; reflected native ownership/trajectory/lifetime continue; the original-owner extra explosion packet is suppressed.
- [D] Reflected FF is detached from the hidden Inferno launcher's active-projectile list so later adapter disposal cannot kill it.
- [D] FF pooled projectile scale is restored before reuse.

### II — Cone of Cold

- [D] One mechanical Cold cone hit per valid target.
- [D] Mechanical cone range knob = `7.5 m` baseline.
- [D] Mechanical cone angle knob = `70 deg` total baseline.
- [D] Flat `+50 percentage points` Freeze/status chance, clamped to 100%.
- [D] Damage normalized to one second of mean reference DPS.
- [D] Mechanical damage routes through native `NetCombat.RouteDamage`.
- [D] Presentation burst = 9 Cryo projectiles at `1.30x` native velocity.
- [D] `CryoVisualAngleMultiplier = 1.0`; visual fan therefore derives from the current 70-degree mechanical cone by default.
- [D] `CryoVisualRangeMultiplier = 1.0`; visual projectile lifetime is derived so nominal visual travel range matches the mechanical cone range by default.
- [D] `CryoVisualProjectileScaleMultiplier = 1.0` baseline.
- [D] Presentation-only Cryo is tagged before spawn-frame collision.
- [D] Presentation-only Cryo cannot author damage/status, Shield Ward reflection, Burning Space, explosive AoE, or PDL/Gravity-Cannon-style interception behavior.
- [D] Native movement/despawn/pooling remains intact.
- [D] Cryo visual scale is pool-safe and restored before reuse.

### LL — held Tesla / Chain Lightning channel

- [D] Native Tesla beam and chain machinery retained.
- [D] Starts at `2.0x` reference DPS.
- [D] Fades linearly to `1.0x` over `1.0 s`.
- [D] May be held indefinitely at `1.0x`.
- [D] RMB release ends the channel and starts shuffle.
- [D] Hidden Tesla damage scale returns to neutral after completion.
- [D] Mechanical primary range knob = `TeslaRangeMeters = 195 m` baseline.
- [D] Chain-count knob = `TeslaChainCount = 1` baseline.
- [D] Mechanical chain-jump range knob = `TeslaChainRangeMeters = 97.5 m` baseline.
- [D] Chain damage knob = `TeslaChainDamageMultiplier = 0.50` baseline.
- [D] Presentation-only beam width knob = `TeslaBeamVisualWidthMultiplier = 1.0` baseline.
- [D] Native beam visual length derives from the same mechanical MaxRange; there is intentionally no independent visual-length multiplier that can lie about hit range.
- [D] Beam width uses native Beam cached max-width values so activation/fade/chain presentation stays native.

### Runtime / lifecycle / multiplayer foundation

- [D] At most one active Orrery invocation per owner.
- [D] Three hidden native spell adapters cached per owner at most.
- [D] Owner-authoritative Core execution/network framework reused.
- [D] II custom damage enters native network damage routing.
- [D] Orrery class exit synchronously disposes hidden spell runtime rather than waiting for a later physics tick.
- [D] Compact Orrery presentation payload remains within its declared 8-satellite / 24-bit element packing bounds.
- [D] Pre-compile static/native API audit completed; see `ORRERY_PRECOMPILE_AUDIT.md`.

---

## TBD — deliberately not implemented or not yet verified

### Verification — next task, not more feature work

- [TBD] Full Unity/mod compile of the exact current branch.
- [TBD] Fix any compiler errors/warnings introduced by Orrery.
- [TBD] Local in-game smoke test.
- [TBD] Validate packaged Inferno projectile/prefab behavior.
- [TBD] Validate reflective `NetCombat.RouteDamage` lookup in the packaged game.
- [TBD] Warp/world-transition test.
- [TBD] Spec/un-spec and class-switch cleanup test.
- [TBD] Host-owned Orrery co-op test.
- [TBD] Client-owned Orrery co-op test.
- [TBD] Performance profiling under combat load.

### Spell tuning / polish

- [TBD] Tune FF `0.65x` speed, `120 deg/s`, `2x` projectile body, `40 m` explosion, and `2 s` lifetime from in-game feel.
- [TBD] Decide whether FF direct-hit + explosion should remain roughly two full components or split a single total damage budget.
- [TBD] Tune II `7.5 m / 70 deg / 9 projectile / 1.30x velocity / 1.0 visual multipliers` after seeing it. The current 7.5 m visual travel is intentionally source-consistent but may read too short in-game.
- [TBD] Tune LL `195 m` range, `97.5 m` chain range, width, chain count, and chain damage after seeing native Tesla behavior in this class context.
- [TBD] Add a separate explosion-art correction multiplier only if the native 40 m prefab visibly fails to match its gameplay radius.

### Focus inheritance

- [TBD] Full resolved modifier snapshot from the actual elemental focus.
- [TBD] Actual focus crit chance / crit damage inheritance.
- [TBD] Actual focus status/debuff inheritance.
- [TBD] Range / projectile speed / pierce / chain / duration / other applicable focus rolls.
- [TBD] Per-spell additive/multiplicative overrides on top of inherited focus stats.

### Satellite durability / presentation

- [TBD] Prevent native destruction at disabled threshold.
- [TBD] Disabled state, intangible collision state, half opacity, repair, and return only at full hull.
- [TBD] Mid-formula satellite disable -> formula reset/shuffle.
- [TBD] Locked slow self-spin.
- [TBD] Firing fast self-spin.

### Wheel / progression / content

- [TBD] Halo-sector wheel rendering.
- [TBD] Predictable later fixed-degrees-per-second wheel follow.
- [TBD] Player-authored satellite editor/save/load.
- [TBD] Third/fourth/fifth active satellite progression.
- [TBD] Higher-arity formulas.
- [TBD] Mixed-pair spell content.
- [TBD] F10 Orrery debug panel.

### Remote presentation

- [TBD] Validate native remote FF projectile presentation under continuous owner steering.
- [TBD] Validate remote II cone/burst presentation.
- [TBD] Validate remote LL held-channel start/release and chaining.
- [TBD] Validate remote satellite lock/shuffle presentation.
- [TBD] Add custom presentation data only where native replication proves insufficient.

---

## Recommended next action

**Compile now. Do not add another feature chunk first.**

Then follow the smoke-test order in `ORRERY_PRECOMPILE_AUDIT.md`: satellites -> weapon suppression -> FF -> FF reflection -> II -> LL -> mixed bad-input shuffle -> warp/class teardown -> co-op.
