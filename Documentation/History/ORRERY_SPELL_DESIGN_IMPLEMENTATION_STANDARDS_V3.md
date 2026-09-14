> Historical snapshot archived 2026-09-14. Current guidance starts in
> [Skill development](../../SKILL_DEVELOPMENT.md). Do not treat this document as
> the current implementation, backlog or validation result.

# Orrery Spell Design & Implementation Standards

**Project:** Star Vortex — Orrery / Celestial Mage / Sphereweaver  
**Purpose:** Define how Orrery spells should be designed, implemented, tuned, networked, and cleaned up without repeating early implementation mistakes.

**Revision:** V3 — Keeps donor bonus-delta inheritance and 1:1 Rate-of-Fire/Tick-Rate → Spell Damage conversion, and adds resolved focus profiles, pre-crit authored damage semantics, combat provenance, hardened presentation-only pooling, bounded event-history replication, spell-specific network payload boundaries, the Compendium pattern, resolved tree tuning, and Unity-safe class-folder organization.

---

# 1. Scope and source evidence

**Reviewed 2026-09-14.** This document governs skill mechanics, inheritance,
geometry and tuning. [DOCUMENTATION.md](<DOCUMENTATION.md>) identifies the maintained
reference for each subject. [Networking for skill authors](<../../Assets/Leviathan/Content/Scripts/NETWORKING.md>) governs
transport, presentation callbacks, serialization and cross-owner grants.

Follow explicit user requirements. Check source for implemented behavior and the
installed game assembly for native signatures. A source/document disagreement
must be investigated; do not silently treat a bug as the intended design.
Historical handoffs and proposed layouts do not override current requirements.
A refactor is not automatically a rebalance.

---

# 2. Core Spell Principle

Orrery spells are not standalone fake weapons.

They are class-owned manifestations built from:

```text
Formula / recipe
    +
Orrery spell identity
    +
optional elemental focus donor
    +
Orrery reference-damage model
    +
spell-specific mechanics
    +
spell-specific presentation
```

Conceptually:

```text
Player invokes recipe
        ↓
Resolve spell definition
        ↓
Resolve matching elemental focus if present
        ↓
Resolve spell power / inherited non-damage properties
        ↓
Create or reuse bounded native-backed adapter where useful
        ↓
Run owner-authoritative gameplay mechanic
        ↓
Run local + replicated presentation
        ↓
Complete / release / detonate / expire
        ↓
Shuffle and rearm satellites
```

The implementation should preserve native Star Vortex semantics wherever they are useful, while allowing Orrery to explicitly replace the parts that define the spell.

---

# 3. Spell Identity vs Formula

A formula identifies a spell.

Examples:

```text
FF → Magma Cannon
II → Cone of Cold
LL → Tesla Coil
FI → future Fire/Ice spell
FL → future Fire/Lightning spell
IL → Shatterbolt
```

Formula order is canonicalized unless a spell is explicitly designed to care about sequence.

Do not make runtime mechanics inspect arbitrary satellite history when a canonical recipe key already exists.

A spell definition should normally contain:

```text
Spell ID
Name
Recipe key
Primary element / damage type
Executor
Interaction style
Optional presentation/network metadata
```

Tree state should determine whether the spell is unlocked or modified.

The runtime should not identify spells by player-facing node names.

---

# 4. Design Every Spell in Two Layers

Every Orrery spell should be designed as two explicit contracts:

```text
A. GAMEPLAY / MECHANICAL CONTRACT
B. PRESENTATION CONTRACT
```

This distinction is mandatory.

## 4.1 Mechanical contract

Defines what actually affects gameplay:

- damage
- hit geometry
- target selection
- crit rolls
- status/debuff rolls
- force / pull / knockback
- cooldown / channel / release rules
- resource use
- authoritative projectile state
- detonation
- spread / chain state
- target immunity history

## 4.2 Presentation contract

Defines what players see and hear:

- visual projectile count
- projectile scale
- beam width
- cone density
- wave count
- visual travel distance
- visual-only offsets
- particles
- explosion scale
- status VFX
- audio
- satellite animation
- remote replication

## 4.3 Do not accidentally bind the two

A visual effect may deliberately exceed gameplay geometry.

Example:

```text
Cone of Cold mechanical hit:
    60 m / 30° / one hit

Cone of Cold presentation:
    five visual waves
    larger envelope
    longer travel
    many native Cryo shards
```

That is valid as long as the extra shards are mechanically inert.

Likewise, a custom mechanical cone may route native Cold damage without pretending each Cryo visual projectile caused the hit.

---

# 5. Source First, Orrery Second

Before implementing a spell around a native Star Vortex item or effect:

```text
1. Inspect the exact native class in the decompile.
2. Verify method signatures and lifecycle.
3. Verify how damage, crit, status, range, pooling, and teardown actually work.
4. Decide which native behavior Orrery keeps.
5. Explicitly identify which behavior Orrery replaces.
```

Do not infer native behavior from the item name or from general Unity knowledge.

Examples of useful native references:

```text
Inferno / explosive projectile
Cryo Gun / ChargingLauncher
Tesla / BeamWeapon chain behavior
BlackHole pull math
ExplosiveArea visual sizing
Projectile pooling
GameShip voluntary destruction
native status-effect layers
native damage routing
```

Native behavior is a reference implementation, not a prison.

Orrery may intentionally change range, cadence, damage integration, chain rules, steering, detonation, or visuals.

The change should be explicit rather than accidental.

---

# 6. Elemental Focus Contract

A matching elemental focus is an enhancement source, not a cast permission gate.

## 6.1 Matching focus

The first equipped weapon of the matching element is the donor.

Search all equipped slots.

Do not assume:

```text
slot 1 only
PrimaryWeapon only
one concrete weapon subclass list
```

Resolve the donor's native `damageType` generically.

## 6.2 What the donor contributes

The Orrery focus rule is:

> **The spell owns its baseline. The donor contributes compatible bonuses. Raw donor damage never becomes spell damage.**

This distinction matters most for spell-authored structural stats. A donor weapon's native geometry or firing pattern must not overwrite the identity of the spell.

For these properties, inherit the donor's **bonus/delta over its native baseline**, not the donor's literal native value:

- Bonus % Range
- Bonus Shot Count / Multishot
- Bonus % Chain Range
- Bonus % Chain Damage

Examples:

```text
Spell base range: 180 m
Torch native range: 60 m
Torch Bonus Range: +25%

Final spell range: 180 m × 1.25 = 225 m
```

The Torch's native 60 m range is irrelevant. A long-range cannon with the same +25% Range bonus produces the same 225 m spell range.

Likewise:

```text
Spell base shot count: 1
Donor native shot count: 3
Donor Bonus Shot Count: +2

Final spell shot count: 3
```

The donor's native three-shot firing pattern does not become the spell's baseline. Only the +2 bonus transfers.

The same principle applies to authored chain behavior:

```text
Final spell chain range
    = spell-authored chain range modified by donor Bonus Chain Range

Final spell chain damage
    = spell-authored chain damage modified by donor Bonus Chain Damage
```

Do not silently import a donor's native chain range, native chain damage fraction, or native multishot pattern.

Other compatible donor properties may still be inherited according to the spell contract, including:

- item rolls
- crit chance
- crit damage
- status chance
- projectile velocity bonuses
- pierce bonuses / compatible pierce behavior
- duration bonuses
- compatible modifiers
- compatible customizers
- other explicitly supported non-damage bonuses

Do not maintain an arbitrary semantic whitelist merely because the first spells only use a subset. However, every inherited property must have a defined receiving meaning.

### 6.2.1 Rate-of-fire and tick-rate translation

Orrery does **not** inherit Bonus Rate of Fire or Bonus Tick Rate as cadence.

Instead, both are always translated directly into Bonus Spell Damage at **1:1 percentage value**:

```text
+10% Rate of Fire  -> +10% Spell Damage
+20% Tick Rate     -> +20% Spell Damage
```

If both are present, both damage bonuses apply using the normal Orrery bonus-damage stacking rules.

This is the Orrery meaning of those donor stats. Do not also make the spell fire, channel, pulse, or tick faster from the same bonuses.

Direct raw/base weapon damage remains excluded. Donor DPS must never leak into Orrery spell damage through native base damage, Rate of Fire, Tick Rate, ShotCount, or any other second path.

## 6.3 Resolved focus profile

Focus resolution should produce one small shared **resolved focus profile** for the spell to consume.

The profile should represent the actual equipped matching focus when one exists, and the standard Orrery fallback when one does not. It should be the shared boundary where cross-family weapon differences are translated into Orrery meanings.

Conceptually:

```text
matching focus
    ↓
read actual focus item/stat level
read compatible crit/status/modifier/customizer data
measure compatible bonus deltas over the donor's native baseline
translate Bonus Rate of Fire / Tick Rate into Bonus Spell Damage
    ↓
resolved focus profile
    ↓
spell maps supported bonuses onto its own authored mechanics
```

A hidden native adapter is **not** automatically the focus donor.

If the player has a matching focus:

- crit/status/customizer inheritance comes from that actual focus where supported
- structural bonus deltas come from that actual focus
- the adapter remains execution, source-slot, pooling, or presentation plumbing

If there is no matching focus:

- materialize the standard fallback values in the same resolved profile
- a borrowed enabled slot may provide native execution context
- the unrelated item in that slot does not contribute donor stats

Do not make every spell understand every native weapon subclass. The shared focus layer may perform the family-specific extraction needed to produce common Orrery meanings.

Likewise, do not blindly apply every resolved bonus to every spell. A bonus only affects a spell when that spell defines a receiving meaning.

Examples:

```text
Bonus Range on Shatterbolt
    might modify acquisition range, chain range, explosion radius, or some subset
    only the spell contract decides which

Bonus Shot Count
    does nothing until the spell defines whether extra shots are full damage,
    split damage, reduced damage, or presentation-only
```

This keeps item inheritance shared without turning spell behavior into a universal effect engine.

## 6.4 No-focus fallback

Current Orrery baseline policy:

```text
matching focus:
    100% reference DPS
    donor effective item/stat level
    compatible donor bonuses
    donor crit/status behavior where defined by the spell contract

no matching focus:
    80% reference DPS
    player level as power level
    10% crit chance
    10% native status chance
```

A spell may add its own status bonus after that baseline.

Example:

```text
Cone of Cold no-focus status:
    10% fallback native status
    +50 percentage points Freeze
    = 60% Freeze chance
```

No-focus adapters may borrow a real enabled native slot as execution context, but the mismatched weapon in that slot is **not** a donor.

---

# 7. Spell Power

Orrery damage should be based on a class reference-DPS model rather than copying donor weapon DPS.

Current reference model:

```text
Per-weapon base: mean 726 or median 546
LevelScale = 1 + 0.02 * (max(1, abs(effectiveLevel)) - 1)
WeaponEquivalentBudget = SmoothStep(2, 4, clamp01((level - 1) / 19))
FocusMultiplier = 0.80 for an unfocused cast, otherwise 1
ReferenceDPS = PerWeaponBase * LevelScale * WeaponEquivalentBudget * FocusMultiplier
```

This is the current `OrrerySpellPower.GetReferenceDps` policy, checked on
2026-09-14. The weapon-equivalent budget rises from two at level 1 to four at
level 20 and stays capped while ordinary level scaling continues. Use that shared
function; do not multiply by the weapon budget again in individual skills.
The constants in `OrrerySpellPower.cs` are the tuning source.

A spell then defines how much reference damage it integrates.

Examples:

```text
instant strike:
    ReferenceDPS × IntegratedSeconds × SpellDamageMultiplier

channel:
    ReferenceDPS × CurrentChannelMultiplier

explosion + direct hit:
    explicitly define whether each component is a fraction or full packet
```

Never normalize a spell against donor raw DPS and then also apply Orrery reference damage. Raw donor DPS is not an Orrery input.

After Orrery reference damage is established, apply compatible donor damage-equivalent bonuses. In particular:

```text
Bonus Rate of Fire -> Bonus Spell Damage, 1:1
Bonus Tick Rate    -> Bonus Spell Damage, 1:1
```

## 7.1 Authored damage multipliers are pre-crit by default

Unless a spell explicitly says otherwise, a spell-authored damage coefficient describes the **base packet before crit**.

Example:

```text
Shatterbolt direct hit:
    ReferenceDPS × 1.0 s × 1.50
        ↓
    this is the ordinary non-crit hit
        ↓
    if the crit roll succeeds, apply the resolved crit multiplier afterward
```

Do **not** divide the authored packet by expected crit value such as:

```text
1 + CritChance × CritModifier
```

merely to make long-run expected damage equal the displayed spell coefficient. That changes the intuitive meaning of a `150%` or `200%` spell packet.

Expected-output normalization is only valid when the spell design explicitly asks for it. If used, name/document it as a different damage policy rather than hiding it inside ordinary spell power.

---

# 8. Crit and Status Rules

Orrery follows the project-wide additive policy for probabilities.

Examples:

```text
10% crit + 5 percentage points = 15% crit
20% status + 7.5 percentage points = 27.5% status
```

Do not interpret `+5 crit` as `×1.05` unless a mechanic explicitly says multiplicative.

When a spell snapshots an initial hit for a later effect, define exactly what is copied.

Example for a spreadable burn:

```text
initial bolt actually deals 359 damage
        ↓
Plasma Burn stores 359 as its total burn value
        ↓
spread copy also burns for 359 total
```

If the initial hit crits, the stored value naturally includes that crit because the spell snapshots the resolved hit result rather than recalculating from base damage later.

Do not reroll crit/status on downstream copies unless the design explicitly says to.

## 8.1 Authored damage provenance

Custom Orrery damage should enter the shared combat provenance/history system when the target and Core API support it.

A routed damage component should preserve, where applicable:

```text
SourceOwner
Spell / effect semantic key
Contributor
Cast / attack instance id
Physical or native source context
```

Use separate semantic keys when mechanically distinct parts of one spell may need to be distinguished later.

Example:

```text
Shatterbolt direct Electric hit
    semantic: Shatterbolt
    contributor: Lightning satellite

Shatterbolt expanding Cold burst
    semantic: ShatterboltFrostBurst
    contributor: Ice satellite

both:
    same cast / attack instance id
    same source owner
```

This makes later tree nodes, kill attribution, combat history, debugging, and cross-skill reactions possible without reverse-engineering the damage after the fact.

Combat provenance does not replace Orrery authority. The owner still decides and applies gameplay; the shared scope records the meaning/correlation of that authored damage.

---

# 9. Geometry and Units

Player-facing Orrery spatial tuning is authored in **meters**.

Convert only at the native Star Vortex boundary.

Current conversion:

```text
20 meters = 1 Unity world unit
```

Use shared meter conversion helpers.

Do not mix meters and Unity units inside tuning blocks.

## 9.1 Shared spatial primitives

Prefer shared, bounded spatial helpers for common shapes:

```text
circle
cone
wide beam / corridor
capsule / swept segment
nearest-N targets
chain-radius query
```

Do not rewrite cone math independently in every spell.

A spell should primarily author:

```text
range
angle
width
origin
forward direction
eligibility predicate
maximum target count
```

Broad-phase collection remains caller-owned when the shared spatial layer is only geometry math. Use native collider overlap/contact when actual collider extent matters.

## 9.2 Visual geometry may differ

If visual geometry intentionally differs from mechanical geometry, name it explicitly:

```text
ConeRangeMeters
ConeAngleDegrees
VisualRangeMultiplier
VisualAngleMultiplier
VisualProjectileScaleMultiplier
```

Never silently change gameplay because an art effect looked too small.

---

# 10. Native-Backed Adapters

A hidden native weapon adapter is useful when Star Vortex already provides valuable behavior such as:

- projectile construction
- beam rendering
- chain traversal
- damage packet structure
- status integration
- native modifiers
- projectile physics
- explosion lifecycle
- pooling

Adapters are an implementation detail.

The spell remains Orrery-owned.

## 10.1 Adapter rules

A hidden adapter should:

```text
use a valid native slot context
have explicit lifetime/ownership
be hidden from normal player weapon presentation
avoid consuming ordinary resources unless intended
be reusable per owner where practical
be cleaned up on class exit / owner replacement / world teardown
```

## 10.2 Do not let adapter visuals become accidental gameplay

If a native projectile exists only for presentation:

- zero authored damage
- zero authored status
- explicitly classify it as presentation-only
- block damage/hit interaction
- block Shield Ward reflection when appropriate
- block PDL / capture targeting when appropriate
- block secondary mechanical effects such as burning-space creation
- preserve its normal pooling/despawn lifecycle

Presentation safety should be explicit, not inferred from `BaseDamage == 0` alone, because global modifiers may still affect an equipped native object.

For pooled native presentation objects, prefer the stronger proven pattern when the object would otherwise run gameplay logic:

```text
spawn from native pool
    ↓
record original pooled state
    ↓
disable native behavior component
disable Rigidbody2D simulation when relevant
disable all gameplay colliders
    ↓
manually drive transform / scale / rotation / animation
    ↓
on cleanup:
restore scale
restore collider enabled states
restore rigidbody simulation state
restore native behavior enabled state
    ↓
PoolDestroy()
```

This is appropriate for presentation-only native `Projectile`, `Wave`, and similar objects whose own update/collider lifecycle would otherwise create mechanics.

Restoring original state **before** returning the object to the pool is mandatory. A disabled collider or behavior left on a pooled object can corrupt a later legitimate reuse.

---

# 11. Projectiles

For projectile spells, define all of these before coding:

```text
spawn source
initial direction
speed
turn rate / steering
collision authority
lifetime
range
direct-hit behavior
expiry behavior
early-release behavior
reflection behavior
capture behavior
pierce behavior
multishot behavior
network presentation
cleanup
```

## 11.1 Guided projectiles

If Orrery owns steering:

- native projectile still owns collision/lifecycle where practical
- Orrery updates velocity/heading only
- release/detonation semantics must be explicit
- reflection or ownership transfer must sever Orrery steering if appropriate

## 11.2 Multishot

Define whether multishot:

```text
adds full-damage projectiles
splits total spell damage
uses a reduced per-projectile coefficient
only changes visuals
```

Do not inherit the donor weapon's native `ShotCount`. Inherit only **Bonus Shot Count / Multishot** above the donor baseline, then apply the receiving spell's authored multishot damage rule.

Do not allow Bonus Shot Count to accidentally multiply total spell damage without an authored rule.

---

# 12. Beams and Lightning

For beam spells, determine whether native `BeamWeapon` already supplies:

```text
range
width
chain target selection
chain range
chain damage fraction
continuous hit cadence
presentation
```

Prefer adjusting the native resolved beam where possible.

For custom wide lightning strokes/corridors, the mechanic may instead use a shared wide-beam spatial query while a native beam/lightning effect provides presentation.

Define separately:

```text
mechanical width
visual width
range
chain/spread radius
maximum jumps
whether already-hit targets are allowed
preference order
```

---

# 13. Explosions

Use native explosion behavior as the reference when possible.

Define:

```text
radius in meters
expansion speed if non-instant
whether direct-hit target also receives AoE
falloff
crit behavior
status behavior
visual prefab
visual radius
mechanical vs visual coupling
```

Be careful: native explosive projectiles may exclude the directly struck object from their AoE loop.

If the spell wants both direct hit and explosion damage on the same target, that must be handled intentionally.

---

# 14. Channels, Charge, Hold, and Release

Each spell must declare its input lifecycle.

Supported patterns include:

```text
press → instant cast
press → begin charge → release fires
press → fire → hold steers → release detonates
press → begin channel → release ends
press → place preview → second input confirms
```

Do not infer release behavior from weapon-family defaults.

Orrery currently uses native input edges:

```text
LMB → lock formula satellite
RMB → invoke completed formula
RMB release → spell-specific post-commit release behavior
```

A spell that owns a held state must clean it up on:

- normal release
- class exit
- owner ship replacement
- UI/pause/global stop
- death / world teardown
- execution invalidation

---

# 15. Multi-Phase and Delayed Spells

If a spell continues after the initial cast, give it explicit bounded state.

Examples:

```text
fireball steering state
five-wave Cone of Cold presentation sequence
spreadable Plasma Burn history
Shatterbolt chain traversal
held Tesla fade state
```

State should contain only what the spell needs.

Avoid a universal giant `SpellState` containing fields for every possible mechanic.

Prefer:

```text
small spell-specific runtime state
+
shared generic primitives only where they are genuinely generic
```

Every delayed phase must have a clear terminal condition.

---

# 16. Debuffs, DOTs, and Spreadable Effects

For custom Orrery debuffs, specify:

```text
name
source owner
source spell
initial resolved magnitude
start time
end time
tick policy
visual state
spread radius
spread cadence
spread target preference
per-target immunity/history duration
stack/refresh policy
network presentation
cleanup
```

## 16.1 Snapshot vs dynamic scaling

State explicitly whether the debuff:

```text
snapshots cast-time damage
snapshots actual hit damage
dynamically recalculates each tick
```

For effects such as Plasma Burn, actual-hit snapshotting is preferable when the design says the burn must equal the initial hit including crit outcome.

## 16.2 Bounded history

Spread/recursion mechanics must keep bounded local history.

Example:

```text
Cannot spread back to a target that had Plasma Burn in the last 10 seconds.
```

Store only the required recent target/time state.

Prune expired entries.

Do not build an unbounded global combat history.

---

# 17. Presentation Density

Do not assume one native projectile equals a readable spell.

Orrery spells may need exaggerated presentation because the camera scale is large.

Useful presentation knobs include:

```text
VisualProjectileCount
VisualWaveCount
VisualWaveIntervalSeconds
VisualRangeMeters
VisualAngleDegrees
VisualProjectileScale
VisualOpacity
VisualExpansionSpeed
VisualLifetime
VisualColorShift
VisualBrightness
```

When fixing readability, identify which dimension is actually weak:

```text
size
length
width
density
duration
brightness
contrast
motion
```

Do not compensate for a density problem by silently changing mechanical damage radius.

---

# 18. Pooling and Object Lifecycle

Pooling is a hard engineering boundary.

If Star Vortex creates an object from a pool, do not destroy it with raw Unity `Destroy` unless the native lifecycle explicitly does so.

Use the native pool/lifecycle path.

Recent Orrery example:

```text
BAD:
    satellite.gameObject.SetActive(false)
    Destroy(satellite.gameObject)

WHY BAD:
    the GameShip hierarchy can contain pooled Burning Layer,
    Radioactive Layer, and other native pooled children.

CORRECT:
    satellite.Destroyed(true, null)
```

The native voluntary destruction path returns status-effect layers and disowns pooled children before final destruction.

This principle applies beyond ships.

For every runtime object, know:

```text
who created it
who owns it
whether it is pooled
how it is returned/destroyed
what happens on cancellation
what happens on class exit
what happens on world teardown
```

---

# 19. Class and Ship-Editor Lifecycle

Orrery must survive heavyweight owner transitions cleanly:

```text
ship editor exit / ship replacement
slot changes
weapon equip/unequip
class spec
class unspec
Orrery ↔ another class
player death
world/star teardown
```

A spell runtime must never assume the current `GameShip` object survives these transitions.

On owner invalidation:

```text
stop channels
stop guided control
cancel pending visual sequences
release/destroy native adapters through correct lifecycle
clear owner dictionaries
clear per-target histories when appropriate
remove presentation replicas
invalidate cached donor/focus state
```

Do not leave class-owned native objects attached to the old ship.

---

# 20. Multiplayer authority and presentation

The gameplay owner decides mechanics through the shared native combat path.
Remote replicas reconstruct visuals and never run a second damage simulation.
For buffs on another player, the recipient's authoritative local ship applies the
registered grant handler; the sender must not mutate a remote replica.

Implement networking through [Networking for skill authors](<../../Assets/Leviathan/Content/Scripts/NETWORKING.md>).
`OrreryNetwork` / `LeviathanNetwork` own mod-specific contracts; Core owns transport,
validation and engine lifecycle hooks. Skills expose semantic state and visual
behavior. They do not add send/receive hooks or duplicate JSON/byte parsing.

The presentation adapter chooses snapshots, bounded event history or timed
refresh as appropriate. This is an integration decision, not a demand that the
skill designer implement netcode. Packet omission is not a gameplay cancellation.
A reliable send result is not a recipient-application acknowledgement.

Do not grow the common Orrery state indefinitely or allocate a permanent Core
slot per spell. Use the shared typed channel and bank. Protocol bounds, multipart
placement and current validation status are documented once in the linked guide.

---

# 21. Hot-Path and Performance Rules

Orrery may eventually have many spells, drones, halos, DOTs, and co-op players active simultaneously.

Do not treat current V0 spell load as peak load.

Hot paths should prefer:

- reused buffers
- cached reflection handles
- bounded collections
- no LINQ
- no repeated Resources scans
- no per-target heap allocation per tick
- no repeated native-type reflection after first resolution
- fixed maximum chain/spread work per event
- event-driven state where practical

For target search:

```text
broadphase query
    ↓
cheap eligibility filtering
    ↓
bounded candidate selection
    ↓
expensive work only for selected targets
```

---

# 22. Knob Policy

Expose more tuning knobs rather than burying important balance/presentation numbers in logic.

Good knobs include:

```text
DamageMultiplier
IntegratedReferenceSeconds
RangeMeters
RadiusMeters
ConeAngleDegrees
WidthMeters
ProjectileSpeedMetersPerSecond
TurnDegreesPerSecond
DurationSeconds
CooldownSeconds
ChargeSeconds
CritChanceAdditive
CritDamageAdditive
StatusChanceAdditive
ChainRangeMeters
ChainCount
SpreadRadiusMeters
DebuffDurationSeconds
ImmunitySeconds
VisualScale
VisualRangeMultiplier
VisualWaveCount
VisualWaveInterval
VisualOpacity
AudioLeadSeconds
```

Do not expose meaningless knobs merely for quantity.

A knob should correspond to a comprehensible design dimension.

## 22.1 Compendium knobs vs engineering bounds

Prefer a human-facing spell Compendium as the place to find stable spell identity plus the values a designer is expected to tune.

Good Compendium content:

```text
spell id / name / recipe
damage coefficients
ranges / radii / widths
chain counts
travel / expansion speeds
durations / cooldowns
visual scales / opacity / timing
```

Engineering safety limits are different. Values such as:

```text
maximum scratch candidates
maximum internal cache entries
hard runaway timeouts
maximum bounded history storage
```

should either remain private to the runtime or live under an unmistakable `Safety / Bounds` subsection. Do not make the balancing sheet look as though increasing a scratch-buffer limit is normal gameplay tuning.

---

# 23. Tree Integration

Spell mechanics should not know tree node names.

Expose Orrery spell knobs/flags.

Tree nodes modify them.

Example:

```text
Cone of Cold runtime asks:
    resolved range
    resolved angle
    resolved Freeze bonus
    visual wave count

It does NOT ask:
    does player own "Absolute Zero Rank 3"?
```

Unlock nodes belong in the tree/registry boundary.

Mechanic code should operate on resolved spell state.

A useful resolution flow is:

```text
Compendium base spell tuning
        +
resolved elemental-focus bonuses
        +
resolved tree modifiers / flags
        ↓
resolved cast tuning
        ↓
spell runtime
```

The runtime should not query tree node names during damage, targeting, or presentation ticks. Resolve the needed values at the appropriate cast/state boundary and carry only the resolved values the spell actually needs.

The same applies to donor bonuses: the focus layer exposes shared meanings, while the spell/tree resolution layer decides what those meanings modify for that spell.

---

# 24. Recommended Per-Spell File Shape

Do not automatically create many files for every spell.

For a small/simple spell, one spell implementation file plus registry data may be enough.

Split only when there is a genuine lifecycle or reusable-system boundary.

Reasonable shape:

```text
OrrerySpellCompendium.cs
    stable spell identity + human-facing base tuning / presentation knobs

OrrerySpellRegistry.cs
    canonical recipe / executor / unlock-registration boundary

OrrerySpellPower.cs
    shared reference DPS policy

OrreryFocusResolver.cs + resolved focus-stat helper
    shared elemental donor discovery + cross-family Orrery bonus meanings

OrrerySpatial.cs / CoreSpatial.cs
    genuinely shared geometry helpers

OrrerySpellName.cs
    mechanic + bounded runtime state for a substantial spell

OrrerySpellNameRemotePresentation.cs
    skill-specific visuals when substantial; register with shared lifecycle hooks

OrreryNetwork.cs / LeviathanNetwork.cs
    mod-specific networking contracts, outside the gameplay skill
```

The Compendium is **not** a universal effect schema. It is the tune sheet. Spell-specific mechanics remain explicit C# in the spell implementation.

Separate presentation helpers are justified when presentation has a distinct lifecycle or safety boundary, such as presentation-only native projectiles.

Avoid:

```text
SpellNameFix.cs
SpellNameFinal.cs
SpellNameNew.cs
SpellNamePatch2.cs
```

One canonical implementation is preferable.

## 24.1 Unity-safe class folder organization

Class-specific folders are encouraged for organization. Ordinary C# scripts do not gain a namespace or change runtime behavior merely because their filesystem path changes.

An optional organizational example (not the current directory layout or a required refactor):

```text
Assets/Leviathan/Content/Scripts/
    Core/
        ...shared class-agnostic systems...

    Classes/
        Leviathan/
            ...Leviathan controller/runtime/tree files...

        Orrery/
            OrreryController.cs
            OrreryCasting.cs
            OrreryFocusResolver.cs
            OrrerySpellPower.cs
            OrrerySpellCompendium.cs
            OrrerySpellRegistry.cs

            Spells/
                OrreryShatterbolt.cs
                OrreryShatterboltRemotePresentation.cs
                ...

            Networking/
                OrreryNetwork.cs

            Satellites/
                ...

            Tree/
                ...
```

When reorganizing Unity assets:

1. Move every `.cs` file together with its existing `.cs.meta` file.
2. Preserve the `.meta` GUID exactly. Unity serialized `MonoScript` references use that GUID; deleting/regenerating the `.meta` can break references even when the C# type name did not change.
3. Do not accidentally move runtime scripts into Unity special folders such as `Editor`. Those folders change compilation/runtime availability.
4. Do not move scripts across an `.asmdef` assembly boundary without intentionally reviewing references and visibility. A folder move is harmless only when the compile boundary remains the same.
5. Respect other Unity special-folder semantics such as `Resources`, plugins, platform folders, and asset-bundle/addressable conventions.
6. Prefer a dedicated organization-only commit for a large move. Avoid mixing path moves with behavioral refactors so GUID/path mistakes are easy to review or revert.

The runtime Scripts directory contains `LeviathanMod.asmdef`; the SDK also has an editor assembly definition. Inspect the actual assembly boundary and references before a move. Ordinary subfolders inside the same asmdef do not themselves add a namespace or assembly boundary.

---

# 25. Spell Design Sheet — Required Before Implementation

Use this as an implementation checklist scaled to the spell. Capture the user-facing mechanic and important unresolved decisions first. The implementer fills in native/API and networking details; the user does not need to specify packet layouts before skill work can proceed.

```text
SPELL NAME:
SPELL ID:
RECIPE:
PRIMARY ELEMENT / DAMAGE TYPE:
UNLOCK CONDITION:

PLAYER FANTASY:
    What should this feel like in one sentence?

INPUT / CAST FLOW:
    press / hold / release / confirm / detonate / channel?

MECHANICAL DAMAGE MODEL:
    reference DPS coefficient:
    integrated seconds:
    direct hit:
    explosion:
    DOT:
    chain/spread:
    authored coefficient means pre-crit base packet by default?
    any explicit expected-output normalization?

CRIT:
    inherited?
    additive bonus?
    snapshot rules?

STATUS / DEBUFF:
    native status chance:
    additive spell bonus:
    custom debuff:
    stack/refresh rule:

FOCUS INHERITANCE:
    what donor bonuses matter to this spell?
    which values come from the resolved focus profile?
    authored structural baselines that must remain spell-owned?
    Bonus Range handling?
    Bonus Shot Count / Multishot handling?
    Bonus Chain Range handling?
    Bonus Chain Damage handling?
    RoF / Tick Rate -> Bonus Spell Damage is always 1:1
    any explicit exclusions besides raw damage?
    confirm hidden native adapter is not accidentally supplying donor stats?

NO-FOCUS BEHAVIOR:
    standard 80% / 10% crit / 10% status?
    any spell-specific override?

MECHANICAL GEOMETRY:
    shape:
    range:
    radius / width:
    cone angle:
    chain range:
    max targets:

TARGETING:
    cursor / nearest / locked / all in geometry?
    target preference:
    can revisit prior targets?
    immunity/history window?

TIMING:
    windup:
    duration:
    tick rate:
    cooldown:
    travel speed:
    expansion speed:

PRESENTATION:
    native prefab/item reference:
    projectile/beam count:
    visual range:
    visual width/angle:
    visual scale:
    wave/rank count:
    wave interval:
    opacity/brightness/color:
    audio:

MECHANICS VS PRESENTATION DIFFERENCES:
    explicitly list them.

NATIVE REFERENCES TO VERIFY:
    classes/methods/assets to inspect before code.

COMBAT PROVENANCE:
    source owner:
    semantic key(s):
    contributor(s):
    cast / attack instance correlation:
    native / physical source context:

MULTIPLAYER:
    owner-authoritative gameplay state:
    remote presentation state/event:
    can remote derive anything from start time/seed/target?
    event catch-up/history needed if updates coalesce?
    what other players need to see:
    what should happen visually on completion, refresh or target loss:
    adapter/transport details are the implementer's responsibility via the networking guide:

POOLING / CLEANUP:
    created objects:
    pool ownership:
    normal completion:
    cancellation:
    class exit:
    ship replacement:
    world teardown:

PERFORMANCE BOUNDS:
    maximum targets per cast:
    maximum chain/spread operations:
    maximum persistent state:
    hard safety/runtime bounds distinct from designer tuning:

TUNING KNOBS:
    list every value expected to need balance or visual iteration.

OPEN QUESTIONS:
    unresolved design choices that must be answered before coding.
```

---

# 26. Implementation Checklist

Before writing code:

```text
[ ] Get newest exact live files.
[ ] Fill out the spell design sheet.
[ ] Record current unrelated tuned values that must not change.
[ ] Inspect native implementation/assets being reused.
[ ] Decide gameplay vs presentation authority.
[ ] Decide focus donor behavior and receiving meanings for inherited bonuses.
[ ] Decide no-focus behavior.
[ ] Decide whether authored damage coefficients are ordinary pre-crit packets or an explicitly different policy.
[ ] Decide exact meter-based geometry.
[ ] Decide crit/status snapshot behavior.
[ ] Decide combat semantic/contributor/cast-instance provenance.
[ ] Decide multiplayer authority and minimal replicated state.
[ ] Budget spell-specific network bytes and coordinate representability.
[ ] Decide whether bounded event-history catch-up is needed.
[ ] Decide object/pool lifecycle.
[ ] Identify reusable Core/Orrery helpers before duplicating code.
```

During implementation:

```text
[ ] Keep owner gameplay authoritative.
[ ] Preserve native data structures when useful.
[ ] Consume actual resolved focus stats; do not accidentally treat the hidden adapter as the donor.
[ ] Keep presentation-only objects mechanically inert.
[ ] Restore pooled native presentation state before PoolDestroy().
[ ] Avoid allocations in repeated target/tick paths.
[ ] Bound every chain/spread/history structure.
[ ] Cache reflection/resources.
[ ] Route native damage through verified native boundaries.
[ ] Wrap authored GameShip damage in appropriate combat provenance scopes.
[ ] Use meters in tuning.
[ ] Do not raw-Destroy pooled/native-owned objects.
[ ] Handle release/cancel/class-exit/world teardown.
```

Before merge/playtest:

```text
[ ] Compile with no avoidable Orrery warnings.
[ ] Test with matching focus.
[ ] Test with no matching focus.
[ ] Test focus in non-first equipment slot.
[ ] Verify inherited stats came from the actual focus, not a hidden adapter.
[ ] Test crit and confirm the spell's authored coefficient has the intended pre/post-crit meaning.
[ ] Test status/debuff.
[ ] Test combat provenance for direct and secondary spell components.
[ ] Test network catch-up when multiple bounded events occur between remote observations.
[ ] Test worst-case payload size and coordinate quantization/representability.
[ ] Test ship editor rebuild.
[ ] Test class unspec/respec.
[ ] Test world transition/teardown.
[ ] Test repeated casts for leaks/pooled-object errors.
[ ] Test co-op remote presentation.
[ ] Confirm remotes do not apply duplicate gameplay.
[ ] Confirm visual geometry matches intended readability.
[ ] Confirm mechanical geometry matches design, not merely VFX.
[ ] Compare actual tuning to the design sheet.
```

---

# 27. Review Questions

Before calling an Orrery spell complete, ask:

```text
Did we start from current live source?
Did we verify the native behavior we are relying on?
Is spell damage coming from Orrery rather than donor DPS?
Does focus enhance rather than gate the spell?
Are inherited crit/status/bonuses coming from the actual focus rather than a hidden adapter?
Does every inherited bonus have an explicit receiving meaning for this spell?
Are no-focus casts valid and using the standard fallback profile?
Does a displayed/authored damage coefficient mean the intended pre-crit packet rather than a hidden expected-damage normalization?
Are crit/status rules explicit?
Are gameplay and presentation geometry separately defined?
Could any presentation-only projectile accidentally deal damage or status?
Was pooled presentation state restored before returning the object?
Are chain/spread histories bounded?
Does every created object have a correct lifecycle?
Did we use native pool/destruction semantics?
What happens if the player edits/replaces their ship mid-spell?
What happens if Orrery is unspecced mid-spell?
What happens on world teardown?
Is gameplay owner-authoritative in co-op?
Can remote presentation be reconstructed without guessing?
Can packet coalescing erase an event, and if so is bounded cumulative history used?
Does the spell fit its explicit network byte budget?
Can every legal retarget/reacquire/movement state fit the chosen coordinate encoding?
Is authored damage tagged with the semantic/contributor/cast provenance future mechanics will need?
Did we introduce a new abstraction because it is truly shared, or just because this spell needed it once?
Did we preserve unrelated tuning?
Is the result visually readable at normal gameplay zoom?
```

---

# 28. Guiding Rule

The preferred Orrery implementation is not the one with the most framework.

It is the smallest implementation that:

```text
uses native Star Vortex behavior where it is good,
replaces it explicitly where the spell needs something different,
keeps gameplay and presentation authority clear,
preserves item identity without copying item DPS,
is bounded and allocation-conscious,
works cleanly in co-op,
and tears itself down without leaving anything behind.
```
