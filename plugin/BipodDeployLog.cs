using System;
using System.Reflection;
using EFT.WeaponMounting;
using SPT.Reflection.Patching;
using UnityEngine;

namespace CustomRpTweaks.Client
{
	// Diagnostics only, with verbose logging on: every time the game looks for a place to deploy a bipod prone, one line says what it found
	// below the bipod and which of its tests failed (something within 0.2 m, no ground in reach, ground too steep). It repeats the game's
	// own checks without changing anything; the game's layers are the point: it only counts the low-poly terrain and doors as ground.
	internal class BipodDeployLogPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(MountingPointDetectionSystem).GetMethod(nameof(MountingPointDetectionSystem.FindMountingPointProne), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(MountingPointDetectionSystem __instance, float distance, float bipodHeight)
		{
			if (Plugin.VerboseLogging == null || !Plugin.VerboseLogging.Value)
			{
				return;
			}

			try
			{
				var root = __instance._gridRoot;
				var layer = __instance._secondaryDetectionLayer;
				var origin = root.position + root.forward * distance;
				var length = Mathf.Min(0.4f, bipodHeight * 2f);
				var blocked = Physics.CheckSphere(origin, 0.2f, layer);
				var found = Physics.Raycast(origin, -root.up, out var hit, length, layer);
				var dot = found ? Vector3.Dot(hit.normal, root.up) : float.NaN;
				var needs = __instance._pointDetectionSettings.MaxProneMountAngleDotDelta;

				// What is really there, on any layer
				var anything = Physics.Raycast(origin + root.up * 0.2f, -root.up, out var other, 1.5f);
				var seen = anything ? $"{other.collider.name} (layer {LayerMask.LayerToName(other.collider.gameObject.layer)}, {other.distance - 0.2f:0.00} m below, normal dot {Vector3.Dot(other.normal, root.up):0.000})" : "nothing within 1.3 m";

				Plugin.Verbose($"Bipod prone check: something within 0.2 m on the terrain layers: {blocked}; ground in reach ({length:0.00} m): {found}"
					+ (found ? $", flat enough: dot {dot:0.000} needs {needs:0.000}" : string.Empty) + $"; on any layer below: {seen}");
			}
			catch (Exception e)
			{
				Plugin.Verbose($"Bipod prone check: couldn't work it out ({e.Message})");
			}
		}
	}
}
