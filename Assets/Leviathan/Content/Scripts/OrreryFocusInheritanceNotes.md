# Orrery focus inheritance implementation note

This branch introduces class-local focus inheritance for hidden Orrery spell adapters.

Contract:

- first equipped matching elemental weapon remains the donor
- OrrerySpellPower remains the sole raw/base spell-damage authority
- donor modifiers are cloned mechanically except Star Vortex's explicit direct-damage modifier family (`Modifier.damageTypes`)
- donor customizers are inherited mechanically except `Customizer.Type.AdjustDamage`
- no per-spell applicability whitelist is introduced; inherited stats that a native attack family does not consume are simply inert
- native spell adapter customizers are preserved and donor customizers are added alongside them

Important follow-up:

- FF currently assumes one tracked guided projectile. A donor ShotCount roll can cause the native launcher to emit more than one projectile, so FF needs its active projectile tracking widened before ShotCount inheritance is considered runtime-complete.
- This branch has not been compiler-, in-game-, or co-op-verified.
