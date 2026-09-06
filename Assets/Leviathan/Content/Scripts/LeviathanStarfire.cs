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

    // Multiplier on the source Torch spike body's native Y scale. Torch damage uses
    // the same spike Collider2D, so this widens both the native visual and hit area.
    // Rank 1 is completely vanilla width; Rank 5 reaches 2.5x width.
    private static readonly float[] WidthMultiplierByRank =
    {
        1.30f, // Rank 1
        1.50f, // Rank 2
        1.70f, // Rank 3
        1.80f, // Rank 4
        2.00f  // Rank 5
    };

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
    // 0.50 = double native chargeTime. Because Torch's own `charge` drives both
    // range and GetDamageData(), this changes the real mechanical ramp, not just VFX.
    private static readonly float[] ChargeRampSpeedMultiplierByRank =
    {
        1.00f, // Rank 1
        1.00f, // Rank 2
        1.00f, // Rank 3
        1.00f, // Rank 4
        1.00f  // Rank 5
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
            // If this Torch was previously touched by Starfire, restore only the
            // components Starfire itself suppressed. Do this once on transition,
            // rather than forcing native component state every frame.
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
            FullySuppressedTorches.Add(torch);
            SetSpikeSuppressed(mainSpike, true);
            SetSpikeSuppressed(mirrorSpike, true);
            return;
        }

        // If equipment changes made a previously-suppressed Torch become the new
        // source, restore its main spike once. Otherwise leave native main-spike
        // enabled/disabled state untouched.
        if (FullySuppressedTorches.Remove(torch))
            SetSpikeSuppressed(mainSpike, false);

        // Starfire is exactly one breath. Keep the source Torch's normal native
        // rectangular spike at its original mount and suppress only the mirror.
        SetSpikeSuppressed(mirrorSpike, true);

        if (applyScale)
        {
            ApplySpikeScale(
                mainSpike,
                GetRankValue(WidthMultiplierByRank, rank)
            );
        }
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

    private static void ApplySpikeScale(
        object nativeSpike,
        float widthMultiplier)
    {
        Transform body = GetSpikeBodyTransform(nativeSpike);
        if (body == null)
            return;

        // Length is supplied through Torch.MaxRange at the native modifier boundary.
        // Torch.SetSpikeScale has already put the correct mechanical length into X
        // and resets Y to 1, so assign width absolutely to avoid compounding across
        // FixedUpdate/LateUpdate. The native rectangular Collider2D scales with it.
        Vector3 scale = body.localScale;
        scale.y = widthMultiplier;
        body.localScale = scale;
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

        Renderer[] renderers = spikeObject.GetComponentsInChildren<Renderer>(true);
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

        // Decompiled Torch.Spike stores its root GameObject in `obj`.
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
        // Keep source/mirror/extra-Torch suppression enforced without relocating
        // the source. Native FollowSpikes remains fully responsible for its mount.
        LeviathanStarfireRuntime.RefreshTorchGeometry(__instance, true);
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
