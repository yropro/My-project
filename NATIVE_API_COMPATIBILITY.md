# Native API compatibility note

On 2026-09-13 Star Vortex changed `NetCombat.RouteDamage` parameter 5 (critical hit) from `bool` to `int`, while this Unity project still had an older game reference. Predator's exact delegate lookup therefore returned null during static initialization and prevented the mod/classes from finishing startup. Other reflected damage callers would have failed when used.

Current rule: after a Star Vortex update, verify native reflection/delegate bindings against the **installed game's current Assembly-CSharp**, not only the Unity/editor reference. RouteDamage callers should validate the full overload signature. Existing binary crit decisions cross the current native boundary as `0` / `1`.

The 2026-09-13 migration updated Predator, OrreryDamageRouter, Starfire, Constrictor, Stellar Converter, and CoreCombat's RouteDamage selector.

## Core timed/cross-owner effect hooks

Cold Fusion introduced two reusable Core boundaries whose native assumptions should be rechecked after a game update:

- `CoreTimedShipEffects` composes temporary effects through `GameShip.ApplyModifier(Modifier.Type, Item.Category, float, bool)`, `Launcher.ReloadTime`, `Thruster.AccelerationFactor`, `Thruster.DodgeFactor`, `Thruster.BoostFactor`, `Thruster.ControlMultiplier`, `GameShip.UpdateHeat`, `Ship.GetClassBoostHeat`, and `Ship.GetClassDisplaceHeat`.
- `CoreCrossOwnerEffects` intercepts the private `NetSession.DispatchAsHost(ConnKey, NetMessageType, string)` and `DispatchAsClient(ConnKey, NetMessageType, string)` methods and reads the private `connToPlayer` map to validate the real sender before forwarding owner-to-owner gameplay intents.
- Cross-owner Core gameplay currently reserves `NetMessageType` byte value **126**. The runtime fails closed if the installed game later defines that enum value. `NetProtocol` currently reserves only bit 7 (`128`) as its gzip flag and masks message ids with `127`, so 126 is the highest usable uncompressed/custom message id under the present protocol.
- `NetSession.SendStarEntityMessage` currently uses `ReliableOrdered` transport for these intents. Do not move cross-owner gameplay onto `CoreNetwork`'s dynamic slots; those slots remain presentation-only by contract.

The installed 2026-09-13 Assembly-CSharp was used to verify these signatures and protocol assumptions when Cold Fusion was added.
