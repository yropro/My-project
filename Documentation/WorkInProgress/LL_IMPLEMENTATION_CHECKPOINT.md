# LL Lightning Rod — first C# implementation checkpoint

Status: **partial source implementation, not a playable LL replacement**.

Repository: `yropro/My-project`  
Branch: `feature/orrery-lightning-rod`  
Base: `42fd79733de00151bac0788eb8c531cfae83fbf9` (`skill-trees` when read)

## Implemented in this checkpoint

### Shared per-playback audio controls

`CoreAudioRuntime.PlayPositionalOneShot` now accepts two optional trailing inputs:

- `playbackDurationSeconds = -1f`: natural clip length; zero is intentional silence;
  positive values cap playback without stretching or looping the WAV.
- `fadeOutStartSeconds = null`: use the existing registered clip default; an explicit
  non-negative value is this voice's fade start; `-1f` disables fade for this voice.

Duration includes the fade. For example:

```csharp
CoreAudioRuntime.PlayPositionalOneShot(
    "thunder", position, 1f,
    playbackDurationSeconds: 0.75f,
    fadeOutStartSeconds: 0.5f);
```

That voice fades from 0.5 to 0.75 seconds. Another call can simultaneously use the
same cached clip with different timing. No clip or other voice is mutated.

The existing clip-default registration remains a real, still-used API, not a second
playback timer. Plasma's existing call and its 2.235-second fade-start registration
are untouched. Explicit LL settings will override the default for that call.

`CoreAudioPlaybackTiming` resolves immutable per-voice timing. The replacement
`CoreAudioVoiceLifetime` starts the native fade, enforces the audible end at its
next update, and cleans up the temporary object. Active voices are also stopped
and destroyed on world reset. Native Effects mixer routing, base pitch 1, spatial
blend 0.75, distance defaults, the 0.50-second cleanup padding, and minimum one-second
object lifetime are preserved. Asset lookup is byte-for-byte unchanged; converting
that existing resolver to ModContent is outside this timing-only change.

### LL target-lock and timing state

`OrreryLightningRodState<TTarget>` is actual C# intended for the authoritative
owner runtime. It stores a read-only target reference and snapshots the cast's
settings. There is no target reassignment or reacquisition API.

With settings `(duration: 6, firstDelay: 0, interval: 2, maxStrikes: 3, grace: 0)`,
ticks on schedule produce hits at 0/2/4 seconds, then complete at 6. Exhausting the
hit count does not prematurely complete the lifetime. Invalid target, terminal
range break, explicit owner cancellation and invalid/backwards clocks fail closed.
Optional range grace never permits strikes while the target is outside range.

Hitch policy: at most one strike per tick and a full interval after each accepted
strike. Late processing may delay or lose remaining strikes; it never creates a
catch-up burst or extends the original cast deadline. This is an explicit
conservative scheduling choice, not a damage rebalance.

The class does not discover targets, measure metres, determine authority, or deal
damage. Its boolean validity/range inputs must describe its exact stored target.
The engine wrapper must also validate the target's actual entity incarnation,
execution generation and owner lifecycle; a reused native ID or object must not
be mistaken for the original cast target. Release the state on teardown rather
than retaining target references as history.

## Deliberately NOT integrated yet

LL still resolves through the existing Tesla executor. This checkpoint does not
claim that Lightning Rod can be selected or playtested in-game.

The remaining cutover is:

1. Add the real owner executor using the maintained signature
   `(GameShip, OrreryCastInvocation, OrrerySpellRegistry.SpellDefinition)`, acquire
   the nearest valid cursor target inside the resolved 85 m range, and route fresh
   per-hit damage through the existing shared combat/native boundary.
2. Hook only the established `OrrerySpellLifetime` fixed-step and cleanup dispatch;
   preserve deferred formula completion when `TryCommit` is on the stack.
3. Wire the reviewed tuning into `OrrerySpellCompendium.LightningRod`. The previous
   42-control design catalog is still a draft, not 42 implemented controls here.
4. Adapt/package the blue Zap prefab and follow BOTH ship endpoints during each
   0.5-second visible window. The vendor file's mere presence does not establish
   packaged availability or a working half-second visibility curve.
5. Integrate owner-confirmed strikes with the then-current typed Orrery channel,
   send-bound publishing and presentation lifecycle callbacks. Optional group
   omission must not invent cancellation or replay a prior strike. Remote clients
   never use this owner scheduler to manufacture damage or presentation events.
6. Switch spell id 2 / the LL recipe from Tesla to the new Custom executor, then
   remove superseded Tesla-specific consumers together. Do not activate a second
   authoritative implementation or create speculative compatibility aliases.

No networking code, codec allocation, spell registration, Compendium tuning,
asset bundle assignment or Plasma spell source was changed in this checkpoint.
Nothing was merged into `skill-trees`.

## Validation and source provenance

Read the supplied `MODDING_REFERENCE(2).md` (v0.8.21) and the supplied native
`SoundEffectPlayer` implementation, along with the maintained repository guides
and relevant source at the pinned base. Native audio Attach/Play/Stop/fade and
scaled-time pause behavior were inspected. This is not an installed-game build.

The original audio source was reconstructed exactly and verified against blob
`a650db3f3b21ea358f43a98804f2c204f20e8a00` before applying the patch. The static audit
compares the preserved asset resolver and tuning against that exact baseline.

- Static source/preservation checks: executed; see the adjacent JSON report.
- Portable C# regression harness: 12 groups authored, linking the production classes;
  **not executed** here because a C#/.NET toolchain was unavailable and SDK download
  was unsuccessful.
- Unity/Star Vortex compilation: **not performed**.
- Native WAV playback, packaged blue-prefab rendering and two-peer tests:
  **not performed**.

Run the portable harness as documented under `Tests/LightningRod.CoreTests` before
continuing integration. Re-read/rebase against any newer `skill-trees` networking
changes instead of treating this pinned snapshot as permanently current.
