# Networking for skill authors

**Status: maintained networking and remote-presentation guide.** Reviewed
2026-09-14. See the [documentation index](../../../../DOCUMENTATION.md) for
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
   codec. `OrreryLegacySpellPresentation.WireMagma` is the first integrated example.

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

Validated on 2026-09-14:

- Full source compilation, including the URP dependency required by Sol's remote
  presentations. The asmdef now names that dependency explicitly.
- 2,864 integration assertions: production Magma golden bytes and round trips,
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
ship death/replacement and loaded native packets. IL2CPP remains unverified.

## Integration provenance

This work incorporates `codex/orrery-coop-fixes` at `fca3060` (PR #5), preserving
the local spell tuning, and OpusMax's revised CoreWire proposal. It adds the shared
callback boundary, typed per-mod APIs, budget selection and validated grant
notifications rather than keeping per-spell engine/network Harmony wrappers.

The ship-state length is a ushort; combat trailer lengths remain a byte matching
their seven-byte footer. Those corrected production paths now have regression
tests. Migrating the Core framing itself to CoreWire is separate from the Magma
integration; existing Tesla/Cryo/Shatterbolt/Plasma codecs also remain explicit
adapters. New skills should use the typed API rather than copy those older codecs.
