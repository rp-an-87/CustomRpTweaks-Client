using System.Linq;
using System.Reflection;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.Vehicle;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace CustomRpTweaks.Client.Patches
{
	// The BTR signal cartridge (added by the server mod, BtrFlare.cs): a white reactive cartridge with no flare type, so the game does nothing with it
	// on its own. Like the artillery flare, it is recognised when the flare succeeds (the game calls Player.HandleFlareSuccessEvent with the position
	// and the ammo when the flare is high enough). The BTR then finishes the segment it is on, does not wait at the stops before the one nearest to
	// where the flare went up (its pause is cut short, as when it is about to leave), and waits at that stop for its normal pause.
	// The BTR keeps to its route: it only has splines between neighbouring stops, so it cannot drive straight to a far stop. On a route that does not
	// repeat (`once`) a stop it has already passed cannot be reached.
	internal static class BtrCall
	{
		// ammo id of the cartridge, same as BtrFlareItems.AmmoId in the server mod
		internal const string AmmoId = "6a1b7c0d4e5f60718293a4b6";
		internal const string BtrTraderId = "656f0f98d80a697f855d34b1";

		private static BTRVehicle _vehicle;
		private static string _stop;

		internal static void Notify(string message)
		{
			Plugin.Log.LogInfo("BTR signal: " + message);
			NotificationManager.DisplayMessageNotification("BTR signal: " + message);
		}

		internal static void OnFlare(Player player, Vector3 flarePosition)
		{
			if (!Plugin.BtrFlareCall.Value || !player.IsYourPlayer)
			{
				return;
			}

			var minStanding = Plugin.BtrFlareMinStanding.Value;
			if (minStanding > 0f)
			{
				double standing = 0;
				if (player.Profile?.TradersInfo != null && player.Profile.TradersInfo.TryGetValue(BtrTraderId, out var info))
				{
					standing = info.Standing;
				}
				if (standing < minStanding)
				{
					Notify($"nobody answers (BTR standing {standing:0.00}, needs {minStanding:0.00}).");
					return;
				}
			}

			var vehicle = BtrController.Instance?.BtrVehicle;
			if (vehicle == null || !vehicle.gameObject.activeInHierarchy || vehicle.VehicleRouteState != EVehicleRouteState.OnRoute)
			{
				Notify("the BTR is not on the map.");
				return;
			}

			var map = vehicle.MapPathConfig;
			var route = vehicle.CurrentPathConfig;
			if (map == null || route?.pathPoints == null || route.pathPoints.Count == 0)
			{
				Notify("the BTR has no route to call it on.");
				return;
			}

			var stops = route.pathPoints
				.Select(id => map.GetPartPathByID(id) as PathDestination)
				.Where(stop => stop != null)
				.ToList();
			if (Plugin.VerboseLogging.Value)
			{
				Plugin.Log.LogInfo($"BTR signal: route {route.id} (once {route.once}, circle {route.circle}, flipFlop {route.flipFlop}): "
					+ string.Join(", ", stops.Select(s => $"{s.id} at {s.transform.position:0}")));
			}

			var best = stops.OrderBy(stop => Flat(stop.transform.position, flarePosition)).First();
			var distance = Flat(best.transform.position, flarePosition);
			if (distance > Plugin.BtrFlareMaxDistance.Value)
			{
				Notify($"the nearest stop is {distance:0} m away, too far.");
				return;
			}

			var traverse = Traverse.Create(vehicle);
			var heading = traverse.Field("_targetPathDestination").GetValue<PathDestination>();
			var paused = traverse.Field("_pauseOn").GetValue<bool>();
			if (heading != null && heading.id == best.id && paused)
			{
				Notify($"the BTR is already at {best.id}, the nearest stop.");
				return;
			}
			if (route.once && heading != null && route.pathPoints.IndexOf(best.id) < route.pathPoints.IndexOf(heading.id))
			{
				Notify($"the BTR has already passed {best.id}, the nearest stop, and does not come back.");
				return;
			}

			_vehicle = vehicle;
			_stop = best.id;
			Notify($"the BTR will stop at {best.id} ({distance:0} m from the flare).");
		}

		// The BTR has reached a stop: its target, or one on the way to it
		internal static void OnStopReached(VehicleBase vehicle, PathDestination stop)
		{
			if (_stop == null)
			{
				return;
			}
			if (!ReferenceEquals(vehicle, _vehicle))
			{
				// left over from an earlier raid
				_vehicle = null;
				_stop = null;
				return;
			}
			if (stop.id == _stop)
			{
				_vehicle = null;
				_stop = null;
				Notify($"the BTR has arrived at {stop.id}.");
				return;
			}
			if (stop.id == vehicle.CurrentPathConfig.exitPoint)
			{
				_vehicle = null;
				_stop = null;
				Notify("the BTR left the map before it got there.");
				return;
			}
			if (Plugin.BtrFlareSkipStops.Value && vehicle.timeToEndPause > vehicle.readyToDeparture)
			{
				// the game's own "about to leave" call: the pause shrinks to its last readyToDeparture seconds
				vehicle.InitReadyToRide();
				Plugin.Verbose($"BTR signal: stop {stop.id} cut short on the way to {_stop}.");
			}
		}

		private static float Flat(Vector3 a, Vector3 b)
		{
			var d = a - b;
			d.y = 0f;
			return d.magnitude;
		}
	}

	internal class BtrFlareSuccessPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(Player).GetMethod(nameof(Player.HandleFlareSuccessEvent), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(Player __instance, Vector3 position, AmmoTemplate ammoTemplate)
		{
			if (ammoTemplate == null || ammoTemplate._id != BtrCall.AmmoId)
			{
				return;
			}
			try
			{
				BtrCall.OnFlare(__instance, position);
			}
			catch (System.Exception e)
			{
				Plugin.Log.LogError("BTR signal failed: " + e);
			}
		}
	}

	internal class BtrStopReachedPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(VehicleBase).GetMethod(nameof(VehicleBase.IncomingToDestination), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(VehicleBase __instance, PathDestination pathDestination)
		{
			try
			{
				BtrCall.OnStopReached(__instance, pathDestination);
			}
			catch (System.Exception e)
			{
				Plugin.Log.LogError("BTR signal (stop) failed: " + e);
			}
		}
	}
}
