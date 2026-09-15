# Arc Resonance - LL source integration

Branch: `feature/orrery-lightning-rod`. No merge into `skill-trees`.

This supersedes the **status** of the earlier gameplay checkpoint. LL spell id 2
now registers the Arc Resonance custom executor, rather than the Tesla channel.
The branch includes the earlier per-playback audio changes and the sector changes
from `skill-trees` at `aeb1021`.

## Before building the playtest package

Run **Star Vortex Mod > Prepare Arc Resonance VFX** once in the Unity project,
then the usual **Build AssetBundles > Package Mod**. Send the same rebuilt mod
package to the co-op group.

The editor command produces a script-free copy of the actual vendor
`VFX_Zap_02_Blue.prefab`, disables autonomous particle playback, and assigns the
copy to `leviathanarcvfx.bundle`. It assigns previously unassigned visual dependency
assets while preserving any existing bundle assignments. The vendor source prefab
itself is not edited. The command is outside the shipped mod assembly.

The branch contains an exact source-blob copy at the destination so the asset and
its GUID exist, but the preparation command still needs to run. The large vendor
prefab could not be retrieved for direct inspection through the connector; it was
copied by its existing Git blob, `9757563be8c0d0dbe98960373cf5c4f97664684f`.
Neither the preparation command nor Unity rendering was executed here.

Prepared destination:
`Assets/Leviathan/Content/Scripts/Orrery/ArcResonanceVFX/OrreryArcResonanceZap.prefab`

Missing or unsafe/unprepared VFX suppresses the visual with a warning, not gameplay.
A packaged/working blue appearance is therefore an explicit playtest item, not an
assertion made by this source checkpoint.

## Implemented behavior

The owner acquires the eligible hostile ship nearest the cursor inside its resolved
range, then retains that exact GameShip plus its combat entity identity. Target
selection is never rerun during the execution. Default center-to-center range is
85 m; leaving it ends the cast permanently. Optional grace defaults to zero and
never permits strikes while out of range. Target death/invalidation, source-slot
replacement, owner invalidation and teardown cancel remaining strikes.

The default schedule is an immediate strike followed by hits every two seconds,
up to three hits. The separate, exclusive six-second deadline retains the formula
until completion. Count, delay, cadence and duration are independent knobs. A
configuration that cannot accommodate its requested count warns instead of silently
rewriting it. Frame hitches never create a catch-up burst or extend the deadline.

Damage is authored in integrated reference-seconds per strike, with normal shared
Orrery focus bonuses and a fresh crit roll/status resolution for each hit. The
provisional baseline is 2 reference-seconds per strike, not a verified conversion
from the old Tesla channel. `AdditionalDamagePercentPerStrike = 20` produces
1.0 / 1.2 / 1.4 ... pre-crit multipliers; zero leaves them equal. The optional final
allowed strike multiplier defaults to one. No previous crit or post-mitigation hit
is fed into the next hit.

Each accepted strike drives one blue connection and one thunder playback. The
connection lasts 0.5 seconds by default and follows both ship endpoints. It is
presentation only, never half a second of continuous damage. The renderer samples
a visible frame of the vendor effect and pauses its particle simulation during
that connection, while the enclosing transform tracks its moving endpoints.
`ZapSampleNormalizedAge` and `ZapTextureRotationDegrees` allow calibration of the
source atlas/curves without changing gameplay. The default final 0.1 seconds fades.

Thunder uses the same cached `thunder` file as Plasma, with explicit per-call timing.
Its default duration is natural clip length and fade starts at 2.235 seconds. Bolt
lifetime does not trim the WAV. Plasma's existing call and default are unchanged.

## Compendium

`OrrerySpellCompendium.ArcResonance` exposes 40 consumed profile controls covering
acquisition/range, count/timing, damage growth and inheritance, crit/status,
width/brightness/tint/fade/sample/offsets/sorting, and independent audio timing and
spatial controls. They are source tuning fields, not a new in-game settings UI.

`LL` and `LLL` are separate value-type profiles consumed by the same executor.
LLL presently copies LL's provisional values; it is not registered or unlocked by
this change. The intended stronger/larger LLL balance remains undecided. Recipe
length never automatically determines strike count.

## Multiplayer boundary

One static typed `OrreryNetwork.Channel<WireState>` uses codec **8**, group 0.
Codec 7 was left for the concurrent Accretion Disk work. The owner snapshot is
sampled only inside the established `PublishForSend` pass. Render/update/forget/
death/reset callbacks register through `OrreryNetwork.Initialize`; Arc adds no
engine or native transport Harmony hooks. No Core framing/budget changes are made.

The payload is 19 bytes: recipe size, strike index, active flag, target net id,
2D target anchor and age of the latest strike. The bank header supplies the uint
cast generation, so it occupies one existing record. Static damage/timing/visual
configuration is not streamed.

Observers deduplicate by cast generation and strike index BEFORE playback. A
repeated snapshot cannot restart a bolt or thunder, and optional-group omission
cannot clear the ledger or manufacture cancellation. Visual deadlines are finite;
late snapshots use remaining visual time. Audio older than one second is suppressed,
and lost intermediate strikes are not replayed in a catch-up burst. These policies
cannot guarantee that every strike is visible under prolonged loss/budget pressure.

Native target lookup binds once per cast and retains the exact local entity key.
After a bound target disappears/rebuilds, its anchor is frozen rather than attached
to a replacement object. Unresolved targets use the owner's transmitted anchor.
Presentation state is bounded to 32 owner replicas, one current generation per
replica, and a finite target-reference lease. Death/replacement/world callbacks
release objects and ledgers.

## Preservation and validation

The source audit passed 74 checks, including exact input blob hashes, preservation
of all pre-existing Compendium definitions, narrow module/bank edits, consumed
knobs, field order, independent payload-size arithmetic and lexical delimiters.
See `ARC_RESONANCE_SOURCE_AUDIT.json` and `Tests/ArcResonance/verify_source.py`.
These are **not** C# execution, a production codec round-trip test, a Unity build,
asset preparation, audio playback or two-peer testing. Those were not run here.
The previously committed portable C# tests remain available; no SDK acquisition
was pursued in this pass.

The native damage call uses the maintained shared integer-critical-hit-compatible
boundary, not the older decompile's direct bool signature. The DLL/decompile were
used for native context and physics/source lifecycle checks.

Old Tesla executor/codec definitions remain unregistered to LL in the shared
legacy spell implementation and existing regression fixtures; they are not a
second active Arc damage or presentation path. Removing those shared legacy
sections is separate cleanup, not disguised mixed-version compatibility.

## First playtest checks

Confirm the displayed LL name and cast, no-target behavior, exact-target retention
while moving the cursor, three strikes with no range break, and loss of remaining
hits after leaving range. Move/rotate both ships during a visible connection; check
blue appearance, width, half-second expiry and a single thunder per hit. Test a
first-strike kill, owner death, source swap and jump/rejoin. Repeat with host and
client ownership reversed, plus Plasma alongside Arc. Tune damage and the blue
sample/rotation controls before treating the current values as final balance.
