using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.Animations;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace CustomRpTweaks.Client
{
	// Vertical (pitch) correction for the zero. In vanilla the laser beam and the rangefinder's measuring ray stay along the barrel while the
	// sight line dips toward it the longer the zero is, so the dot or the ray walks up from the reticle as you zero longer. While you aim through a
	// sight, the device is turned for the moment the beam is drawn or the ray is cast (and straight back after, so the model never moves) by only
	// the pitch the sight line has turned since the sight's shortest zero. At that zero it is exactly vanilla; the sideways direction is never
	// touched, so every mount keeps the offset vanilla gives it. Off the sights nothing changes.
	internal static class ZeroPitch
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
			var sight = animation == null || !animation.IsAiming || animation.IsGrenadeLauncher ? null : animation.CurrentAimingMod;
			if (sight == null || !sight.HasOpticCalibrationPoints(sight.SelectedScopeIndex) || !OnYourWeapon(part))
			{
				return none;
			}

			var point = animation.GetWorldCalibrationPoint(sight.GetCurrentOpticCalibrationPoint());
			var scope = animation.CurrentScope;
			var scopePosition = scope != null && scope.Bone != null ? scope.Bone.position : target.position;
			var toPoint = point - scopePosition;
			if (toPoint.sqrMagnitude < 1f)
			{
				return none;
			}

			// Vanilla is the base: the laser keeps pointing along the barrel at the sight's shortest zero, where its dot has the offset a mount
			// gives it (beside the barrel, on the scope rail...). From there the beam is turned by exactly the angle the sight line turned when the
			// zero went longer, so that offset stays the same at every zero. A beam parallel to the sight line instead would sit the laser's whole
			// height below the scope's line, which vanilla does not (its sight line dips toward the barrel axis).
			var scopeIndex = sight.SelectedScopeIndex;
			var steps = sight.GetScopeCalibrationDistances(scopeIndex);
			var points = sight.OpticCalibrationPoints != null && scopeIndex < sight.OpticCalibrationPoints.Length ? sight.OpticCalibrationPoints[scopeIndex] : null;
			if (steps == null || points == null || steps.Length == 0 || steps.Length != points.Length)
			{
				return none;
			}

			var shortest = 0;
			for (var i = 1; i < steps.Length; i++)
			{
				if (steps[i] < steps[shortest])
				{
					shortest = i;
				}
			}

			var fromShortest = animation.GetWorldCalibrationPoint(points[shortest]) - scopePosition;
			if (fromShortest.sqrMagnitude < 1f)
			{
				return none;
			}

			// Only the weapon's vertical (pitch) change between the two sight lines is applied, about the weapon's own right axis; the beam's
			// sideways direction, and so the sideways offset of the mount, is never touched
			var origin = animation.GetWorldCalibrationPoint(Vector3.zero);
			var right = (animation.GetWorldCalibrationPoint(Vector3.right) - origin).normalized;
			var turn = VerticalTurn(fromShortest.normalized, toPoint.normalized, right);
			var state = new State { Applied = true, Target = target, Rotation = target.rotation };
			target.rotation = turn * target.rotation;
			var turned = AngleBetween(state.Rotation * Vector3.forward, target.forward);
			if (Time.unscaledTime >= _nextLog)
			{
				_nextLog = Time.unscaledTime + 2f;
				Plugin.Verbose($"Zero pitch: {what} turned {turned:0.000} degrees since the shortest zero ({sight.GetCurrentOpticCalibrationDistance()} m)");
			}

			return state;
		}

		// Unity's FromToRotation returns "no turn" for angles under about 0.08 degrees and Vector3.Angle is coarse there too, and the correction
		// is that small on a short zero, so the turn is built from atan2 of the cross product, which stays exact for small angles
		// The turn about the axis "right" that takes the pitch of the first direction to the pitch of the second (both are flattened onto the plane
		// across that axis first); exact for tiny angles
		private static Quaternion VerticalTurn(Vector3 from, Vector3 to, Vector3 right)
		{
			var a = Vector3.ProjectOnPlane(from, right);
			var b = Vector3.ProjectOnPlane(to, right);
			if (a.sqrMagnitude < 1e-9f || b.sqrMagnitude < 1e-9f)
			{
				return Quaternion.identity;
			}

			var angle = Mathf.Atan2(Vector3.Dot(Vector3.Cross(a, b), right), Vector3.Dot(a, b)) * Mathf.Rad2Deg;
			return Quaternion.AngleAxis(angle, right);
		}

		private static float AngleBetween(Vector3 a, Vector3 b) => Mathf.Atan2(Vector3.Cross(a, b).magnitude, Vector3.Dot(a, b)) * Mathf.Rad2Deg;

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

	internal class LaserZeroPitchPatch : ModulePatch
	{
		private static readonly FieldInfo LightField = AccessTools.Field(typeof(LaserBeam), "_light");

		protected override MethodBase GetTargetMethod() => typeof(LaserBeam).GetMethod(nameof(LaserBeam.LateUpdate), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPrefix]
		private static void Prefix(LaserBeam __instance, out ZeroPitch.State __state)
		{
			try
			{
				__state = ZeroPitch.Apply(__instance, __instance.transform, new ConfigEntryBool(Plugin.LaserZeroPitch), "laser");
			}
			catch (Exception e)
			{
				__state = default;
				Plugin.Verbose($"Zero pitch (laser): {e.Message}");
			}
		}

		[PatchPostfix]
		private static void Postfix(LaserBeam __instance, ZeroPitch.State __state)
		{
			if (!__state.Applied)
			{
				return;
			}

			// The beam's light is a child of the laser and was placed in the world while the laser was turned: put it back where it was set
			var light = LightField?.GetValue(__instance) as Light;
			var position = light == null ? Vector3.zero : light.transform.position;
			var rotation = light == null ? Quaternion.identity : light.transform.rotation;
			ZeroPitch.Restore(__state);
			if (light != null)
			{
				light.transform.SetPositionAndRotation(position, rotation);
			}
		}
	}

	internal class RangefinderZeroPitchPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(TacticalRangeFinderController).GetMethod(nameof(TacticalRangeFinderController.MeasureDistance), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPrefix]
		private static void Prefix(TacticalRangeFinderController __instance, out ZeroPitch.State __state)
		{
			try
			{
				__state = ZeroPitch.Apply(__instance, __instance._boneToCastRay, new ConfigEntryBool(Plugin.RangefinderZeroPitch), "rangefinder");
			}
			catch (Exception e)
			{
				__state = default;
				Plugin.Verbose($"Zero pitch (rangefinder): {e.Message}");
			}
		}

		[PatchPostfix]
		private static void Postfix(ZeroPitch.State __state) => ZeroPitch.Restore(__state);
	}
}
