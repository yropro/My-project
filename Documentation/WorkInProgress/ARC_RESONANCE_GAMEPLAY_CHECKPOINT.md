# Arc Resonance — owner gameplay checkpoint

Work branch: `feature/orrery-lightning-rod`. Based on merge `23c6a00`, preserving the earlier audio/scheduler work and `skill-trees` at `aeb1021`. Nothing is merged into `skill-trees`.

## Source added

`OrreryArcResonance.cs` is the owner-side executor, not another reference model. It acquires the eligible enemy nearest the cursor inside the resolved caster range, locks the exact GameShip plus CoreCombat entity key, checks range/validity each fixed step, and routes each accepted hit through the current shared damage/critical-hit boundary. The formula completion is deferred past TryCommit and guarded against replacement executions. The existing OrrerySpellLifetime dispatcher handles ticking, target destruction, owner replacement and world teardown; no new engine Harmony patches were introduced.

The Compendium now has `ArcResonance.LL` and a separate value-copy `LLL` profile. Count, duration, delay and interval are independent. Per-strike additive damage growth defaults to zero; an optional final-strike multiplier defaults to one. Recipe length never sets strike count. LLL has no invented power bonus and is not unlocked or registered here.

Current LL values: 85 m, six-second lifetime, no first delay, two-second interval, maximum three strikes. Damage remains provisional at two integrated reference-seconds per strike. A configuration that cannot fit all requested strikes warns instead of rewriting duration/count. A hitch emits at most one strike and never shortens the next interval.

Cosmetic settings include the requested half-second blue Zap, width/brightness/opacity/fade/tint/endpoint offsets, and independent per-playback thunder timing. Gameplay consumes the gameplay settings now; visual/audio settings await the presentation adapter. Invalid cosmetic lifetime/offset values cannot extend owner-state retention indefinitely or corrupt the snapshot.

## Explicit cutover boundary

The LL recipe is deliberately STILL registered to Tesla in this intermediate commit. The owner executor is implemented and wired to lifetime dispatch, but it must not be represented as selectable/playable until the new presentation and registry cutover land together.

Remaining integration:
1. Blue Zap renderer with both endpoints following their ships for 0.5 seconds; one audio playback per accepted strike, independent of decorative particles.
2. Typed Orrery presentation adapter, sampled only by PublishForSend, with strike generation retention across optional group omissions and bounded visual lifetime. No remote gameplay scheduler.
3. Reserve a new codec centrally. Codec 7 is already used by the concurrent Accretion Disk branch (`6e623d6`); do not claim it. Codec 8 is the proposed Arc reservation, not yet allocated here.
4. Switch spell identity 2 to the Arc custom executor and retire its previous LL routing without changing unrelated spell tuning.

The blue vendor prefab exists at `Assets/Vefects/Zap VFX URP/VFX/Zap/Particles/VFX_Zap_02_Blue.prefab`, blob `9757563be8c0d0dbe98960373cf5c4f97664684f`, but the connector returned no contents for the oversized asset. No claim is made that its particle curves, dependency bundle assignments or runtime rendering have been verified.

## Validation status

This is an uncompiled source checkpoint. No Unity build, native rendering/audio or peer test is claimed. The portable C# tests from the earlier checkpoint remain available; compiler acquisition is not being pursued at the user's request. Unrelated Compendium tuning was preserved from the hash-verified source. Fresh per-hit crit/status behavior uses CoreNativeCriticalHits and OrreryDamageRouter rather than the older decompile's bool native binding.
