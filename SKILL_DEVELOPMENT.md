# Skill development

This is the starting point for Leviathan/Orrery skill work. The three maintained
project guides are this file, [Networking](Assets/Leviathan/Content/Scripts/NETWORKING.md),
and [Native API compatibility](NATIVE_API_COMPATIBILITY.md). Earlier standards,
handoffs and audits are preserved in [Documentation/History](Documentation/History).
They record past decisions; they are not additional implementation requirements.

Explicit user decisions govern the intended mechanic. Inspect current source for
what is implemented and the installed game DLL for native signatures. The game's
developer reference is external evidence about the game, not a fourth mod standard.
Review proposals and patches against the current checkout before applying them.

## Designing a skill

Describe the recipe, target/aim rule, damage or buff, timing, release/cancellation,
and intended visuals. Identify which elemental implement affects each component.
The implementer fills in technical details; a designer does not need packet layouts
or a long engineering form to propose a skill.

Keep mechanics and presentation separately tunable. Three visible lightning
strikes need not deal three hits. Extra cone shards must not add accidental damage,
status or collision. State explicitly whether each phase is a real hit, a repeated
hit, a damage-over-time tick, or visual only.

Formula identity belongs to the spell registry. Persistent mechanics belong in the
spell runtime. Reuse native behavior where it fits, after checking its damage,
pooling and lifetime rules; native item names alone do not establish those rules.

## Elemental implements and damage

Each elemental hit inherits compatible modifiers and traits only from its matching
implement: Fire/Thermal, Ice/Cold, Lightning/Electric. Resolve mixed spells per
component rather than combining donor stats into one cast-wide damage profile.
A downstream component does not gain another element's traits just because that
element started the spell. Any copied-damage mechanic must explicitly describe its
snapshot exception and whether the receiving element modifies that budget.

`OrreryFocusResolver` selects the first equipped matching damage source across all
slots. `OrreryFocusProfile` translates its stats into spell meanings. Do not add
weapon-family checks to individual skills. Resolve once at the appropriate cast
or effect boundary; invalidate cached profiles when their equipment context changes.

The spell supplies its baseline; the implement supplies compatible bonuses:

| Property | Inheritance rule |
|---|---|
| Power level | Implement stat level (`BaseRequiredLevel`), not its reduced equip requirement |
| Raw weapon damage/DPS | Never copied into the spell's reference damage |
| Rate of fire / beam tick rate | Bonus percentage becomes spell damage at 1:1; do not also speed up cadence |
| Range / chain range / chain damage / velocity / duration | Apply the bonus to the component's authored value where that component supports it |
| Extra shots / extra chains | Transfer bonus counts at 1:1 to explicitly supported behavior; do not copy the weapon's native shot count |
| Crit / status / compatible traits | Use that component's implement; preserve the native proc path where applicable |

For example, +25% range changes a 180 m spell to 225 m regardless of the implement's
own native range. A trait needs a defined receiving behavior; an unsupported native
weapon mechanic is not silently transformed into a new spell mechanic.

Without a matching implement, casting remains available: 80% reference power at
player level, 10% crit chance, +100% crit damage, 10% status chance, and no donor
bonuses. A borrowed native slot supplies execution context only. Its unrelated item
must not become the donor or receive the spell's item-specific proc attribution.
This elemental restriction concerns implement inheritance; ordinary ship-wide
bonuses retain their existing shared/native rules.

`OrrerySpellPower` owns the power curve. At present, mean reference DPS is 726
(median mode 546), multiplied by `1 + 0.02 * (level - 1)`, a smooth weapon budget
from 2 at level 1 to 4 at level 20, and the focused/unfocused multiplier. Read the
source for tuning; do not reproduce that formula inside each spell.

Authored damage multipliers are pre-crit unless a mechanic explicitly says
otherwise. Do not divide by expected crit and then reroll crit. Keep the actual
hit amount separate from native DPS/status scaling data. Use `OrreryDamageRouter`
and `CoreNativeCriticalHits` rather than adding native bindings per skill.

Wrap authored damage in the existing `CoreCombat` scope, with the correct semantic,
elemental contributor and cast identity, and close it in `finally`. Use confirmed
outcomes when a mechanic depends on actual damage, including co-op mitigation.
An attempted remote hit is not proof that it dealt damage. Choose acknowledgement
behavior that the native source-slot path can actually satisfy.

### Plasma Bolt's copied-damage exception

Plasma's Electric strike uses Lightning. The confirmed damage it dealt is the
seed for Plasma Burn and Immolation. Each seed is multiplied by its own tuning
multiplier and Fire's spell-damage bonus once. Spread copies the finished Burn
budget without multiplying again. Thermal ticks use Fire status chance,
damage-limit bypass and native kill-trait source, with no extra crit roll.
Missing Fire uses the no-donor profile. This preserves the requested copied-hit
mechanic while keeping the receiving component's implement effects separate.
## Geometry, visuals and native objects

Use `OrreryUnits` at the metres/world-unit boundary (20 metres per Unity unit).
Use shared spatial helpers where applicable. Damage eligibility comes from the
native damage interface and `CanBeDamagedBy`, with explicit spell-specific limits.
Use the native shield world-radius helper rather than treating shield scale as a radius.

Keep gameplay dimensions and visual dimensions named separately. Guided shots need
bounded turn rate and clear release/detonation behavior. Chains need target/repeat
rules and a finite bound. Expanding areas need an explicit per-target hit policy.
Do not change hitbox size just to make a visual brighter or denser.

Load content through `ModContent` so overrides work. Follow the game's URP and
AssetBundle requirements in the native guide. Configure presentation-only native
objects through the existing safety boundary: zeroing base damage alone can still
leave global modifiers or status active. Remote presentation cannot apply gameplay.

Repeated borrowed Orrery assets resolve lazily through `OrreryContent`; one-off
lookups may use `ModContent` directly. Cache only successful stable source-asset
references, treat them as read-only templates, and create/configure runtime items or
visual instances separately. Keep faction-dependent projectile selection at the use
site rather than caching one faction's prefab globally. Do not add a `Resources`
fallback after `ModContent`. Dedicated Tesla-only legacy lookup paths are explicitly
deferred until Conductor replaces that implementation.

For every object, identify its creator, owner, pool and release path. Return native
pooled objects through their native lifecycle. In particular, native ships can own
pooled status layers; use their voluntary destruction path rather than directly
destroying the hierarchy. Reset stale trails, callbacks, targets and flags on reuse.

## Gameplay lifetime

New persistent Orrery spells use `OrrerySpellLifetime` for fixed ticking, owner
replacement, class exit, ship destruction and world teardown. Add explicit dispatch
to the methods the spell needs; keep its mechanics and bounded state in the spell.
Expose narrow `FixedTick`, `Forget` and `Reset` methods rather than introducing a
generic spell engine or copying controller/world Harmony patches.

Invocation completion, gameplay completion, visual retirement and semantic history
can have different lifetimes. Shuffling or finishing the formula must not freeze
existing explosions, burns, pending outcomes or fading visuals. The spell decides
what target death means; the lifetime layer forwards the event.

Cleanup must be idempotent: class exit, owner replacement, `Destroyed`, `OnDestroy`
and world teardown can overlap. Release spell-owned resources and owner/target
references. Shared services such as the damage router are reset centrally.

Preserve clock semantics: fixed delta for integration, scaled time for authored
effects, unscaled time for deadlines that intentionally survive time scaling.
Do not blanket-catch gameplay ticks and conceal half-mutated state. Protected
network/presentation callbacks belong at the shared boundary described in Networking.

Legacy Magma/Cone/Tesla still use `OrrerySpellRuntime`, including native weapon and
late-tick behavior. Do not give them a second tick path when adding a new spell.

## Tuning and integration

Put player-facing spell tuning in `OrrerySpellCompendium`; tree tuning uses the
existing resolved tree-knob layer. Keep engineering bounds, protocol identities
and pool capacities clearly separate from balance knobs. Describe snapshot versus
dynamic scaling, stacking/refresh, spread, reapplication and expiry explicitly.
Bound persistent effects and history; descendants must not repeatedly rescale a
snapshot unless escalation is an intentional mechanic.

Keep class-specific scripts under their class folder. Preserve `.meta` GUIDs when
moving Unity assets. The project uses `LeviathanMod.asmdef` and separate SDK/editor
assemblies; do not follow archived advice claiming there are no asmdefs.

Networking integration lives in the mod's network module and shared Core services.
The skill exposes semantic state and consumes gameplay APIs. Use the
[networking guide](Assets/Leviathan/Content/Scripts/NETWORKING.md) when implementing
the adapter; do not put send/receive hooks or byte parsing inside a new skill.

## Validation and reporting

Test the behavior the change can break: matching and missing implements, mixed
elements with different bonuses, crit/status attribution, repeated casts, release,
target death, class/owner/world changes and post-cast tails. For co-op, check both
host and client casting and a remote recipient. Verify each authoritative effect
ticks once and remote visuals remain inert.

Run the appropriate build and regression checks. A compile, selector check or
standalone test double is not a live-game result. Report what changed, what passed,
and what still needs in-game verification. Update the relevant one of these three
guides when a shared contract changes; keep review history in the archive.