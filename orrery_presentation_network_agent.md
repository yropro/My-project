# Orrery Reusable Presentation Networking Agent

**Project:** Star Vortex — Orrery / Celestial Mage / Sphereweaver  
**Document purpose:** Exhaustive design/decision/implementation handoff for replacing spell-owned permanent CoreNetwork slot growth with a small reusable Orrery presentation transport.  
**Target branch:** `skill-trees`  
**Source snapshot reviewed:** `f573c41f586fba6113d67a55d1d4945dc5b903e0`  
**Primary live examples:** `CoreNetwork`, `OrreryNetwork`, `OrreryShatterbolt`, `OrreryShatterboltRemotePresentation`, `OrreryPlasmaBolt`, `OrreryPlasmaBoltPresentation`  
**Related architecture handoff:** `spell_lifetime_agent.md`  
**Status:** Architecture/design handoff. This document intentionally constrains the solution without pretending that one universal spell-presentation codec has already been proven.

---

# 0. Executive Summary

The Orrery has reached the point where spell presentation networking needs a reusable class-owned transport layer.

The problem is no longer hypothetical.

The current branch already contains two very different real spell-presentation systems:

```text
Shatterbolt
    slots 7, 8, 9
    moving orb
    cumulative impact history
    resolved explosion radius
    multi-slot atomic decode
    short presentation tail

Plasma Bolt
    slots 10, 11
    instant bolt stroke
    target-attached burn presentation
    rotating refresh batches
    packet sequence
    cast sequence
    remote expiration / refresh timeout
```

Core itself currently has:

```text
MaxSlots       = 16 active dynamic slots in one snapshot
MaxSlotBytes   = 32 bytes per dynamic slot
MaxPayloadBytes = 384 bytes following Core header
MaxCombinedBytes = 500 bytes native + Core extension
```

Core's dynamic transport is already the correct low-level foundation. It is bounded, versioned, owner-class scoped, allocation-stable, stale-aware, and appended to the same 20 Hz PlayerShipState stream.

The problem is at the Orrery layer:

```text
Today:
    every sufficiently complex spell can claim permanent Core slot ids
    every spell can invent its own send patch
    every spell can invent its own registration patch
    every spell can invent its own remote render hook
    every spell can invent its own sequencing / stale semantics
    every spell can independently consume multiple active slot positions

If repeated across a large spell library:
    slot ids proliferate forever
    active dynamic slot pressure grows
    packet budget becomes harder to reason about
    overlapping lingering effects become ad hoc
    transport plumbing is duplicated
    late/stale packet correctness becomes spell-by-spell engineering
```

The desired direction is:

```text
CoreNetwork
    remains generic class/skill transport
        ↓
Orrery base presentation
    stable class casting/satellite state in slot 6
        ↓
small fixed reusable Orrery presentation bank
    a bounded number of reusable lanes
        ↓
spell/effect-specific codecs
    Shatterbolt codec
    Plasma Bolt codec
    future Magma / Cone / Tesla / status / field / satellite codecs
        ↓
remote presentation only
```

The most important rule is:

> **The bank owns transport capacity and lifetime of presentation lanes. The spell/effect codec owns the meaning of its payload.**

Do not turn this into a universal spell engine.

Do not make the bank know what an orb, burn, beam, cone, chain, nova, satellite formation, or delayed detonation means.

Do not make remote presentation authoritative for gameplay.

Do not allocate one permanent Core slot family per spell.

The recommended first implementation should likely reuse the already-occupied Orrery presentation region rather than immediately expanding it:

```text
slot 6
    stable Orrery class/casting/satellite state

slots 7..11
    candidate reusable presentation bank
```

That is a candidate, not an immutable requirement. Five lanes are attractive because the current branch already consumes exactly slots 7 through 11 for spell presentation, meaning the migration can potentially improve architecture without immediately increasing the Core footprint.

The document does **not** mandate that every effect receive exactly one lane.

Some effects may need multiple lanes.

Some may need one.

Some may need none.

Some may share a codec but not an instance.

Some may live after the cast invocation completes.

The transport must support that reality.

---

# 1. Why This Exists Now

The Orrery proof-of-concept strategy was correct: build at least one polished, difficult spell before extracting shared networking infrastructure.

Before Shatterbolt and Plasma Bolt existed, a reusable presentation layer would have been mostly guesswork.

Now there are two strong counterexamples to simplistic designs.

## 1.1 Shatterbolt proves history-heavy moving presentation

Shatterbolt presentation needs to represent:

```text
one moving orb position
one cast identity
orb active/inactive state
0..N completed impact positions
resolved Frost Burst radius
presentation continuing briefly after gameplay cast completion
```

It is not enough to send only the current orb position.

If a remote peer misses the packet containing impact #3, the next accepted packet should still allow that peer to learn that impact #3 happened and create the corresponding Frost Burst presentation.

That is why Shatterbolt uses bounded cumulative impact history instead of transient one-frame impact events.

The current implementation uses three Core slots:

```text
slot 7
    header + orb + radius + first impact positions

slot 8
    additional impact positions

slot 9
    final impact positions
```

The slots are interpreted atomically from the same Core snapshot. If required history is malformed or missing, Shatterbolt presentation is suppressed rather than partially applied.

This is a good correctness property.

It is not a scalable permanent slot-allocation strategy.

## 1.2 Plasma Bolt proves target-attached soft-state presentation

Plasma Bolt has a fundamentally different presentation problem.

The bolt itself is short-lived and easy to snapshot:

```text
bolt present flag
bolt start
bolt end
bolt width
cast sequence
```

The difficult part is Plasma Burn.

A single cast can create many active burns over time because infections spread.

Sending all live burns every 20 Hz would be wasteful.

The current implementation therefore uses:

```text
slot 10
    fixed bolt/header state

slot 11
    rotating batch of up to six target net-id + remaining-time entries
```

Remote burn visuals are soft state:

```text
owner refreshes a subset each send
remote retains refreshed burn visuals temporarily
remote clears a burn when:
    target dies
    authored remaining time expires
    refresh timeout expires
```

That is a very different codec from Shatterbolt.

This proves that a good reusable Orrery transport cannot assume:

```text
all presentation == full snapshot
all presentation == event history
all presentation == one spell == one slot
all presentation == one cast lifetime
all presentation == world-space coordinates only
```

## 1.3 The slot-growth problem is already visible

Current known Core slot ownership at this snapshot includes:

```text
1  Stellar Converter
2  Starfire
3  Predator
4  Constrictor
5  Behemoth
6  Orrery casting
7  Orrery / Shatterbolt presentation
8  Orrery / Shatterbolt impact history
9  Orrery / Shatterbolt impact history tail
10 Orrery / Plasma Bolt presentation
11 Orrery / Plasma Burn refresh batch
```

Important nuance:

`CoreNetwork.RegisterSlot` accepts byte ids and does not mean the numeric namespace ends at 16.

However, the dynamic snapshot storage is hard bounded to:

```text
MaxSlots = 16
```

If more than 16 dynamic slots are in use in one outgoing snapshot, Core drops additional slots and logs a warning.

So the real risks are both:

```text
A. permanent slot-id proliferation
B. active-snapshot slot pressure
```

A future Orrery with dozens of spells should not solve every presentation need by reserving permanent slot ids forever.

## 1.4 Packet budget is shared, not free

Even if active-slot count were unlimited, packet bytes are not.

CoreNetwork has a bounded extension budget:

```text
MaxPayloadBytes = 384
MaxCombinedBytes = 500
```

The native PlayerShipState is variable length.

On an unusually large native packet, Core may skip its extension entirely rather than risk a LiteNetLib oversized sequenced packet.

That is the correct failure policy.

It means Orrery presentation must be designed for:

```text
packet loss
occasional skipped Core extensions
bounded recovery
no requirement that every 20 Hz frame arrive
```

The reusable bank should make presentation budgeting easier, not harder.

---

# 2. Authority / Source Order for the Implementing Agent

Before changing code, use this order:

```text
1. newest explicit user requirement
2. newest exact live source on skill-trees
3. this document
4. ORRERY_SPELL_DESIGN_IMPLEMENTATION_STANDARDS_V3.md
5. spell_lifetime_agent.md where lifetime integration overlaps
6. Core networking / architecture standards
7. older handoffs and prototype comments
```

This document is intentionally grounded in source snapshot:

```text
f573c41f586fba6113d67a55d1d4945dc5b903e0
```

Another agent may modify the branch.

Therefore, before every implementation chunk:

```text
refetch branch head
refetch CoreNetwork.cs
refetch OrreryNetwork.cs
refetch current spell presentation files
verify slot ids and payload versions
verify no other agent has claimed/restructured the same transport
```

Do not implement from stale snippets copied into this document.

The source is authoritative for exact signatures and tuned values.

---

# 3. Problem Statement

The reusable presentation layer exists to solve **transport orchestration and bounded capacity**, not spell mechanics.

The repeated / growing problems are:

1. permanent Core slot ids per spell,
2. permanent Core slot ids per sub-effect,
3. duplicated Core send hooks,
4. duplicated Core registration hooks,
5. duplicated remote render hooks,
6. duplicated serialization helpers,
7. duplicated sequence handling,
8. duplicated stale-time handling,
9. duplicated malformed-payload handling,
10. unclear policy when multiple lingering Orrery effects overlap,
11. unclear policy when presentation capacity is exhausted,
12. unclear policy when one effect needs multiple 32-byte slots,
13. risk that late data from one effect is interpreted as a replacement effect,
14. risk that missing one sub-slot partially mutates remote presentation,
15. difficulty reasoning about total Orrery packet cost as spell count grows.

The desired outcome is not “less code at all costs.”

The desired outcome is:

> **A bounded class-owned transport bank with explicit spell/effect-owned codecs.**

---

# 4. Non-Goals

This section is mandatory. The networking refactor can become destructive if its scope is not constrained.

## 4.1 Do not build a universal spell schema

Do not create a payload DSL like:

```text
GenericSpellPayload
    projectileCount
    projectilePositions[]
    targetCount
    targets[]
    radius
    width
    duration
    statusFlags
    beamFlags
    satelliteFlags
    explosionFlags
    ...
```

That becomes a union of every Orrery spell.

Shatterbolt and Plasma already prove that spell presentation shapes differ materially.

The bank should carry opaque bounded codec bytes.

## 4.2 Do not network gameplay authority

Remote presentation must never decide:

- whether a target was hit,
- whether a burn exists mechanically,
- whether a burn spreads,
- damage values,
- crit results,
- status application,
- chain targets,
- projectile collision,
- cooldown completion,
- formula completion,
- satellite unlocks,
- focus inheritance,
- target immunity history.

Gameplay remains owner-authoritative.

Presentation can disappear completely and gameplay must remain correct.

## 4.3 Do not move spell mechanics into networking

The presentation bank must not know:

```text
how Shatterbolt chooses targets
how Plasma Burn spreads
how Magma tracks cursor
how Cone of Cold hits
how Tesla fades damage
```

The codec may know how to serialize the presentation snapshot provided by its spell.

That is all.

## 4.4 Do not require one lane per effect

A lane is transport capacity, not a spell identity.

Some effects may need:

```text
0 lanes
1 lane
2 lanes
3 lanes
```

The architecture must not assume one-to-one mapping.

## 4.5 Do not require one effect at a time

The Orrery can produce overlapping presentation lifetimes.

Examples:

```text
Shatterbolt Frost Burst tail still visible
    +
new cast begins

Plasma Burn still visible on targets
    +
new spell fires

future persistent gravity field
    +
future satellite formation
    +
new direct cast
```

Any design that says “the current spell owns all presentation bytes” is too restrictive.

## 4.6 Do not force all persistent visuals to network at 20 Hz

A burn timer, stationary field, or long-lived status does not necessarily need 20 full updates per second.

The bank should allow codecs to choose compact soft-state/heartbeat behavior.

## 4.7 Do not redesign CoreNetwork unless necessary

CoreNetwork already supplies useful generic guarantees:

- class ownership,
- slot registration,
- bounded slot size,
- bounded active slot count,
- dynamic snapshot lifetime,
- packet assembly,
- malformed-read safety,
- remote stale timeout,
- co-op transport integration.

The first Orrery networking refactor should sit **above** CoreNetwork.

Do not rewrite the global Core transport merely because Orrery is becoming sophisticated.

## 4.8 Do not combine this with spell balancing

No damage, range, duration, count, burn, chain, or visual-size rebalance is authorized by this work.

## 4.9 Do not combine this with focus inheritance cleanup

`OrreryFocusProfile` is independent of presentation transport.

## 4.10 Do not combine this with the full spell-lifetime migration unless explicitly coordinated

`spell_lifetime_agent.md` and this document are complementary.

They can be implemented sequentially.

Avoid simultaneous broad edits unless branch ownership is coordinated.

---

# 5. CoreNetwork Facts the Design Must Respect

This section records the important current transport facts.

Verify them against live source before implementation.

## 5.1 Transport cadence

CoreNetwork extends Star Vortex `PlayerShipState` traffic.

The live source documents that player ship state is replicated approximately:

```text
20 times per second
```

Do not invent a second high-frequency Orrery transport unless there is a proven requirement.

## 5.2 Dynamic slots are TLV state

Core dynamic state is serialized as:

```text
classId
slotCount
repeat slotCount:
    slotId
    slotLength
    slot bytes
```

Unknown slot ids can be skipped.

Inactive slots cost nothing in that packet.

That is a good substrate for a reusable bank.

## 5.3 Per-slot hard limit

Current:

```text
MaxSlotBytes = 32
```

Every presentation lane built on Core slots must respect that exact bound.

Do not assume the bank can write 40 bytes into one lane because “the bank owns it.”

## 5.4 Active-slot hard limit

Current:

```text
MaxSlots = 16
```

This is the maximum number of active dynamic slots stored/sent in one Core snapshot.

A new slot beyond the active capacity is dropped.

This is one reason permanent per-spell slot design becomes dangerous when effects overlap.

## 5.5 Payload hard limit

Current:

```text
MaxPayloadBytes = 384
```

This is the upper bound for bytes after the Core extension header.

## 5.6 Combined packet safety bound

Current:

```text
MaxCombinedBytes = 500
```

If native packet size plus extension would exceed the safe combined bound, Core refuses to append the extension.

Presentation therefore must tolerate an entire update being absent.

## 5.7 Dynamic stale behavior

Current Core dynamic stale backstop is approximately:

```text
0.5 seconds
```

That is for genuine stream loss.

Ordinary “effect ended” behavior should be communicated by valid subsequent dynamic snapshots that omit the effect/lane.

## 5.8 Same-mod guarantee

The project intentionally distributes the exact same mod build to all players.

Core notes that `ModsMatch` rejects mismatched mod sets.

Therefore this architecture does not need internet-scale backward compatibility negotiation.

Version fields are still valuable because they make development mistakes fail loudly rather than decode silently.

## 5.9 Remote read path must never throw

Malformed presentation is cosmetic.

A presentation decoder must not turn malformed bytes into connection strikes or a crash.

Validate lengths, versions, bounds, finite floats, ids, part counts, and flags before mutating remote visual state.

---

# 6. Current Orrery Networking Topology

At the reviewed snapshot, Orrery networking is split across multiple systems.

## 6.1 Slot 6 — base Orrery class presentation

`OrreryNetwork` uses:

```text
CoreNetwork.SlotOrrery == 6
```

It represents stable class/casting/satellite presentation such as:

- cast phase,
- formula capacity,
- locked count,
- invocation sequence,
- spell id,
- last locked satellite id,
- locked mask,
- disabled mask,
- packed satellite elements.

This should remain conceptually separate from transient spell-effect presentation.

Slot 6 is not the problem.

## 6.2 Slots 7–9 — Shatterbolt-specific

Current Shatterbolt presentation uses three permanent Core registrations.

The code explicitly treats them as one logical bounded payload over one atomic Core snapshot.

Good properties to preserve:

- base Orrery state survives malformed spell presentation,
- no gameplay depends on remote presentation,
- all required pieces are validated before committing remote Shatterbolt state,
- bounded impact count,
- finite-value checks,
- no per-send arrays,
- cumulative history allows missed-packet recovery.

Problem to remove:

```text
Shatterbolt owns permanent transport slots by name forever.
```

## 6.3 Slots 10–11 — Plasma-specific

`OrreryPlasmaBoltPresentation` currently registers:

```text
slot 10 = Plasma Bolt presentation
slot 11 = Plasma Burn refresh batch
```

It also independently patches:

```text
CoreNetwork.AppendLocalExtension
CoreNetwork.RegisterDefaultSlots
RemoteShipDriver.Render
WorldController.OnDestroy
```

Good properties to preserve:

- owner-side presentation read is bounded,
- burn batch size is fixed,
- target ids are stable net ids,
- remaining duration is quantized compactly,
- remote state validates full payload before mutation,
- stale burn visuals disappear without gameplay effects,
- packet/cast sequence prevents duplicate visual replay,
- active burns are rotated rather than fully retransmitted every packet.

Problem to remove:

```text
Plasma owns permanent transport slots and its own transport integration plumbing.
```

---

# 7. Core Architectural Principle

The presentation system should be layered like this:

```text
SPELL / EFFECT RUNTIME
    owns mechanical state
    owns authoritative gameplay
    produces bounded presentation snapshot/state
        ↓
SPELL / EFFECT PRESENTATION CODEC
    knows how to encode/decode that effect's presentation
    knows its own payload semantics
    knows its own stale/refresh model
        ↓
ORRERY PRESENTATION BANK
    owns a small fixed amount of CoreNetwork capacity
    allocates/releases lanes
    associates lanes with effect instance + codec
    enforces byte/count bounds
    publishes lanes
    dispatches received lanes to the correct codec
        ↓
CORENETWORK
    owns actual ship-state extension transport
```

The shortest statement is:

> **Core transports slots. Orrery transports presentation lanes. Codecs transport meaning.**

---

# 8. Terminology

Use these terms consistently.

## 8.1 Base Orrery state

The stable class/casting/satellite presentation in Core slot 6.

## 8.2 Presentation bank

The small fixed set of reusable Core dynamic slots reserved for Orrery spell/effect presentation.

## 8.3 Lane

One reusable transport unit backed by one Core slot, currently max 32 bytes.

A lane is not permanently a Shatterbolt lane or Plasma lane.

## 8.4 Codec

Spell/effect-specific serializer/decoder for presentation bytes.

Examples:

```text
ShatterboltPresentationCodec
PlasmaBoltPresentationCodec
future MagmaCannonPresentationCodec
```

The exact class naming is not mandated.

## 8.5 Presentation instance

One logical replicated visual/effect lifetime.

Examples:

```text
one Shatterbolt cast presentation
one persistent Plasma Burn population owned by a player
one future gravity field
one future satellite formation
```

## 8.6 Instance id / generation

A bounded owner-local identity used so reused lanes cannot cause old data to mutate the wrong replacement presentation.

## 8.7 Part

One lane-sized piece of a multi-lane presentation instance.

## 8.8 Snapshot codec

A codec where each accepted update describes the entire presentation state needed by the remote.

## 8.9 Cumulative-history codec

A codec where each accepted update includes bounded history so missed transient events can be reconstructed.

Shatterbolt impact history is this pattern.

## 8.10 Rotating-refresh codec

A codec where each update refreshes a bounded subset of a larger soft-state population and remote entries expire without refresh.

Plasma Burn is this pattern.

## 8.11 Presentation tail

Presentation that intentionally remains after the gameplay invocation has completed.

---

# 9. Mandatory Invariants

These are not suggestions.

## 9.1 Gameplay must survive total presentation failure

If every Orrery presentation lane is dropped:

```text
all damage still happens correctly
all status/debuff behavior still happens correctly
all cooldown/formula behavior still happens correctly
all target selection still happens correctly
```

Only remote visuals/audio may degrade.

## 9.2 Lane capacity is bounded

No dynamic lists that can grow with spell count, target count, projectile count, or fight duration in the send path.

## 9.3 Lane ownership is explicit

At any point, each active lane must have one clear logical owner:

```text
codec id
presentation instance id
part index
part count or equivalent grouping identity
```

## 9.4 Reuse must be safe

A lane released by old instance A and allocated to new instance B must not allow:

```text
late A payload
    ↓
remote B state mutation
```

Use instance/generation identity.

## 9.5 Multi-lane decode is atomic at the effect level

If an effect requires multiple lane parts, do not mutate its remote state from only half the required parts unless the codec explicitly defines partial availability as valid.

Default behavior:

```text
validate complete required group
then commit
```

## 9.6 Base Orrery state is isolated

Malformed spell presentation must not invalidate slot 6 casting/satellite presentation.

## 9.7 Remote decoders validate before mutation

Do not:

```text
spawn two visuals
then discover byte 27 is malformed
then return false
```

Decode into temporary bounded state first.

Validate.

Then mutate visual state.

## 9.8 No unbounded event queues

The 20 Hz stream is state-oriented.

Do not create an ever-growing reliable-event emulation layer on top of it.

Use bounded cumulative history or repeat windows where needed.

## 9.9 Clear semantics are explicit

Every codec must define how the remote knows an effect ended.

Possible mechanisms:

```text
lane omitted from next valid snapshot
instance removed from bank
explicit inactive flag
remaining duration reaches zero
refresh timeout expires
new generation replaces old generation
```

Do not leave this implicit.

## 9.10 Presentation overlap is legal

The bank must be designed under the assumption that multiple presentation instances can coexist.

## 9.11 Capacity exhaustion cannot affect gameplay

If the bank has no free presentation capacity, the system may:

```text
degrade a cosmetic tail
skip a low-priority visual
reduce refresh coverage
replace an older low-priority presentation
```

It must not:

```text
cancel a spell
skip damage
change a target
alter cooldown
alter DOT mechanics
```

## 9.12 Hot send/read paths avoid managed allocation

Use fixed arrays, structs, reusable scratch buffers, and bounded loops.

Do not allocate byte arrays/LINQ/enumerators each ship-state packet.

---

# 10. Why “One Active Spell Payload” Is Not Enough

A tempting simple design is:

```text
slot 7 = current spell id + current spell payload
```

That is insufficient.

Consider:

```text
time 0.0
    Plasma Bolt casts

time 0.1
    bolt visual ends
    Plasma Burns remain for 5 seconds

time 0.6
    satellites rearm

time 1.0
    Shatterbolt casts

time 1.1+
    Shatterbolt orb + impact history need replication
    Plasma Burn presentation still needs refresh
```

There is no single “current spell presentation.”

There are concurrent presentation instances from multiple completed/current casts.

Future Orrery design makes this even more likely:

- lingering fields,
- orbiting effects,
- delayed detonations,
- satellite formations,
- beams while statuses remain,
- summoned/duplicated satellite visuals,
- persistent marks,
- temporary shields,
- multiple long-lived target attachments.

Therefore the transport needs a **bank**, not a single current-spell union.

---

# 11. Why “One Permanent Slot Per Spell” Is Also Wrong

The opposite simple design is:

```text
Shatterbolt gets permanent slots
Plasma gets permanent slots
Magma gets permanent slots
Tesla gets permanent slots
...
```

This scales poorly for several reasons.

## 11.1 Most spell slots are idle most of the time

Permanent ids are cheap when omitted, but they still spread registration and codec ownership across many files.

## 11.2 Complex overlap can hit active-slot count

Core can carry at most 16 active dynamic slots in one snapshot.

The class should not spend this budget accidentally based on how many spell authors independently decided they needed two or three ids.

## 11.3 Packet reasoning becomes global archaeological work

To understand Orrery packet cost, an engineer would have to search every spell file for Core slot registration.

A bank makes the maximum obvious.

## 11.4 Cross-spell prioritization is impossible

If every spell owns its own slots, there is no class-level place to decide:

```text
active projectile is more important than old fading tail
new beam is more important than one missed burn refresh
```

## 11.5 Transport integration proliferates

Plasma already proves this with separate Harmony hooks.

That pattern should not be repeated for dozens of spells.

---

# 12. Recommended Direction: Fixed Reusable Lane Bank

The strongest current candidate is:

```text
Core slot 6
    stable Orrery class state

fixed bank of Orrery presentation lanes
    lane 0 -> one Core slot
    lane 1 -> one Core slot
    lane 2 -> one Core slot
    lane 3 -> one Core slot
    lane 4 -> one Core slot
```

The natural first bank to investigate is:

```text
Core slots 7..11
```

because those ids are already consumed by current Shatterbolt + Plasma presentation.

This could allow migration without increasing the current Orrery slot footprint.

Do not treat five as sacred.

The implementing agent must measure whether five lanes provide acceptable overlap for the known effects.

## 12.1 What each lane conceptually carries

Every occupied lane needs enough identity for a remote to understand:

```text
which codec owns this lane?
which presentation instance is this?
which part of that instance is this?
how many parts are required?
what bytes belong to the codec?
```

Possible conceptual header:

```text
codec/effect id
instance id / generation
part index
part count
flags/version
opaque codec bytes
```

The exact byte layout is **not frozen by this document**.

Byte overhead matters because each lane is only 32 bytes.

The agent should compare at least the candidate layouts in Section 20.

## 12.2 Lane identity vs slot identity

The Core slot id should identify:

```text
Orrery presentation lane #N
```

not:

```text
Shatterbolt history tail
```

The payload identifies which effect currently owns the lane.

## 12.3 Occupancy changes over time

Example:

```text
packet A
    lane 0 = Plasma population instance 41 part 0/2
    lane 1 = Plasma population instance 41 part 1/2

packet B
    lane 0 = Plasma population instance 41 part 0/2
    lane 1 = Plasma population instance 41 part 1/2
    lane 2 = Shatterbolt instance 42 part 0/3
    lane 3 = Shatterbolt instance 42 part 1/3
    lane 4 = Shatterbolt instance 42 part 2/3

packet C after Shatterbolt ends
    lane 0 = Plasma population instance 41 part 0/2
    lane 1 = Plasma population instance 41 part 1/2

packet D after Plasma ends
    no bank lanes active
```

The exact allocation order does not need to be remotely predetermined if each lane is self-identifying.

---

# 13. Bank Capacity Is a Deliberate Cosmetic Limit

A fixed bank means there is a maximum concurrent amount of Orrery presentation data.

That is good.

The alternative is unbounded transport growth.

The design must define what happens when demand exceeds capacity.

## 13.1 Capacity exhaustion is presentation-only

Possible policy:

```text
try to allocate requested lanes
if impossible:
    degrade / suppress selected presentation
    log rate-limited diagnostic in debug builds
    gameplay continues unchanged
```

## 13.2 Do not silently allocate new Core slots forever

If five lanes prove insufficient, that should be an explicit architecture decision:

```text
measure real overlap
measure packet cost
increase bank from 5 to 6 or 7 if justified
```

not:

```text
spell #8 just registers slots 19 and 20
```

## 13.3 Capacity should be tunable in one place

The bank size should be obvious from one class/file.

---

# 14. Presentation Priority

If bank capacity can be exhausted, the architecture needs a bounded priority model.

Do not overengineer this on day one.

A practical initial hierarchy could be:

```text
Priority 3 / Critical presentation
    currently controlled/aimed major cast visual
    moving projectile where absence is highly noticeable

Priority 2 / Active gameplay-correlated presentation
    active beam/field/status population
    currently expanding nova

Priority 1 / Tail / recovery presentation
    completed impact history retained for packet recovery
    fading status visual
    old explosion tail

Priority 0 / Decorative
    optional cosmetic-only flourish
```

Important:

“Critical” here means visually important, **not gameplay critical**.

The first implementation may use only two levels if that is enough:

```text
active
retained-tail
```

Do not build a scheduler with dozens of weights until evidence requires it.

---

# 15. Preemption Policy

If a new higher-priority presentation needs lanes and the bank is full, possible strategies include:

```text
A. refuse new presentation
B. preempt oldest low-priority tail
C. preempt lowest-priority refresh population
D. reduce a multi-lane codec to a degraded mode
```

Recommended default:

```text
never preempt active high-value presentation for a cosmetic tail
allow old tails to be preempted first
allow codecs to define an optional degraded lane count
```

Example:

Shatterbolt could potentially have:

```text
full mode
    orb + complete history

degraded mode
    orb + latest impacts only
```

Do **not** implement degraded Shatterbolt merely because this document mentions it.

Only add codec-specific degradation when needed.

---

# 16. Presentation Instance Identity

Reusable lanes require stronger identity than permanent spell-owned slots.

## 16.1 Why identity is required

Suppose:

```text
lane 2 belonged to Shatterbolt cast A
lane 2 is released
lane 2 is immediately reused by Magma cast B
```

A late/stale Shatterbolt state must never be interpreted as Magma B.

Core snapshots are sequenced state, which already helps, but explicit instance identity is still valuable for remote lifecycle correctness and codec replay suppression.

## 16.2 Recommended identity shape

Use an owner-local bounded instance/generation value.

A likely candidate:

```text
ushort PresentationInstanceId
```

The bank increments it when a new logical presentation instance is created.

The remote key becomes conceptually:

```text
(remoteOwner, codecId, instanceId)
```

## 16.3 Invocation sequence may be reused when appropriate

For a presentation exactly tied to one spell invocation, the invocation sequence low 16 bits may be a valid instance id.

However, not every future presentation will map cleanly to one invocation.

Example:

```text
persistent class aura
long-lived satellite formation
population-level Plasma Burn refresh state
```

Therefore the bank should not require that instance id == cast id.

The codec may carry cast identity separately if needed.

## 16.4 Wraparound

A ushort wraps.

That is acceptable if:

- stale windows are bounded,
- released instances are not retained for thousands of casts,
- comparison is equality-based for identity rather than naive greater-than ordering,
- the bank does not reuse one instance id while an old same-id presentation could reasonably remain alive remotely.

If implementation wants serial-number ordering, use correct modular comparison.

Do not invent ordinary signed integer `>` semantics across wrap.

---

# 17. Codec Identity

Each presentation family needs a stable codec/effect id.

This may be:

```text
ushort codecId
```

or a smaller byte if the project is confident the namespace is sufficient.

Do not conflate:

```text
spell registry id
combat semantic effect id
presentation codec id
```

They may sometimes share numbers, but their lifetimes/purposes differ.

A presentation codec can represent:

```text
one spell
one sub-effect shared by multiple spells
one persistent class visual
```

The mapping should be explicit.

---

# 18. Versioning

Versioning should exist at the narrowest useful level.

Possible layers:

```text
bank wire version
codec payload version
```

Because all peers run the same mod build, versioning is diagnostic rather than compatibility negotiation.

Recommended behavior:

```text
unknown bank version
    ignore bank presentation
    preserve base Orrery state

unknown codec id
    ignore that presentation instance

known codec, wrong codec version
    ignore that instance
```

Never reinterpret unknown bytes heuristically.

---

# 19. Candidate Lane Header Designs

The implementing agent should explicitly compare these rather than blindly selecting one.

## 19.1 Candidate A — self-describing every lane

Each Core lane begins with something like:

```text
byte/ushort codec id
ushort instance id
byte part descriptor
byte codec version or flags
... codec bytes
```

Advantages:

- every lane independently identifies itself,
- lane ordering can change freely,
- no directory/control slot needed,
- easy to validate multi-lane groups,
- missing lanes fail locally.

Disadvantages:

- repeated header cost on every lane,
- a 32-byte lane may lose ~5–7 bytes to metadata.

This is currently the safest conceptual default.

## 19.2 Candidate B — one bank directory + raw data lanes

One Core slot describes:

```text
lane ownership table
codec ids
instance ids
part mapping
```

Other slots carry raw codec bytes.

Advantages:

- payload lanes use nearly all 32 bytes,
- multi-lane structure centralized.

Disadvantages:

- permanently spends one lane on directory metadata,
- directory itself becomes a new bottleneck,
- malformed directory can suppress entire bank,
- more complex encode/decode,
- harder to degrade partially.

Do not choose this unless byte measurements show self-describing overhead is materially harmful.

## 19.3 Candidate C — fixed lane roles by position

Example:

```text
lane 0 always control
lane 1 always active cast
lane 2 always persistent status
...
```

Advantages:

- small headers,
- simple.

Disadvantages:

- assumes presentation categories before enough spells exist,
- wastes capacity when one category is idle,
- future spells can violate categories,
- weak support for multi-lane effects.

Not recommended as the general architecture.

## 19.4 Candidate D — one variable mega-payload outside slots

Modify CoreNetwork so Orrery gets a large custom block.

Advantages:

- avoids 32-byte slot boundaries.

Disadvantages:

- redesigns Core transport,
- complicates global packet budgeting,
- special-cases Orrery in Core,
- bypasses proven slot machinery,
- increases blast radius.

Not recommended unless real measurements prove the slot abstraction impossible.

---

# 20. Recommended First Header Experiment

A reasonable first experiment is a compact self-describing lane header.

For example, conceptually:

```text
byte 0     bank/lane format + flags
byte 1     codec id low / or byte codec id
byte 2..3  instance id
byte 4     partIndex / partCount packed
byte 5     codec payload version
byte 6..   codec payload
```

This is only an example.

The agent should measure whether:

```text
26-ish codec bytes per lane
```

is enough for Shatterbolt and Plasma without increasing lane count.

If not, try alternate packing before expanding the bank.

Possible optimizations:

- pack part index/count into nibbles,
- omit redundant version if codec id implies version under same-build guarantee,
- use one byte codec id initially,
- derive part count from codec contract when fixed,
- reserve flag bits for continuation/priority.

Do not sacrifice stale/reuse correctness merely to save one byte.

---

# 21. Multi-Lane Effects

Multi-lane effects are allowed and expected.

## 21.1 Grouping

Every part must be groupable by:

```text
codec id
instance id
part index
part count
```

or an equivalent unambiguous contract.

## 21.2 Required-part validation

Default decode algorithm:

```text
collect bank lanes from one Core snapshot
validate each lane header
partition lanes by (codec, instance)
for each group:
    validate required part count
    validate no duplicate part indices
    validate indices in range
    pass immutable/temp part views to codec
    codec validates full payload
    only then mutate remote presentation
```

## 21.3 Atomic snapshot advantage

Core already commits all dynamic slots from one accepted PlayerShipState snapshot together.

Use that.

Do not treat lane 7 from one packet and lane 8 from a later packet as pieces of one atomic update unless the codec explicitly implements that behavior.

## 21.4 Missing part

If an effect requires 3 parts and only 2 are present:

```text
do not partially decode by default
```

Depending on codec semantics, remote may:

```text
retain last valid visual for short refresh window
or clear immediately
```

That policy belongs to the codec/presentation lifetime contract.

---

# 22. Single-Lane Effects

Many effects should remain single-lane.

Examples could include:

- short beam endpoints,
- one projectile pose,
- one explosion center/radius,
- one satellite formation state,
- one active cast charge/progress.

Do not force them into a multi-part abstraction beyond the common header.

---

# 23. State vs Event History vs Soft State

The bank must support at least three proven semantics.

## 23.1 Full snapshot state

Every update contains the current presentation state.

Remote can replace its previous decoded state directly.

Good for:

- projectile pose,
- beam endpoints,
- charge progress,
- radius/progress values.

## 23.2 Bounded cumulative history

Each update contains current state plus bounded past events needed to recover from missed packets.

Shatterbolt impact positions are the canonical example.

Good for:

- one-shot explosions that must not disappear because one packet was lost,
- finite chains,
- finite burst history.

Bound the history at design time.

## 23.3 Rotating soft-state refresh

Each update contains a subset of a larger current visual population.

Remote retains entries for a short refresh window.

Plasma Burn is the canonical example.

Good for:

- many target-attached statuses,
- large but bounded populations,
- slowly changing persistent effects.

The codec must define:

```text
refresh cadence
maximum refresh gap
remote expiry timeout
per-entry remaining-time representation
```

---

# 24. One-Shot Events Must Be Loss-Tolerant

Do not send a presentation event exactly once and assume it arrives.

The Core stream is sequenced/unreliable enough that individual updates can be absent.

Preferred patterns:

```text
A. retain event in bounded cumulative history for N packets/time
B. represent event as state with a short visible-until timestamp
C. repeat the event identity for a bounded presentation tail
```

Examples:

Shatterbolt:

```text
completed impacts remain in history briefly
```

Plasma bolt:

```text
bolt flag/start/end remain published for ~0.75 s
```

These are good patterns.

---

# 25. Remote Presentation Lifetime

Remote visuals need explicit lifetime behavior independent of gameplay.

Every codec must answer:

```text
What creates the remote presentation?
What refreshes it?
What makes it expire naturally?
What happens if updates stop?
What happens if its lane disappears?
What happens if the remote owner disappears?
What happens if a target disappears?
What happens if a new instance reuses the lane?
```

## 25.1 Missing from next valid bank snapshot

For ordinary snapshot effects, absence should usually mean:

```text
this instance is no longer being published
```

The remote can clear it immediately or run a defined fade.

## 25.2 Stream loss

If Core dynamic state itself becomes stale, all bank-owned remote presentation should eventually clear.

Do not leave beams/projectiles/status overlays frozen forever.

## 25.3 Codec-specific retention

A rotating refresh codec like Plasma may intentionally retain target visuals across packets where that target was not included.

That is valid because the codec defines a refresh timeout.

The bank should not second-guess that semantic.

---

# 26. Lane Release Semantics

Owner-side lane release should be explicit.

Conceptually:

```text
Release(instanceHandle)
```

means:

```text
this logical presentation instance no longer requests bank capacity
```

On the next valid dynamic snapshot, those lane slots are absent or reassigned.

The remote sees the old `(codec, instance)` disappear and performs codec-defined cleanup.

Do not rely only on local object destruction to imply network release.

---

# 27. Lane Allocation Semantics

A presentation instance may request capacity when it starts.

Conceptual request:

```text
Request(
    codecId,
    instanceId,
    desiredParts,
    minimumParts,
    priority)
```

This is conceptual, not a required API.

Important behaviors:

- fixed maximum lanes,
- no allocations in hot path after initialization,
- deterministic local ownership,
- no gameplay dependency on success,
- optional degraded lane count if codec supports it.

## 27.1 Do not expose Core slot ids to spell code

Spell presentation should ask for a lane/group handle.

It should not say:

```text
use Core slot 12
```

The bank maps handles to actual Core slots.

---

# 28. Allocation Timing

Do not continuously release/reallocate lanes every 20 Hz merely because payload length changes.

Prefer stable occupancy for the lifetime of a presentation instance when practical.

This reduces:

- churn,
- generation changes,
- remote cleanup/recreate noise,
- allocator complexity.

A codec can use fewer bytes inside its assigned parts on a given packet.

If an effect genuinely changes required part count, define the transition explicitly.

---

# 29. Stable Instance vs Rotating Data

Plasma is an important example.

The burn population can be one persistent presentation instance whose lane payload rotates target entries.

Do **not** create a separate presentation-bank instance per infected target unless measurements justify it.

That would consume capacity proportional to target count and defeat the bank's purpose.

Instead:

```text
one Plasma population codec instance
    contains rotating target refresh records
```

This mirrors the successful current design.

---

# 30. Shatterbolt Mapping onto the Bank

This section is illustrative, not a mandate to rewrite Shatterbolt immediately.

## 30.1 Current needs

Shatterbolt currently needs:

```text
cast sequence
orb active
orb world position
resolved burst radius
impact count
up to 10 impact world positions
```

## 30.2 Current byte shape

The live implementation uses full float positions and spreads the data across three Core slots.

This was chosen to avoid assumptions about owner/target movement and delta-encoding range.

That correctness rationale should be preserved unless a measured alternative is clearly safe.

## 30.3 Bank representation

Likely:

```text
one Shatterbolt presentation instance
requested parts: 2–3 depending on chosen packing
codec: Shatterbolt
instance id: cast/presentation generation
```

The codec remains responsible for:

- impact count bound,
- finite positions,
- radius validation,
- required history parts,
- unseen-impact spawning,
- presentation tail.

The bank is responsible for:

- lanes,
- grouping,
- identity,
- transport write/read dispatch.

## 30.4 Do not erase cumulative history

A migration that merely sends “latest impact this packet” is a regression.

Packet loss would make explosions disappear remotely.

Keep bounded cumulative history or replace it with an equally loss-tolerant design.

---

# 31. Plasma Bolt Mapping onto the Bank

## 31.1 Current needs

Plasma currently needs two distinct categories:

```text
bolt header/state
rotating burn refresh batch
```

## 31.2 Likely bank representation

Potentially one logical Plasma presentation instance with two parts:

```text
part 0
    bolt/cast state

part 1
    rotating target refresh batch
```

or two logical codec instances if lifecycle evidence makes that cleaner:

```text
PlasmaBoltStroke instance
PlasmaBurnPopulation instance
```

The implementing agent should compare both.

## 31.3 Recommendation

Prefer separating **lifetimes**, not merely files.

The bolt stroke is very short-lived.

The burn population can persist for seconds and across later casts.

Therefore a strong candidate is:

```text
codec A = Plasma stroke
codec B = Plasma burn population
```

This could allow the stroke lane to release quickly while the burn refresh lane remains.

However, two codec instances cost extra identity/header bytes.

Measure before freezing.

## 31.4 Preserve rotating refresh

Do not “simplify” Plasma by sending all active burns every packet.

The current rotating bounded refresh exists for a good reason.

---

# 32. Future Presentation Shapes the Bank Must Not Block

The bank should be tested mentally against several future categories.

## 32.1 Traveling projectile

Needs:

```text
instance
position
rotation/heading maybe
progress maybe
```

## 32.2 Beam/channel

Needs:

```text
origin
endpoint or direction/range
width
charge/fade state
```

## 32.3 Expanding ring/nova

Needs:

```text
center
radius/progress
instance
```

## 32.4 Delayed detonation

Needs:

```text
position/target
charge progress
release/detonation transition
```

## 32.5 Persistent target status population

Needs:

```text
rotating target IDs
remaining time/intensity
```

## 32.6 Persistent world field

Needs:

```text
center
radius
remaining duration
state flags
```

## 32.7 Satellite formation manipulation

Needs:

```text
satellite masks/ids
formation mode
anchors/target
progress
```

## 32.8 Multiple visual duplicates

Needs bounded count/positions or a deterministic reconstruction seed/state.

Do not require one lane per visual duplicate.

---

# 33. Position Encoding Policy

There is no universal rule that every codec must use full floats or quantized deltas.

The codec owns that choice within project standards.

## 33.1 Full world-space floats

Advantages:

- simple,
- robust under moving owner/targets,
- no range clipping,
- no anchor ambiguity.

Cost:

```text
Vector2 = 8 bytes
```

## 33.2 Quantized positions

Advantages:

- much smaller,
- good for bounded local effects.

Risks:

- anchor drift,
- clipping,
- assumptions invalidated by reacquisition/movement,
- visible error.

## 33.3 Recommended standard

Each codec must document:

```text
coordinate frame
precision
maximum representable range
clamp behavior
why those bounds are safe
```

If the bound cannot be proven from spell mechanics, use a safer representation.

Do not hide clamping.

---

# 34. Time Encoding Policy

Presentation time should be encoded according to what the remote needs.

Options:

- remaining duration quantized,
- progress byte,
- tick count,
- sequence + local receipt time,
- no time at all if state is refreshed continuously.

Plasma's current:

```text
remaining byte * 0.05 seconds
```

is a good example of compact effect-specific quantization.

Do not send absolute `Time.time` between peers as if clocks are synchronized.

---

# 35. Target Identity Policy

For remote target-attached visuals, use stable network target identity where available.

Plasma currently uses:

```text
GameShip.netId
```

and resolves it through `NetWorldBridge`.

Good properties:

- compact uint,
- does not depend on world-space proximity guesses,
- naturally supports moving targets.

Validate:

```text
id != 0
resolved target exists
resolved target is appropriate type
```

Presentation failure to resolve a target should simply suppress/clear that visual.

---

# 36. Sequence Numbers

There are several distinct sequence concepts.

Do not collapse them accidentally.

## 36.1 Core packet sequence/order

Provided implicitly by the underlying sequenced transport and Core snapshot update.

## 36.2 Presentation instance id

Identifies one logical presentation lifetime.

## 36.3 Codec-local event sequence

May be needed to prevent replay within one persistent instance.

Plasma currently has packet/cast sequences.

## 36.4 Spell invocation sequence

Owned by Orrery casting.

Can correlate with presentation but is not always the same as presentation lifetime.

Document which sequence a codec uses and why.

---

# 37. Remote State Keying

Remote presentation state should normally be keyed by:

```text
remote owner ship
    +
codec id
    +
instance id
```

Avoid keying only by lane number.

Lane numbers are reusable transport addresses.

If remote state is keyed only by lane, lane reuse can accidentally mutate the previous effect's objects.

---

# 38. Lane Reassignment

When a lane changes from:

```text
(codec A, instance X)
```

to:

```text
(codec B, instance Y)
```

remote processing should conceptually:

1. observe old group no longer present,
2. run old codec cleanup/retention semantics,
3. validate new group,
4. create/update new presentation.

Do not mutate old presentation objects into unrelated new effect objects merely because they share transport lane index.

---

# 39. Snapshot Assembly Order

The bank should publish all of its lanes from one centralized send integration point.

Current anti-pattern:

```text
OrreryNetwork publishes some slots
Plasma patch publishes other slots in CoreNetwork.AppendLocalExtension prefix
future spell patch publishes more
```

Desired:

```text
CoreNetwork asks/receives local state
    ↓
OrreryPresentationBank publishes all current Orrery presentation lanes
```

The exact hook can be:

- one Orrery patch before Core builds payload,
- a direct call from one existing Orrery/Core integration point,
- another narrow mechanism consistent with current architecture.

What matters is one owner for Orrery presentation publication.

---

# 40. Slot Registration

Register the bank lanes once.

Conceptually:

```text
Orrery Presentation Lane 0
Orrery Presentation Lane 1
Orrery Presentation Lane 2
Orrery Presentation Lane 3
Orrery Presentation Lane 4
```

Do not register:

```text
Magma slot
Magma explosion slot
Tesla slot
Tesla chain slot
Cone slot
...
```

Centralize registration.

Prefer no Harmony patch per codec solely to register its slots.

---

# 41. Remote Render Integration

Remote presentation decoding/ticking should also converge on one Orrery integration path.

Current Plasma directly patches `RemoteShipDriver.Render`.

Future dozens of spell-specific Render patches would be unnecessary duplication.

Desired conceptual path:

```text
RemoteShipDriver.Render
    ↓ one Orrery presentation bridge
OrreryPresentationBank.TickRemote(remoteOwner)
    ↓
dispatch decoded active instances to codecs/presenters
```

The bank does not render the effects itself.

It calls the appropriate codec/presenter.

---

# 42. World / Owner / Target Cleanup

Networking presentation cleanup must integrate with the lifetime architecture without duplicating mechanics.

## 42.1 World teardown

One Orrery presentation reset path should:

- release owner-side bank occupancy,
- clear remote presentation instance tables,
- ask codecs to destroy/return pooled visuals,
- clear reusable scratch/state.

## 42.2 Owner destroyed / remote owner disappears

Clear all presentation instances owned by that ship.

## 42.3 Target destroyed

Target-attached codecs may need a notification.

Example:

```text
Plasma Burn visual attached to destroyed target
```

Do not require the generic bank to understand “burn target.”

Instead give codecs a narrow target-destroy notification if needed.

## 42.4 Class exit

Orrery presentation lanes disappear when the owner is no longer Orrery.

Remote Orrery visuals must clear.

---

# 43. Relationship to spell_lifetime_agent.md

These systems solve different problems.

```text
SpellLifetime
    owner-side gameplay/runtime ticking and teardown

PresentationBank
    owner-side presentation transport + remote presentation dispatch
```

A spell may remain mechanically alive with no network presentation capacity.

A presentation tail may remain briefly after the cast gameplay completes.

Neither layer should infer the other's lifetime from one boolean.

A clean interaction is:

```text
spell runtime updates its presentation snapshot/source state
presentation codec reads bounded snapshot
presentation bank transports it
```

The lifetime layer does not need to serialize bytes.

The bank does not need to tick Plasma Burn damage.

---

# 44. Owner-Side Presentation API Shape

Do not freeze a complex interface prematurely.

A minimal first-stage design may be explicit.

Example conceptual shape:

```csharp
OrreryPresentationBank.PublishLocal(owner)
{
    // Ask known codecs what they currently need.
    ShatterboltPresentationCodec.Publish(owner, bank);
    PlasmaPresentationCodec.Publish(owner, bank);
}
```

This is completely acceptable initially.

Do **not** assume a registry/interface is automatically better.

When the spell count makes explicit calls unwieldy, add a small fixed codec registry.

---

# 45. Codec Interface — Only If It Earns Its Keep

If/when a common interface becomes useful, keep it transport-focused.

Conceptual example:

```text
CodecId
TryBuildLocal(owner, bankWriter)
TryDecodeRemote(owner, groupedParts, tempState)
CommitRemote(owner, decodedState)
ForgetRemote(owner, instance)
Reset()
```

Do not add generic spell mechanics methods.

Bad:

```text
GetDamage()
GetTargets()
GetRadius()
ApplyStatus()
```

Those do not belong here.

---

# 46. Bank Writer Responsibilities

A bank writer/helper may enforce:

- lane count,
- per-lane byte count,
- instance metadata,
- part indexing,
- no duplicate claims,
- deterministic release,
- overflow failure without gameplay effect.

The codec writes only its opaque payload after the common lane header.

---

# 47. Bank Reader Responsibilities

A bank reader/dispatcher may enforce:

- known lane slot ids,
- lane header validity,
- codec id range,
- instance id validity rules,
- part index/count validity,
- duplicate-part rejection,
- grouping by `(codec, instance)`,
- bounded number of groups,
- no allocation in steady state.

Then it passes validated grouped payload slices/readers to the codec.

---

# 48. No Per-Packet Heap Work

The bank should preallocate enough storage for:

```text
MaxBankLanes
lane metadata
payload scratch
remote decoded lane descriptors
small group table
```

Do not create:

```text
List<Lane> each packet
Dictionary<Instance, ...> each packet
byte[] each lane each packet
LINQ GroupBy
```

Persistent remote presentation state dictionaries keyed by owner can be acceptable if bounded by actual remote player count and not created every frame.

---

# 49. Bounded Grouping Algorithm

With a small bank, grouping can be intentionally simple.

Example for 5 lanes:

```text
for each lane i
    decode metadata into fixed descriptor[i]

for each descriptor i
    find matching group in small fixed group array
    or create next group if capacity remains

validate each group
```

O(5²) is trivial and can be clearer than a dictionary.

Do not optimize tiny fixed sets into complicated machinery.

---

# 50. Bank Size Decision

The first implementation should explicitly document why it chose its bank size.

## 50.1 Five-lane argument

Pros:

- slots 7–11 already exist today,
- no immediate increase to current Orrery registered presentation footprint,
- enough to carry current Shatterbolt (3) + current Plasma (2) simultaneously in worst current form,
- simple migration target.

Cons:

- leaves no spare capacity if both current effects consume their full present form and a third presentation overlaps,
- common lane headers may increase part count unless codecs repack.

## 50.2 Six/seven-lane argument

Pros:

- more overlap headroom,
- easier codec migration.

Cons:

- more active-slot pressure,
- more packet budget available to Orrery whether or not wise,
- less discipline.

## 50.3 Recommendation

Prototype against **five reusable lanes first** because that is the current physical footprint.

Measure:

- Shatterbolt required parts after common header,
- Plasma required parts after common header,
- simultaneous overlap,
- total bytes,
- whether useful degradation works.

Expand only if evidence demands it.

---

# 51. Core Active-Slot Interaction

Remember the bank is not alone.

Core dynamic snapshot can contain other class/skill slots depending on the active class.

For Orrery specifically, the expected active Core slots are primarily:

```text
slot 6 base Orrery
+ active bank lanes
```

Leviathan-specific slots should not ordinarily be active for an Orrery owner.

Still, Core's global limit is shared infrastructure and should not be casually consumed.

---

# 52. Byte Budget Accounting

The implementation should have a human-readable budget table.

Example template:

```text
BANK LANE HEADER
    metadata                 X bytes
    remaining codec payload  32-X bytes

SHATTERBOLT
    part 0 ...
    part 1 ...
    part 2 ...
    worst-case total ...

PLASMA STROKE
    ...

PLASMA BURN REFRESH
    ...

WORST CURRENT OVERLAP
    Core slot 6 ...
    bank lanes ...
    dynamic TLV overhead ...
    total ...
```

Do this before committing wire format.

---

# 53. Dynamic TLV Overhead Matters

Each active Core slot itself adds outer dynamic-block overhead:

```text
slot id
slot length
```

plus the bytes inside the slot.

A three-lane effect therefore costs more than merely `3 * payload`.

This is another reason not to fragment effects across more lanes than necessary.

---

# 54. Codec Packing Should Be Spell-Specific

Do not make the bank decide that all floats become half floats, all positions become deltas, etc.

The bank enforces maximum bytes.

The codec chooses efficient safe semantics.

Examples:

```text
Plasma remaining duration -> byte
Shatterbolt world position -> currently float Vector2
satellite mask -> ushort
progress -> byte if sufficient
```

---

# 55. Malformed Data Policy

For each accepted Core snapshot:

```text
invalid lane header
    ignore that lane/group

unknown codec
    ignore group

missing required part
    codec update not committed

invalid codec payload
    codec update not committed

base Orrery slot valid
    base presentation remains valid
```

Do not clear every Orrery visual because one lane is malformed unless the bank itself is fundamentally invalid.

---

# 56. Partial Failure vs Previous Valid State

The codec must define what happens if a new update is invalid.

Reasonable default:

```text
retain previous valid presentation only until its normal refresh/stale deadline
```

Do not extend a presentation indefinitely merely because new malformed updates keep arriving.

---

# 57. Clearing Old Instances

After decoding one valid bank snapshot, determine which previously known remote instances are absent.

For each absent instance:

```text
notify codec that publication ended
```

Codec then:

- clears immediately,
- starts fade,
- relies on authored remaining time,
- keeps soft state until refresh timeout,

according to its contract.

---

# 58. Persistent Population Codecs

A persistent population codec like Plasma Burn deserves special treatment in design documentation.

It should have a fixed upper bound on remote entries.

Current Plasma uses the spell's maximum active infection count.

The bank does not need one lane per remote entry.

The codec cycles through them.

This pattern should be reusable conceptually for future:

- marks,
- status glows,
- drone target highlights,
- tether endpoints,
- ally buffs.

Again, conceptually reusable does not mean generic effect logic.

---

# 59. Audio

If remote audio is tied to presentation, the same instance identity should prevent repeated replay.

Examples:

```text
new bolt instance -> play bolt sound once
same instance refreshed -> do not replay
new impact history entry -> play impact sound once
```

Do not use raw packet receipt as “play sound.”

Packet repeats are expected.

---

# 60. Randomized Visuals

Visual randomness must not create gameplay divergence because visuals are inert.

For remote visual consistency, a codec may:

- use local random cosmetic jitter,
- or send a compact seed if exact appearance matters.

Do not send dozens of particle coordinates if a seed/state can reconstruct them.

Plasma's jagged bolt is a good candidate for local cosmetic randomness because exact segment jitter does not affect gameplay.

---

# 61. Presentation Object Pooling

Network refactor does not change the existing rule:

- pooled native visuals must be mechanically inert,
- restore modified fields before pool return,
- do not destroy pooled native children incorrectly,
- remote presentation cleanup must return pooled objects safely.

The bank should call codec cleanup, not manipulate native visual components generically.

---

# 62. Owner Authority and Remote Reconstruction

The owner sends only what remotes cannot reliably derive.

Prefer deriving from replicated build/tree state when possible.

Do not network:

- colors derivable from element,
- constant prefab choice derivable from codec/spell id,
- fixed tuning values already identical across builds,
- static visual scale if it is fully determined by replicated tree/focus state available remotely.

Network:

- current positions,
- current progress,
- selected target ids,
- event history,
- randomized outcomes that affect what must be shown,
- active effect populations,
- runtime-resolved geometry when remote cannot reconstruct it safely.

---

# 63. Focus-Derived Presentation Values

Be careful with donor-derived values.

If a visual radius/width depends on the owner's actual focus item and that focus item is not fully reconstructed on the remote, send the **resolved presentation value** rather than asking the remote to guess.

Shatterbolt currently sends resolved explosion radius.

That is sensible.

Do not send the entire focus profile merely to derive one visual number remotely.

---

# 64. Tree-Derived Presentation Values

If Core specialization replication guarantees the remote can deterministically resolve the same tree value, deriving remotely can save bytes.

Document case-by-case.

Do not duplicate derived values without reason.

---

# 65. Presentation Bank Ownership Model

The bank should be class-owned, not spell-owned.

Likely file shape:

```text
OrreryPresentationNetwork.cs
    bank registration
    lane allocation/ownership
    owner-side publish
    remote-side decode/group/dispatch
    common lane header helpers
    common primitive serializers if justified

spell-specific files
    Shatterbolt presentation codec/presenter
    Plasma presentation codec/presenter
```

The exact file name is not mandated.

Avoid putting all spell codec code into one giant `OrreryNetwork.cs`.

---

# 66. What Happens to Current OrreryNetwork

A reasonable end state is:

```text
OrreryNetwork
    slot 6 base Orrery state
    maybe common primitive serialization helpers

OrreryPresentationNetwork / Bank
    slots 7..N reusable lanes
```

or one file may own both if it remains readable.

The architectural boundary matters more than the number of files.

---

# 67. Common Serialization Helpers

It is reasonable to share narrow helpers such as:

```text
WriteFloat
ReadFloat
WriteUShort
ReadUShort
WriteUInt
ReadUInt
finite checks
```

Do not create a generic serializer framework.

These helpers should be allocation-free and explicit about endianness.

---

# 68. Network Primitive Safety

Every primitive reader must be used only after length/bounds validation provided by `SlotReader`/codec checks.

Validate floats:

```text
not NaN
not infinity
reasonable semantic bounds where applicable
```

Examples:

```text
width > 0
radius > 0
count <= configured max
partIndex < partCount
partCount <= bank lanes
remainingTimeByte != 0 when active
```

---

# 69. Remote Decode Should Be Two-Phase

Recommended pattern:

```text
PHASE A: parse and validate
    no visual mutation

PHASE B: commit
    spawn/update/clear presentation
```

This is already a successful pattern in both current Shatterbolt and Plasma code.

Preserve it.

---

# 70. Remote Object State Must Not Equal Wire State

Do not store only raw packet bytes and make visuals directly query them forever.

Decode into bounded semantic presentation state.

Example:

```text
wire:
    target net id + remaining byte

remote semantic state:
    resolved GameShip target
    expiresAt
    refreshUntil
    pooled StatusEffectLayer
```

That separation is healthy.

---

# 71. Bank Debugging / Diagnostics

Add lightweight diagnostics eventually, but do not block first implementation on a UI.

Useful debug information:

```text
bank capacity
occupied lanes
codec id per lane
instance id
part index/count
bytes used
priority
age
last publish time
remote decode failures
preemption count
capacity-drop count
```

This could later fit the planned F10 debug subtab.

Do not log every packet.

Use counters/rate-limited warnings.

---

# 72. Failure Modes to Design Against

## 72.1 Lane omitted because packet budget was tight

Expected:

- gameplay unaffected,
- remote retains only within codec stale/refresh rules,
- later packets recover.

## 72.2 Entire Core extension skipped

Expected:

- presentation survives only through bounded stale timers,
- no permanent frozen visuals.

## 72.3 One multi-lane part malformed

Expected:

- group rejected,
- no partial new state mutation.

## 72.4 Unknown codec id

Expected:

- ignored safely.

## 72.5 Instance id changes

Expected:

- old instance cleanup semantics run,
- new instance creates independently.

## 72.6 Target net id cannot resolve

Expected:

- skip/clear that target visual,
- do not fail entire unrelated bank.

## 72.7 Owner ship destroyed

Expected:

- all bank state for that owner cleared.

## 72.8 Target destroyed before effect expiry

Expected:

- target-attached codec clears that target visual.

## 72.9 World teardown

Expected:

- all local/remote bank and codec visuals reset.

## 72.10 Bank full

Expected:

- presentation degrades according to bounded priority policy,
- gameplay unchanged.

## 72.11 Codec requests impossible lane count

Expected:

- request rejected,
- rate-limited diagnostic,
- no gameplay effect.

## 72.12 Duplicate lane part

Expected:

- group invalid/rejected.

## 72.13 Part count changes unexpectedly for same instance

Expected:

- codec/bank defines explicit transition or treats as new configuration.

Do not silently concatenate old and new layouts.

---

# 73. Packet-Loss Recovery Examples

## 73.1 Shatterbolt

Packet 1:

```text
impactCount = 1
history = [A]
```

Packet 2 lost:

```text
impactCount = 2
history = [A,B]
```

Packet 3 arrives:

```text
impactCount = 3
history = [A,B,C]
```

Remote compares with prior known count and creates missing B and C presentations.

This is good.

## 73.2 Plasma Burn

Packet 1 refreshes targets:

```text
A B C D E F
```

Packet 2 lost.

Packet 3 refreshes:

```text
M N O P Q R
```

A..F remain until their codec refresh timeout/remaining duration.

Later rotation refreshes them again if still active.

This is also good.

The bank must support both recovery models.

---

# 74. Avoiding Reliable-Event Emulation

Do not add ACK/retry queues for every visual event.

The project already has owner-authoritative mechanics and a frequent state stream.

For presentation, bounded state repetition is simpler and safer.

Use:

```text
state
history tail
refresh windows
```

rather than:

```text
reliable custom event bus inside PlayerShipState
```

unless a future effect truly proves it necessary.

---

# 75. Send Frequency / Scheduling

The bank should initially use the existing Core dynamic packet cadence.

Within that cadence, codecs can decide what changes each packet.

Potential future optimization:

```text
codec marks itself dirty / heartbeat due
```

But do not prematurely build a complex scheduler.

Because inactive Core slots cost nothing, simply rebuilding a tiny fixed bank description each packet can be cheap.

---

# 76. Dirty-State Optimization

If later needed, distinguish:

```text
continuous state
    projectile position -> changes every tick

heartbeat state
    persistent field duration -> may update less often

rotating refresh
    Plasma burn population -> intentionally changes batch each send
```

Any scheduler must still guarantee bounded recovery after loss.

---

# 77. Lane Occupancy vs Packet Publication

A logical instance may own lanes even if it does not write meaningful changes every tick.

The implementation can either:

```text
A. republish its current state every packet
B. publish on heartbeat/dirty cadence while keeping remote timeout longer
```

Start simple with A where payload cost is acceptable.

Optimize only measured hotspots.

---

# 78. Remote Refresh Timeouts

Remote soft-state timeout should exceed ordinary expected packet loss gaps but remain short enough to clear abandoned visuals.

Plasma currently uses a ~1.25 s refresh timeout for burn visuals.

That is separate from Core's 0.5 s dynamic stream stale timeout.

The distinction is valid:

```text
Core stale timeout
    whole dynamic stream stopped

codec refresh timeout
    this target entry has not appeared in rotating batches
```

Do not collapse these blindly.

---

# 79. Presentation Tail Semantics

A codec may continue requesting lanes after gameplay completion.

Examples:

- impact history recovery,
- fade-out,
- final explosion visibility,
- status VFX.

The lifetime must be bounded.

Document:

```text
why tail exists
maximum duration
what data remains published
effect of preemption
```

---

# 80. Bank and Cast Completion

Do not auto-release a presentation instance merely because:

```text
OrreryCasting phase != Invoking
```

Shatterbolt and Plasma both prove that presentation can outlive invocation completion.

The codec/runtime must explicitly release/end its presentation instance.

---

# 81. Bank and Shuffle

Satellite shuffle may begin while spell presentation remains.

That is legal.

The bank should not tie occupancy to shuffle state.

---

# 82. Bank and Respec / Class Exit

On class exit, all Orrery presentation should clear.

On respec while remaining Orrery, active spell behavior should follow current class/runtime rules.

Do not let stale presentation survive an owner replacement.

---

# 83. Network Memory Bounds

Owner-side bank:

```text
fixed lane array
fixed metadata
fixed scratch
```

Remote-side per owner:

```text
fixed lane descriptors
bounded active instance records
codec-owned bounded visual state
```

If a dictionary is used for remote owners, its size is bounded by actual remote player ships.

Within each owner, prefer fixed small arrays because bank lane count is tiny.

---

# 84. Instance Record Bound

The maximum simultaneously network-visible presentation instance count cannot exceed bank lanes if every instance requires at least one lane.

Therefore remote bank bookkeeping can be fixed to bank capacity.

A codec may internally represent many targets within one instance.

Plasma is the example.

---

# 85. Part Count Bound

Part count must never exceed bank lane count.

Reject impossible values before grouping.

If part count is packed into a nibble, ensure bank size remains <= representable range.

---

# 86. Lane Header Flags

Only add common flags that are genuinely transport-level.

Possible transport-level flags:

- continuation/part metadata,
- explicit clear if needed,
- priority class if remote needs it (likely not),
- compressed encoding flag only if bank itself supports alternate framing.

Spell semantics like:

```text
orb active
burning
beam charged
```

belong inside codec payload.

---

# 87. Priority Does Not Need to Cross Network

Owner-side priority is mainly an allocation decision.

Remote usually does not need to know why an effect won a lane.

Do not spend payload bytes on priority unless a real remote behavior depends on it.

---

# 88. Preemption and Remote Cleanup

If an owner preempts presentation instance A to make room for B:

```text
A disappears from next bank snapshot
```

Remote A then follows ordinary disappearance cleanup.

No special “preempted” packet is required unless a codec proves it needs one.

---

# 89. Graceful Degradation Philosophy

Presentation networking is best-effort.

When capacity/budget is constrained, prefer:

```text
less visual fidelity
```

over:

```text
more protocol complexity
```

Examples:

- fewer historical decorative impacts,
- slower refresh rotation,
- old fade ends early,
- optional secondary particles absent.

Never degrade mechanical correctness.

---

# 90. Audio Degradation

If a presentation is dropped due to capacity, remote audio may also be absent.

That is acceptable if the sound is cosmetic.

Do not make gameplay timing depend on hearing a replicated sound.

---

# 91. Bandwidth Accounting Under Worst Current Overlap

The implementing agent should explicitly test at least:

```text
Orrery base slot active
Shatterbolt maximum impact history active
Plasma Burn maximum population active
Plasma bolt stroke active
```

Even if ordinary casting rules make some overlap unlikely, tails can overlap.

Measure:

- number of active bank lanes,
- total lane bytes,
- outer TLV bytes,
- Core payload size with/without spec block,
- behavior if native PlayerShipState is unusually large.

---

# 92. Same-Build Policy Enables Simpler Migration

Because co-op peers use the same exact mod build, the project can change:

```text
slots 7..11 from spell-specific meanings
```

to:

```text
slots 7..11 as reusable bank lanes
```

in one release without supporting mixed old/new interpretations.

Still bump relevant wire versions so stale development builds fail cleanly.

---

# 93. Migration Must Avoid Double Publication

During migration, do not leave both:

```text
old Shatterbolt slot publisher
and
new bank Shatterbolt publisher
```

active simultaneously.

Likewise for Plasma.

Migrate one codec at a time with explicit ownership.

---

# 94. Recommended Migration Sequence

This is intentionally chunked so an agent can stop after each verified step.

## Phase 0 — inventory only

Before editing:

- refetch branch,
- enumerate all `CoreNetwork.RegisterSlot` calls,
- enumerate all `BeginSlot` calls,
- enumerate all `TryReadSlot` calls,
- enumerate Harmony patches touching `AppendLocalExtension`, `RegisterDefaultSlots`, `RemoteShipDriver.Render`, world teardown,
- record current Core slot ids,
- record current payload versions.

No code change.

## Phase 1 — introduce bank skeleton, no spell migration

Add class-owned bank constants/helpers and register candidate reusable lanes.

However, do **not** create duplicate registrations with current spell-specific slots.

Therefore this phase may need to remain compile-time scaffolding until one old registration is removed.

A safer alternative is to implement bank code but not register/publish yet.

Goal:

- fixed arrays,
- lane header encode/decode,
- bounded grouping,
- unit-like debug self-checks if available.

No spell mechanics touched.

## Phase 2 — migrate one simple/controlled codec

Choose either:

```text
Plasma stroke only
```

or a small Shatterbolt subset if easier.

Prove:

- reusable lane ownership,
- instance identity,
- remote create/update/clear,
- no duplicate Core hooks.

## Phase 3 — migrate full Plasma presentation

Move:

- bolt state,
- rotating burn refresh,
- remote burn lifecycle,

onto the bank.

Remove permanent Plasma slot registration and send/register hooks.

Gameplay `OrreryPlasmaBolt` mechanics remain unchanged.

## Phase 4 — migrate Shatterbolt

Move its multi-part cumulative-history presentation onto the bank.

Preserve:

- full impact bound,
- atomic multi-part validation,
- presentation tail,
- resolved radius,
- moving orb,
- unseen-impact replay.

Remove Shatterbolt-specific permanent slot meanings.

## Phase 5 — centralize remote render bridge

If not already done, ensure one Orrery remote presentation tick path dispatches all codecs.

Remove redundant per-spell `RemoteShipDriver.Render` patches.

## Phase 6 — centralize reset/cleanup hooks

Coordinate with `spell_lifetime_agent.md` if that migration is active.

Remove redundant world reset hooks for presentation.

## Phase 7 — measure

Test worst current overlap and packet size.

Only then decide whether bank size is sufficient.

---

# 95. Which Codec Should Migrate First?

There are arguments both ways.

## Plasma first

Pros:

- current presentation file is already isolated,
- two-slot design,
- demonstrates persistent rotating soft state,
- removes separate send/register/render hooks quickly.

Cons:

- target-resolution/refresh semantics are more complex.

## Shatterbolt first

Pros:

- already integrated into OrreryNetwork,
- very explicit multi-slot atomic payload,
- excellent test for multi-part grouping.

Cons:

- three-part worst-case payload stresses header overhead immediately.

Recommendation:

**Prototype the bank header against Shatterbolt's byte requirements first, but migrate Plasma presentation first if its isolated file makes the code change safer.**

This is not a hard rule.

---

# 96. Shatterbolt Migration Acceptance Criteria

After migration:

- baseline Orrery slot 6 still works if spell presentation is absent,
- owner gameplay unchanged,
- orb appears remotely,
- orb movement appears remotely,
- inherited extra chains produce corresponding remote impacts,
- every completed impact can be recovered after a missed packet within presentation tail,
- burst radius matches owner's resolved value,
- malformed one-part payload does not partially spawn history,
- end/clear removes orb/tail correctly,
- no permanent Shatterbolt-specific Core slots remain outside the bank,
- no new managed allocation in packet hot path.

---

# 97. Plasma Migration Acceptance Criteria

After migration:

- direct bolt mechanics unchanged,
- burn mechanics unchanged,
- remote bolt shows once per cast instance,
- remote burn visuals track target net ids,
- rotating refresh still covers bounded infection population,
- missed packets do not instantly clear every burn,
- stale/unrefreshed burns eventually clear,
- target death clears visual,
- owner/class/world teardown clears visuals,
- no permanent Plasma-specific Core slots remain outside the bank,
- separate Plasma send/register/render patches are removed or reduced to codec-specific visual hooks only where genuinely necessary.

---

# 98. Bank Acceptance Criteria

The reusable bank is successful when:

1. Orrery has a fixed documented maximum presentation lane footprint.
2. Adding a new spell normally does not require a new Core slot id.
3. New spell presentation normally does not require a new Core send Harmony patch.
4. New spell presentation normally does not require a new remote render Harmony patch.
5. Multiple lingering effects can coexist up to the explicit bank capacity.
6. Lane reuse cannot cross-contaminate effect instances.
7. Multi-lane effects decode atomically by default.
8. Missing/malformed presentation never affects gameplay.
9. Presentation disappears under bounded stale/clear rules.
10. Hot paths remain allocation-stable.
11. Packet byte cost can be calculated from one bank definition plus active codecs.
12. Shatterbolt and Plasma retain their distinct codec semantics rather than being forced into one generic schema.

---

# 99. Anti-Patterns

Reject these during review.

## 99.1 `RegisterSlot` inside every new spell

This is exactly what the bank is intended to stop.

## 99.2 Spell code hard-codes lane Core slot ids

Use bank handles/indices.

## 99.3 Bank contains fields for every spell mechanic

Wrong abstraction.

## 99.4 Bank owns gameplay state

Wrong authority.

## 99.5 Remote presentation writes back into mechanical runtime

Wrong direction.

## 99.6 Dynamic allocation per packet

Avoid.

## 99.7 One-frame event with no repeat/history

Loss-prone.

## 99.8 Partial multi-lane mutation before validation

Can corrupt visuals and instance identity.

## 99.9 Clearing base Orrery presentation because one spell codec is malformed

Too broad.

## 99.10 One lane per infected/burning target

Does not scale.

## 99.11 Global “current spell presentation” singleton

Cannot handle overlap.

## 99.12 Expanding bank every time a codec feels cramped

Measure/repack first.

## 99.13 Networking focus/tree/raw item objects directly

Send resolved runtime presentation values only when they cannot be derived.

---

# 100. Detailed Implementation Sketch — Intentionally Non-Binding

The following is pseudocode to make the architecture concrete.

It is not a demand for these exact type names.

```csharp
public static class OrreryPresentationBank
{
    public const int LaneCount = 5;

    private struct LocalLane
    {
        public bool Active;
        public byte CodecId;
        public ushort InstanceId;
        public byte PartIndex;
        public byte PartCount;
        public byte Priority;
        public int PayloadLength;
        // fixed payload storage or mapped Core writer usage
    }

    private static readonly LocalLane[] local =
        new LocalLane[LaneCount];

    public static void PublishLocal(GameShip owner)
    {
        // Rebuild bounded desired bank state.
        // Ask explicit known codecs for current presentation demand.
        // Resolve capacity/priority.
        // Write lanes to fixed Core slot ids.
    }
}
```

Remote side concept:

```csharp
public static void TickRemote(GameShip remoteOwner)
{
    // Read all bank Core slots for this one Core snapshot.
    // Decode headers to fixed descriptor array.
    // Group by codec + instance.
    // Validate groups.
    // Dispatch to codec decoders.
    // Inform codecs about disappeared instances.
}
```

Again:

- no LINQ,
- no per-frame `new List`,
- no giant generic effect state.

---

# 101. Alternative Simpler First Implementation

If lane allocation handles feel premature, the first implementation can be even more explicit.

Example:

```text
OrreryPresentationBank
    owns slots 7..11

BuildDesiredLanes(owner)
    clear lane descriptors
    ask Plasma codec to reserve/write what it needs
    ask Shatterbolt codec to reserve/write what it needs
    resolve fixed order/priority
```

This can be enough for the current two codecs.

A generalized registration API can wait until more spells exist.

That is acceptable architecture.

Do not confuse “reusable bank” with “must have a plugin framework today.”

---

# 102. Explicit Codec Ordering

If explicit codec calls are used, define ordering deliberately.

Example:

```text
active cast visuals first
persistent status populations second
presentation tails last
```

But be careful: codec type is not always equivalent to priority.

A persistent active beam can be more important than a new decorative projectile flourish.

The first two real codecs can use hard-coded priorities until evidence requires a richer policy.

---

# 103. Allocation Fairness

Do not let one persistent low-value codec permanently starve every new effect.

If an effect uses all lanes for a long duration, either:

- that is a conscious design choice because its presentation is important,
- it supports degradation,
- or preemption is allowed.

Document this per codec.

---

# 104. Long-Lived Effects

Future long-lived effects need heartbeats and lane occupancy discipline.

Examples:

- 20-second Temporal-like field,
- satellite tethers,
- persistent aura.

Do not continuously reserve three lanes for static data if one compact lane can describe it.

---

# 105. Very Short Effects

For visuals shorter than a network tick, do not rely on a single instantaneous occupancy frame.

Publish for a bounded minimum visibility/recovery window.

Example:

```text
bolt visible locally 0.1 s
network presentation retained 0.5–0.75 s with same instance
remote plays once
```

Plasma already follows this spirit.

---

# 106. Repeated Casts of Same Spell

Instance identity must distinguish:

```text
Shatterbolt cast 20
Shatterbolt cast 21
```

Remote must not append cast 21 impacts to cast 20 history.

Likewise Plasma bolt sound/visual should replay on a new cast even if the burn population from an older cast still exists.

---

# 107. Persistent Population Across Multiple Casts

This is a subtle design choice.

Plasma Burn populations from multiple casts can overlap mechanically.

The presentation codec may choose:

```text
A. one owner-wide Plasma Burn visual population instance
```

or:

```text
B. separate population per cast
```

Current presentation behavior is effectively owner-wide target refresh state.

That is bandwidth-efficient.

Unless distinct cast ownership needs to be visible, preserve owner-wide population presentation.

Gameplay provenance remains separate in CoreCombat.

---

# 108. Presentation Identity Is Not Combat Provenance

Do not use presentation instance ids as the only combat correlation key.

CoreCombat attack/cast ids and semantics remain the authoritative gameplay history/provenance system.

Presentation ids are for visual lifecycle and transport reuse.

They can correlate, but neither replaces the other.

---

# 109. Presentation Bank Is Not CoreCombatHistory

Do not serialize combat history wholesale through the bank.

Only send the presentation facts needed by remote peers.

Example:

Shatterbolt remote needs impact positions.

It does not need:

- exact damage,
- crit flags unless visual depends on them,
- Core event ids,
- target health deltas.

---

# 110. Security / Trust Model

This is a friendly co-op mod with identical builds.

Still treat remote bytes defensively because malformed data can arise from bugs, stale dev builds, or unexpected native state.

Do not assume a remote payload is structurally valid merely because the peer is a friend.

Bounds checks are crash prevention, not adversarial security theater.

---

# 111. Logging Policy

Do not log every malformed packet at 20 Hz.

Use:

- first-warning flags,
- rate-limited warnings,
- debug counters.

Examples worth warning once:

- codec requests too many lanes,
- lane payload exceeds maximum,
- impossible part count,
- duplicate codec registration,
- unsupported codec id encountered under same-build session.

---

# 112. Debug Counters Worth Adding Eventually

Potential counters:

```text
BankPublishPackets
BankActiveLaneHighWater
BankBytesHighWater
BankCapacityDrops
BankPreemptions
MalformedLaneCount
MalformedCodecPayloadCount
UnknownCodecCount
RemoteInstanceCreates
RemoteInstanceClears
```

No need to add all of these in first implementation.

---

# 113. Testing Without Packet-Loss Tooling

Even without a dedicated network simulator, test logic can be reasoned/manually exercised by:

- temporarily skipping every Nth publish,
- temporarily suppressing one lane part,
- forcing capacity pressure,
- forcing owner destruction,
- casting while previous tails remain,
- repeatedly casting same spell,
- filling Plasma infection capacity,
- letting targets die mid-effect.

Any debug fault injection should be removable/disabled before normal builds.

---

# 114. Required Test Matrix

At minimum test:

## Solo/local

- no remote presentation errors,
- bank publication does not change mechanics.

## Host Orrery, client observer

- Shatterbolt full chain,
- Plasma bolt,
- Plasma spread,
- overlap.

## Client Orrery, host observer

Same cases.

## Two Orrery players simultaneously

Each remote owner must have independent bank/instance state.

## Orrery + vanilla/non-Orrery co-op player

No bank bleed between owners/classes.

## Owner changes class

All Orrery presentation clears.

## World transition

All presentation state clears.

## Target destroyed mid-status

Attached visuals clear.

## Heavy native packet

If Core extension is skipped, visuals recover later and do not freeze indefinitely.

---

# 115. Two Orrery Players Are Important

Do not accidentally design the bank as one global remote instance set.

Every remote Orrery owner has independent lane contents.

Remote state must be scoped by owner ship/player identity.

Owner A lane 0 and owner B lane 0 are unrelated.

---

# 116. Local Owner Assumption

Each game client controls one local player ship in current project usage.

Owner-side bank can therefore remain simple.

Do not overbuild multi-local-player support unless native game architecture actually requires it.

Remote side still supports multiple remote Orrery owners.

---

# 117. Interaction with Core Specialization Block

Core may sometimes send specialization data alongside dynamic slots.

The bank must not assume the full 384-byte payload is always available for Orrery dynamic presentation.

Core already calculates actual packet budget.

Keep Orrery presentation bounded enough that it coexists comfortably with spec bursts/heartbeats.

---

# 118. Do Not Depend on Spec Packet Arrival for Effect Decode

A codec should not require “the same packet must also contain the spec block” unless absolutely unavoidable.

Remote replicated specialization is persistent state.

Presentation can use the currently resolved remote tree state if needed.

---

# 119. Bank Layout and Core Slot IDs

If using slots 7..11 as bank lanes, stabilize their new meanings once shipped:

```text
7 = Orrery presentation lane 0
8 = Orrery presentation lane 1
9 = Orrery presentation lane 2
10 = Orrery presentation lane 3
11 = Orrery presentation lane 4
```

Do not continue referring to them in comments as Shatterbolt/Plasma-specific after migration.

Because same-build sessions are enforced, this change can happen atomically in one mod version.

---

# 120. What About Slots 12+?

Do not reserve them preemptively “for future Orrery.”

Leave Core capacity available until measurement proves expansion is needed.

---

# 121. Bank Codec Registry

Eventually a small registry may be useful:

```text
codec id -> codec implementation
```

Requirements:

- fixed/known set per build,
- duplicate id rejected,
- no runtime discovery/reflection needed,
- no allocations per packet.

For the first two codecs, an explicit switch can be simpler and perfectly acceptable.

---

# 122. Switch vs Dictionary

For a small static codec set:

```csharp
switch (codecId)
{
    case Shatterbolt: ...
    case PlasmaBurn: ...
}
```

is fine.

Do not use a dictionary merely because “registry” sounds architectural.

When many codecs exist, a prebuilt array/dictionary may become cleaner.

---

# 123. Bank Handle Lifetime

If an allocation handle object/struct is introduced, it should be:

- value-type or pooled/fixed,
- owner-local,
- invalid after release,
- generation-checked,
- impossible to mutate another instance after lane reuse.

Do not let stale spell code keep a raw lane index and overwrite the next owner.

---

# 124. Generation-Checked Handles

Conceptual:

```text
handle:
    allocation index
    generation
```

Bank record:

```text
current generation
```

A write/release with wrong generation is ignored/rejected.

This mirrors the project-wide rule that late confirmations must not mutate replacement runtime.

Presentation deserves the same protection.

---

# 125. Do We Need Handles Immediately?

Maybe not.

If the first implementation rebuilds desired lane occupancy from codec snapshots every packet rather than persistent allocation handles, generation-checked handles may be unnecessary.

Compare two models:

## Persistent allocation model

Effect acquires lanes for lifetime.

Pros:

- stable mapping,
- explicit capacity ownership.

Cons:

- handle lifecycle complexity.

## Declarative desired-state model

Every publish tick, codecs submit bounded presentation requests.

Bank deterministically selects/assigns lanes.

Pros:

- no stale local handles,
- easy recovery,
- simpler release: stop submitting.

Cons:

- lane assignment can churn unless stabilized,
- bank needs matching logic by instance id.

**The declarative desired-state model is very attractive for this project.**

It naturally matches a 20 Hz state stream.

The agent should seriously consider it before implementing persistent allocation handles.

---

# 126. Recommended Allocation Model: Declarative Desired State

A strong current recommendation is:

```text
each publish tick:
    codecs describe active presentation instances
        codec id
        instance id
        priority
        desired part count
        optional minimum part count
        payload builder

bank:
    matches existing lane ownership where possible
    preserves stable placement where possible
    resolves capacity
    writes selected instances
```

Benefits:

- no explicit release API required for normal end; instance simply disappears,
- no stale owner-side handle bugs,
- recovery after internal reset is natural,
- bank can centrally prioritize overlap,
- still bounded because request count is fixed.

Do not implement request lists dynamically.

Use fixed request slots/buffers.

---

# 127. Desired-State Request Bound

The number of simultaneous presentation requests must itself be bounded.

Because bank has only N lanes, a reasonable maximum request count can be:

```text
N
or a small multiple of N
```

Low-priority requests beyond capacity can be discarded before encoding.

---

# 128. Stable Placement in Declarative Model

To avoid lane churn:

```text
first preserve previous mapping for still-present (codec,instance)
then allocate free lanes to new requests
then consider preemption if necessary
```

This can be done with tiny fixed arrays.

---

# 129. Codec Payload Build Timing

Do not fully serialize every low-priority request before capacity selection if that wastes work.

Better:

1. collect lightweight metadata/request,
2. select capacity,
3. serialize selected requests into Core lanes.

For very small bank sizes, either order may be fine.

Measure clarity first.

---

# 130. Multi-Part Serialization

A codec selected for K parts can be given K bounded writers or one conceptual writer segmented into parts.

Avoid temporary concatenated heap buffers.

Possible pattern:

```text
codec writes part 0 directly
codec writes part 1 directly
...
```

or:

```text
fixed stack/static scratch of K * payloadBytes
then split
```

Use whatever is simplest and allocation-stable.

---

# 131. Common Header Overhead Measurement

Before choosing header fields, take current payloads and repack them on paper.

Shatterbolt worst-case:

- orb position,
- radius,
- up to 10 positions,
- impact count/flags/sequence.

Plasma:

- bolt start/end/width,
- bolt/cast identity,
- six `(netId, remaining)` pairs.

Calculate exact part counts under each candidate header.

This should be written into the implementation commit message or adjacent comments.

---

# 132. Potential Optimization: Codec Header in Part 0 Only

One possible compromise:

```text
part 0 carries full codec + instance metadata
continuation parts carry shorter continuation header
```

Pros:

- saves bytes on multi-part payload.

Cons:

- continuation parts depend more strongly on part 0,
- harder to validate independently,
- lane reordering/grouping complexity.

Because Core slots are atomic in one snapshot, this may be viable.

Do not adopt unless needed by byte budget.

---

# 133. Potential Optimization: Fixed Part Count by Codec

If Shatterbolt always uses 3 bank lanes, part count could be implied by codec.

But actual current Shatterbolt omits unused history slots when impact count is small.

Forcing 3 lanes from cast start would waste packet bytes.

Prefer variable active part count unless header overhead makes it worse.

---

# 134. Lane Omission and Variable Part Count

If part count grows during an instance as history grows:

```text
impact 0 -> 1 or 2 parts
impact 7 -> 3 parts
```

this is legal if the remote treats the current snapshot's part count as the complete current representation.

Do not concatenate old omitted parts.

---

# 135. Part Shrink

If an instance later requires fewer parts, omitted parts should be considered released from that group.

Remote should use the new complete representation or codec tail semantics.

---

# 136. Shatterbolt History Tail and Part Growth

Shatterbolt is a perfect test for variable part growth.

As impact count increases:

- more history bytes are required,
- bank may add parts,
- instance id remains same,
- remote decodes latest full group.

The bank must not mistake added parts as a new effect.

---

# 137. Presentation Tail Under Capacity Pressure

When Shatterbolt gameplay ends, its history may be retained briefly.

If a new high-priority spell begins and bank is full, it is reasonable to preempt some/all Shatterbolt tail.

That means a remote might miss the last cosmetic burst in extreme overlap.

That is acceptable if gameplay remains correct.

Document that tradeoff.

---

# 138. Plasma Refresh Under Capacity Pressure

If Plasma burn refresh is delayed by one or two packets because a new high-priority cast uses lanes, remote burn visuals should not instantly disappear.

Its existing refresh timeout gives resilience.

This is a good example of why codecs own their own remote soft-state retention.

---

# 139. Suggested Initial Priority Mapping

Candidate:

```text
100 active projectile/beam/stroke state
80 active expanding gameplay-correlated field/nova
60 persistent target status refresh population
30 post-completion history tail
10 decorative-only flourish
```

Exact numbers do not matter.

A small enum is probably clearer:

```text
CriticalActive
Active
Persistent
Tail
Decorative
```

Do not expose these as balance knobs.

---

# 140. Deterministic Tie-Breaking

If two requests have equal priority and capacity is insufficient, use deterministic owner-local tie-breaking.

Examples:

- preserve already-mapped instance first,
- then older instance,
- then codec id/instance id stable order.

Avoid random lane allocation.

Remote does not need to reproduce allocation; it receives lane headers.

But deterministic local behavior makes debugging easier.

---

# 141. Should Presentation Be Guaranteed for the Current Cast?

It is reasonable to reserve enough capacity for at least one high-priority current cast presentation.

Do not hard-reserve a lane that sits empty forever unless measurements justify it.

Priority/preemption can achieve the same effect more flexibly.

---

# 142. Bank Pressure from Old Persistent Effects

Long-lived effects should not permanently block current cast feedback.

That is a core reason for priority/preemption.

---

# 143. Codec Degradation Contract

A codec may optionally expose:

```text
PreferredParts
MinimumParts
```

Example:

```text
preferred 3
minimum 2
```

Only implement this if a real codec can meaningfully degrade.

Do not force every codec to support it.

If `MinimumParts` unavailable, skip that presentation instance.

---

# 144. No Hidden Mechanical Side Effects from Degradation

If Shatterbolt presentation drops old impact history due to degradation, it must not change mechanical Frost Burst lifetime or damage.

Obvious, but worth stating.

---

# 145. Remote Instance Cleanup on Preemption

Remote cannot know “preempted” vs “naturally ended” unless sent.

It usually does not need to.

Codec cleanup behavior should be visually acceptable for either disappearance.

If a fade is important, it can fade on any disappearance.

---

# 146. Coalescing Similar Presentation

Future codecs may be able to coalesce multiple mechanical effects into one visual population.

Example:

```text
many burning targets -> one PlasmaBurnPopulation codec
```

This is encouraged when it reduces lane count without losing required visual information.

Do not coalesce effects whose separate identity matters visually.

---

# 147. Remote Sound/Event Replay Tracking

For history codecs, track last seen bounded event identity/count per instance.

Shatterbolt currently uses impact count/history.

That is enough to know which impacts are new.

Do not replay all history sounds every packet.

---

# 148. Remote Presentation State Replacement

When a new instance with same codec arrives:

```text
cleanup/fade old instance according to codec
initialize new instance cleanly
```

Do not carry old counters/history across instance id changes.

---

# 149. Remote Owner Snapshot Loss

If Core no longer has a valid dynamic snapshot for the remote owner, the bank's remote tick should treat all bank instances as stale and clear/fade them.

Do not independently keep them forever.

---

# 150. Base Slot and Bank Version Independence

It may be useful for slot 6 base Orrery payload version to evolve independently from bank wire version.

Do not require changing base casting payload version every time one spell codec changes.

---

# 151. Codec Version Independence

Likewise, changing Plasma payload should not require changing Shatterbolt codec version.

Keep version scope narrow.

---

# 152. Codec Id Stability

Once a codec id is used in shipped same-build sessions, keep it stable within project history unless there is a strong reason to reassign.

This is mostly for debugging/source clarity rather than mixed-version compatibility.

---

# 153. Bank Slot Id Stability

Once slots 7..N become generic bank lanes, do not later reinterpret slot 8 as a special one-off spell slot unless the bank architecture is intentionally retired.

---

# 154. Packet Size Comments Must Stay Accurate

Current Shatterbolt and Plasma files contain useful exact byte comments.

Preserve that practice.

Each codec should document:

- common header bytes,
- codec payload bytes,
- parts required,
- worst case total,
- quantization.

Do not leave stale comments after layout changes.

---

# 155. Suggested File-Level Responsibilities

One possible end state:

```text
OrreryNetwork.cs
    base slot 6 state
    narrow primitive helpers

OrreryPresentationBank.cs
    bank slot registration
    common lane header
    desired-state collection
    capacity resolution
    Core slot writes
    remote lane read/group
    codec dispatch

OrreryShatterboltRemotePresentation.cs
    Shatterbolt codec/presenter

OrreryPlasmaBoltPresentation.cs
    Plasma codec/presenter
```

Do not create extra files merely to match this drawing.

---

# 156. Naming

Prefer names that clearly say presentation/networking rather than gameplay.

Good:

```text
OrreryPresentationBank
OrreryPresentationCodec
OrreryRemotePresentation
```

Avoid ambiguous:

```text
OrreryEffectSystem
OrrerySpellEngine
OrreryRuntime2
```

---

# 157. Interaction with `OrrerySpellRegistry`

Spell registry maps formula to gameplay executor/identity.

Presentation codec registry does not need to be the same registry.

A spell may have:

- no custom network presentation,
- one codec,
- multiple presentation effects/codecs,
- shared codec with another spell.

Keep concerns separate.

---

# 158. Interaction with `OrrerySpellCompendium`

Compendium remains the home of authored spell tuning/identity.

Network byte layout constants may live with the codec/bank rather than Compendium unless they are meaningful gameplay/presentation tuning.

Example:

```text
MaxActiveInfections
```

can be spell tuning/runtime bound.

```text
LaneHeaderBytes
```

belongs to network architecture.

---

# 159. Interaction with `OrreryCombat`

None mechanically.

Presentation may use cast/semantic identity for naming/debugging, but should not depend on combat-history delivery to render ordinary remote state unless explicitly designed.

---

# 160. Interaction with `OrreryFocusProfile`

Presentation codecs may serialize resolved visual geometry that depends on focus.

Do not make remote codecs perform donor reflection or item-roll extraction.

---

# 161. Interaction with `OrreryUnits`

Wire position values are currently in Unity world coordinates for direct Vector2 serialization.

Player-facing tuning remains meters.

If codec serializes meters (radius/width), document it explicitly.

Do not mix units silently.

---

# 162. Interaction with Native Pooling

Remote presenters remain responsible for safe pool lifecycle.

Bank cleanup calls presenters; it does not know how to return `Wave`, `StatusEffectLayer`, `Projectile`, etc.

---

# 163. Harmony Patch Reduction Goal

A successful migration should reduce Orrery networking patches toward something like:

```text
one send integration
one remote render integration
one world cleanup integration
```

rather than per spell.

Do not chase zero Harmony patches as a goal if one narrow native lifecycle patch is genuinely codec-specific.

The goal is ownership clarity, not patch-count vanity.

---

# 164. Why This Is Not Overengineering

The reusable bank is justified because the branch already has:

```text
5 Orrery spell-presentation slots
2 independent presentation architectures
multiple independent send/register/render hooks
known future spell count much larger than 2
known overlapping presentation lifetimes
hard Core slot count and byte budgets
```

The abstraction is responding to repeated proven pressure.

It is not being invented for a hypothetical first spell.

---

# 165. What Would Be Overengineering

Examples:

- arbitrary variable-length reliable message bus,
- generic graph serializer,
- reflection-discovered codecs,
- runtime plugin loading,
- per-effect ACK/retry protocol,
- compression framework before measuring bytes,
- custom BitReader/BitWriter for every field before ordinary byte packing proves insufficient,
- dozens of priority weights,
- lock-free queues for main-thread ship-state processing.

Avoid.

---

# 166. Candidate Minimal Common Metadata

The common metadata must answer only transport questions.

Likely fields:

```text
codec id
instance id
part index
part count
codec version/format
```

Potentially omit one if safely derivable.

Do not put:

```text
damage type
spell element
range
radius
crit
```

in common lane metadata.

---

# 167. Codec Payload Reader Boundaries

When dispatching to a codec, give it only its payload length/view.

The codec must not be able to read into the next lane or Core slot.

Use existing `SlotReader` semantics or an equivalent bounded sub-reader.

---

# 168. Codec Payload Writer Boundaries

Likewise, a codec cannot exceed assigned payload bytes.

Overflow should:

- invalidate/drop that presentation update,
- warn rate-limited,
- never corrupt adjacent lanes.

---

# 169. Build-Time Constants vs Runtime Values

Bank lane count and slot ids should be build-time constants.

Active occupancy and codec payload lengths are runtime values.

Do not make lane count player-configurable.

That would create co-op protocol mismatch risk.

---

# 170. Mod Versioning

Because transport semantics change, bump whatever mod/package version policy the project uses before distributing a build with bank migration.

Do not rely solely on source commit identity.

---

# 171. Rollback Strategy

Migrate in commits that can be reverted independently.

Example:

1. add bank scaffolding,
2. migrate Plasma,
3. migrate Shatterbolt,
4. remove old helpers,
5. cleanup docs.

If Shatterbolt migration fails, Plasma bank work should remain testable.

---

# 172. No Rebalance During Migration

Before and after migration, record exact relevant tuning values and compare.

Network refactor should not alter:

- Shatterbolt chain count,
- Shatterbolt radii,
- Shatterbolt speed,
- Plasma burn duration,
- Plasma spread radius,
- Plasma damage,
- presentation visual widths unless byte representation was intentionally changed with approval.

---

# 173. No Gameplay Timing Changes

Do not change when casts complete/shuffle merely to make presentation transport easier.

If a codec needs a presentation tail, transport it independently.

---

# 174. No Remote Mechanical Colliders

Remote presentation objects must remain mechanically inert.

A refactor that accidentally re-enables native projectile/wave colliders on peers is a gameplay bug.

---

# 175. Soundness Checklist for Every New Codec

Before approving a future codec, answer:

```text
What is the logical presentation instance?
What is its max lifetime?
How many lanes preferred?
How many lanes minimum?
What is its priority?
Is it full snapshot, cumulative history, rotating refresh, or hybrid?
How does it recover from a missed packet?
How does remote know it ended?
What identifies a new instance?
What happens if target disappears?
What happens if owner disappears?
What happens if bank is full?
What is worst-case bytes?
What values are derived vs transmitted?
Are coordinates full/quantized and why?
Does remote mutation occur only after full validation?
Are all loops/counts bounded?
Does total presentation loss leave gameplay unchanged?
```

If these cannot be answered, the codec design is incomplete.

---

# 176. Example New Spell Review

Suppose future spell creates one black-hole visual for 8 seconds.

Bad first instinct:

```text
register slot 12 BlackHole
```

Bank-era question:

```text
codec: SingularityField
instance: cast sequence
preferred lanes: 1
priority: Persistent/Active
payload:
    center Vector2
    radius float
    remaining/progress byte
    maybe phase flags
```

No new Core slot registration needed.

---

# 177. Example Satellite Formation Review

Future spell freezes satellites around a target.

Likely remote data:

```text
target net id
satellite mask
formation mode
progress
```

The remote already knows satellite identities and elements from Orrery base state.

Do not send each satellite's full transform if formation can be deterministically reconstructed.

This is exactly the type of bandwidth discipline the bank should encourage.

---

# 178. Example Beam Review

Future channel beam:

```text
origin maybe derivable from owner
end position / target id
width
phase/progress
instance id common header
```

Potentially one lane.

No permanent beam-specific Core slot.

---

# 179. Example Explosion Swarm Review

If a future spell spawns 40 purely visual particles around one explosion, do not send 40 positions.

Send:

```text
center
radius/progress
seed
```

and reconstruct visuals remotely.

---

# 180. Example Multiple Mechanical Projectiles

If projectile positions are mechanically owner-authoritative and visually important, a codec may need bounded multiple positions.

Spell design must define maximum projectile count before networking.

Do not let item multishot create unbounded network array sizes.

---

# 181. Extra Shot / Extra Chain Implications

Resolved focus can increase bounded projectile/chain counts.

Networking must size for the **maximum resolved supported count**, not only base count.

Shatterbolt's 10-impact storage is an example.

Document why the bound is safe and what happens if future item roll maxima change.

---

# 182. Tree Modifiers and Network Bounds

If a tree later increases chain count, projectile count, duration population, etc., revisit codec bounds.

Do not let a tree node silently exceed fixed network storage and truncate without an authored policy.

---

# 183. Presentation Bank and Future Four/Five Satellite Progression

Increasing formula satellite count may create more complex spells but does not itself require more bank lanes.

Lane requirements are based on presentation state, not recipe length.

Do not preallocate lanes based on satellite count.

---

# 184. Presentation Bank and Drones/Clones

If future Orrery duplicates satellites/visuals, consider deterministic reconstruction rather than one lane per clone.

---

# 185. Presentation Bank and Status Effects

Target-attached status visuals are likely best represented as bounded populations, not per-status Core slots.

Plasma Burn is the proof of concept.

---

# 186. Presentation Bank and Large Co-op Load

Project expectations include several co-op players and many simultaneous combat effects.

Network design should assume:

- multiple remote Orrery owners,
- each with independent bank state,
- other class/Leviathan Core traffic,
- late-game visual load.

Keep per-owner processing bounded by bank size, not by world entity count.

Codec internals like Plasma target population may have their own explicit bounded count.

---

# 187. CPU Budget

Bank encode/decode work per remote owner should be approximately:

```text
O(bank lanes)
```

plus codec-specific bounded work.

Do not scan all world ships merely to decode presentation.

Use target net ids where attachment is known.

---

# 188. Bandwidth Budget

The bank provides an architectural maximum:

```text
LaneCount * (2-byte outer TLV + <=32-byte slot payload)
```

plus slot 6 and Core block overhead.

Actual cost is lower because inactive lanes are omitted.

This bounded maximum is a key benefit.

---

# 189. Memory Budget

Per remote owner, bank-level memory should be tiny:

- N lane descriptors,
- N group descriptors,
- N instance metadata entries,
- codec-specific bounded presentation states.

No packet history accumulation at bank level.

---

# 190. Threading

Current Core ship-state processing is main-thread oriented according to CoreNetwork comments.

Do not introduce locks/concurrent collections without evidence.

Keep presentation bank main-thread and simple.

---

# 191. Determinism

Gameplay determinism is owner-authoritative.

Presentation exact particle randomness does not need deterministic network parity unless art direction requires it.

Transport allocation should still be deterministic locally for debugging/repeatability.

---

# 192. Serialization Endianness

Current helpers write little-endian bytes explicitly.

Continue using explicit byte order.

Do not rely on platform-native `BitConverter` assumptions if avoidable.

---

# 193. Float Serialization

Current Orrery uses explicit bit reinterpretation to avoid boxing/allocation.

This is acceptable.

If a shared primitive helper remains, keep it allocation-free.

---

# 194. Half Floats / Compression

Do not introduce half-float compression merely because it exists.

Only use if:

- precision bound is understood,
- code complexity is justified by measured lane pressure.

Ordinary float or custom integer quantization is easier to audit.

---

# 195. Network Protocol Comments

Every bank/codec source file should contain a compact wire-format comment.

Future maintainers should not need to reverse engineer byte offsets from writes.

---

# 196. Tests for Malformed Payload

At minimum reason/test:

- zero part count,
- part index >= part count,
- part count > bank lanes,
- duplicate part,
- unknown codec,
- wrong version,
- payload shorter than expected,
- payload longer than expected,
- NaN position,
- infinity width,
- target id 0,
- count > configured max,
- stale instance replacement.

All should fail presentation safely.

---

# 197. Tests for Lane Reuse

Construct sequence:

```text
packet 1:
    lane 0 = Shatterbolt instance 100

packet 2:
    lane 0 = Plasma instance 101
```

Verify:

- Shatterbolt old visuals cleanup/fade,
- Plasma starts clean,
- no old impact history carried over.

---

# 198. Tests for Same Codec Reuse

```text
packet 1:
    lane 0 = Shatterbolt instance 100

packet 2:
    lane 0 = Shatterbolt instance 101
```

Verify old history does not merge.

---

# 199. Tests for Multi-Lane Reordering

If bank allocation allows parts in different physical lane indices over time:

```text
packet 1:
    lane 0 part0
    lane 1 part1

packet 2:
    lane 3 part0
    lane 4 part1
```

Remote should decode by metadata, not physical slot identity.

If implementation intentionally pins parts to stable lane positions, document that instead.

---

# 200. Tests for Part Growth

Shatterbolt example:

```text
snapshot A requires 1 part
snapshot B requires 2
snapshot C requires 3
```

No stale bytes from old representation may remain active.

---

# 201. Tests for Part Shrink

Persistent effect may reduce payload.

Ensure omitted old part is not still considered current.

---

# 202. Tests for Bank Full

Force all lanes occupied with low-priority tails, then start new active cast.

Verify policy:

- expected tails preempt/drop,
- active cast gets presentation if policy says so,
- gameplay identical.

---

# 203. Tests for Entire Extension Loss

Artificially suppress Core extension for > one packet but < stale timeout.

Verify no catastrophic visual reset if codec intentionally tolerates gap.

Suppress longer than stale bound.

Verify eventual cleanup.

---

# 204. Tests for Spec Burst Coexistence

Trigger/respec so Core specialization block is also being sent.

Verify dynamic bank remains within payload budget and no connection errors occur.

---

# 205. Tests for Native Large Packet

If possible, create many native variable-length ship-state fields (auto targets, grapple links, etc.).

Verify Core safely skips extension and Orrery visuals recover later.

---

# 206. Tests for Two Remote Orrery Owners

Ensure owner A's instance ids do not collide with owner B's remote state.

Instance ids are owner-local.

---

# 207. Tests for Rapid Recast

Cast same spell as soon as allowed repeatedly.

Verify:

- new instance recognized,
- old visual does not suppress new one because sequence matches stale state,
- no lane leaks.

---

# 208. Tests for World Reset

After world unload/reload:

- no old remote dictionary entries,
- no pooled visuals left active,
- lane bank starts empty,
- instance counters can reset safely because old world state is gone.

---

# 209. Tests for Owner Replacement

If local player ship object is replaced while Orrery remains selected:

- old bank desired state disappears,
- spell runtime cleanup occurs via lifetime layer/current paths,
- new owner presentation starts with clean instance context.

---

# 210. Tests for Target Replacement / Net ID Reuse

If native game can reuse net ids, remote refresh timeout and target object validation should prevent old visuals attaching indefinitely.

Do not assume a resolved net id forever points to the same live object across world teardown.

---

# 211. Documentation Update After Implementation

Once the bank is proven, update:

- `ORRERY_SPELL_DESIGN_IMPLEMENTATION_STANDARDS_V3.md`,
- Core/Orrery architecture docs,
- any networking standards that still imply one permanent slot per skill/spell,
- spell implementation checklist.

New standard should say roughly:

> Orrery spell presentation normally uses the class-owned reusable presentation bank. New permanent Core slot ids require an explicit architecture justification.

---

# 212. New Spell Checklist Addition

Add to future spell design checklist:

```text
PRESENTATION NETWORKING:
    Does remote need custom presentation data?
    Can remote derive it instead?
    What logical presentation instance(s) exist?
    Max lifetime?
    Preferred/min lane count?
    Priority?
    Snapshot/history/refresh model?
    Exact worst-case bytes?
    Loss recovery?
    Clear/stale behavior?
    Target ids or positions?
    Quantization bounds?
```

---

# 213. Recommended Decision Summary

Current strongest recommendations:

1. Keep CoreNetwork unchanged initially.
2. Keep Orrery base class state in slot 6.
3. Convert the current Orrery spell presentation region into a small reusable bank.
4. Prototype five lanes using current slots 7..11 before increasing footprint.
5. Make lanes self-describing unless byte measurement proves header cost unacceptable.
6. Use explicit codec id + presentation instance identity.
7. Support multi-lane effects.
8. Support multiple concurrent instances.
9. Preserve codec-specific semantics: full snapshot, cumulative history, rotating refresh.
10. Centralize Orrery send/register/remote-render integration.
11. Keep spell/effect codecs explicit and narrow.
12. Prefer declarative desired presentation state over complicated persistent lane handles unless evidence favors handles.
13. Capacity exhaustion is cosmetic only.
14. Preserve Shatterbolt's cumulative history guarantees.
15. Preserve Plasma's rotating bounded refresh behavior.
16. Do not freeze a universal payload schema yet.

---

# 214. Open Decisions the Implementing Agent Must Resolve with Measurements

These are intentionally not falsely settled.

## 214.1 Exact bank lane count

Starting hypothesis: 5.

Need byte/overlap validation.

## 214.2 Exact common header

Need packing comparison.

## 214.3 Byte vs ushort codec id

Likely byte is enough now; ushort provides more namespace.

Measure overhead/value.

## 214.4 How codec version is represented

Common header vs implied by bank build/codec id.

## 214.5 Persistent handle vs declarative desired-state allocation

Recommendation: declarative desired state.

Prove in code simplicity.

## 214.6 Shatterbolt part count after common header

Need exact repacking.

## 214.7 Plasma stroke and burn population as one codec instance or two

Recommendation: consider two lifetimes/codecs, but measure header/lane cost.

## 214.8 Priority granularity

Start minimal.

## 214.9 Whether any codec needs degraded lane count

Do not add until proven useful.

---

# 215. Questions That Are Already Settled

Do not reopen these without a new user requirement.

```text
Gameplay is owner-authoritative.
Remote presentation cannot apply damage.
Core dynamic slots are bounded.
Orrery base state remains separate from spell transient presentation.
Persistent/lingering presentation can overlap later casts.
One permanent slot family per future spell is not the desired architecture.
Shatterbolt needs loss-tolerant impact presentation.
Plasma Burn needs bounded target refresh semantics.
Hot paths should remain allocation-stable.
Malformed presentation must degrade safely.
Same exact mod build is used in co-op sessions.
```

---

# 216. Review Standard

When reviewing an implementation of this document, ask:

> Did this reduce repeated transport ownership while preserving the weirdness of individual spell presentation?

If the answer is yes, the architecture is probably healthy.

If the implementation instead created:

```text
one giant generic spell payload
one giant effect engine
more permanent slots
more Harmony patches
unbounded request/state containers
remote mechanical authority
```

then it missed the point.

---

# 217. Practical “Boring First” Target

The first polished bank implementation does **not** need to be clever.

A good first result can be:

```text
5 fixed reusable lanes
small self-describing header
explicit Shatterbolt codec
explicit Plasma codec
explicit priority order
fixed arrays
one send hook
one remote render hook
one reset path
```

That is enough.

Do not add generalized dynamic registration until another spell demonstrates the need.

---

# 218. Why This Architecture Is Realistically Useful

This is not wishful abstraction.

It directly addresses current live facts:

```text
Shatterbolt already uses 3 permanent slots.
Plasma already uses 2 more.
Both duplicate transport integration.
Core has a 16-active-slot bound.
Core has a 32-byte per-slot bound.
Core has a bounded total packet extension.
Future Orrery spell count is expected to be large.
Lingering effects already overlap cast lifetime.
```

The bank converts a growth pattern that scales with **number of spell implementations** into a growth pattern bounded by **maximum presentation concurrency**.

That is the correct dimension to bound.

---

# 219. Final Architectural Rule

If only one sentence survives from this document, use this:

> **Orrery presentation networking should scale with the bounded number of effects that can be meaningfully shown at once, not with the total number of spells that exist in the class.**

That is the reason for the reusable presentation bank.

---

# 220. Agent Handoff Summary

For the implementation agent:

```text
DO:
    refetch live source first
    inventory every current Orrery Core slot/hook
    keep slot 6 base state separate
    prototype a fixed reusable bank using current presentation footprint
    preserve owner authority
    preserve Shatterbolt cumulative history
    preserve Plasma rotating refresh
    use instance identity for lane reuse
    validate complete groups before remote mutation
    keep arrays/counts bounded
    centralize send/register/render plumbing
    make capacity failure cosmetic only
    migrate in small reviewable commits

DO NOT:
    rebalance spells
    change gameplay to fit transport
    build universal spell schema
    give every spell permanent slots
    make bank understand spell mechanics
    use unbounded collections in packet hot paths
    rely on one-shot packets arriving
    partially apply malformed multi-lane payloads
    let late old data mutate a replacement instance
    expand CoreNetwork unless measured evidence proves bank-on-slots insufficient
```

The intended outcome is a small, boring, reusable transport boundary that makes the next twenty Orrery spells easier to add without making their mechanics less explicit.
