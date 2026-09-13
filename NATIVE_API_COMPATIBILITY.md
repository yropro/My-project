# Native API compatibility note

On 2026-09-13 Star Vortex changed `NetCombat.RouteDamage` parameter 5 (critical hit) from `bool` to `int`, while this Unity project still had an older game reference. Predator's exact delegate lookup therefore returned null during static initialization and prevented the mod/classes from finishing startup. Other reflected damage callers would have failed when used.

Current rule: after a Star Vortex update, verify native reflection/delegate bindings against the **installed game's current Assembly-CSharp**, not only the Unity/editor reference. RouteDamage callers should validate the full overload signature. Existing binary crit decisions cross the current native boundary as `0` / `1`.

The 2026-09-13 migration updated Predator, OrreryDamageRouter, Starfire, Constrictor, Stellar Converter, and CoreCombat's RouteDamage selector.
