# Orrery Status — D / TBD

**Branch:** `skill-trees`  
**Date:** 2026-09-13  
**Meaning of D:** implemented/source-reviewed. D does **not** mean Unity compiler-, in-game-, or co-op-verified unless explicitly stated.

Current canonical guidance:

- `ORRERY_SPELL_DESIGN_IMPLEMENTATION_STANDARDS_V3.md` — broad spell design/implementation policy.
- `ORRERY_SPELL_LIFETIME_STANDARD.md` — canonical persistent-spell lifetime boundary.
- `ORRERY_PRESENTATION_NETWORK_STANDARD.md` — canonical Orrery remote-presentation transport.

Historical snapshots such as `ORRERY_IMPLEMENTATION_HANDOFF.md` and `ORRERY_PRECOMPILE_AUDIT.md` remain useful for the state/date they describe, but they are not current-status documents.

---

# D — Implemented in Source

## Class / formula foundation

- [D] Standalone Orrery class through shared Core class lifecycle.
- [D] Knowledge Points progression/root infrastructure.
- [D] Fire / Ice / Lightning elements.
- [D] Baseline two active formula satellites and two-rune formulas.
- [D] Five persisted satellite design slots reserved for later progression.
- [D] Canonical unordered `FF`, `II`, `LL`, `FI`, `FL`, `IL` recipe identity.
- [D] Conventional Primary / Secondary / Special / AutoSpecial firing suppressed while Orrery is active; equipped items remain available as focus donors.
- [D] Formula lock/invoke/shuffle control is semantically separated from presentation networking.

## Satellite / orbit / shuffle foundation

- [D] Physical local Orrery satellites spawn from the existing runtime carrier.
- [D] Semantic satellite IDs remain distinct from physical construction order.
- [D] Locked satellites stop normal orbital advancement.
- [D] Physical shuffle/rearm path exists and is shared across cast/reset outcomes.
- [D] Ordinary targeting rejects semantic Orrery satellites while native physical interaction remains intentionally separate.
- [D] Baseline satellite incoming-damage multiplier = 0.50.

## Focus / spell power foundation

- [D] First matching equipped elemental focus resolved in slot order.
- [D] Thermal = Fire, Cold = Ice, Electric = Lightning.
- [D] Native raw donor damage is excluded from Orrery spell damage.
- [D] Shared mean/median Orrery reference-DPS curves exist.
- [D] Resolved focus profile translates compatible donor bonuses into Orrery meanings.
- [D] Bonus Rate of Fire / Tick Rate translate 1:1 into Bonus Spell Damage rather than changing spell cadence.
- [D] Authored Orrery damage components use shared Core combat provenance/semantic/contributor infrastructure where applicable.

## Baseline pure formulas

- [D] `FF` guided explosive fireball / Magma-style formula path implemented.
- [D] `II` Cone of Cold path implemented with one authored mechanical cone and mechanically inert native-looking Cryo presentation.
- [D] `LL` held Tesla/chain-lightning channel implemented.
- [D] Existing legacy FF/II/LL scheduling remains intentionally separate from the newer explicit persistent-spell lifetime path until a reviewed migration is warranted.

## Shatterbolt — Ice + Lightning

- [D] Stable mixed-pair spell identity and implementation.
- [D] Presentation orb travels toward the hostile nearest the cursor.
- [D] Chain selection prefers not-yet-directly-hit ships, then permits revisits while avoiding the ship just struck.
- [D] Base direct Electric packet = 150% of one-second Orrery reference damage before crit.
- [D] Each impact creates an independently expanding Cold burst = 200% reference damage before crit.
- [D] Baseline chain radius = 160 m.
- [D] Baseline projectile speed = 120 m/s.
- [D] Baseline Frost Burst radius = 50 m, expansion = 85 m/s.
- [D] Compatible inherited extra projectile/chain count expands the bounded impact allowance.
- [D] Direct and Frost Burst authored damage retain distinct Core combat semantics/contributors.
- [D] Persistent gameplay/presentation lifetime is dispatched through `OrrerySpellLifetime`.
- [D] Completed bounded impact history remains briefly available for packet-loss-resistant presentation without extending gameplay authority.

## Plasma Bolt / Plasma Burn — Fire + Lightning

- [D] Stable mixed-pair spell implementation.
- [D] Lightning stroke baseline width = 25 m and authored Lightning packet = 200% reference damage before crit.
- [D] Core combat outcome is used to capture the **actual confirmed initial damage** after mitigation.
- [D] Plasma Burn total budget is exactly that confirmed damage value; a crit therefore naturally produces a larger burn.
- [D] Plasma Burn lasts five seconds and applies its frozen total over the authored burn ticks.
- [D] Spread radius = 50 m.
- [D] Descendant infections inherit the original frozen burn payload rather than recalculating from their source's remaining budget.
- [D] A target that has had Plasma Burn remains ineligible for reinfection for ten seconds.
- [D] Reinfection history is gameplay semantic state, independent of presentation/network batching.
- [D] Concurrent infection storage is explicitly bounded; the bound is not a generation/spread-count rule.
- [D] Persistent burn/pending-confirmation/spread lifetime is dispatched through `OrrerySpellLifetime`.

## Shared persistent-spell lifetime

- [D] `OrrerySpellLifetime` is the canonical local persistent-spell scheduler/teardown boundary.
- [D] Shatterbolt and Plasma Bolt each retain their own mechanic-specific runtime state.
- [D] Exactly one shared Orrery controller FixedUpdate bridge dispatches migrated persistent spell fixed ticks.
- [D] Owner replacement, class exit, ship destruction, native `Destroyed`, Unity `OnDestroy`, and world teardown converge through explicit idempotent cleanup paths.
- [D] Cast completion and shuffle do not globally kill legitimate gameplay/presentation tails.
- [D] Shared service teardown remains class/lifetime-owned rather than arbitrarily spell-owned.

## Shared Orrery presentation networking

- [D] Stable Orrery base/casting presentation remains in Core slot 6.
- [D] `OrreryPresentationNetwork` centrally owns six fixed 32-byte records mapped to Core slots 7..12.
- [D] All six presentation slots are registered centrally.
- [D] One central `CoreNetwork.AppendLocalExtension` hook samples Orrery presentation immediately before the actual Core ship-state send.
- [D] `OrreryControl` no longer directly publishes network state.
- [D] Shatterbolt no longer republishes network state every gameplay FixedUpdate/impact/tail transition.
- [D] Presentation bank owns transport framing/capacity only; spell codecs own payload semantics.
- [D] Multipart framing carries codec id, group/part descriptor, and full nonzero generation identity.
- [D] Complete multipart validation occurs before spell presentation mutates remote state.
- [D] Missing/malformed presentation fails cosmetically and cannot affect gameplay correctness.
- [D] Shatterbolt uses records 0..2 / Core slots 7..9 with bounded cumulative impact-history semantics.
- [D] Plasma stroke uses record 3 / slot 10.
- [D] Plasma Burn rotating visual refresh uses records 4..5 / slots 11..12.
- [D] Plasma's current six-target refresh entry count is presentation batching only and cannot cap or alter gameplay spread.
- [D] Current worst simultaneous Orrery presentation is approximately 193 dynamic bytes / 199 appended bytes including the Core extension header, below the 384-byte Core payload ceiling.

## Source-level readiness

- [D] Networking ownership/slot arithmetic reviewed after migration.
- [D] Shatterbolt/Plasma gameplay lifetime remains separate from presentation networking.
- [D] No known source-level architecture blocker currently justifies another speculative infrastructure refactor before runtime evidence.
- [D] Current recommendation is to compile/package and playtest rather than add another feature layer first.

---

# TBD — Verification / Next Evidence

## Immediate verification

- [TBD] Full Unity/mod compile of the exact current branch.
- [TBD] Fix compiler errors/warnings attributable to current Orrery work.
- [TBD] Local smoke test of class activation, satellites, formula controls, FF/II/LL, Shatterbolt, Plasma Bolt, and teardown.
- [TBD] Host-owned Orrery co-op test.
- [TBD] Client-owned Orrery co-op test.
- [TBD] Warp/world-transition test.
- [TBD] Spec/un-spec and Orrery <-> other-class transition test.
- [TBD] Performance profiling under representative combat load.

## Networking-specific playtest checks

- [TBD] Verify remote satellite/formula lock/invoke/shuffle state after send-boundary sampling migration.
- [TBD] Verify Shatterbolt moving orb presentation, cumulative impacts, and replacement casts from both host/client ownership directions.
- [TBD] Verify Shatterbolt old-generation presentation never contaminates a later cast.
- [TBD] Verify Plasma remote stroke and target-attached burn visuals.
- [TBD] Verify Plasma with more than six simultaneous active burns so rotating refresh is exercised without changing gameplay spread.
- [TBD] Verify a Plasma target remains mechanically immune from burn expiry at five seconds until reinfection lockout expiry at ten seconds.
- [TBD] Verify owner/target death clears remote presentation without orphan burn/orb/explosion VFX.

## Feature / progression work still open

- [TBD] Satellite disabled/intangible/half-opacity/repair lifecycle.
- [TBD] Wheel/sector presentation polish and later predictable follow behavior.
- [TBD] Player-authored satellite editor/save/load.
- [TBD] Third/fourth/fifth active satellite progression and higher-arity formulas.
- [TBD] Additional mixed/higher-arity spells and actual Orrery spell-tree topology.
- [TBD] F10 Orrery debug panel when debugging pressure justifies it.

## Organization cleanup

- [TBD] Optional organization-only move of class-owned C# files into `Scripts/Orrery/` and `Scripts/Leviathan/`, keeping shared/Core files at the Scripts root.
- [TBD] Preserve every existing `.cs.meta` GUID during that move; do not introduce namespaces or behavior changes as part of the organization commit.
- [TBD] `OrreryPresentationNetwork.cs` and `OrreryShatterboltPresentationCodec.cs` currently have no committed `.meta` files; Unity is expected to generate them on import and those generated metas should then be committed.

---

# Recommended Next Action

**Compile/package and playtest the exact current branch.**

Do not add another networking/lifetime abstraction before seeing runtime evidence.

For presentation networking architecture, use `ORRERY_PRESENTATION_NETWORK_STANDARD.md` rather than the superseded long-form agent proposal.
