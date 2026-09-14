# Historical network standard — 2026-09-13

**Archived on 2026-09-14. Not current implementation guidance.**
The document below describes the old six-record bank and send-prefix hook.
Use [the current networking guide](../../Assets/Leviathan/Content/Scripts/NETWORKING.md) and
[the documentation index](../../DOCUMENTATION.md).
The original text is preserved below for design history.

---

# Orrery Presentation Network Standard

**Project:** Star Vortex — Orrery / Celestial Mage / Sphereweaver  
**Status:** CANONICAL IMPLEMENTATION STANDARD  
**Applies to:** all new Orrery remote-presentation networking and all maintenance/refactors touching Orrery presentation transport  
**Established from live implementation:** `skill-trees` through `018404b2af23927bf37b827a932f1a4cfac3b980`  
**Date:** 2026-09-13

---

# 1. Purpose

This document records the Orrery presentation-network architecture that is now implemented so future spell work does not recreate permanent per-spell Core slots, per-spell send hooks, or ad-hoc multipart/stale handling.

This is a **standard**, not a proposal.

It supersedes the earlier architectural proposal in `orrery_presentation_network_agent.md` and supersedes older networking guidance in `ORRERY_SPELL_DESIGN_IMPLEMENTATION_STANDARDS_V3.md` wherever the older document assumes one 32-byte Orrery spell payload or permanent spell-owned Core slots.

The governing rule is:

> **CoreNetwork owns generic transport. OrreryPresentationNetwork owns a small fixed presentation record bank and framing. Each spell/effect codec owns the meaning of its payload. Gameplay never depends on presentation delivery.**

Do not replace this with a universal spell schema or generic lifecycle engine merely because a future spell has a different presentation shape.

---

# 2. Authority and Source Order

When guidance conflicts, use this order:

```text
1. The user's newest explicit requirement
2. The newest exact live source on skill-trees
3. This presentation-network standard
4. ORRERY_SPELL_LIFETIME_STANDARD.md
5. ORRERY_SPELL_DESIGN_IMPLEMENTATION_STANDARDS_V3.md
6. shared Core networking / architecture standards
7. older handoffs, audits, agent plans, and prototype comments
```

The live source remains authoritative for exact symbols, payload versions, and tuned values.

This document is authoritative for the intended ownership boundaries.

---

# 3. Current Authoritative Architecture

The live outgoing path is intentionally explicit:

```text
CoreNetwork.AppendLocalExtension
    ↓ Harmony Prefix
OrreryPresentationNetwork.PublishForSend()
    ↓
resolve current local Orrery owner once
    ↓
OrreryNetwork.PublishLocal(owner)
    ├─ write stable Orrery base/casting state to slot 6
    └─ if Shatterbolt presentation exists:
         OrreryShatterboltPresentationCodec.Publish(...)
             -> records 0..2 / slots 7..9
    ↓
OrreryPlasmaBoltPresentation.Publish()
    ├─ bolt stroke -> record 3 / slot 10
    └─ burn refresh -> records 4..5 / slots 11..12 as needed
    ↓
CoreNetwork serializes the final latest-value dynamic snapshot once
```

The important timing rule is:

> **Sample local Orrery presentation at the actual Core send boundary.**

Spell gameplay/fixed ticks should mutate their own authoritative state. They should not repeatedly call `OrreryNetwork.PublishLocal` merely because presentation changed.

Core ship-state traffic is already the cadence. Do not create another high-frequency Orrery transport.

---

# 4. Slot Ownership

Current stable Core slot ownership:

```text
1  Stellar Converter
2  Starfire
3  Predator
4  Constrictor
5  Behemoth
6  Orrery base casting/satellite presentation
```

Orrery presentation bank:

```text
record 0 -> Core slot 7
record 1 -> Core slot 8
record 2 -> Core slot 9
record 3 -> Core slot 10
record 4 -> Core slot 11
record 5 -> Core slot 12
```

All six bank slots are registered centrally by `OrreryPresentationNetwork.EnsureInitialized()`.

A future Orrery spell must **not** register its own permanent Core slot simply because it needs custom presentation bytes.

A future spell must **not** add another `CoreNetwork.AppendLocalExtension` Harmony patch.

If the six-record bank proves insufficient under real overlapping presentation pressure, treat that as an architecture review point. Do not silently start permanent slot growth again.

---

# 5. Core Transport Bounds

Current CoreNetwork bounds that the Orrery bank must respect:

```text
ProtocolVersion      = 3
MaxPayloadBytes      = 384
MaxCombinedBytes     = 500
MaxSlots             = 16
MaxSlotBytes         = 32
Dynamic stale backstop ≈ 0.5 s
```

Each Orrery record is therefore exactly bounded to at most 32 bytes of Core slot data.

Current six-record Orrery bank does not change CoreNetwork's generic limits.

The project assumes all co-op peers use the exact same mod build and `ModsMatch` rejects mismatches. Development version/framing checks still exist to fail safely and visibly, not to support mixed-version public servers.

---

# 6. Bank Responsibilities

`OrreryPresentationNetwork` should remain deliberately dumb.

It owns:

- the six fixed physical records,
- stable mapping from record index to Core slot id,
- explicit codec ids,
- bounded multipart framing,
- generation identity transport,
- group/part validation,
- contiguous-record grouping,
- maximum group/part bounds,
- central registration,
- central per-send dispatch.

It does **not** own:

- spell gameplay,
- damage,
- target selection,
- burn/spread rules,
- chain rules,
- visual object lifetime,
- visual target attachment,
- event-history semantics,
- heartbeat/refresh semantics,
- spell-specific stale behavior,
- spell-specific payload layouts.

If the bank begins to know what an orb, burn, beam, cone, field, chain, satellite formation, or detonation means, the abstraction is becoming too broad.

---

# 7. Record Framing

Current framing is intentionally tiny:

```text
every part:
    byte codecId
    byte descriptor

leader part only:
    uint generation
```

Descriptor:

```text
bits 0..1  = part index
bits 2..3  = part count minus one
bits 4..7  = group id
```

Current bounds:

```text
LeaderHeaderBytes       = 6
ContinuationHeaderBytes = 2
MaximumPartsPerGroup    = 4
MaximumGroupId          = 15
RecordBytes             = 32
```

Payload capacity:

```text
1 part  = 26 semantic bytes
2 parts = 56 semantic bytes
3 parts = 86 semantic bytes
4 parts = 116 semantic bytes
```

`WriteGroup` takes opaque codec-owned bytes and writes them across contiguous records.

The bank must not inspect those semantic bytes.

---

# 8. Multipart Read Safety

A multipart group must validate completely before spell presentation mutates remote state.

`TryReadGroup` validates each expected record for:

- record presence,
- minimum framing length,
- expected codec id,
- expected group id,
- exact part index,
- exact part count,
- nonzero generation.

If any part is missing or malformed, the group fails presentation-only.

Do not partially apply part 0 and then discover part 1 is invalid.

Do not let malformed presentation bytes affect gameplay, damage, target state, or connection authority.

Spell codecs must additionally validate their own semantic payload:

- exact or allowed byte length,
- count bounds,
- flags,
- ids,
- finite floats,
- positive radii/widths where required,
- exact reader exhaustion.

---

# 9. Generation / Replacement Safety

A bank generation is the identity of the presentation instance/group carried by the framing.

It exists so late/stale presentation from an older instance cannot mutate a replacement runtime.

Rules:

- generation zero is invalid,
- counters skip zero on wrap,
- spell/effect codec decides when a new semantic instance requires a new generation,
- remote presentation compares the full generation where replacement safety matters,
- low-byte gameplay cast sequence is not sufficient by itself for long-lived transport identity.

Do not make the shared bank infer spell-generation semantics.

---

# 10. Shatterbolt Reference Codec

Shatterbolt is the reference case for bounded cumulative event-history presentation.

Current allocation:

```text
codec id       = 1
records         = 0..2
Core slots      = 7..9
part count      = 3
first record    = 0
group id        = 0
max semantic payload = 86 bytes
```

Payload meaning is Shatterbolt-owned:

```text
flags                  1
impact count           1
explosion radius float 4
optional orb position  8
impact positions       8 each
```

The cast can carry up to the bounded Shatterbolt maximum impact history. The three-record group provides exactly 86 semantic bytes, enough for the current maximum case.

Important semantic rule:

> **Remote presentation consumes cumulative impact history, not transient one-packet impact events.**

If the remote previously saw impact count 2 and next receives impact count 5 for the same generation, it should present impacts 3, 4, and 5.

This prevents packet coalescing/loss from deleting bounded chain-impact presentation.

Do not generalize this history model to every spell. It is correct because Shatterbolt's event sequence is naturally bounded.

---

# 11. Plasma Bolt / Plasma Burn Reference Codec

Plasma demonstrates a different presentation shape and is intentionally not forced into Shatterbolt's cumulative-history model.

Current allocation:

```text
codec id = 2

bolt stroke:
    record 3 / slot 10
    group id 0
    one part
    semantic payload = 20 bytes

burn refresh:
    records 4..5 / slots 11..12
    group id 1
    one or two parts depending on entry count
```

Bolt stroke semantic payload:

```text
start Vector2  8
end Vector2    8
width float    4
```

Burn refresh semantic payload:

```text
count                          1
repeat count:
    target network id uint     4
    remaining-time byte        1
```

Current presentation refresh batch maximum is six targets per actual send.

**This batch size is transport/presentation bookkeeping only.**

It must never become a gameplay rule.

Plasma gameplay remains owner-authoritative:

- direct lightning impact decides the initial confirmed actual damage,
- that actual confirmed amount becomes the frozen burn total,
- crit naturally carries into the burn because actual damage is snapshotted,
- descendants inherit the original frozen payload,
- burn lasts five seconds,
- a target that has had Plasma Burn remains ineligible for reinfection for ten seconds,
- spread mechanics do not know or care whether remote VFX refresh entries are sent in batches of six.

Future agents must never infer a six-target spread cap from the presentation codec.

---

# 12. Refresh / Soft-State Presentation

Some remote effects are better represented as soft state than complete snapshots or event logs.

Plasma Burn is the reference case:

```text
owner rotates through bounded active infection presentation
    ↓
remote refreshes target-attached visual
    ↓
remote visual survives briefly without another refresh
    ↓
visual clears on authored expiry, target death, or refresh timeout
```

Current Plasma presentation refresh timeout is 1.25 seconds.

The gameplay burn duration/reinfection history is not derived from that timeout.

A future persistent visual may use a similar bounded heartbeat only when presentation can safely fail/expire independently of gameplay.

---

# 13. Absence and Stale Semantics

Core dynamic state is latest-value state.

A valid subsequent dynamic snapshot that omits an effect/group is ordinary effect absence. Core's approximately 0.5-second stale backstop exists for genuine stream loss; it is not the primary spell-lifetime mechanism.

Each spell presentation owns how absence affects its remote visuals.

Examples:

- Shatterbolt uses its explicit generation/history and presentation-tail logic.
- Plasma can clear source-owned presentation when neither Plasma group is present, while individual target burn visuals also have their own authored/refresh expiry.

Do not make gameplay wait for remote presentation stale timers.

---

# 14. Publication Timing

This is a key current rule established by the migration.

Before the migration, spell/control code frequently called `OrreryNetwork.PublishLocal(owner)` while mutating state. Those calls only preloaded latest-value Core slots; they did not themselves send a packet.

The canonical model is now:

```text
gameplay/control/lifetime code
    mutates authoritative state

Core send begins
    ↓
OrreryPresentationNetwork.PublishForSend()
    samples final current presentation state
    ↓
Core serializes it
```

Therefore:

- `OrreryControl` should not know about network publication,
- Shatterbolt fixed ticks should not call `PublishLocal` every movement/impact/tail tick,
- new spells should not publish from gameplay ticks merely because their visual state changed.

If a future effect truly requires an event to survive between Core sends, solve that inside bounded codec state/history—not by adding another transport cadence.

---

# 15. Wire Budget

Current worst simultaneous Orrery presentation case:

```text
Core dynamic block header                 2
slot 6 Orrery base TLV + 16 bytes        18
max Shatterbolt three records + TLVs    102
Plasma stroke record + TLV               28
Plasma burn six-entry two-part group     43
                                         ---
current dynamic total                    193 bytes
Core extension header                      6 bytes
                                         ---
current appended total                   199 bytes
```

Core `MaxPayloadBytes` is 384 bytes, so the current Orrery presentation set is comfortably bounded below the extension payload ceiling.

This arithmetic should be revisited when adding a new codec that can overlap existing lingering presentation.

Do not reason about a new spell's bytes in isolation if another spell/status can remain visible concurrently.

---

# 16. Performance Rules

Presentation networking is a hot path.

Prefer:

- static/fixed scratch buffers,
- bounded arrays,
- fixed record/group counts,
- explicit manual dispatch,
- no LINQ,
- no per-send heap allocation,
- cached reflection where unavoidable,
- no string formatting/logging on normal send/read paths.

Current examples:

```text
Shatterbolt codec:
    one static byte[86] payload scratch buffer

Plasma presentation:
    fixed target scratch[6]
    fixed remaining scratch[6]
    fixed stroke payload[20]
    fixed burn payload[31]
```

Do not build a generic allocator/handle/event-history framework to avoid a few explicit fixed arrays.

---

# 17. New Codec Integration Checklist

Before adding custom remote presentation for a new Orrery spell/effect:

```text
[ ] Decide whether native replication already provides enough presentation.
[ ] Keep gameplay owner-authoritative and independent from the codec.
[ ] Define the minimum irreducible presentation state.
[ ] Decide whether it is latest state, bounded cumulative history, soft refresh state, or a deliberate combination.
[ ] Reuse the fixed Orrery presentation bank rather than claiming permanent Core slots.
[ ] Assign an explicit codec id and group id(s).
[ ] Prove maximum semantic payload size before implementation.
[ ] Prove record/part count fits bank capacity and overlap requirements.
[ ] Define generation/replacement semantics.
[ ] Validate the complete multipart group before mutating remote presentation.
[ ] Validate all semantic lengths/counts/flags/ids/floats before mutation.
[ ] Use bounded allocation-stable scratch storage.
[ ] Add publication to the tiny explicit central send dispatch only if needed.
[ ] Do not add a spell-local Core send patch.
[ ] Do not add a spell-local Core slot-registration patch.
[ ] Define absence/stale/visual-tail behavior.
[ ] Recalculate worst simultaneous Orrery wire budget.
[ ] Test host-owned and client-owned casting.
[ ] Test replacement casts and packet-loss-like skipped observations.
[ ] Test owner death, target death, class exit, and world teardown.
```

---

# 18. Refactor Guardrails

Do not replace this architecture merely to obtain:

- reflection-based codec registration,
- interfaces for aesthetic consistency,
- generic lane allocators,
- handles,
- dynamic scheduling/preemption,
- generic event histories,
- a universal spell payload,
- a universal effect graph,
- a service locator,
- dependency injection.

The current shape is deliberately boring:

```text
CoreNetwork
    ↓
six dumb Orrery records
    ↓
tiny explicit dispatch
    ↓
spell-specific codecs/presenters
```

A more complicated replacement must solve a demonstrated live problem and preserve:

1. bounded memory,
2. bounded packet bytes,
3. bounded active records,
4. owner-authoritative gameplay,
5. presentation-only failure isolation,
6. complete multipart validation,
7. stale replacement safety,
8. packet-loss recovery appropriate to each spell,
9. no per-spell Core transport hooks,
10. no steady-state allocation regression.

---

# 19. Source Organization

Presentation networking is class-owned Orrery code, not Core code.

If/when the runtime Scripts directory is reorganized by ownership, these files belong under the Orrery-specific folder:

```text
OrreryPresentationNetwork.cs
OrreryNetwork.cs
OrreryShatterboltPresentationCodec.cs
OrreryShatterboltRemotePresentation.cs
OrreryPlasmaBoltPresentation.cs
```

`CoreNetwork.cs` remains shared/Core and should stay at the shared root unless a future broader source-layout policy moves all Core files together.

A filesystem move must not introduce namespaces or behavior changes. Move each `.cs` together with its existing `.meta` file and preserve GUIDs exactly.

---

# 20. Current Verification Status

As of the source snapshot named at the top of this document:

- architecture/source review: complete for this migration,
- Shatterbolt migration: implemented,
- Plasma presentation migration: implemented,
- send-boundary sampling migration: implemented,
- redundant OrreryControl/Shatterbolt preload publication: removed,
- static payload/slot arithmetic: reviewed,
- Unity/mod compile: **not yet verified**, 
- local in-game smoke test: **not yet verified**, 
- host/client co-op behavior: **not yet verified**.

Do not promote this architecture from source-verified to runtime-verified until those passes succeed.

If runtime evidence disagrees with this document, trust the actual packaged-game evidence and update the standard after understanding the discrepancy. Do not add compensating complexity blindly.
