# Native API compatibility

**Scope:** installed-game integration. Reviewed 2026-09-14.
Start with [DOCUMENTATION.md](DOCUMENTATION.md) for the other development guides.

## Evidence before bindings

The failures investigated on 2026-09-13 exposed a mismatch between the installed
Star Vortex assembly and the Unity project's older reference. This investigation
date is not a verified game-release date. An exact delegate lookup returned null
and prevented class initialization; other callers still used old parameter types.

Check reflected targets and full overload signatures against the installed game's
`Star Vortex_Data/Managed/Assembly-CSharp.dll`. Compilation against the editor
reference alone does not establish runtime compatibility. Do not copy old method
signatures from the historical precompile audit.

## Critical-hit boundary

The installed API uses integer critical tiers at damage/heal boundaries, including
`NetCombat.RouteDamage`, `Damageable.Damage`, `GameShip.Damage`, `Damageable.Heal`
and `DamageBeam.GetDamageData(GameShip, ref int critTier)`.

Use `CoreNativeCriticalHits` and the shared damage router where applicable instead
of adding another per-skill reflective binding. Preserve integer tiers when
forwarding native results. An existing skill that deliberately makes a binary
crit decision maps that decision to 0/1; that is not a rule to flatten every native
tier into a boolean. Healing without a crit uses tier 0.

Harmony normally binds original arguments by name. A patch requesting `crit`
cannot bind an original parameter named `critTier`. Match the actual signature or
use a verified positional argument with the correct type. Check the complete
parameter list after updates, not only whether the method name still exists.

## Shared effects and transport

`CoreTimedShipEffects` owns the native stat/query hooks needed to compose temporary
contributions. Skills use its public API and register presentation callbacks.

`CoreCrossOwnerEffects` owns native dispatch interception and sender validation.
Its current reserved message byte is 126; initialization rejects a native enum
collision. Grants use native reliable delivery. A dispatched request is not proof
that a remote recipient applied it. Presentation observers consume typed validated
notices rather than patching private receive methods or parsing JSON again.

See [Networking for skill authors](Assets/Leviathan/Content/Scripts/NETWORKING.md) for current integration APIs and
packet formats. A mod-version match alone does not prove identical development
binaries; rebuild and distribute the same package to all peers for co-op testing.

## Verification boundaries

The networking runner compiles the source and checks target selectors and the
native damage-router binding against the installed game. It also tests framing
and routing, with test doubles explicitly identified. It does not execute every
Harmony patch or provide a live Unity/co-op result. Recheck the native boundary
when the game or reference assemblies change; do not invent fallback signatures.
