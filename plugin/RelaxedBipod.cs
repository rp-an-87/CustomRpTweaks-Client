using System;
using System.Reflection;
using EFT.WeaponMounting;
using SPT.Reflection.Patching;
using UnityEngine;

namespace CustomRpTweaks.Client
{
	// Deploying a bipod prone is looser than the game makes it. Where the game looks for ground under the bipod it only counts low-poly
	// terrain and doors, refuses when anything is within 0.2 m of the spot, looks no further than a few tens of centimetres down and at
	// one spot only, and wants the ground nearly level. This does the same search with settings you can loosen:
	//  - other solid surfaces count as ground too (rocks, props),
	//  - the clearance around the spot is smaller (or off),
	//  - the search reaches further down,
	//  - when there is no ground right under the bipod (the edge of a ledge), the spots back toward you are tried too,
	//  - the ground may be steeper (the server's setting is used when it is already more lenient).
	// It rests the bipod on what it finds, so it may not look perfect. Anything unexpected falls back to the game's own search.
	internal class RelaxedProneBipodPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(MountingPointDetectionSystem).GetMethod(nameof(MountingPointDetectionSystem.FindMountingPointProne), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPrefix]
		private static bool Prefix(MountingPointDetectionSystem __instance, float yaw, float distance, float bipodHeight, float currentPitch)
		{
			if (!Plugin.BipodRelax.Value)
			{
				return true;
			}

			try
			{
				__instance.CancelFindingPoint();
				var root = __instance._gridRoot;
				root.eulerAngles = Vector3.up * yaw + Vector3.right * currentPitch;
				var forward = root.forward;
				var up = root.up;
				__instance._gridRootForward = forward;

				var settings = __instance._pointDetectionSettings;
				var gameLayer = __instance._secondaryDetectionLayer;
				var layer = Plugin.BipodAnySurface.Value ? (LayerMask)(gameLayer.value | __instance._mainDetectionLayer.value) : gameLayer;
				var reach = Mathf.Min(0.4f, bipodHeight * 2f) + Plugin.BipodExtraReach.Value;
				var clearance = Plugin.BipodClearance.Value;
				var minDot = (float)Math.Min(settings.MaxProneMountAngleDotDelta, Plugin.BipodMinFlatness.Value);
				var ledge = Plugin.BipodLedgeReach.Value;
				var tries = ledge > 0.01f ? 4 : 1;

				for (var i = 0; i < tries; i++)
				{
					var back = tries == 1 ? 0f : ledge * i / (tries - 1);
					var origin = root.position + forward * (distance - back);
					if (clearance > 0.001f && Physics.CheckSphere(origin, clearance, gameLayer))
					{
						continue;
					}

					if (!Physics.Raycast(origin, -up, out var hit, reach, layer) || hit.distance == 0f || Vector3.Dot(hit.normal, up) < minDot)
					{
						continue;
					}

					Plugin.Verbose($"Relaxed bipod deploy: ground found {back:0.00} m back from the usual spot, {hit.distance:0.00} m down, on {hit.collider.name}");
					__instance.OnHorizontalMountingPointFound(new MountPointData(hit.point + hit.normal * (float)settings.PointHorizontalMountOffset, forward, EMountSideDirection.Forward));
					return false;
				}

				Plugin.Verbose("Relaxed bipod deploy: no ground found");
				__instance.OnNothingFound();
				return false;
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning($"Relaxed bipod deploy failed, using the game's own search: {e.Message}");
				return true;
			}
		}
	}
}
