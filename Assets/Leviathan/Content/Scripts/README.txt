Leviathan Specialization Framework - Growth Point test build
============================================================

WHAT CHANGED
------------
This build is no longer read-only.

Native Star Vortex upgrade points remain fully native. A new normal Leviathan
upgrade, EVOLUTION (custom Upgrade.Key 87), costs one ordinary upgrade point per
rank and grants 3 Growth Points per rank.

Evolution ranks: 1 / 2 / 3 / 4 / 5
Growth Points:   3 / 6 / 9 / 12 / 15

Growth Points are derived from Evolution rank minus specialization node ranks
spent. There is no second mutable currency counter that can desync.

The Starfire specialization window is still opened with F10 for this test build.
Each specialization node rank costs 1 Growth Point.

PERSISTENCE
-----------
Specialization selections are saved under:

  Application.persistentDataPath/LeviathanSpecializations/

The file is keyed from the current Player save metadata UID, with the save file
as a fallback. The previous Pilot-reflection read-only safety path is gone.

Evolution itself is a native Upgrade, so its rank is persisted by Star Vortex in
Pilot.upgradeUnlocks like the other Leviathan skills.

SAFE REFUNDS
------------
Evolution cannot be refunded below the Growth Point capacity currently required
by invested specialization nodes.

Example:
  Evolution 2 = 6 Growth Points
  5 points invested in specialization nodes
  refunding Evolution 2 -> 1 would leave only 3 points, so the refund is blocked.

A native Reset Upgrades also resets/persists the specialization web.

STARFIRE TEST VALUES
--------------------
The existing tree definition is now live against Starfire gameplay.

Expanded Lungs (3):
  +10% Width / rank

Long Reach (3):
  +10% Length / rank

Sustained Flame (3):
  +10% Duration / rank
  -3% Damage / rank
  Requires Expanded Lungs 1 OR Long Reach 1

Deep Breath (1):
  +25% Width
  +20% Length
  +25% Duration
  +20% Damage
  +0.75 sec startup wind-up
  Requires Expanded Lungs 2 AND Long Reach 2 AND Sustained Flame 2
  Exclusive with Steady Breathing / Forceful Exhalation

Rapid Recovery (3):
  -10% Recharge Time / rank

Measured Breath (3):
  +8% Duration / rank
  -6% Recharge Time / rank
  -4% Damage / rank
  Requires Rapid Recovery 1 OR Sustained Flame 1

Steady Breathing (1):
  -10% Width
  -8% Length
  -8% Damage
  +30% Duration
  -20% Recharge Time
  Requires Rapid Recovery 2 AND Measured Breath 2
  Exclusive with Deep Breath / Forceful Exhalation

Pressure (3):
  +12% Damage / rank
  -6% Duration / rank

Searing Breath (3):
  +6% Damage / rank
  +8% Debuff Chance / rank
  Requires Pressure 1 OR Measured Breath 1

Violent Release (2):
  +15% Damage / rank
  -12% Duration / rank
  +5% Debuff Chance / rank
  Requires Pressure 2 AND Searing Breath 1

Forceful Exhalation (1):
  +25% Damage
  -35% Duration
  First ~22% of Starfire's charge/collapse cycle gets another 1.60x burst multiplier
  Requires Pressure 3 AND Violent Release 2
  Exclusive with Deep Breath / Steady Breathing

STAT MAPPING
------------
Starfire now exposes named knobs directly through LeviathanStarfireKnobs.cs.

Relative-percent knobs include Width, Length, Damage, Heat Generation,
Charge Ramp Speed and overall Breath Duration.

Critical Chance, Critical Damage and Status Chance use additive percentage
points. Example: +5% Critical Chance changes a 10% source value to 15%.

Breath timing is independently addressable through Full-Size Hold, Retreat
Duration, Minimum Breath Length, Retreat Curve and Startup Delay. Duration is
an overall multiplier on both hold and retreat time; it no longer changes
charge-ramp speed.

Native Activatable recovery can be changed together with RechargeTime or
independently through Cooldown and RechargeSeconds.

Cone/hitbox/visual knobs also expose fan angle, muzzle width, hitbox width
scale, center-length shaping, opacity, beam fill, minimum beam width, end
feather, beam count and visual length-segment count.

FILES
-----
LeviathanSpecializationCore.cs
  Generic nodes, nested AND/OR requirements, exclusivity, refund validation,
  stat effects, automatic graph layout, cycle detection, registry.

LeviathanStarfireTree.cs
  Data-only Starfire tree definition. This is where most future node/branch
  edits should happen.

LeviathanSpecializationRuntime.cs
  Per-pilot tree state, Growth Point bank, save/load, modifier/flag lookup.

LeviathanSpecializationUI.cs
  Generated F10 tree UI.

LeviathanSpecializationCurrency.cs
  Native Evolution skill registration/injection, metadata, downgrade safety,
  native reset integration.

LeviathanStarfireSpecializationBridge.cs
  Hooks generic specialization stats/flags into current Starfire mechanics.

ADDING A NORMAL NODE
--------------------
Example:

  tree.Add(new LeviathanSpecializationNode(
      "compressed_plasma",
      "Compressed Plasma",
      3,
      LeviathanSpecializationNodeType.Major,
      LeviathanReq.Any(
          LeviathanReq.Rank("pressure", 2),
          LeviathanReq.Rank("searing_breath", 1)
      ),
      null,
      "Concentrates the breath.",
      LeviathanSpecializationEffect.Percent(Stats.Damage, 0.12f),
      LeviathanSpecializationEffect.Percent(Stats.Width, -0.08f),
      LeviathanSpecializationEffect.Percent(Stats.DebuffChance, 0.05f)
  ));

No renderer coordinates or line definitions are required. The prerequisite graph
regenerates its layout automatically.

INSTALL / TEST
--------------
Replace the four files from the previous specialization prototype and ADD the two
new files:

  LeviathanSpecializationCore.cs
  LeviathanStarfireTree.cs
  LeviathanSpecializationRuntime.cs
  LeviathanSpecializationUI.cs
  LeviathanSpecializationCurrency.cs
  LeviathanStarfireSpecializationBridge.cs

Keep your current LeviathanStarfire.cs and LeviathanSkills.cs. This test build
hooks them rather than requiring destructive edits.

Then:
1. Compile.
2. Open Leviathan's native skill list and confirm Evolution appears.
3. Buy one Evolution rank: it should consume one normal native skill point.
4. Press F10. Header should show 3 Growth Points granted.
5. Spend nodes and verify Growth Points fall.
6. Fire Starfire and check width/length/duration/damage behavior.
7. Save/load and verify specialization ranks return.
8. Try refunding Evolution while too many Growth Points are committed; it should
   be blocked until enough specialization ranks are refunded.

This is still a test/prototype presentation: F10 is temporary, the numbers are
first-pass test values, and the native Evolution skill can later be integrated
straight into LeviathanSkills.cs rather than injected by a bridge patch.
