using HarmonyLib;
using StarVortex;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using static StarVortex.Damageable;

/// <summary>
/// Starfire transforms one equipped Torch Primary weapon into a broad Leviathan
/// breath attack. The first equipped Primary Torch in native slot order is the source;
/// all other equipped Primary Torches are suppressed while Starfire is active.
///
/// The source Torch remains the source of truth for native damage packet
/// construction, damage type, legendary/customizer behavior, native
/// charge/tick timing and attribution. Starfire reshapes the native spike and
/// exposes neutral tuning hooks for damage, crits, debuffs and future charge
/// profiles while scaling heat/geometry by rank.
/// </summary>
public static class LeviathanStarfireRuntime
{
    // =========================================================================
    // BALANCE TUNING
    // =========================================================================
    // Arrays are Rank 1 -> Rank 5. These are the main Starfire balance knobs.

    // Multiplier on native Torch heat generation from the one Starfire source.
    // Rank 1 starts at double heat. Rank 5 is 50% lower than that starting
    // burden and returns to the source Torch's native heat generation.
    private static readonly float[] HeatMultiplierByRank =
    {
        1.50f, // Rank 1
        1.45f, // Rank 2
        1.40f, // Rank 3
        1.35f, // Rank 4
        1.30f  // Rank 5
    };

    // Multiplier on the selected Torch's native MaxRange result. This is applied at
    // the same Equippable.ApplyModifier(MaxRange) boundary used by Striker, so
    // Torch's own charge, VFX length and collider length all inherit it natively.
    private static readonly float[] LengthMultiplierByRank =
    {
        1.15f, // Rank 1
        1.25f, // Rank 2
        1.3f, // Rank 3
        1.4f, // Rank 4
        1.5f  // Rank 5
    };

    // Multiplier on Starfire's base fan angle. The native Torch body stays at
    // Y scale 1; width is now encoded in one trapezoid collider plus cosmetic
    // beam strips. With BaseFanHalfAngleDegrees = 10, these values produce
    // half-angles of 13 / 15 / 17 / 18 / 20 degrees by rank.
    private static readonly float[] WidthMultiplierByRank =
    {
        1.30f, // Rank 1
        1.50f, // Rank 2
        1.70f, // Rank 3
        1.80f, // Rank 4
        2.00f  // Rank 5
    };

    // Cosmetic fan only. All strips share one generated mesh per native Torch
    // SpriteRenderer layer and never receive colliders or damage logic.
    private const int VisualBeamCount = 16;

    // Rank spread = this angle * WidthMultiplierByRank. This is the main cone
    // angle knob. Example: 10 degrees * Rank 5's 2.0 = 20 degree half-angle.
    private const float BaseFanHalfAngleDegrees = 15.0f;

    // Width at the muzzle as a fraction of the native Torch half-width. The
    // far end then expands according to the fan angle and current beam length.
    private const float MuzzleHalfWidthMultiplier = 0.65f;

    // 1.0 means neighboring cosmetic strips just touch at their center spacing.
    // Below 1 leaves visible seams; above 1 deliberately overlaps neighboring
    // strips. 1.35 is intentionally dense so ten beams read as one plume.
    private const float VisualBeamFillFraction = 1.6f;

    // Never let an individual strip become thinner than this fraction of the
    // native Torch half-width. 1.00 keeps each cosmetic strip approximately
    // native-beam thickness instead of squeezing ten skinny ribbons into the fan.
    private const float VisualMinimumHalfWidthMultiplier = 1.20f;

    // Slightly pad the single mechanical trapezoid beyond the visible fan to be
    // forgiving of motion/netcode without adding extra overlap queries.
    private const float HitboxWidthPaddingMultiplier = 1.05f;

    // Global opacity of the cosmetic Starfire plume. 1.00 is fully opaque;
    // 0.50 is half opacity. This affects visuals only, never hit detection.
    private const float VisualOpacity = 0.70f;

    // Terminal shaping shared by visuals and the single mechanical collider.
    // The outermost beam pair keeps the base breath length while progressively
    // more central pairs extend farther. With 10 beams, pair weights are
    // 0 / 25 / 50 / 75 / 100 percent of this bonus.
    private const float VisualCenterLengthBonusFraction = 0.10f;

    // Starfire AoE timing is intentionally independent of native Torch charge.
    // Arrays are Rank 1 -> Rank 5 so each rank can tune the sustained full-size
    // phase without changing the geometry/damage implementation.
    private static readonly float[] FullSizeHoldSecondsByRank =
    {
        2.00f, // Rank 1
        2.00f, // Rank 2
        2.00f, // Rank 3
        2.00f, // Rank 4
        2.00f  // Rank 5
    };

    // Time spent shrinking from full range to BreathMinimumLengthFraction.
    private static readonly float[] BreathRetreatSecondsByRank =
    {
        2.00f, // Rank 1
        2.00f, // Rank 2
        2.00f, // Rank 3
        2.00f, // Rank 4
        2.00f  // Rank 5
    };

    // Remaining longitudinal breath length after the retreat completes. 0.25
    // leaves one quarter of the initial breath range until the trigger is released.
    private const float BreathMinimumLengthFraction = 0.25f;

    // Shapes retreat progress after the full-size hold. 1 = linear; values above
    // 1 keep the breath large longer, then make it collapse faster near the end.
    private static readonly float[] BreathRetreatCurveExponentByRank =
    {
        2.00f, // Rank 1
        2.00f, // Rank 2
        2.00f, // Rank 3
        2.00f, // Rank 4
        2.00f  // Rank 5
    };

    // Only the terminal edge is alpha-feathered. The rest of the breath remains
    // at VisualOpacity for the full firing cycle. 0.10 softens the final 10% of
    // each cosmetic ribbon without narrowing it.
    private const float VisualEndFeatherFraction = 0.10f;

    // More longitudinal sections make the endpoint alpha feather smoother without
    // adding more Torch strips. This is still a very small cosmetic mesh.
    private const int VisualLengthSegments = 32;

    // Direct multiplier on the source Torch's native DamageData[] packet.
    // Neutral for now; future Starfire branches can change this independently.
    private static readonly float[] DamageMultiplierByRank =
    {
        1.10f, // Rank 1
        1.20f, // Rank 2
        1.30f, // Rank 3
        1.35f, // Rank 4
        1.40f  // Rank 5
    };

    // Multiplier on the equipped Torch's native crit chance. 1.00 preserves the
    // source weapon's exact native roll. Non-neutral values reroll only Starfire
    // hits and leave every other Torch/weapon untouched.
    private static readonly float[] CritChanceMultiplierByRank =
    {
        1.00f, // Rank 1
        1.00f, // Rank 2
        1.00f, // Rank 3
        1.00f, // Rank 4
        1.00f  // Rank 5
    };

    // Multiplier on the BONUS portion of native crit damage. Example: a native
    // +50% crit with this at 2.00 becomes +100%, not 2x the entire final hit.
    private static readonly float[] CritDamageMultiplierByRank =
    {
        1.00f, // Rank 1
        1.00f, // Rank 2
        1.00f, // Rank 3
        1.00f, // Rank 4
        1.00f  // Rank 5
    };

    // Multiplier on native Torch status/debuff proc chance. Damage type and the
    // actual status applied remain entirely inherited from the equipped Torch.
    private static readonly float[] DebuffChanceMultiplierByRank =
    {
        1.00f, // Rank 1
        1.00f, // Rank 2
        1.00f, // Rank 3
        1.00f, // Rank 4
        1.00f  // Rank 5
    };

    // Future Deep Breath / charge-profile hooks. These are intentionally neutral
    // today. StartupDelaySeconds is an explicit wind-up before Starfire can deal
    // damage; native charge/VFX can still build during that telegraph.
    private static readonly float[] StartupDelaySecondsByRank =
    {
        0.00f, // Rank 1
        0.00f, // Rank 2
        0.00f, // Rank 3
        0.00f, // Rank 4
        0.00f  // Rank 5
    };

    // Multiplier on native Torch charge speed. 2.00 = half native chargeTime;
    // 0.50 = double native chargeTime. Native charge still affects Torch damage and
    // weapon behavior, but Starfire AoE geometry now uses its separate breath timer.
    private static readonly float[] ChargeRampSpeedMultiplierByRank =
    {
        0.90f, // Rank 1
        0.80f, // Rank 2
        0.70f, // Rank 3
        0.60f, // Rank 4
        0.30f  // Rank 5
    };

    // =========================================================================
    // NATIVE ACCESS
    // =========================================================================

    private static readonly FieldInfo ParentShipField =
        AccessTools.Field(typeof(Equippable), "parentShip");

    private static readonly FieldInfo MainSpikeField =
        AccessTools.Field(typeof(Torch), "mainSpike");

    private static readonly FieldInfo MirrorSpikeField =
        AccessTools.Field(typeof(Torch), "mirrorSpike");

    // Verified native Torch fields: UpdatePower moves `charge` toward 0/1 at
    // deltaTime / chargeTime, and both UpdateSpikeScale/GetDamageData multiply by
    // `charge`. Temporarily changing chargeTime therefore cleanly changes ramp speed.
    private static readonly FieldInfo ChargeField =
        AccessTools.Field(typeof(Torch), "charge");

    private static readonly FieldInfo ChargeTimeField =
        AccessTools.Field(typeof(Torch), "chargeTime");

    // Native Torch.GetDamageData halves each packet whenever the equipped weapon
    // has a mirrorGameObject, because vanilla subsequently applies both spikes.
    // Starfire suppresses the mirror, so its one surviving breath recombines this.
    private static readonly FieldInfo MirrorGameObjectField =
        AccessTools.Field(typeof(Equippable), "mirrorGameObject");

    // GameShip caches the aggregate heat-per-second contribution of equipped
    // activatables in this field. GameShip.AddHeat() has no amount parameter; it
    // only opens a short heat-latency window. Starfire therefore adjusts this
    // cached rate only while GameShip.UpdateHeat() executes.
    private static readonly FieldInfo GameShipHeatPerSecondField =
        AccessTools.Field(typeof(GameShip), "heatPerSecond");

    private static readonly FieldInfo ActivatableActiveField =
        AccessTools.Field(typeof(Activatable), "active");

    private static readonly HashSet<Torch> TouchedTorches =
        new HashSet<Torch>();

    private static readonly HashSet<Torch> FullySuppressedTorches =
        new HashSet<Torch>();

    // Source Torch instance -> first native damage-attempt time for the current
    // charge cycle. Cleared once native charge falls back to zero.
    private static readonly Dictionary<Torch, float> StartupBeginTimes =
        new Dictionary<Torch, float>();

    private static bool warnedNoSpikeFields;
    private static bool warnedNoSpikeObject;
    private static bool warnedNoFanCollider;
    private static bool warnedNoFanVisual;

    private sealed class FanVisualLayer
    {
        public SpriteRenderer sourceRenderer;
        public bool sourceWasEnabled;
        public GameObject objectInstance;
        public Mesh mesh;
        public Material material;
        public float uMin;
        public float uMax;
        public float vMin;
        public float vMax;
        public float nativeHalfWidth;
    }

    private sealed class FanState
    {
        public object nativeSpike;
        public GameObject spikeObject;
        public Transform body;
        public FieldInfo colliderField;
        public Collider2D originalCollider;
        public bool originalColliderEnabled;
        public GameObject fanColliderObject;
        public PolygonCollider2D fanCollider;
        public float minX;
        public float maxX;
        public float centerY;
        public float baseHalfWidth;
        public float breathStartTime = -1f;
        public readonly List<FanVisualLayer> visualLayers =
            new List<FanVisualLayer>();
    }

    private static readonly Dictionary<Torch, FanState> FanStates =
        new Dictionary<Torch, FanState>();


    // Narrower context used only while the source Torch's native DoSpikeDamage
    // runs, so the DamageData[] RouteDamage packet can be scaled without touching
    // unrelated damage that may occur elsewhere during a Torch update.
    [ThreadStatic]
    private static Torch currentDamageTorch;

    [ThreadStatic]
    private static int damageContextDepth;

    // =========================================================================
    // STARFIRE STATE / SOURCE SELECTION
    // =========================================================================

    public static bool TryGetStarfireRank(GameShip player, out int rank)
    {
        rank = 0;

        if (player == null ||
            LeviathanMod.Controller == null ||
            !IsCurrentPlayer(player))
        {
            return false;
        }

        Pilot pilot = GameShip.GetPlayerSourcePilot(player);

        if (pilot == null ||
            pilot.GetUpgradeLevel(LeviathanMod.GrowthUpgrade) < 1 ||
            LeviathanMod.Controller.GetActiveSectionCount(player) <
                LeviathanGrowth.GetBodySegmentCountForRank(1) + 2)
        {
            return false;
        }

        rank = pilot.GetUpgradeLevel(LeviathanMod.StarfireUpgrade);
        return rank >= 1;
    }

    private static bool TryGetStarfireContext(
        Torch torch,
        out GameShip player,
        out int rank)
    {
        player = GetParentShip(torch);

        if (!IsPrimaryTorch(torch, player))
        {
            rank = 0;
            return false;
        }

        return TryGetStarfireRank(player, out rank);
    }

    private static GameShip GetParentShip(Torch torch)
    {
        if (torch == null || ParentShipField == null)
            return null;

        return ParentShipField.GetValue(torch) as GameShip;
    }

    private static Torch FindSourceTorch(GameShip player)
    {
        if (player == null || player.slots == null)
            return null;

        for (int i = 0; i < player.slots.Length; i++)
        {
            Slot slot = player.slots[i];

            if (slot == null ||
                slot.type != Item.Type.PrimaryWeapon ||
                slot.equippable == null)
            {
                continue;
            }

            Torch torch = slot.equippable as Torch;

            if (torch != null && torch.type == Item.Type.PrimaryWeapon)
                return torch;
        }

        return null;
    }

    private static bool IsPrimaryTorch(Torch torch, GameShip player)
    {
        if (torch == null ||
            player == null ||
            player.slots == null ||
            torch.type != Item.Type.PrimaryWeapon)
        {
            return false;
        }

        for (int i = 0; i < player.slots.Length; i++)
        {
            Slot slot = player.slots[i];

            if (slot == null ||
                slot.type != Item.Type.PrimaryWeapon ||
                slot.equippable == null)
            {
                continue;
            }

            if (ReferenceEquals(slot.equippable, torch))
                return true;
        }

        return false;
    }

    private static bool IsSourceTorch(Torch torch, GameShip player)
    {
        return torch != null &&
            player != null &&
            ReferenceEquals(FindSourceTorch(player), torch);
    }

    private static bool IsCurrentPlayer(GameShip player)
    {
        return player != null &&
            WorldController.instance != null &&
            WorldController.instance.GetCurrentPlayerShip() == player;
    }

    // =========================================================================
    // GEOMETRY / VISUAL SUPPRESSION
    // =========================================================================

    public static void RefreshTorchGeometry(Torch torch, bool applyScale)
    {
        if (torch == null)
            return;

        GameShip player;
        int rank;
        if (!TryGetStarfireContext(torch, out player, out rank))
        {
            RestoreStarfireFan(torch);

            if (TouchedTorches.Remove(torch))
            {
                if (FullySuppressedTorches.Remove(torch))
                    SetSpikeSuppressed(GetMainSpike(torch), false);

                SetSpikeSuppressed(GetMirrorSpike(torch), false);
            }

            return;
        }

        TouchedTorches.Add(torch);

        bool source = IsSourceTorch(torch, player);
        object mainSpike = GetMainSpike(torch);
        object mirrorSpike = GetMirrorSpike(torch);

        if (!source)
        {
            RestoreStarfireFan(torch);
            FullySuppressedTorches.Add(torch);
            SetSpikeSuppressed(mainSpike, true);
            SetSpikeSuppressed(mirrorSpike, true);
            return;
        }

        if (FullySuppressedTorches.Remove(torch))
            SetSpikeSuppressed(mainSpike, false);

        // Starfire still owns exactly one native damage spike. The mirror and
        // every extra Primary Torch remain mechanically suppressed.
        SetSpikeSuppressed(mirrorSpike, true);

        // Native Torch charge eases back toward zero after release. Starfire should
        // not remain visible during that discharge tail: releasing the trigger (or
        // being forced inactive by overheat) ends the breath immediately. Reset the
        // retreat cycle too, so a quick re-press begins at full Starfire range.
        bool firing = torch.IsActive();
        SetStarfireFanActive(torch, firing);

        if (!firing)
        {
            StartupBeginTimes.Remove(torch);
            return;
        }

        if (applyScale)
            ApplyStarfireFan(torch, mainSpike, rank);
    }

    private static object GetMainSpike(Torch torch)
    {
        if (MainSpikeField == null)
        {
            WarnNoSpikeFields();
            return null;
        }

        return MainSpikeField.GetValue(torch);
    }

    private static object GetMirrorSpike(Torch torch)
    {
        if (MirrorSpikeField == null)
            return null;

        return MirrorSpikeField.GetValue(torch);
    }

    private static void ApplyStarfireFan(
        Torch torch,
        object nativeSpike,
        int rank)
    {
        if (torch == null || nativeSpike == null)
            return;

        FanState state = EnsureStarfireFan(torch, nativeSpike);
        if (state == null)
            return;

        float fanHalfAngle = Mathf.Deg2Rad *
            BaseFanHalfAngleDegrees *
            GetRankValue(WidthMultiplierByRank, rank);

        float localLength = Mathf.Max(0.0001f, state.maxX - state.minX);
        float fullRangeScaleX = Mathf.Max(0.0001f, Mathf.Abs(torch.MaxRange));
        float x0 = 0f;
        float x1 = localLength;

        // Starfire uses its own sustained-breath clock rather than native Torch
        // charge for geometry. This lets the AoE stay fully open for a while, then
        // retreat on its own schedule while native charge remains free to drive the
        // Torch's normal damage/weapon behavior. Release/overheat resets this timer.
        if (state.breathStartTime < 0f)
            state.breathStartTime = Time.time;

        float elapsed = Mathf.Max(0f, Time.time - state.breathStartTime);
        float holdSeconds = Mathf.Max(
            0f,
            GetRankValue(FullSizeHoldSecondsByRank, rank)
        );
        float retreatSeconds = Mathf.Max(
            0f,
            GetRankValue(BreathRetreatSecondsByRank, rank)
        );
        float retreatProgress;

        if (elapsed <= holdSeconds)
        {
            retreatProgress = 0f;
        }
        else if (retreatSeconds <= 0.0001f)
        {
            retreatProgress = 1f;
        }
        else
        {
            retreatProgress = Mathf.Clamp01(
                (elapsed - holdSeconds) / retreatSeconds
            );
        }

        float curveExponent = Mathf.Max(
            0.01f,
            GetRankValue(BreathRetreatCurveExponentByRank, rank)
        );
        float curvedRetreatProgress = Mathf.Pow(
            retreatProgress,
            curveExponent
        );
        float minimumLengthFraction = Mathf.Clamp(
            BreathMinimumLengthFraction,
            0.01f,
            1f
        );
        float remainingLengthFraction = Mathf.Lerp(
            1f,
            minimumLengthFraction,
            curvedRetreatProgress
        );
        float breathScaleX = Mathf.Max(
            0.0001f,
            fullRangeScaleX * remainingLengthFraction
        );

        // Override the native growing X scale after Torch.UpdateSpikeScale. This one
        // transform drives the real collider range; generated visuals use the same
        // scale explicitly, so cosmetic and mechanical reach stay synchronized.
        Vector3 bodyScale = state.body.localScale;
        bodyScale.x = breathScaleX;
        bodyScale.y = 1f;
        state.body.localScale = bodyScale;

        float scaledLength = localLength * breathScaleX;
        float nearHalfWidth = Mathf.Max(
            state.baseHalfWidth * MuzzleHalfWidthMultiplier,
            state.baseHalfWidth * 0.01f
        );
        float farHalfWidth =
            nearHalfWidth + Mathf.Tan(fanHalfAngle) * scaledLength;
        farHalfWidth = Mathf.Max(farHalfWidth, nearHalfWidth);

        float cy = state.centerY;

        // Still exactly one PolygonCollider2D / one native OverlapCollider call.
        // Its points are derived from the current cosmetic beam silhouette, so
        // beam count, length staggering, fill/thickness and feather changes carry
        // into the approximate damage envelope automatically.
        state.fanCollider.points = BuildMechanicalFanPointsFromVisual(
            x0,
            x1,
            cy,
            nearHalfWidth,
            farHalfWidth,
            state.baseHalfWidth
        );

        state.originalCollider.enabled = false;
        state.fanCollider.enabled = true;

        if (state.colliderField != null &&
            !ReferenceEquals(
                state.colliderField.GetValue(state.nativeSpike),
                state.fanCollider))
        {
            state.colliderField.SetValue(
                state.nativeSpike,
                state.fanCollider
            );
        }

        UpdateFanVisuals(
            torch,
            state,
            nearHalfWidth,
            farHalfWidth,
            x0,
            x1,
            cy,
            breathScaleX
        );
    }

    private static Vector2[] BuildMechanicalFanPointsFromVisual(
        float x0,
        float x1,
        float centerY,
        float nearHalfWidth,
        float farHalfWidth,
        float nativeHalfWidth)
    {
        // Build the one mechanical polygon from the same beam-count, staggered
        // endpoint, beam-fill/thickness and terminal-feather knobs used by the
        // cosmetic plume. This keeps one cheap native overlap while making most
        // visual-shape tuning automatically reshape the damage envelope too.
        int beamCount = Mathf.Max(1, VisualBeamCount);
        float centerLengthBonus = Mathf.Max(
            0f,
            VisualCenterLengthBonusFraction
        );
        float endFeatherFraction = Mathf.Clamp(
            VisualEndFeatherFraction,
            0f,
            0.50f
        );
        float nearSpacing = beamCount > 1
            ? (nearHalfWidth * 2f) / (beamCount - 1)
            : nearHalfWidth * 2f;
        float farSpacing = beamCount > 1
            ? (farHalfWidth * 2f) / (beamCount - 1)
            : farHalfWidth * 2f;
        float safeNativeHalfWidth = Mathf.Max(
            0.0001f,
            nativeHalfWidth
        );
        float minimumHalfThickness =
            safeNativeHalfWidth * VisualMinimumHalfWidthMultiplier;
        float nearHalfThickness = Mathf.Max(
            minimumHalfThickness,
            nearSpacing * VisualBeamFillFraction * 0.5f
        );
        float farHalfThickness = Mathf.Max(
            minimumHalfThickness,
            farSpacing * VisualBeamFillFraction * 0.5f
        );

        // Treat the midpoint of the alpha-feather as the practical visible edge.
        // Geometry continues through a fully-transparent tail, but damage should
        // follow what the player can meaningfully see rather than that invisible
        // final sliver. SmoothStep is 50% alpha at the feather midpoint.
        float visibleEndT = 1f - endFeatherFraction * 0.5f;
        List<Vector2> points = new List<Vector2>(beamCount + 4);
        float nearOuterExtent =
            (nearHalfWidth + nearHalfThickness) *
            HitboxWidthPaddingMultiplier;

        points.Add(new Vector2(x0, centerY + nearOuterExtent));

        // Trace the terminal visual silhouette from upper outer beam through the
        // center and down to the lower outer beam. Endpoint depth, fan expansion
        // and ribbon thickness use the same formulas as UpdateFanVisuals().
        for (int beam = beamCount - 1; beam >= 0; beam--)
        {
            float beamT = beamCount == 1
                ? 0f
                : Mathf.Lerp(
                    -1f,
                    1f,
                    beam / (beamCount - 1f)
                );
            float centerLengthWeight = GetCenterLengthWeight(
                beam,
                beamCount
            );
            float beamLengthScale =
                1f + centerLengthBonus * centerLengthWeight;
            float fanT = visibleEndT * beamLengthScale;
            float terminalX = x0 +
                (x1 - x0) * fanT;
            float terminalHalfWidth =
                nearHalfWidth +
                (farHalfWidth - nearHalfWidth) * fanT;
            float terminalHalfThickness =
                nearHalfThickness +
                (farHalfThickness - nearHalfThickness) * fanT;
            float beamCenterY =
                centerY + terminalHalfWidth * beamT;

            if (Mathf.Abs(beamT) <= 0.0001f)
            {
                float paddedThickness =
                    terminalHalfThickness * HitboxWidthPaddingMultiplier;
                points.Add(new Vector2(
                    terminalX,
                    beamCenterY + paddedThickness
                ));
                points.Add(new Vector2(
                    terminalX,
                    beamCenterY - paddedThickness
                ));
            }
            else
            {
                float outerY = beamCenterY +
                    Mathf.Sign(beamT) * terminalHalfThickness;
                outerY = centerY +
                    (outerY - centerY) * HitboxWidthPaddingMultiplier;

                points.Add(new Vector2(terminalX, outerY));
            }
        }

        points.Add(new Vector2(x0, centerY - nearOuterExtent));
        return points.ToArray();
    }

    private static float GetCenterLengthWeight(int beam, int beamCount)
    {
        if (beamCount <= 1)
            return 1f;

        int pairDepth = Mathf.Min(
            beam,
            beamCount - 1 - beam
        );
        int maxPairDepth = Mathf.Max(
            1,
            (beamCount - 1) / 2
        );

        return pairDepth / (float)maxPairDepth;
    }

    private static FanState EnsureStarfireFan(
        Torch torch,
        object nativeSpike)
    {
        GameObject spikeObject = GetSpikeGameObject(nativeSpike);
        Transform body = GetSpikeBodyTransform(nativeSpike);

        if (spikeObject == null || body == null)
            return null;

        FanState existing;
        if (FanStates.TryGetValue(torch, out existing))
        {
            if (existing != null &&
                existing.spikeObject == spikeObject &&
                ReferenceEquals(existing.nativeSpike, nativeSpike) &&
                existing.fanCollider != null)
            {
                return existing;
            }

            CleanupFanState(existing);
            FanStates.Remove(torch);
        }

        FieldInfo colliderField =
            AccessTools.Field(nativeSpike.GetType(), "collider");
        Collider2D originalCollider = colliderField == null
            ? null
            : colliderField.GetValue(nativeSpike) as Collider2D;

        if (originalCollider == null)
            originalCollider =
                spikeObject.GetComponentInChildren<Collider2D>(true);

        float minX;
        float maxX;
        float centerY;
        float halfWidth;

        if (originalCollider == null ||
            !TryGetColliderLocalProfile(
                originalCollider,
                out minX,
                out maxX,
                out centerY,
                out halfWidth))
        {
            if (!warnedNoFanCollider)
            {
                warnedNoFanCollider = true;
                Debug.LogError(
                    "[Leviathan] Starfire could not resolve the native Torch " +
                    "collider profile; fan geometry will remain native."
                );
            }

            return null;
        }

        FanState state = new FanState();
        state.nativeSpike = nativeSpike;
        state.spikeObject = spikeObject;
        state.body = body;
        state.colliderField = colliderField;
        state.originalCollider = originalCollider;
        state.originalColliderEnabled = originalCollider.enabled;
        state.minX = minX;
        state.maxX = maxX;
        state.centerY = centerY;
        state.baseHalfWidth = halfWidth;

        // Add the polygon to an empty child, not the native SpriteRenderer object.
        // Unity otherwise attempts sprite-outline generation against Star Vortex's
        // non-readable textures when PolygonCollider2D is created at runtime.
        GameObject colliderObject =
            new GameObject("Starfire Fan Collider");
        colliderObject.layer = originalCollider.gameObject.layer;
        colliderObject.transform.SetParent(originalCollider.transform, false);
        colliderObject.transform.localPosition = Vector3.zero;
        colliderObject.transform.localRotation = Quaternion.identity;
        colliderObject.transform.localScale = Vector3.one;
        state.fanColliderObject = colliderObject;

        PolygonCollider2D fanCollider =
            colliderObject.AddComponent<PolygonCollider2D>();
        fanCollider.isTrigger = originalCollider.isTrigger;
        fanCollider.sharedMaterial = originalCollider.sharedMaterial;
        fanCollider.usedByEffector = originalCollider.usedByEffector;
        fanCollider.enabled = true;
        state.fanCollider = fanCollider;

        originalCollider.enabled = false;
        if (colliderField != null)
            colliderField.SetValue(nativeSpike, fanCollider);

        BuildFanVisualLayers(state);
        FanStates[torch] = state;
        return state;
    }

    private static void BuildFanVisualLayers(FanState state)
    {
        if (state == null || state.spikeObject == null)
            return;

        SpriteRenderer[] renderers =
            state.spikeObject.GetComponentsInChildren<SpriteRenderer>(true);

        for (int i = 0; i < renderers.Length; i++)
        {
            SpriteRenderer source = renderers[i];
            if (source == null || source.sprite == null)
                continue;

            Shader shader = source.sharedMaterial == null
                ? Shader.Find("Sprites/Default")
                : source.sharedMaterial.shader;

            if (shader == null)
                continue;

            FanVisualLayer layer = new FanVisualLayer();
            layer.sourceRenderer = source;
            layer.sourceWasEnabled = source.enabled;
            layer.nativeHalfWidth = GetRendererHalfWidthInBodySpace(
                state,
                source
            );

            // Fall back to the native collider profile only if this particular
            // sprite layer cannot report usable geometry. Normally the sprite
            // bounds are the correct source of truth for vanilla visual width.
            if (layer.nativeHalfWidth <= 0.0001f)
                layer.nativeHalfWidth = state.baseHalfWidth;

            GameObject meshObject =
                new GameObject("Starfire Fan Visual");
            meshObject.transform.SetParent(state.spikeObject.transform, false);
            meshObject.transform.localPosition = Vector3.zero;
            meshObject.transform.localRotation = Quaternion.identity;
            meshObject.transform.localScale = Vector3.one;
            layer.objectInstance = meshObject;

            MeshFilter filter = meshObject.AddComponent<MeshFilter>();
            MeshRenderer renderer = meshObject.AddComponent<MeshRenderer>();

            Mesh mesh = new Mesh();
            mesh.name = "Starfire Fan Mesh";
            mesh.MarkDynamic();
            layer.mesh = mesh;
            filter.sharedMesh = mesh;

            Material material = source.sharedMaterial == null
                ? new Material(shader)
                : new Material(source.sharedMaterial);
            layer.material = material;

            if (material.HasProperty("_MainTex"))
                material.mainTexture = source.sprite.texture;

            renderer.sharedMaterial = material;
            renderer.sortingLayerID = source.sortingLayerID;
            renderer.sortingOrder = source.sortingOrder;

            Vector2[] spriteUv = source.sprite.uv;
            layer.uMin = float.PositiveInfinity;
            layer.uMax = float.NegativeInfinity;
            layer.vMin = float.PositiveInfinity;
            layer.vMax = float.NegativeInfinity;

            for (int uvIndex = 0; uvIndex < spriteUv.Length; uvIndex++)
            {
                Vector2 value = spriteUv[uvIndex];
                layer.uMin = Mathf.Min(layer.uMin, value.x);
                layer.uMax = Mathf.Max(layer.uMax, value.x);
                layer.vMin = Mathf.Min(layer.vMin, value.y);
                layer.vMax = Mathf.Max(layer.vMax, value.y);
            }

            if (spriteUv.Length == 0)
            {
                layer.uMin = 0f;
                layer.uMax = 1f;
                layer.vMin = 0f;
                layer.vMax = 1f;
            }

            if (source.flipX)
            {
                float temp = layer.uMin;
                layer.uMin = layer.uMax;
                layer.uMax = temp;
            }

            if (source.flipY)
            {
                float temp = layer.vMin;
                layer.vMin = layer.vMax;
                layer.vMax = temp;
            }

            int beamCount = Mathf.Max(1, VisualBeamCount);
            int lengthSegments = Mathf.Max(2, VisualLengthSegments);
            int vertsPerBeam = (lengthSegments + 1) * 2;
            int trisPerBeam = lengthSegments * 6;
            Vector3[] vertices =
                new Vector3[beamCount * vertsPerBeam];
            Vector2[] uv = new Vector2[beamCount * vertsPerBeam];
            int[] triangles = new int[beamCount * trisPerBeam];
            Color[] colors = new Color[beamCount * vertsPerBeam];

            for (int beam = 0; beam < beamCount; beam++)
            {
                int vBase = beam * vertsPerBeam;
                int triBase = beam * trisPerBeam;

                for (int section = 0;
                     section <= lengthSegments;
                     section++)
                {
                    float t = section / (float)lengthSegments;
                    float u = Mathf.Lerp(layer.uMin, layer.uMax, t);
                    int v = vBase + section * 2;

                    uv[v + 0] = new Vector2(u, layer.vMax);
                    uv[v + 1] = new Vector2(u, layer.vMin);
                    colors[v + 0] = source.color;
                    colors[v + 1] = source.color;
                }

                for (int section = 0;
                     section < lengthSegments;
                     section++)
                {
                    int v = vBase + section * 2;
                    int tri = triBase + section * 6;

                    triangles[tri + 0] = v + 0;
                    triangles[tri + 1] = v + 1;
                    triangles[tri + 2] = v + 2;
                    triangles[tri + 3] = v + 2;
                    triangles[tri + 4] = v + 1;
                    triangles[tri + 5] = v + 3;
                }
            }

            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = triangles;
            mesh.colors = colors;
            mesh.RecalculateBounds();

            source.enabled = false;
            state.visualLayers.Add(layer);
        }

        if (state.visualLayers.Count == 0 && !warnedNoFanVisual)
        {
            warnedNoFanVisual = true;
            Debug.LogWarning(
                "[Leviathan] Starfire did not find a SpriteRenderer on the " +
                "Torch spike prefab. The fan hitbox will still work, but the " +
                "cosmetic beam fan cannot be generated automatically."
            );
        }
    }

    private static void UpdateFanVisuals(
        Torch torch,
        FanState state,
        float nearHalfWidth,
        float farHalfWidth,
        float x0,
        float x1,
        float centerY,
        float breathScaleX)
    {
        if (torch == null ||
            state == null ||
            state.spikeObject == null ||
            state.body == null)
        {
            return;
        }

        int beamCount = Mathf.Max(1, VisualBeamCount);
        int lengthSegments = Mathf.Max(2, VisualLengthSegments);
        int vertsPerBeam = (lengthSegments + 1) * 2;
        float nearSpacing = beamCount > 1
            ? (nearHalfWidth * 2f) / (beamCount - 1)
            : nearHalfWidth * 2f;
        float farSpacing = beamCount > 1
            ? (farHalfWidth * 2f) / (beamCount - 1)
            : farHalfWidth * 2f;

        float centerLengthBonus = Mathf.Max(
            0f,
            VisualCenterLengthBonusFraction
        );
        float endFeatherFraction = Mathf.Clamp(
            VisualEndFeatherFraction,
            0.001f,
            0.50f
        );
        float endFeatherStart = 1f - endFeatherFraction;

        for (int layerIndex = 0;
             layerIndex < state.visualLayers.Count;
             layerIndex++)
        {
            FanVisualLayer layer = state.visualLayers[layerIndex];
            if (layer == null || layer.mesh == null)
                continue;

            if (layer.sourceRenderer != null)
                layer.sourceRenderer.enabled = false;

            // Fan spacing is expressed in native collider/body coordinates while
            // cosmetic strips display the actual Torch sprite. Correct thickness
            // by real vanilla sprite width so 1.00 remains one vanilla beam wide.
            float nativeHalfWidth = Mathf.Max(
                0.0001f,
                layer.nativeHalfWidth
            );
            float colliderHalfWidth = Mathf.Max(
                0.0001f,
                state.baseHalfWidth
            );
            float visualWidthScale = nativeHalfWidth / colliderHalfWidth;

            float minimumHalfThickness =
                nativeHalfWidth * VisualMinimumHalfWidthMultiplier;
            float nearHalfThickness = Mathf.Max(
                minimumHalfThickness,
                nearSpacing * VisualBeamFillFraction * 0.5f *
                    visualWidthScale
            );
            float farHalfThickness = Mathf.Max(
                minimumHalfThickness,
                farSpacing * VisualBeamFillFraction * 0.5f *
                    visualWidthScale
            );

            Vector3[] vertices = layer.mesh.vertices;
            Color[] colors = layer.mesh.colors;
            if (vertices == null ||
                vertices.Length != beamCount * vertsPerBeam)
            {
                continue;
            }

            if (colors == null || colors.Length != vertices.Length)
                colors = new Color[vertices.Length];

            Color sourceColor = layer.sourceRenderer == null
                ? Color.white
                : layer.sourceRenderer.color;
            float baseAlpha =
                sourceColor.a * Mathf.Clamp01(VisualOpacity);

            for (int beam = 0; beam < beamCount; beam++)
            {
                float beamT = beamCount == 1
                    ? 0f
                    : Mathf.Lerp(
                        -1f,
                        1f,
                        beam / (beamCount - 1f)
                    );
                // Shape the plume's terminal silhouette by staggering whole-beam
                // lengths rather than narrowing/fading the end of each ribbon.
                // For 10 beams, pairDepth is 0/1/2/3/4/4/3/2/1/0, matching the
                // desired outer +0 through center +4 style progression.
                float centerLengthWeight = GetCenterLengthWeight(
                    beam,
                    beamCount
                );
                float beamLengthScale =
                    1f + centerLengthBonus * centerLengthWeight;
                float beamEndX =
                    x0 + (x1 - x0) * beamLengthScale;

                int vBase = beam * vertsPerBeam;

                for (int section = 0;
                     section <= lengthSegments;
                     section++)
                {
                    float t = section / (float)lengthSegments;
                    float fanT = t * beamLengthScale;
                    float x = Mathf.Lerp(x0, beamEndX, t);
                    float halfWidth =
                        nearHalfWidth +
                        (farHalfWidth - nearHalfWidth) * fanT;
                    float halfThickness =
                        nearHalfThickness +
                        (farHalfThickness - nearHalfThickness) * fanT;
                    float center = centerY + halfWidth * beamT;

                    // Keep brightness static across the active plume. Only the
                    // last portion of each ribbon fades to transparent, which
                    // softens the stepped ten-beam silhouette without narrowing
                    // the ribbon or adding more cosmetic Torch strips.
                    float endAlpha = t <= endFeatherStart
                        ? 1f
                        : 1f - Mathf.SmoothStep(
                            0f,
                            1f,
                            (t - endFeatherStart) / endFeatherFraction
                        );

                    float alpha = baseAlpha * endAlpha;
                    int v = vBase + section * 2;

                    vertices[v + 0] =
                        BodyPointToSpikeLocalWithXScale(
                            state,
                            new Vector3(
                                x,
                                center + halfThickness,
                                0f
                            ),
                            breathScaleX
                        );
                    vertices[v + 1] =
                        BodyPointToSpikeLocalWithXScale(
                            state,
                            new Vector3(
                                x,
                                center - halfThickness,
                                0f
                            ),
                            breathScaleX
                        );

                    Color vertexColor = sourceColor;
                    vertexColor.a = alpha;
                    colors[v + 0] = vertexColor;
                    colors[v + 1] = vertexColor;
                }
            }

            layer.mesh.vertices = vertices;
            layer.mesh.colors = colors;
            layer.mesh.RecalculateBounds();
        }
    }

    private static float GetRendererHalfWidthInBodySpace(
        FanState state,
        SpriteRenderer renderer)
    {
        if (state == null ||
            state.body == null ||
            renderer == null ||
            renderer.sprite == null)
        {
            return 0f;
        }

        Bounds bounds = renderer.sprite.bounds;
        Vector3 min = bounds.min;
        Vector3 max = bounds.max;

        Vector3[] corners = new Vector3[]
        {
            new Vector3(min.x, min.y, 0f),
            new Vector3(min.x, max.y, 0f),
            new Vector3(max.x, min.y, 0f),
            new Vector3(max.x, max.y, 0f)
        };

        float lowY = float.PositiveInfinity;
        float highY = float.NegativeInfinity;

        for (int i = 0; i < corners.Length; i++)
        {
            Vector3 world = renderer.transform.TransformPoint(corners[i]);
            Vector3 bodyLocal = state.body.InverseTransformPoint(world);
            lowY = Mathf.Min(lowY, bodyLocal.y);
            highY = Mathf.Max(highY, bodyLocal.y);
        }

        if (float.IsInfinity(lowY) || float.IsInfinity(highY))
            return 0f;

        return Mathf.Max(0f, (highY - lowY) * 0.5f);
    }

    private static Vector3 BodyPointToSpikeLocal(
        FanState state,
        Vector3 bodyLocalPoint)
    {
        Vector3 world = state.body.TransformPoint(bodyLocalPoint);
        return state.spikeObject.transform.InverseTransformPoint(world);
    }

    private static Vector3 BodyPointToSpikeLocalWithXScale(
        FanState state,
        Vector3 bodyLocalPoint,
        float xScale)
    {
        if (state == null || state.body == null || state.spikeObject == null)
            return Vector3.zero;

        Transform body = state.body;
        Vector3 scale = body.localScale;
        scale.x = xScale;

        Vector3 parentLocal =
            body.localPosition +
            body.localRotation * Vector3.Scale(bodyLocalPoint, scale);
        Vector3 world = body.parent == null
            ? parentLocal
            : body.parent.TransformPoint(parentLocal);

        return state.spikeObject.transform.InverseTransformPoint(world);
    }

    private static void SetStarfireFanActive(Torch torch, bool active)
    {
        if (torch == null)
            return;

        FanState state;
        if (!FanStates.TryGetValue(torch, out state) || state == null)
            return;

        if (!active)
            state.breathStartTime = -1f;
        else if (state.breathStartTime < 0f)
            state.breathStartTime = Time.time;

        if (state.fanCollider != null)
            state.fanCollider.enabled = active;

        for (int i = 0; i < state.visualLayers.Count; i++)
        {
            FanVisualLayer layer = state.visualLayers[i];
            if (layer != null && layer.objectInstance != null)
                layer.objectInstance.SetActive(active);
        }
    }

    public static void RestoreStarfireFan(Torch torch)
    {
        if (torch == null)
            return;

        FanState state;
        if (!FanStates.TryGetValue(torch, out state))
            return;

        CleanupFanState(state);
        FanStates.Remove(torch);
    }

    public static void ForgetStarfireFan(Torch torch)
    {
        RestoreStarfireFan(torch);
    }

    private static void CleanupFanState(FanState state)
    {
        if (state == null)
            return;

        if (state.colliderField != null &&
            state.nativeSpike != null &&
            state.originalCollider != null)
        {
            try
            {
                state.colliderField.SetValue(
                    state.nativeSpike,
                    state.originalCollider
                );
            }
            catch
            {
            }
        }

        if (state.originalCollider != null)
            state.originalCollider.enabled = state.originalColliderEnabled;

        if (state.fanCollider != null)
            state.fanCollider.enabled = false;

        if (state.fanColliderObject != null)
            UnityEngine.Object.Destroy(state.fanColliderObject);

        for (int i = 0; i < state.visualLayers.Count; i++)
        {
            FanVisualLayer layer = state.visualLayers[i];
            if (layer == null)
                continue;

            if (layer.sourceRenderer != null)
                layer.sourceRenderer.enabled = layer.sourceWasEnabled;

            if (layer.mesh != null)
                UnityEngine.Object.Destroy(layer.mesh);
            if (layer.material != null)
                UnityEngine.Object.Destroy(layer.material);
            if (layer.objectInstance != null)
                UnityEngine.Object.Destroy(layer.objectInstance);
        }

        state.visualLayers.Clear();
    }

    private static bool TryGetColliderLocalProfile(
        Collider2D collider,
        out float minX,
        out float maxX,
        out float centerY,
        out float halfWidth)
    {
        minX = 0f;
        maxX = 0f;
        centerY = 0f;
        halfWidth = 0f;

        if (collider == null)
            return false;

        BoxCollider2D box = collider as BoxCollider2D;
        if (box != null)
        {
            minX = box.offset.x - box.size.x * 0.5f;
            maxX = box.offset.x + box.size.x * 0.5f;
            centerY = box.offset.y;
            halfWidth = Mathf.Abs(box.size.y) * 0.5f;
            return maxX > minX && halfWidth > 0f;
        }

        CapsuleCollider2D capsule = collider as CapsuleCollider2D;
        if (capsule != null)
        {
            minX = capsule.offset.x - capsule.size.x * 0.5f;
            maxX = capsule.offset.x + capsule.size.x * 0.5f;
            centerY = capsule.offset.y;
            halfWidth = Mathf.Abs(capsule.size.y) * 0.5f;
            return maxX > minX && halfWidth > 0f;
        }

        CircleCollider2D circle = collider as CircleCollider2D;
        if (circle != null)
        {
            minX = circle.offset.x - circle.radius;
            maxX = circle.offset.x + circle.radius;
            centerY = circle.offset.y;
            halfWidth = Mathf.Abs(circle.radius);
            return maxX > minX && halfWidth > 0f;
        }

        PolygonCollider2D polygon = collider as PolygonCollider2D;
        if (polygon != null && polygon.pathCount > 0)
        {
            float lowX = float.PositiveInfinity;
            float highX = float.NegativeInfinity;
            float lowY = float.PositiveInfinity;
            float highY = float.NegativeInfinity;

            for (int pathIndex = 0;
                 pathIndex < polygon.pathCount;
                 pathIndex++)
            {
                Vector2[] path = polygon.GetPath(pathIndex);
                for (int i = 0; i < path.Length; i++)
                {
                    Vector2 point = path[i] + polygon.offset;
                    lowX = Mathf.Min(lowX, point.x);
                    highX = Mathf.Max(highX, point.x);
                    lowY = Mathf.Min(lowY, point.y);
                    highY = Mathf.Max(highY, point.y);
                }
            }

            if (!float.IsInfinity(lowX) &&
                highX > lowX &&
                highY > lowY)
            {
                minX = lowX;
                maxX = highX;
                centerY = (lowY + highY) * 0.5f;
                halfWidth = (highY - lowY) * 0.5f;
                return true;
            }
        }

        EdgeCollider2D edge = collider as EdgeCollider2D;
        if (edge != null && edge.points != null && edge.points.Length > 1)
        {
            float lowX = float.PositiveInfinity;
            float highX = float.NegativeInfinity;
            float lowY = float.PositiveInfinity;
            float highY = float.NegativeInfinity;

            for (int i = 0; i < edge.points.Length; i++)
            {
                Vector2 point = edge.points[i] + edge.offset;
                lowX = Mathf.Min(lowX, point.x);
                highX = Mathf.Max(highX, point.x);
                lowY = Mathf.Min(lowY, point.y);
                highY = Mathf.Max(highY, point.y);
            }

            if (highX > lowX && highY > lowY)
            {
                minX = lowX;
                maxX = highX;
                centerY = (lowY + highY) * 0.5f;
                halfWidth = (highY - lowY) * 0.5f;
                return true;
            }
        }

        Bounds bounds = collider.bounds;
        Vector3[] worldCorners =
        {
            new Vector3(
                bounds.min.x,
                bounds.min.y,
                collider.transform.position.z),
            new Vector3(
                bounds.min.x,
                bounds.max.y,
                collider.transform.position.z),
            new Vector3(
                bounds.max.x,
                bounds.min.y,
                collider.transform.position.z),
            new Vector3(
                bounds.max.x,
                bounds.max.y,
                collider.transform.position.z)
        };

        float fallbackMinX = float.PositiveInfinity;
        float fallbackMaxX = float.NegativeInfinity;
        float fallbackMinY = float.PositiveInfinity;
        float fallbackMaxY = float.NegativeInfinity;

        for (int i = 0; i < worldCorners.Length; i++)
        {
            Vector3 local =
                collider.transform.InverseTransformPoint(worldCorners[i]);
            fallbackMinX = Mathf.Min(fallbackMinX, local.x);
            fallbackMaxX = Mathf.Max(fallbackMaxX, local.x);
            fallbackMinY = Mathf.Min(fallbackMinY, local.y);
            fallbackMaxY = Mathf.Max(fallbackMaxY, local.y);
        }

        if (fallbackMaxX <= fallbackMinX ||
            fallbackMaxY <= fallbackMinY)
        {
            return false;
        }

        minX = fallbackMinX;
        maxX = fallbackMaxX;
        centerY = (fallbackMinY + fallbackMaxY) * 0.5f;
        halfWidth = (fallbackMaxY - fallbackMinY) * 0.5f;
        return halfWidth > 0f;
    }

    private static Transform GetSpikeBodyTransform(object nativeSpike)
    {
        if (nativeSpike == null)
            return null;

        FieldInfo bodyField = AccessTools.Field(nativeSpike.GetType(), "body");
        if (bodyField == null)
            return null;

        return bodyField.GetValue(nativeSpike) as Transform;
    }

    private static void SetSpikeSuppressed(object nativeSpike, bool suppressed)
    {
        GameObject spikeObject = GetSpikeGameObject(nativeSpike);
        if (spikeObject == null)
            return;

        Renderer[] renderers =
            spikeObject.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i] != null)
                renderers[i].enabled = !suppressed;
        }

        Collider2D[] colliders =
            spikeObject.GetComponentsInChildren<Collider2D>(true);

        for (int i = 0; i < colliders.Length; i++)
        {
            if (colliders[i] != null)
                colliders[i].enabled = !suppressed;
        }
    }

    private static GameObject GetSpikeGameObject(object nativeSpike)
    {
        if (nativeSpike == null)
            return null;

        Type type = nativeSpike.GetType();

        FieldInfo named = AccessTools.Field(type, "obj");
        GameObject result = GetGameObjectFromValue(
            named == null ? null : named.GetValue(nativeSpike)
        );

        if (result != null)
            return result;

        FieldInfo[] fields = type.GetFields(
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic
        );

        for (int i = 0; i < fields.Length; i++)
        {
            object value;

            try
            {
                value = fields[i].GetValue(nativeSpike);
            }
            catch
            {
                continue;
            }

            result = GetGameObjectFromValue(value);
            if (result != null)
                return result;
        }

        if (!warnedNoSpikeObject)
        {
            warnedNoSpikeObject = true;
            Debug.LogError(
                "[Leviathan] Starfire found Torch.Spike but could not resolve " +
                "its native GameObject; breath geometry will remain unchanged."
            );
        }

        return null;
    }

    private static GameObject GetGameObjectFromValue(object value)
    {
        GameObject gameObject = value as GameObject;
        if (gameObject != null)
            return gameObject;

        Component component = value as Component;
        return component == null ? null : component.gameObject;
    }

    private static void WarnNoSpikeFields()
    {
        if (warnedNoSpikeFields)
            return;

        warnedNoSpikeFields = true;
        Debug.LogError(
            "[Leviathan] Starfire could not resolve Torch.mainSpike; " +
            "breath geometry will remain native."
        );
    }

    // Mirrors the native Striker Laser/Bolt/Torch Range path. Torch.MaxRange
    // calls Equippable.ApplyModifier(MaxRange, ..., includeParentShip: true);
    // scale only the selected Starfire source at that native stat boundary.
    public static void ScaleMaxRange(
        Equippable equippable,
        Modifier.Type modifierType,
        bool includeParentShip,
        ref float value)
    {
        if (modifierType != Modifier.Type.MaxRange || !includeParentShip)
            return;

        Torch torch = equippable as Torch;
        GameShip player;
        int rank;

        if (torch == null ||
            !TryGetStarfireContext(torch, out player, out rank) ||
            !IsSourceTorch(torch, player))
        {
            return;
        }

        value *= GetRankValue(LengthMultiplierByRank, rank);
    }

    // =========================================================================
    // HEAT / EXTRA-TORCH SUPPRESSION
    // =========================================================================

    public struct HeatRateState
    {
        public bool changed;
        public float originalHeatPerSecond;
    }

    // Starfire owns exactly one Primary Torch. Extra Primary Torches are true
    // disabled weapons, not merely invisible damage sources. This runs before
    // Torch.FixedUpdate -> Activatable.FixedUpdate -> UpdateHeat.
    public static void SuppressNonSourceTorchActivation(Torch torch)
    {
        if (torch == null || ActivatableActiveField == null)
            return;

        GameShip player;
        int rank;

        if (!TryGetStarfireContext(torch, out player, out rank) ||
            IsSourceTorch(torch, player))
        {
            return;
        }

        ActivatableActiveField.SetValue(torch, false);
    }

    // Native heat flow:
    //   Activatable.UpdateHeat() -> GameShip.AddHeat() [no args, latency flag]
    //   GameShip.UpdateHeat() -> heat += cached heatPerSecond * deltaTime
    //
    // Temporarily rewrite only that cached aggregate while UpdateHeat executes.
    // Suppressed Primary Torches contribute zero. The source Torch keeps its
    // native contribution, with the Starfire multiplier applied only while the
    // source itself is active. Every non-Torch contribution remains untouched.
    public static HeatRateState PrepareShipHeatRate(GameShip player)
    {
        HeatRateState state = new HeatRateState();

        if (player == null || GameShipHeatPerSecondField == null)
            return state;

        int rank;
        if (!TryGetStarfireRank(player, out rank))
            return state;

        Torch source = FindSourceTorch(player);
        if (source == null)
            return state;

        object raw = GameShipHeatPerSecondField.GetValue(player);
        if (!(raw is float))
            return state;

        float original = (float)raw;
        float adjusted = original;

        if (player.slots != null)
        {
            for (int i = 0; i < player.slots.Length; i++)
            {
                Slot slot = player.slots[i];

                if (slot == null ||
                    slot.type != Item.Type.PrimaryWeapon ||
                    slot.equippable == null ||
                    slot.equippable.durability == 0)
                {
                    continue;
                }

                Torch torch = slot.equippable as Torch;
                if (torch == null || ReferenceEquals(torch, source))
                    continue;

                // GameShip.RegenerateModifiers included this value in its cached
                // aggregate. Remove it because Starfire disables this Torch.
                adjusted -= torch.heatPerSecond;
            }
        }

        if (source.durability != 0 && source.IsActive())
        {
            float multiplier = GetRankValue(HeatMultiplierByRank, rank);
            adjusted += source.heatPerSecond * (multiplier - 1f);
        }

        adjusted = Mathf.Max(0f, adjusted);

        if (Mathf.Approximately(adjusted, original))
            return state;

        state.changed = true;
        state.originalHeatPerSecond = original;
        GameShipHeatPerSecondField.SetValue(player, adjusted);
        return state;
    }

    public static void RestoreShipHeatRate(
        GameShip player,
        HeatRateState state)
    {
        if (!state.changed ||
            player == null ||
            GameShipHeatPerSecondField == null)
        {
            return;
        }

        GameShipHeatPerSecondField.SetValue(
            player,
            state.originalHeatPerSecond
        );
    }

    // =========================================================================
    // CHARGE / STARTUP
    // =========================================================================

    public static bool PrepareChargeRamp(Torch torch, out float originalChargeTime)
    {
        originalChargeTime = 0f;

        GameShip player;
        int rank;

        if (!TryGetStarfireContext(torch, out player, out rank) ||
            !IsSourceTorch(torch, player) ||
            ChargeTimeField == null)
        {
            return false;
        }

        object raw = ChargeTimeField.GetValue(torch);
        if (!(raw is float))
            return false;

        originalChargeTime = (float)raw;
        float speed = GetRankValue(ChargeRampSpeedMultiplierByRank, rank);

        if (Mathf.Approximately(speed, 1f))
            return false;

        // Native UpdatePower uses deltaTime / chargeTime. Divide chargeTime by
        // the requested speed multiplier so >1 charges faster and <1 slower.
        float safeSpeed = Mathf.Max(0.0001f, speed);
        ChargeTimeField.SetValue(
            torch,
            originalChargeTime <= 0f
                ? originalChargeTime
                : originalChargeTime / safeSpeed
        );

        return true;
    }

    public static void RestoreChargeRamp(
        Torch torch,
        bool changed,
        float originalChargeTime)
    {
        if (changed && torch != null && ChargeTimeField != null)
            ChargeTimeField.SetValue(torch, originalChargeTime);

        // A fully discharged Torch begins a new Starfire startup cycle next time.
        if (torch != null && ReadCharge(torch) <= 0.0001f)
            StartupBeginTimes.Remove(torch);
    }

    private static float ReadCharge(Torch torch)
    {
        if (torch == null || ChargeField == null)
            return 0f;

        object raw = ChargeField.GetValue(torch);
        return raw is float ? (float)raw : 0f;
    }

    private static bool IsInsideStartupDelay(Torch torch, int rank)
    {
        float delay = GetRankValue(StartupDelaySecondsByRank, rank);
        if (delay <= 0f || torch == null)
            return false;

        float started;

        if (!StartupBeginTimes.TryGetValue(torch, out started))
        {
            started = Time.time;
            StartupBeginTimes[torch] = started;
        }

        return Time.time - started < delay;
    }

    // =========================================================================
    // DAMAGE
    // =========================================================================

    public static bool BeginSpikeDamage(
        Torch torch,
        object[] args,
        out bool suppress)
    {
        suppress = false;

        GameShip player;
        int rank;

        if (!TryGetStarfireContext(torch, out player, out rank))
            return false;

        if (!IsSourceTorch(torch, player))
        {
            suppress = true;
            return false;
        }

        if (IsInsideStartupDelay(torch, rank))
        {
            suppress = true;
            return false;
        }

        object mirrorSpike = GetMirrorSpike(torch);

        // Native Torch may call DoSpikeDamage once for each spike. If the method
        // exposes the Spike as an argument, explicitly reject the mirror packet.
        if (mirrorSpike != null && args != null)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (ReferenceEquals(args[i], mirrorSpike))
                {
                    suppress = true;
                    return false;
                }
            }
        }

        if (damageContextDepth == 0)
            currentDamageTorch = torch;

        damageContextDepth++;
        return true;
    }

    public static void EndSpikeDamage(bool entered)
    {
        if (!entered || damageContextDepth <= 0)
            return;

        damageContextDepth--;

        if (damageContextDepth == 0)
            currentDamageTorch = null;
    }

    public static void ScaleNativeTorchDamage(object[] args)
    {
        Torch torch = currentDamageTorch;

        if (damageContextDepth <= 0 ||
            torch == null ||
            args == null ||
            args.Length < 10 ||
            !ReferenceEquals(args[9], torch))
        {
            return;
        }

        GameShip player;
        int rank;

        if (!TryGetStarfireContext(torch, out player, out rank) ||
            !IsSourceTorch(torch, player))
        {
            return;
        }

        DamageData[] source = args[2] as DamageData[];
        if (source == null || source.Length == 0)
            return;

        float packetMultiplier = GetRankValue(
            DamageMultiplierByRank,
            rank
        );

        // Native mirrored Torches split one weapon's damage across two spikes.
        // Starfire emits exactly one spike, so restore the source weapon's full
        // aggregate packet before applying any Starfire damage multiplier.
        if (HasNativeMirror(torch))
            packetMultiplier *= 2f;

        if (Mathf.Approximately(packetMultiplier, 1f))
            return;

        DamageData[] scaled = new DamageData[source.Length];

        for (int i = 0; i < source.Length; i++)
        {
            DamageData datum = source[i];
            datum.damage *= packetMultiplier;
            datum.dps *= packetMultiplier;
            scaled[i] = datum;
        }

        args[2] = scaled;
    }

    public static void ScaleNativeCritChance(Torch torch, ref float value)
    {
        GameShip player;
        int rank;

        if (!IsCurrentDamageSource(torch) ||
            !TryGetStarfireContext(torch, out player, out rank))
        {
            return;
        }

        value = Mathf.Clamp01(
            value * GetRankValue(CritChanceMultiplierByRank, rank)
        );
    }

    public static void ScaleNativeCritModifier(Torch torch, ref float value)
    {
        GameShip player;
        int rank;

        if (!IsCurrentDamageSource(torch) ||
            !TryGetStarfireContext(torch, out player, out rank))
        {
            return;
        }

        // Native GetDamageData uses (1 + GetCritModifier()). Multiplying the
        // modifier here scales only the crit BONUS portion, exactly as intended.
        value *= GetRankValue(CritDamageMultiplierByRank, rank);
    }

    public static void ScaleNativeDebuffChance(Torch torch, ref float value)
    {
        GameShip player;
        int rank;

        if (!IsCurrentDamageSource(torch) ||
            !TryGetStarfireContext(torch, out player, out rank))
        {
            return;
        }

        value = Mathf.Clamp01(
            value * GetRankValue(DebuffChanceMultiplierByRank, rank)
        );
    }

    private static bool IsCurrentDamageSource(Torch torch)
    {
        if (damageContextDepth <= 0 ||
            torch == null ||
            !ReferenceEquals(currentDamageTorch, torch))
        {
            return false;
        }

        GameShip player = GetParentShip(torch);
        return IsSourceTorch(torch, player);
    }

    private static bool HasNativeMirror(Torch torch)
    {
        if (torch == null || MirrorGameObjectField == null)
            return false;

        UnityEngine.Object mirror =
            MirrorGameObjectField.GetValue(torch) as UnityEngine.Object;

        return mirror != null;
    }

    // =========================================================================
    // PUBLIC TUNING HELPERS
    // =========================================================================

    public static float GetHeatMultiplier(int rank)
    {
        return GetRankValue(HeatMultiplierByRank, rank);
    }

    public static float GetLengthMultiplier(int rank)
    {
        return GetRankValue(LengthMultiplierByRank, rank);
    }

    public static float GetWidthMultiplier(int rank)
    {
        return GetRankValue(WidthMultiplierByRank, rank);
    }

    public static float GetDamageMultiplier(int rank)
    {
        return GetRankValue(DamageMultiplierByRank, rank);
    }

    public static float GetCritChanceMultiplier(int rank)
    {
        return GetRankValue(CritChanceMultiplierByRank, rank);
    }

    public static float GetCritDamageMultiplier(int rank)
    {
        return GetRankValue(CritDamageMultiplierByRank, rank);
    }

    public static float GetDebuffChanceMultiplier(int rank)
    {
        return GetRankValue(DebuffChanceMultiplierByRank, rank);
    }

    public static float GetStartupDelaySeconds(int rank)
    {
        return GetRankValue(StartupDelaySecondsByRank, rank);
    }

    public static float GetChargeRampSpeedMultiplier(int rank)
    {
        return GetRankValue(ChargeRampSpeedMultiplierByRank, rank);
    }

    public static float GetFullSizeHoldSeconds(int rank)
    {
        return GetRankValue(FullSizeHoldSecondsByRank, rank);
    }

    public static float GetBreathRetreatSeconds(int rank)
    {
        return GetRankValue(BreathRetreatSecondsByRank, rank);
    }

    public static float GetBreathRetreatCurveExponent(int rank)
    {
        return GetRankValue(BreathRetreatCurveExponentByRank, rank);
    }

    private static float GetRankValue(float[] values, int rank)
    {
        int index = Mathf.Clamp(rank, 1, values.Length) - 1;
        return values[index];
    }
}

// Native Striker range uses Equippable.ApplyModifier(MaxRange) and category
// modifiers. Hook the same stat boundary, but only for Starfire's one source.
[HarmonyPatch]
public static class LeviathanStarfireMaxRangePatch
{
    public static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(Equippable),
            "ApplyModifier",
            new Type[]
            {
                typeof(Modifier.Type),
                typeof(float),
                typeof(bool),
                typeof(bool)
            }
        );
    }

    public static void Postfix(
        Equippable __instance,
        Modifier.Type __0,
        bool __3,
        ref float __result)
    {
        LeviathanStarfireRuntime.ScaleMaxRange(
            __instance,
            __0,
            __3,
            ref __result
        );
    }
}

// -----------------------------------------------------------------------------
// Native Torch hooks
// -----------------------------------------------------------------------------

// Native BuildSpikes starts by destroying the previous spike. Clear generated
// fan state first so collider/material references cannot survive a rebuild.
[HarmonyPatch(typeof(Torch), "DestroySpikes")]
public static class LeviathanStarfireDestroySpikesPatch
{
    public static void Prefix(Torch __instance)
    {
        LeviathanStarfireRuntime.ForgetStarfireFan(__instance);
    }
}

[HarmonyPatch(typeof(Torch), "BuildSpikes")]
public static class LeviathanStarfireBuildSpikesPatch
{

    public static void Postfix(Torch __instance)
    {
        LeviathanStarfireRuntime.RefreshTorchGeometry(__instance, true);
    }
}

[HarmonyPatch(typeof(Torch), "FollowSpikes")]
public static class LeviathanStarfireFollowSpikesPatch
{

    public static void Postfix(Torch __instance)
    {
        // Generated fan objects are children of the native spike and follow its
        // transform automatically. No mesh/collider rebuild is needed here.
        LeviathanStarfireRuntime.RefreshTorchGeometry(__instance, false);
    }
}

[HarmonyPatch(typeof(Torch), "UpdateSpikeScale")]
public static class LeviathanStarfireUpdateSpikeScalePatch
{

    public static void Postfix(Torch __instance)
    {
        LeviathanStarfireRuntime.RefreshTorchGeometry(__instance, true);
    }
}

// Extra Primary Torches must be inactive before Activatable.FixedUpdate performs
// duration/cooldown/heat work. The selected source Torch is untouched.
[HarmonyPatch(typeof(Torch), "FixedUpdate")]
public static class LeviathanStarfireTorchFixedUpdatePatch
{
    public static void Prefix(Torch __instance)
    {
        LeviathanStarfireRuntime.SuppressNonSourceTorchActivation(__instance);
    }
}

// GameShip.AddHeat() is only a no-argument latency flag. Actual weapon heat is
// applied here from GameShip's cached heatPerSecond field, so this is the narrow
// native point where Starfire can change only Torch contributions.
[HarmonyPatch(typeof(GameShip), "UpdateHeat")]
public static class LeviathanStarfireGameShipUpdateHeatPatch
{

    public static void Prefix(
        GameShip __instance,
        out LeviathanStarfireRuntime.HeatRateState __state)
    {
        __state = LeviathanStarfireRuntime.PrepareShipHeatRate(__instance);
    }

    public static void Postfix(
        GameShip __instance,
        LeviathanStarfireRuntime.HeatRateState __state)
    {
        LeviathanStarfireRuntime.RestoreShipHeatRate(__instance, __state);
    }
}

[HarmonyPatch(typeof(Torch), "UpdatePower")]
public static class LeviathanStarfireChargeRampPatch
{

    public static void Prefix(
        Torch __instance,
        out Tuple<bool, float> __state)
    {
        float original;
        bool changed = LeviathanStarfireRuntime.PrepareChargeRamp(
            __instance,
            out original
        );

        __state = Tuple.Create(changed, original);
    }

    public static void Postfix(
        Torch __instance,
        Tuple<bool, float> __state)
    {
        LeviathanStarfireRuntime.RestoreChargeRamp(
            __instance,
            __state != null && __state.Item1,
            __state == null ? 0f : __state.Item2
        );
    }
}

/// <summary>
/// Suppress every non-source Torch's native damage, and suppress the source
/// Torch's mirror spike if native DoSpikeDamage exposes that Spike as an arg.
/// For the one allowed source packet, establish a narrow context so RouteDamage
/// can apply the Starfire damage multiplier without recreating Torch mechanics.
/// </summary>
[HarmonyPatch(typeof(Torch), "DoSpikeDamage")]
public static class LeviathanStarfireDoSpikeDamagePatch
{

    public static bool Prefix(
        Torch __instance,
        object[] __args,
        out bool __state)
    {
        bool suppress;
        __state = LeviathanStarfireRuntime.BeginSpikeDamage(
            __instance,
            __args,
            out suppress
        );

        return !suppress;
    }

    public static void Postfix(bool __state)
    {
        LeviathanStarfireRuntime.EndSpikeDamage(__state);
    }
}

[HarmonyPatch(typeof(Torch), "GetCritChance")]
public static class LeviathanStarfireCritChancePatch
{

    public static void Postfix(Torch __instance, ref float __result)
    {
        LeviathanStarfireRuntime.ScaleNativeCritChance(
            __instance,
            ref __result
        );
    }
}

[HarmonyPatch(typeof(Torch), "GetCritModifier")]
public static class LeviathanStarfireCritModifierPatch
{

    public static void Postfix(Torch __instance, ref float __result)
    {
        LeviathanStarfireRuntime.ScaleNativeCritModifier(
            __instance,
            ref __result
        );
    }
}

[HarmonyPatch(typeof(Torch), "GetStatusEffectChance")]
public static class LeviathanStarfireDebuffChancePatch
{

    public static void Postfix(Torch __instance, ref float __result)
    {
        LeviathanStarfireRuntime.ScaleNativeDebuffChance(
            __instance,
            ref __result
        );
    }
}

[HarmonyPatch]
public static class LeviathanStarfireRouteDamagePatch
{
    public static MethodBase TargetMethod()
    {
        return typeof(NetCombat)
            .GetMethods(
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic
            )
            .FirstOrDefault(
                m => m.Name == "RouteDamage" &&
                     m.GetParameters().Length == 14 &&
                     m.GetParameters()[2].ParameterType == typeof(DamageData[])
            );
    }

    public static void Prefix(object[] __args)
    {
        LeviathanStarfireRuntime.ScaleNativeTorchDamage(__args);
    }
}
