# Native API compatibility

**Scope:** installed-game integration. Reviewed 2026-09-14.
Start with [Skill development](SKILL_DEVELOPMENT.md) for the other development guides.

## Evidence before bindings

The failures investigated on 2026-09-13 exposed a mismatch between the installed
Star Vortex assembly and the Unity project's older reference. This investigation
date is not a verified game-release date. An exact delegate lookup returned null
and prevented class initialization; other callers still used old parameter types.

Check reflected targets and full overload signatures against the installed game's
`Star Vortex_Data/Managed/Assembly-CSharp.dll`. Compilation against the editor
reference alone does not establish runtime compatibility. Do not copy old method
signatures from the historical precompile audit.

## Developer reference and build environment

The [developer's modding reference](<../starvortex/Notes_Readme_Documentation/MODDING_REFERENCE.md>)
is a verbatim external source for v0.8.21, generated 2026-09-14. It identifies Mono,
URP 2D and Unity 2022.3.62f2, requiring the same Unity version for AssetBundles.
The local standalone test runner currently defaults to 2022.3.62f1; a successful
DLL check there does not validate f2 bundle packaging. Do not silently upgrade the
project or treat compiler success as proof of compatible assets.

DLL initialization precedes bundle loading. A failed `Init`/Harmony patch marks
the mod as errored and prevents its bundles loading, explaining missing classes
and missing-mod save warnings after a DLL load failure. Do not access Main-scene
singletons during Splash initialization. Load runtime content through `ModContent`
to honor overrides. The SDK uses an asmdef and generated `components.json` to
restore native components; missing native scripts in the mod editor can be expected.

That reference deliberately does not repeat signatures; inspect the installed
assembly for those. The older decompile in the notes still has boolean critical
arguments and is not evidence of the installed ABI.

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

## Accretion barrier bindings (2026-09-15)

Audited against supplied installed Assembly-CSharp.dll:
MVID `dacecd9e-6ef3-4df5-8118-67cde157a00b`, SHA-256
`971e8a13a525ce3fab52074ae1b73926626e533671e696c39b84c84049d93f5b`.

`CoreIncomingDamage` validates the unique direct base-call argument shape in
GameShip.Damage (integer critical tier) and GameShip.DirectDamage. A whole-hit veto
returns before native defenses and the enclosing received-hit tail. Separately,
NetCombat.ApplyDamageEvent and ImpaleMissile.HitObject have tails outside that
override; the shared application watch suppresses only a vetoed first application.
The network tail gate remains after ClearPendingKillContext.

`CoreProjectileSweep` validates native Projectile/FuzzyProjectile query, reflect
and virtual HitObject sites. `CoreProjectileSpawnGuard` covers Projectile.Init's
pre-registration collision sweep and Launcher.ShootProjectile completion. Native
CaptureDestroy calls NotifyNetDespawn and AutoDestroy.ScheduleDestroy directly,
bypassing the ordinary Projectile.ScheduleDestroy explosion override. Fired captured
shot valuation uses the verified `capturedDamagePercentage` and
`currentShotBonusDamage` float fields. PoolDestroy/ResetObject invalidate holds.

`AccretionNativeTests` checks these real method bodies, signatures, ordering and
independent wire bytes. A changed MVID deliberately requires re-auditing rather
than accepting old selector evidence. Tests transform actual native IL but do not
execute Unity physics, render placeholders, or certify Harmony patch execution in
the game's Mono runtime. ProjectVersion.txt and bundle versions are unchanged.
