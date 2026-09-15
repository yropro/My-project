# Accretion Disk co-op implementation decisions

Baseline: `codex/orrery-accretion-disk` at 60db21e. Corrective work is isolated on
`codex/orrery-accretion-coop`; no packaged binary or gameplay deployment is implied.
The September 15 source map and handoff govern the mechanic.

## Authority and transaction boundary

| Operation | Authority / contract |
| --- | --- |
| Cast | Caster resolves one immutable snapshot and targets a player; remote dispatch is not an application ACK. |
| Capacity, expiry, healing | Recipient's local ship owns one reservoir, including forwarded Leviathan section damage. |
| Incoming damage | Shared Core veto at the actual GameShip-to-Damageable call, after native scaling/caps but before defenses; returning from GameShip also skips its received-hit tail. |
| Projectile sweep | Projectile simulator uses the native query origin, direction, range and projectile radius; barrier contact competes with earlier native hits. No replica destruction, fabricated collider or point-defense eligibility. |
| Remote interception | Reliable gameplay field announcement, then bounded prepare / authorize / captured-or-aborted result / receipt. Announcements never own capacity. |
| Reservation | Recipient earmarks the lesser of remaining available capacity and the validated nominal shot cost before authorizing. Other shots/hits cannot reuse that capacity or final-hit grace. |
| Commit | Simulator holds the exact native instance, captures it only after authorization, retains the terminal result and retries until receipted. Recipient commits/heals once from the reservation, not from dispatch success. |
| Unknown outcome | Never refund an authorized unknown result on timeout. Retry/query; retire the affected old generation without healing if the peer/session cannot resolve it. Explicit abort releases the earmark. |
| Replacement | Nonzero process-lifetime generation and transaction identifiers; old callbacks cannot mutate replacement gameplay or pooled projectiles. |
| Presentation | Recipient-generated typed snapshots, including explicit ended generation; optional omission is not a collapse. Depletion and duration are separate semantic values. |

This adds a minimal shared projectile capture transaction service because native
Gravity Cannon grants require a cannon slot and the old Accretion capture-first
reply is not an atomic accounting contract. It does not add a new network receive
patch, private packet envelope or per-hit capacity broadcast. Fixed-size state and
rate limits bound work under load. Candidates rejected by capacity/bounds keep their
native behavior; damage that actually reaches the recipient still takes the same
receiver path.

## Gameplay choices

- Every positive eligible incoming application is wholly blocked while capacity
  is available. Sum finite positive scaled packet components once; no component or
  final-hit spillover. Invalid/zero applications retain native behavior.
- All actual incoming damage routes, including friendly/self/environment and DOT,
  use the same receiver policy. Direct resource changes that do not call damage
  are not silently converted into damage events.
- Native healing uses integer tier 0, hull first then native excess-to-shield,
  never resurrection. Only capacity actually spent generates recovery.
- One recipient pool; a new cast replaces it. Class changes do not remove a
  successfully granted effect. Death/ship replacement/world teardown do.
- Void supplies the barrier's reference power, damage bonus, duration and range
  bonuses. Stellar supplies the healing conversion bonus. These distinct roles
  do not average unrelated implements or copy their raw weapon DPS.
- Interception radius is the enclosing live shield/hull/Leviathan-section radius
  plus authored extension. Brightness never changes mechanics.
- Recipe stays unordered Fire/Ice/Ice (Stellar/Void/Void). Any temporary playtest
  activation must be explicitly labeled; no silent change to the two-rune class.

## Validation boundary

Portable tests compile production pure-state code with explicit engine doubles.
Installed-reference compilation uses the user's September 15 managed DLL archive.
Neither is live Unity rendering, native transport execution or a multi-peer test.
Exact executed results and remaining cases are recorded when work is finalized.

## First co-op playtest

Use `codex/orrery-accretion-coop`, rebuild the mod DLL in the existing Unity project,
and Package Mod. Distribute that **same rebuilt package** to every peer. Do not
use an older checked-in ModPackage DLL as evidence that these source changes are
active. No new bundles are required for the existing Frost Nova placeholder;
no project/editor upgrade or automatic game-directory deployment was performed.

Choose Orrery, aim near a friendly player, and press **Ctrl+Shift+F8**. The recipient
may be any class. With no eligible ally near the cursor the spell targets self.
The normal SVV definition remains registered, but normal three-rune input waits
for future third-satellite progression. The shortcut is deliberately temporary and
has a one-second input guard plus the normal shuffle. Its log distinguishes local
application/dispatch from a remote application acknowledgement.

Starting knobs in `OrrerySpellCompendium.AccretionDisk`:
8-second base duration; 80-metre extension beyond the live ship/anatomy footprint;
capacity = 4 seconds of the Void implement's resolved reference power (not its raw
weapon DPS); 30% base conversion; 120-metre cursor search / 300-metre target range.
Void duration/range/damage bonuses and Stellar conversion bonus apply once at cast.
Placeholder opacity is 0.38, brightness increases from 1 to 3 as capacity depletes,
and duration fading is independently available but disabled by default.

Start with self, then host-to-client, client-to-host and client-to-client casts,
including a non-Orrery/Leviathan recipient. Check ordinary and fast shots, missiles,
mines and explosive shots; then beams, repeated halo ticks, existing explosions and
DOT. A final oversized hit must pass no damage, spend only the remaining pool and
heal only from that expenditure. The following distinct hit must behave normally.
Shots crossing the extended field can consume capacity even if they miss the hull.
Successful capture must not explode. Existing explosions are not erased for other
targets. A projectile may briefly pause on its simulator while capture is authorized.

Check hull/shield recovery at low/full health; overlapping fields; Leviathan head,
sections, AoE and footprint while turning; recast, death/rebuild, class change,
caster departure and star transition. Check remote early collapse and brightness,
and repeat with latency/loss or heavy projectile traffic. Read Player.log for native
patch failures, optional missing-placeholder warnings and unresolved-capture logs.

## Executed validation and remaining limits

Before repository publication, source compiled against the supplied installed
managed assemblies. Executed: 7,097 network integration checks; 58 native target
selectors; 92 Accretion installed-IL/codec/packet checks; 626 production reservoir /
three-isolated-runtime protocol checks; 12 original cross-owner routing checks;
51 CoreWire checks; and the original timed-effect suite. Test logs are retained by
the portable GitHub workflow. The exact final revision must pass that workflow too.

The three-runtime harness deliberately doubles Unity objects, clock and native
capture, plus the existing reliable transport (which has its own production-source
suite). It actually executes the production transaction service, reservoir, spawn
guard and observation code, including delayed/lost/duplicate messages, explicit
abort, uncertainty retirement, final-grace contention, reentry/forwarding, stale
recast results, pool reuse, expiry, invalid values and budget pressure. Native tests
transform the actual installed IL and compare an independently specified byte
vector, but do not install those patches into a running Unity game.

Live Unity/Harmony execution, placeholder appearance, native physics/socket timing
and the live co-op matrix above have **not** been run here. The field intentionally
fails conservatively under sustained communication failure: unresolved authorized
captures retire that old field without refund/healing; overloaded or unsupported
captures resume/preserve native projectiles rather than generating free absorption.
Source-less/unknown-value projectiles cannot receive safe spatial valuation; their
actual incoming damage still receives barrier protection. This is a disclosed
spatial-coverage limitation, not proof that every custom projectile type is supported.
