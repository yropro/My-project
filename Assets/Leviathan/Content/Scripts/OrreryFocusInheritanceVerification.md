# Orrery focus inheritance verification target

Implemented on `orrery-focus-inheritance`:

- donor modifier cloning except `Modifier.damageTypes`
- donor customizer inheritance except `Customizer.Type.AdjustDamage`
- shared writable `Base*` stat/property inheritance except `BaseDamage`
- no per-spell stat whitelist
- OrrerySpellPower remains raw spell-damage authority

Still requires compile and runtime validation. In particular, inherited ShotCount can expose the existing single-fireball tracking assumption in FF and must be widened before declaring full inheritance runtime-complete.
