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
	// While you aim through a sight, the laser beam and the rangefinder's measuring ray of the device on your weapon point along your sight line
	// (at the sight line), not along the barrel. The turn is applied only for the moment the beam is drawn or the ray is cast
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
		internal static State Apply(Component part, Transform target, ConfigEntryBool enabled, string what, LaserBeam converge = null)
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
				Diagnose(part, what, sight == null ? "no sight with zero points (or not aiming, or grenade mode)" : "not on your weapon", 0f, 0f, 0);
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

			// The sight's own axis, the one the game itself turns toward the zero point and the reticle follows; my own line to the zero point
			// is only the fallback (and what the stats compare it with)
			var axis = SightAxis(scope);
			var direction = axis ?? toPoint.normalized;

			// A laser is aimed at the spot the sight line hits, so its dot lands on the reticle at any distance and from either side. A beam
			// only parallel to the sight line would sit the laser's mount offset (a few cm) off the reticle. Nothing hit: parallel.
			if (converge != null)
			{
				// The sight line is cast from as far forward as the laser itself starts, so the shooter's own body is not hit
				var along = Mathf.Max(0f, Vector3.Dot(target.position - scopePosition, direction));
				var origin = scopePosition + direction * (along + converge.RayStart);
				if (Physics.Raycast(origin, direction, out var hit, converge.MaxDistance, converge.Mask))
				{
					var toHit = hit.point - target.position;
					if (toHit.sqrMagnitude > 0.25f)
					{
						direction = toHit.normalized;
					}
				}
			}

			var state = new State { Applied = true, Target = target, Rotation = target.rotation };
			target.rotation = Between(target.forward, direction) * target.rotation;
			var turned = AngleBetween(state.Rotation * Vector3.forward, target.forward);
			var scopeAngle = axis.HasValue ? Vector3.Angle(axis.Value, toPoint) : -1f;
			Diagnose(part, what, null, turned, scopeAngle, sight.GetCurrentOpticCalibrationDistance(), Plugin.VerboseOn ? PointInfo(sight) : null);
			TraceFlip(part, what, turned, state.Rotation * Vector3.forward, direction, toPoint.normalized, target.position, scope != null && scope.Bone != null ? scope.Bone.position : target.position);
			if (Time.unscaledTime >= _nextLog)
			{
				_nextLog = Time.unscaledTime + 2f;
				Plugin.Verbose($"Aim tilt: {what} turned {turned:0.000} degrees toward the zero point ({sight.GetCurrentOpticCalibrationDistance()} m)");
			}

			return state;
		}

		// Verbose only: once a second and per device part, how often the tilt was applied or skipped (and why), its smallest and largest
		// turn, and how far the sight axis is from the computed line (to see which call flips between frames)
		private class Stat
		{
			internal int Applied, Skipped, Distance;
			internal float Min = float.MaxValue, Max, Off;
			internal string Reason = string.Empty, Point = string.Empty;
			internal float Next;
		}

		private static readonly Dictionary<int, Stat> Stats = new Dictionary<int, Stat>();

		private static void Diagnose(Component part, string what, string skipReason, float turned, float off, int distance, string point = null)
		{
			if (!Plugin.VerboseOn || part == null)
			{
				return;
			}

			var id = part.GetInstanceID();
			if (!Stats.TryGetValue(id, out var stat))
			{
				stat = new Stat();
				Stats[id] = stat;
			}

			if (skipReason == null)
			{
				stat.Applied++;
				stat.Min = Mathf.Min(stat.Min, turned);
				stat.Max = Mathf.Max(stat.Max, turned);
				stat.Off = off;
				stat.Distance = distance;
				stat.Point = point ?? string.Empty;
			}
			else
			{
				stat.Skipped++;
				stat.Reason = skipReason;
			}

			if (Time.unscaledTime < stat.Next)
			{
				return;
			}

			stat.Next = Time.unscaledTime + 1f;
			if (stat.Applied > 0)
			{
				Plugin.Verbose($"Aim tilt stats ({what} {id} on {part.transform.parent?.name}): zero {stat.Distance} m, applied {stat.Applied}x, skipped {stat.Skipped}x, turn {stat.Min:0.0000} to {stat.Max:0.0000} degrees, sight axis off my computed line by {stat.Off:0.0000} degrees, {stat.Point}");
			}
			else if (stat.Skipped > 0)
			{
				Plugin.Verbose($"Aim tilt stats ({what} {id} on {part.transform.parent?.name}): only skipped, {stat.Skipped}x, because {stat.Reason}");
			}

			stat.Applied = stat.Skipped = 0;
			stat.Min = float.MaxValue;
			stat.Max = 0f;
		}

		private static string PointInfo(SightComponent sight)
		{
			var scope = sight.SelectedScopeIndex;
			var steps = sight.GetScopeCalibrationDistances(scope);
			var point = sight.GetCurrentOpticCalibrationPoint();
			return $"point ({point.x:0.0000}, {point.y:0.0000}, {point.z:0.0000}) index {sight.GetCurrentOpticCalibrationPointIndex(scope)} of {(steps == null ? 0 : steps.Length)} [{(steps == null ? string.Empty : string.Join(",", steps))}]";
		}

		// Verbose only: when the turn jumps from one frame to the next, the numbers of that frame and the one before are logged (at most 30 times)
		private static readonly Dictionary<int, float> LastTurn = new Dictionary<int, float>();
		private static string _lastTrace = string.Empty;
		private static int _traces;

		private static void TraceFlip(Component part, string what, float turned, Vector3 laserForward, Vector3 axis, Vector3 line, Vector3 laserPosition, Vector3 scopePosition)
		{
			if (!Plugin.VerboseOn)
			{
				return;
			}

			var id = part.GetInstanceID();
			var pitch = Mathf.Asin(Mathf.Clamp(laserForward.y, -1f, 1f)) * Mathf.Rad2Deg;
			var axisPitch = Mathf.Asin(Mathf.Clamp(axis.y, -1f, 1f)) * Mathf.Rad2Deg;
			var linePitch = Mathf.Asin(Mathf.Clamp(line.y, -1f, 1f)) * Mathf.Rad2Deg;
			var now = $"frame {Time.frameCount}: turn {turned:0.0000}, laser pitch {pitch:0.0000}, axis pitch {axisPitch:0.0000}, line pitch {linePitch:0.0000}, laser y {laserPosition.y:0.0000}, scope y {scopePosition.y:0.0000}, laser-scope {Vector3.Distance(laserPosition, scopePosition):0.0000}";
			if (LastTurn.TryGetValue(id, out var before) && Mathf.Abs(turned - before) > 0.04f && _traces < 30)
			{
				_traces++;
				Plugin.Verbose($"Aim tilt flip ({what} {id}): before [{_lastTrace}] now [{now}]");
			}

			LastTurn[id] = turned;
			_lastTrace = now;
		}

		// Where the sight you aim through really looks: the optic's lens (or the red dot's), after the game turned it for the zero
		private static Vector3? SightAxis(ProceduralWeaponAnimation.SightNBone scope)
		{
			var cache = scope == null ? null : scope.ScopePrefabCache;
			if (cache == null)
			{
				return null;
			}

			Transform lens = null;
			if (scope.IsOptic)
			{
				var optic = cache.FirstOptic;
				lens = optic != null && optic.ScopeData != null ? optic.ScopeData.transform : null;
			}
			else
			{
				var collimator = cache.FirstCollimator;
				lens = collimator != null ? collimator.transform : null;
			}

			return lens == null ? (Vector3?)null : lens.forward.normalized;
		}

		// Unity's FromToRotation returns "no turn" for angles under about 0.08 degrees, and Vector3.Angle is coarse there too; the tilt is
		// that small on a short zero, so the turn is built from the cross product, which stays exact for small angles
		private static Quaternion Between(Vector3 from, Vector3 to)
		{
			var cross = Vector3.Cross(from, to);
			var sine = cross.magnitude;
			var cosine = Vector3.Dot(from, to);
			if (sine < 1e-9f)
			{
				return Quaternion.identity;
			}

			return cosine <= 0f ? Quaternion.FromToRotation(from, to) : Quaternion.AngleAxis(Mathf.Atan2(sine, cosine) * Mathf.Rad2Deg, cross / sine);
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

	internal class LaserFollowsZeroPatch : ModulePatch
	{
		private static readonly FieldInfo LightField = AccessTools.Field(typeof(LaserBeam), "_light");

		protected override MethodBase GetTargetMethod() => typeof(LaserBeam).GetMethod(nameof(LaserBeam.LateUpdate), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPrefix]
		private static void Prefix(LaserBeam __instance, out AimTilt.State __state)
		{
			try
			{
				__state = AimTilt.Apply(__instance, __instance.transform, new ConfigEntryBool(Plugin.LaserFollowsZero), "laser", __instance);
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
