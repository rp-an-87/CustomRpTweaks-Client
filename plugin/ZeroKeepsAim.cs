using System;
using System.Reflection;
using EFT;
using EFT.Animations;
using SPT.Reflection.Patching;
using UnityEngine;

namespace CustomRpTweaks.Client
{
	// When the zero of the sight you aim through changes by a step, the game turns the sight relative to the gun, so the reticle jumps up or
	// down and you have to re-aim by hand. With this on, your aim is moved by the same angle the other way, so the reticle stays on what it was on.
	internal class ZeroKeepsAimPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(ProceduralWeaponAnimation).GetMethod(nameof(ProceduralWeaponAnimation.OpticCalibrationSwitch), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		// The sight's elevation (degrees above the horizon) of the line from the scope to the point of the current zero
		private static float? Elevation(ProceduralWeaponAnimation animation)
		{
			var sight = animation.CurrentAimingMod;
			var scope = animation.CurrentScope;
			if (sight == null || scope == null || scope.Bone == null || !sight.HasOpticCalibrationPoints(sight.SelectedScopeIndex))
			{
				return null;
			}

			var direction = animation.GetWorldCalibrationPoint(sight.GetCurrentOpticCalibrationPoint()) - scope.Bone.position;
			return direction.sqrMagnitude < 1f ? (float?)null : Mathf.Asin(Mathf.Clamp(direction.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
		}

		[PatchPrefix]
		private static void Prefix(ProceduralWeaponAnimation __instance, out float? __state)
		{
			__state = null;
			try
			{
				var player = GamePlayerOwner.MyPlayer;
				if (Plugin.ZeroKeepsAim.Value && player != null && __instance == player.ProceduralWeaponAnimation && __instance.IsAiming && !__instance.IsGrenadeLauncher)
				{
					__state = Elevation(__instance);
				}
			}
			catch (Exception e)
			{
				Plugin.Verbose($"Zero keeps aim: {e.Message}");
			}
		}

		[PatchPostfix]
		private static void Postfix(ProceduralWeaponAnimation __instance, float? __state)
		{
			if (__state == null)
			{
				return;
			}

			try
			{
				var after = Elevation(__instance);
				var player = GamePlayerOwner.MyPlayer;
				if (after == null || player == null)
				{
					return;
				}

				// Looking down raises Pitch, so the sight line's change in elevation is added to it: the line goes back to where it was
				var delta = Mathf.Clamp(after.Value - __state.Value, -5f, 5f);
				if (Mathf.Abs(delta) < 0.0005f)
				{
					return;
				}

				var rotation = player.Rotation;
				player.Rotation = new Vector2(rotation.x, rotation.y + delta);
				Plugin.Verbose($"Zero keeps aim: moved the aim {delta:0.000} degrees to keep the reticle on target");
			}
			catch (Exception e)
			{
				Plugin.Verbose($"Zero keeps aim: {e.Message}");
			}
		}
	}
}
