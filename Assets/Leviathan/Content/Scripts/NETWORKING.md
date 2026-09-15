# Networking for skill authors

**Status: maintained networking and remote-presentation guide.** Reviewed
2026-09-14. See the [Skill development](../../../../SKILL_DEVELOPMENT.md) for
gameplay design, authoritative gameplay lifetime, native APIs and historical notes.
Older network standards and proposals do not override this guide's current APIs.

Skills own mechanics and the information needed to depict them. Core owns engine
hooks, packet framing, validation, packet budgets and connection lifecycle. Each
mod's network entry point connects its skill presentations to Core.

The goal is to add a skill without adding a Harmony patch to a send, receive,
render or destruction method, and without duplicating a packet writer/reader.

## Where things belong

| File | Responsibility |
|---|---|
| `CoreNetwork.cs` | Native ship-state extension, specialization sync, slot registry, combat provenance, player-owned entity classification |
| `CoreNetworkBudget.cs` | Essential state first; optional multipart groups selected atomically in publication order |
| `CoreNetworkPresentation.cs` | One shared set of render/update/death/destruction hooks and registered presentation callbacks |
| `CoreWire.cs` | One format declaration used for both serialization and deserialization |
| `CoreCrossOwnerEffects.cs` | Reliable targeted gameplay grants, host validation and typed presentation observers |
| `CoreTimedShipEffects.cs` | Authoritative timed effects, successful-application observers and remaining-duration/revision snapshots |
| `Leviathan/LeviathanNetwork.cs` | Predator and Stellar Converter wire contracts |
| `Orrery/OrreryNetwork.cs` | Orrery registration, common state, typed `Channel<T>` API and timed-effect presentation |
| `Orrery/OrreryPresentationNetwork.cs` | Orrery's shared record bank, codec identities and publication order |

Existing specialized presentation files still own their visual implementations.
Those are allowed to contain skill-specific rendering: an orbiting projectile,
beam, impact history and timed buff genuinely have different presentation rules.
They should not implement another engine lifecycle or transport.

## Adding an Orrery presentation

1. Expose a small snapshot from the skill, such as its current projectile position,
   angle and cast identity. Keep capture read-only. Apply damage on the existing
   authoritative gameplay path.
2. In the network/presentation adapter, define a plain value-type state and one
   `Wire(ref CoreWire, ref State)` method. Prefer value fields; the transactional
   wrappers do not deep-copy referenced objects inside a struct.
3. Reserve a stable codec ID in `OrreryPresentationNetwork` and create one static
   `OrreryNetwork.Channel<State>`. Do not construct channels or delegates per tick.
4. Sample it in the module's `PublishForSend` pass with `channel.Publish(castId,
   ref snapshot)`. It chooses the required record count and validates the payload.
   Publication outside this final pass is deliberately refused.
5. Register render/forget/reset callbacks in `OrreryNetwork.Initialize`. The render
   callback uses `channel.TryRead(owner, ref snapshot, out castId)` and updates
   visual-only objects. Core supplies the engine hooks and exception boundaries.
6. Pin expected bytes and test decoding failures before changing an existing
   codec. `OrreryLegacySpellPresentation.WireMagma`/`WireCryo`/`WireTesla` and
   `OrreryShatterboltPresentationCodec.WireShatterbolt` and Plasma
   `WireStroke`/`WireRefresh` are integrated examples.
   Each pins its bytes against the pre-conversion writer's rules, not against
   itself, so a layout change fails instead of agreeing with itself.

```csharp
// Example adapter format, not code that belongs in the gameplay skill.
private static void Wire(ref CoreWire wire, ref PresentationState state)
{
    wire.Flags(ref state.ProjectilePresent);
    if (state.ProjectilePresent)
    {
        wire.Position(ref state.Position);
        wire.Float(ref state.AngleDegrees);
    }
}
```

Format conditions may use previously serialized fields or agreed protocol
constants. They must not use the local clock, ship, scene or gameplay lookup.
Never publish a failed encode or apply a failed decode. The channel already
enforces this; raw cursor users must check both `Ok` and `AtEnd` on reads.

## Adding Leviathan state

Use `LeviathanNetwork` as the boundary. Predator supplies a lunge boolean;
Stellar Converter supplies a `ConverterState`. Their gameplay files no longer
write or decode bytes. Keep their existing seven-byte/one-byte layouts stable.
New presentation callbacks can use the same `CoreNetworkPresentation` registry;
a new multipart bank is only needed if actual payload requirements justify it.

## Effects and spawned ships

- Apply local temporary contributions through `CoreTimedShipEffects`. Register
  the visual callback once with `RegisterPresentation`, instead of patching
  `ApplyOrRefresh` in the ability. Visual failure cannot reject applied gameplay.
- Use `CoreCrossOwnerEffects.RequestGrant` for effects on another player.
  Register a gameplay handler on every client, including clients playing another
  class. Only the recipient's authoritative local ship receives gameplay.
- Register cross-owner visuals with `RegisterObserver`. Core passes a typed
  `GrantNotice` after envelope validation and canonicalizes the sender at the
  host. Observers do not parse JSON or patch private receive methods.
- A grant request's `true` result means dispatched (or applied if the host is the
  target). It is **not a recipient acknowledgement**. Host-validated observers
  likewise are not an application ACK from a remote recipient. Effects needing
  a confirmed transaction require a deliberate acknowledgement design.
- Cold Fusion cross-owner visuals retain Sol's bounded player-identity leases.
  Self/locally applied Orrery buffs also use a snapshot channel containing the
  remaining duration and an application revision; repeated snapshots do not
  restart the aura. The revision changes on refresh, not on every packet.
- For a native follower owned by the player, set ownership/team in the owning
  subsystem and call `CoreNetwork.ConfigurePlayerOwnedEntity` after construction.
  This selects native player-entity replication instead of star authority.

## Packet pressure and lifetime

Core reserves ungrouped state as essential. The Orrery common slot remains
essential; each spell group is marked optional only after all its records have
been written. A group either fits in full or is omitted. Smaller later groups
can still fit after a large group is omitted. Counts describe the selected bytes.

The native-plus-extension limit remains 500 bytes; the Core payload limit remains
384 bytes. Specialization bursts retain their alternating policy when necessary.
If even essential state cannot fit, the dynamic block is omitted. This cannot
guarantee all simultaneous visuals will be delivered under every packet budget.

Absence of an optional group is not proof of gameplay completion. Timer-backed
visuals should expire using their own bounded lifetime. Continuous snapshots may
temporarily disappear under pressure. New event codecs should define bounded
repetition and generation retention to avoid missing or replaying an effect.
Keep these semantics in the presentation adapter, not in skill mechanics.

Core isolates render/update/cleanup failures per registered entry. A publishing
exception discards the unfinished extension sample and lets the native packet
continue. It does not send a partially written group. Warnings from that lifecycle
registry are limited to one per entry per world.

## Validation

Run from this directory:

```powershell
.\Tests\run-network-tests.ps1
```

The runner uses Unity's generated `LeviathanMod.csproj`, the installed Unity Mono
runtime, and the .NET SDK compiler. Override `UnityEditorRoot` or `GameManaged`
when their locations differ. Output goes to a temporary directory; the runner
does not install the mod, change the project checkout or push to GitHub.

Validated on 2026-09-14 after the Opus patch batch and Plasma integration:

- Full source compilation, including the URP dependency required by Sol's remote
  presentations. The asmdef now names that dependency explicitly.
- 7,096 integration assertions: production Magma, Cryo, Tesla, Shatterbolt and
  Plasma golden bytes, truncation/invalid-field rejection and round trips,
  every truncated prefix of test packets, production ship-state writer/reader
  above 255 bytes, production combat footer writers/locator, group budgets and
  injected publisher failure preserving the native packet.
- All 14 mod slots and repeated module initialization, plus 55 Harmony target
  selectors and the integer damage-router binding against the installed game.
- 12 cross-owner routing checks using native transport/JSON/Unity test doubles:
  canonical sender, target-host relay, duplicate application suppression,
  invalid-star rejection, recipient rejection and non-target observation.
- Opus's updated 51 CoreWire self-tests, including deliberate callback exceptions
  and failed-encode cleanup, using Vector2/Debug test doubles.
- Timed-effect tests with engine clock/ship/shield test doubles: contribution
  composition, shield integration, refresh identity and notification, remaining
  duration, expiry and remote-authority rejection.

These are not a live Unity rendering test or two-peer co-op test. Test the same
rebuilt mod on host and client: all spells in both directions, Cold Fusion self
and ally casts, satellite visibility, overlapping effects, world transitions,
ship death/replacement and loaded native packets. The developer reference identifies
the game backend as Mono. These checks used the local Unity 2022.3.62f1 toolchain;
the v0.8.21 developer reference requires f2 for compatible AssetBundle packaging.

## Integration provenance

This work incorporates `codex/orrery-coop-fixes` at `fca3060` (PR #5), preserving
the local spell tuning, and OpusMax's revised CoreWire proposal. It adds the shared
callback boundary, typed per-mod APIs, budget selection and validated grant
notifications rather than keeping per-spell engine/network Harmony wrappers.

The ship-state length is a ushort; combat trailer lengths remain a byte matching
their seven-byte footer. Those corrected production paths now have regression
tests. Migrating the Core framing itself to CoreWire remains separate from the
Orrery codec migration.

All five damaging spell presentations now use typed `Channel<T>` formats.
The 2026-09-14 Opus patch batch converted Tesla, Cryo and Shatterbolt and reworked
Plasma gameplay. Integration also converted Plasma's remaining byte codecs,
added its separate Immolation presentation group, and reran the actual harness.
Core framing itself still uses its existing tested serializer.

Plasma uses codec 2 with three groups: 0 is the unchanged 20-byte stroke; 1 is
Plasma Burn refresh; 2 is Immolation refresh. Refresh lists contain a count plus
up to six `(uint target, byte remaining)` entries. Remaining time is quantized
at 0.05 seconds, capped at 12.75 seconds. A shared six-effect sampling limit and
round-robin cursor bound total capture work. The two effects retain separate
remote lifetimes even on the same target; both currently use native fire visuals.
This extends the earlier protocol: old builds do not know group 2 and reject
refresh durations over five seconds. Distribute the same rebuilt package to peers.

`Channel<T>` now rejects nonminimal part counts and zero generations centrally,
so individual codecs do not need to restate those transport checks. Shatterbolt
can use four records for its full 94-byte orb-plus-ten-impact snapshot; its old
three-record ceiling dropped that valid shape. Whole-group budget selection still
applies, so larger frames can be omitted when essential state leaves insufficient
room. Remaining live checks include overlapping Plasma/Immolation effects and
visual refresh under packet loss/budget pressure.

## Recipient barriers and shared projectile capture (2026-09-15)

Accretion is the first consumer of `CoreIncomingDamage`'s **whole-application**
veto. Its GameShip gate is after incoming scaling/caps and before the native base
call. A blocked application returns from the enclosing override; it does not
merely mutate the caller's shared damage array. `CoreDamageApplicationObservation`
tracks the first application of a scoped native received-hit call, propagating
only pre-native section forwarding, not independent reflected/conduit hits.
`CoreIncomingDamageTails` prevents network knockback/impale and local ImpaleMissile
received-hit tails after that exact application is vetoed. Ordinary inactive paths
continue normally. Direct resource costs that bypass damage are not intercepted.

`CoreProjectileCapture` reserves cross-owner effect id **0x0101** on the existing
reliable `CoreCrossOwnerEffects` lane (no new native receive hook). Providers own
ReadField/Reserve/Settle/Fault/Eligible policy; Core owns fixed records, identity,
packet validation, native authority and retries. Accretion provider id is **1**;
its cast grant remains **0x0202**. Retired prototype ids 0x0203/0x0204 are not used.

The six existing uint payload words are `(generation, transaction-or-field-sequence,
projectile-id-or-radius-bits, value-or-duration-bits, zero, version/phase/provider)`.
The final word packs version 1 in bits 16..31, phase in 8..15 and provider in 0..7.
Phases are Announce=1, Prepare=2, Authorize=3, Reject=4, Captured=5, Aborted=6,
Receipt=7, Unknown=8, Query=9. Unexpected control payloads and non-finite/zero shot
costs are rejected. Announcements carry geometry, remaining duration and generation,
**not capacity**. They are gameplay leases, independent of optional VFX snapshots.

Simulator contact holds the exact authoritative projectile, including native
lifetime/physics, while the recipient earmarks capacity before Authorize. Only
Captured settles expenditure/healing; explicit Aborted restores the earmark.
Simulator results are retained/retried until Receipt. Duplicate/stale exchanges
cannot consume a second ticket. A process-lifetime id/high-watermark prevents
reallocating completed old proposals. An authorization may settle after authored
expiry, but never mutate a replaced generation. Unknown outcomes are not refunded:
after the bounded retry window the affected generation is retired without healing.
This conservative degradation is preferable to unlimited free captures.

Bounds per client: 4 registered providers, 16 peer slots, 64 simulator exchanges,
64 recipient exchanges, 32 new remote captures/second, 192 outgoing service
messages/second and 12/tick. Hold timeout is 1.25 seconds, retry interval 0.20,
uncertainty window 8 seconds. Field refresh interval is 0.25 seconds with a 1-second
lease. Exhausted budgets preserve native projectiles (or explicitly abort/resume
held ones); the recipient damage veto remains active. Terminal exchanges may stay
until receipt/world cleanup, so persistent transport failure can temporarily exhaust
capture slots; it never grants free absorption. Reliable grants already use the
native star-scoped relay. No per-hit capacity broadcast was introduced.

`CoreProjectileSweep` uses the installed Projectile/FuzzyProjectile native queries
and inserts field contact before the next accepted native collision. It preserves
prior obstacles, radius, piercing/reflect continuation and subclass cadence. Mine
and fired CapturedProjectile retain their virtual behavior; starting-inside checks
also cover stationary mines. Orbiting/drawn captured shots and unvalued/source-less
shots are deliberately not captured; any actual incoming damage still reaches the
receiver gate. Nominal launcher Damage values ordinary/explosive shots, without a
fictional crit or multiplication by potential explosion victims. Fired captured
shots add their explicit flat/percentage budget, excluding the cannon's transient
per-hit bonus.

`CoreProjectileSpawnGuard` is necessary because native Projectile.Init sweeps before
Launcher.AddProjectile/RegisterNetProjectile. An init contact can stop/hold that
sweep, but despawn waits for the enclosing launcher completion or the shared next
tick for another native spawn path. The field is revalidated then. Pool reset,
recast, object replacement and world teardown cannot capture a reused instance.

Accretion's lifetime bridge ticks the local recipient independently of Orrery class
membership. Typed presentation codec **7** is likewise published before class-only
send gating. Active payloads are **18 bytes**, ended payloads **5 bytes**, plus the
existing channel framing/generation. The active payload is flags, uint sample,
float remaining, float authored duration, float radius, byte capacity fraction.
Monotone samples, bounded deadlines and ended-generation tombstones prevent stale
snapshots from restarting a disk. Whole-group omission is not cancellation.

Tests: `Tests/run-network-tests.ps1` retains the full installed-reference build,
network integration/selector suites and adds native Accretion IL/codec assertions.
Its multi-runtime protocol tests use .NET 8, not Unity Mono. A .NET 8 SDK is needed.
`python Tests/run-portable-tests.py` runs portable production-code suites without
installed game DLLs. The GitHub workflow runs these same sources with explicitly
named Unity/transport doubles; passing it is not live physics/socket validation.
