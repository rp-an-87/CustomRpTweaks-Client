using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace CustomRpTweaks.Client
{
	// While you aim through a sight, the laser beam and the rangefinder's measuring ray of the device on your weapon point along your sight line
	// (at the point of the sight's zero), not along the barrel. The turn is applied only for the moment the beam is drawn or the ray is cast
	// and is undone straight after, so the device model never moves. Off the sights nothing changes.
	internal static class AimTilt
	{
		private static Weapon _cachedWeapon;
		private static readonly Dictionary<int, bool> Mine = new Dictionary<int, bool>();
		private static float _nextLog;

		internal struct State
		{
			internal bool Applied;
			internal Transform Target;
			internal Quaternion Rotation;
		}

		// Turns the transform toward the sight line's point of zero; the state tells Restore what to undo
		internal static State Apply(Component part, Transform target, ConfigEntryBool enabled, string what)
		{
			var none = default(State);
			if (!enabled.Value || part == null || target == null)
			{
				return none;
			}

			var player = GamePlayerOwner.MyPlayer;
			var animation = player == null ? null : player.ProceduralWeaponAnimation;
			var sight = animation == null || !animation.IsAiming ? null : animation.CurrentAimingMod;
			if (sight == null || !sight.HasOpticCalibrationPoints(sight.SelectedScopeIndex) || !OnYourWeapon(part))
			{
				return none;
			}

			var point = animation.GetWorldCalibrationPoint(sight.GetCurrentOpticCalibrationPoint());
			var toPoint = point - target.position;
			if (toPoint.sqrMagnitude < 1f)
			{
				return none;
			}

			var state = new State { Applied = true, Target = target, Rotation = target.rotation };
			target.rotation = Quaternion.FromToRotation(target.forward, toPoint.normalized) * target.rotation;
			if (Time.unscaledTime >= _nextLog)
			{
				_nextLog = Time.unscaledTime + 2f;
				Plugin.Verbose($"Aim tilt: {what} turned {Vector3.Angle(state.Rotation * Vector3.forward, target.forward):0.000} degrees toward the zero point ({sight.GetCurrentOpticCalibrationDistance()} m)");
			}

			return state;
		}

		internal static void Restore(State state)
		{
			if (state.Applied && state.Target != null)
			{
				state.Target.rotation = state.Rotation;
			}
		}

		// The device (laser, rangefinder) is on the weapon in your hands; remembered per part so the weapon's items are not walked every frame
		private static bool OnYourWeapon(Component part)
		{
			var player = GamePlayerOwner.MyPlayer;
			var weapon = player == null ? null : (player.HandsController as Player.FirearmController)?.Item;
			if (weapon == null)
			{
				return false;
			}

			if (weapon != _cachedWeapon)
			{
				_cachedWeapon = weapon;
				Mine.Clear();
			}

			var id = part.GetInstanceID();
			if (!Mine.TryGetValue(id, out var mine))
			{
				var device = part.GetComponentInParent<TacticalComboVisualController>();
				mine = device != null && device.LightMod != null && weapon.GetAllItemsFromCollection().Any(item => item == device.LightMod.Item);
				Mine[id] = mine;
			}

			return mine;
		}
	}

	// A named wrapper so the setting can be passed without the generic type in every signature
	internal struct ConfigEntryBool
	{
		private readonly BepInEx.Configuration.ConfigEntry<bool> _entry;

		internal ConfigEntryBool(BepInEx.Configuration.ConfigEntry<bool> entry) => _entry = entry;

		internal bool Value => _entry != null && _entry.Value;
	}

	internal class LaserFollowsZeroPatch : ModulePatch
	{
		private static readonly FieldInfo LightField = AccessTools.Field(typeof(LaserBeam), "_light");

		protected override MethodBase GetTargetMethod() => typeof(LaserBeam).GetMethod(nameof(LaserBeam.LateUpdate), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPrefix]
		private static void Prefix(LaserBeam __instance, out AimTilt.State __state)
		{
			try
			{
				__state = AimTilt.Apply(__instance, __instance.transform, new ConfigEntryBool(Plugin.LaserFollowsZero), "laser");
			}
			catch (Exception e)
			{
				__state = default;
				Plugin.Verbose($"Aim tilt (laser): {e.Message}");
			}
		}

		[PatchPostfix]
		private static void Postfix(LaserBeam __instance, AimTilt.State __state)
		{
			if (!__state.Applied)
			{
				return;
			}

			// The beam's light is a child of the laser and was placed in the world while the laser was turned: put it back where it was set
			var light = LightField?.GetValue(__instance) as Light;
			var position = light == null ? Vector3.zero : light.transform.position;
			var rotation = light == null ? Quaternion.identity : light.transform.rotation;
			AimTilt.Restore(__state);
			if (light != null)
			{
				light.transform.SetPositionAndRotation(position, rotation);
			}
		}
	}

	internal class RangefinderFollowsZeroPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(TacticalRangeFinderController).GetMethod(nameof(TacticalRangeFinderController.MeasureDistance), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPrefix]
		private static void Prefix(TacticalRangeFinderController __instance, out AimTilt.State __state)
		{
			try
			{
				__state = AimTilt.Apply(__instance, __instance._boneToCastRay, new ConfigEntryBool(Plugin.RangefinderFollowsZero), "rangefinder");
			}
			catch (Exception e)
			{
				__state = default;
				Plugin.Verbose($"Aim tilt (rangefinder): {e.Message}");
			}
		}

		[PatchPostfix]
		private static void Postfix(AimTilt.State __state) => AimTilt.Restore(__state);
	}
}
