using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.Communications;
using EFT.GlobalEvents;
using EFT.InventoryLogic;
using EFT.Vehicle;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace CustomRpTweaks.Client.Patches
{
	// The BTR signal cartridge (added by the server mod, BtrFlare.cs): a white reactive cartridge with no flare type, so the game does nothing with it
	// on its own. Like the artillery flare it is recognised when the flare succeeds (the game calls Player.HandleFlareSuccessEvent with the position and
	// the ammo once the flare is high enough).
	//
	// The BTR goes to the stop nearest to the flare, on its route or not. Every move is the game's own "take me to" call, BTRVehicle.MoveToDestination,
	// which drives the one road between the stop the BTR is at and the target. On Woods every stop has a road to every other, so that is enough; a stop
	// without a road from where the BTR is simply is not offered. If the BTR is waiting at a stop it leaves at once, otherwise it finishes the segment it
	// is on and goes on at once. At the called stop it waits its normal pause. If the stop is on its route the game carries on from there. If not, it
	// cannot (it finds the next stop from the place of the current one in the route), so the BTR drives back to the nearest route stop first.
	internal static class BtrCall
	{
		// ammo id of the cartridge, same as BtrFlareItems.AmmoId in the server mod
		internal const string AmmoId = "6a1b7c0d4e5f60718293a4b6";
		// the loose 26x75 round for the ZiD SP-81 signal pistol (BtrFlareItems.PistolAmmoId)
		internal const string PistolAmmoId = "6a1b7c0d4e5f60718293a4b9";

		internal static bool IsBtrAmmo(string templateId) => templateId == AmmoId || templateId == PistolAmmoId;
		internal const string BtrTraderId = "656f0f98d80a697f855d34b1";

		private enum Kind { None, Call, Return, FirstLeg }

		private static BTRVehicle _vehicle;
		private static Kind _kind;
		private static string _arriveAt;     // the stop the BTR will reach next
		private static string _then;         // the stop to go on to from there, before the journey's last one
		private static bool _targetOnRoute;

		private static void Notify(string message)
		{
			Plugin.Log.LogInfo("BTR signal: " + message);
			NotificationManager.DisplayMessageNotification("BTR signal: " + message);
		}

		private static void Clear()
		{
			_vehicle = null;
			_kind = Kind.None;
			_arriveAt = null;
			_then = null;
		}

		private static bool Joined(MapPathConfig map, string a, string b) => map.GetPathSplineByPoints(a, b) != null;

		private static float Flat(Vector3 a, Vector3 b)
		{
			var d = a - b;
			d.y = 0f;
			return d.magnitude;
		}

		// ---- the call -------------------------------------------------------------------------------------------------------------------------

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
			if (_kind != Kind.None && ReferenceEquals(vehicle, _vehicle))
			{
				Notify("the BTR is busy, try again in a moment.");
				return;
			}

			var traverse = Traverse.Create(vehicle);
			var current = traverse.Field("_currentPathDestination").GetValue<PathDestination>();
			var heading = traverse.Field("_targetPathDestination").GetValue<PathDestination>();
			var paused = traverse.Field("_pauseOn").GetValue<bool>();
			// where the BTR will be next: the stop it waits at, or the one it is driving to
			var start = paused ? current : heading;
			if (start == null)
			{
				Notify("the BTR is not at a stop and has none to go to.");
				return;
			}

			// the stops it can go to from there: the one itself and every one with a road to it, never the entry or exit point (arriving at the exit sends the BTR away)
			var stops = map.PathDestinations
				.Where(stop => stop != null && stop.id != route.enterPoint && stop.id != route.exitPoint && (stop.id == start.id || Joined(map, start.id, stop.id)))
				.OrderBy(stop => Flat(stop.transform.position, flarePosition))
				.ToList();
			if (Plugin.VerboseLogging.Value)
			{
				Plugin.Log.LogInfo($"BTR signal: flare at {flarePosition:0}, route {route.id} {string.Join(",", route.pathPoints)}, BTR {(paused ? "waiting at" : "heading for")} {start.id}; "
					+ $"stops it can go to: {string.Join(", ", stops.Select(s => $"{s.id}{(route.pathPoints.Contains(s.id) ? "" : " (off route)")} {Flat(s.transform.position, flarePosition):0} m"))}");
			}

			var best = stops.FirstOrDefault();
			if (best == null)
			{
				Notify("the BTR cannot reach any stop from here.");
				return;
			}
			var distance = Flat(best.transform.position, flarePosition);
			if (distance > Plugin.BtrFlareMaxDistance.Value)
			{
				Notify($"the nearest stop ({best.id}) is {distance:0} m away, too far.");
				return;
			}
			if (paused && best.id == start.id)
			{
				Notify($"the BTR is already at {best.id}, the nearest stop.");
				return;
			}

			_vehicle = vehicle;
			_kind = Kind.Call;
			_targetOnRoute = route.pathPoints.Contains(best.id);
			Notify($"the BTR will stop at {best.id}{(_targetOnRoute ? "" : " (off its route)")}, {distance:0} m from the flare.");

			if (paused && !Hop(vehicle, start.id, best.id, keepPause: false))
			{
				Clear();
			}
			else if (!paused)
			{
				// finish the segment, then go on from the stop it is driving to
				_arriveAt = start.id;
				_then = best.id == start.id ? null : best.id;
			}
		}

		// ---- the BTR reaches a stop (the game calls this once its pause there has started) -----------------------------------------------------

		internal static void OnStopReached(VehicleBase vehicle, PathDestination stop)
		{
			if (_kind == Kind.None)
			{
				return;
			}
			if (!ReferenceEquals(vehicle, _vehicle) || stop.id != _arriveAt)
			{
				Plugin.Log.LogWarning($"BTR signal: the BTR reached {stop.id}, expected {_arriveAt}; dropped.");
				Clear();
				return;
			}

			// a stop on the way: straight on
			if (_then != null)
			{
				var next = _then;
				_then = null;
				if (!Hop((BTRVehicle)vehicle, stop.id, next, keepPause: false))
				{
					Clear();
				}
				return;
			}

			switch (_kind)
			{
				case Kind.FirstLeg:
					Clear();
					return;

				case Kind.Return:
					Notify($"the BTR is back on its route at {stop.id}.");
					ShortPause(vehicle, 1f);
					Clear();
					return;
			}

			// reached the stop it was called to
			Notify($"the BTR has arrived at {stop.id}.");
			if (_targetOnRoute)
			{
				Clear();
				return;
			}

			// off its route: wait the normal pause here, then drive back to the nearest route stop it has a road to
			var map = ((BTRVehicle)vehicle).MapPathConfig;
			var back = vehicle.CurrentPathConfig.pathPoints.Distinct()
				.Select(id => map.GetPartPathByID(id) as PathDestination)
				.Where(route => route != null && Joined(map, stop.id, route.id))
				.OrderBy(route => Flat(route.transform.position, stop.transform.position))
				.FirstOrDefault();
			if (back == null || !Hop((BTRVehicle)vehicle, stop.id, back.id, keepPause: true))
			{
				Plugin.Log.LogWarning($"BTR signal: no route stop to drive back to from {stop.id}.");
				Clear();
				return;
			}
			_kind = Kind.Return;
			Plugin.Log.LogInfo($"BTR signal: after the wait the BTR drives back to {back.id}.");
		}

		// Drives from the stop the BTR is at to the next one with the game's "take me to" call. That call also cuts the pause to its last seconds: for a
		// stop passed on the way it is made a second; where the BTR is to wait its normal pause it is put back.
		private static bool Hop(BTRVehicle vehicle, string from, string to, bool keepPause)
		{
			if (!Joined(vehicle.MapPathConfig, from, to))
			{
				Plugin.Log.LogWarning($"BTR signal: no road from {from} to {to}; dropped.");
				return false;
			}

			var traverse = Traverse.Create(vehicle);
			var elapsed = traverse.Field("_tempPauseDuration").GetValue<float>();
			var total = traverse.Field("_pauseDuration").GetValue<float>();
			vehicle.MoveToDestination(to);
			if (keepPause)
			{
				traverse.Field("_tempPauseDuration").SetValue(elapsed);
				vehicle.timeToEndPause = total - elapsed;
			}
			else
			{
				ShortPause(vehicle, 1f);
			}
			_arriveAt = to;
			Plugin.Verbose($"BTR signal: {from} -> {to}{(keepPause ? " after the normal wait" : "")}.");
			return true;
		}

		// The pause at the current stop ends in this many seconds
		private static void ShortPause(VehicleBase vehicle, float seconds)
		{
			var traverse = Traverse.Create(vehicle);
			var total = traverse.Field("_pauseDuration").GetValue<float>();
			traverse.Field("_tempPauseDuration").SetValue(Mathf.Max(0f, total - seconds));
			vehicle.timeToEndPause = Mathf.Min(seconds, total);
		}

		// ---- the BTR's first leg -----------------------------------------------------------------------------------------------------------------

		// Called right after the game put the BTR on its entry point and sent it to the route's first stop: sends it to a random stop of the route instead,
		// and the route goes on from there. An entry point has a road to one stop only (on Woods p10 to p5), so the BTR passes that stop first, a second
		// there, unless it is the one chosen.
		internal static void StartAtRandomStop(VehicleBase vehicle)
		{
			var btr = vehicle as BTRVehicle;
			var route = vehicle.CurrentPathConfig;
			var map = vehicle.MapPathConfig;
			var entry = Traverse.Create(vehicle).Field("_currentPathDestination").GetValue<PathDestination>();
			if (!Plugin.BtrRandomStart.Value || btr == null || map == null || entry == null || route?.pathPoints == null || route.pathPoints.Count < 2 || route.once || !route.circle)
			{
				return;
			}

			var chosen = route.pathPoints[Random.Range(0, route.pathPoints.Count)];
			string first;   // the stop to drive to now
			if (chosen == route.pathPoints[0] && Joined(map, entry.id, chosen))
			{
				Plugin.Log.LogInfo($"BTR first stop: {chosen}, the route's own first stop.");
				return;
			}
			if (Joined(map, entry.id, chosen))
			{
				first = chosen;
			}
			else
			{
				first = map.PathDestinations.Where(stop => stop != null && stop.id != entry.id && Joined(map, entry.id, stop.id) && Joined(map, stop.id, chosen)).Select(stop => stop.id).FirstOrDefault();
				if (first == null)
				{
					Plugin.Verbose($"BTR first stop: no road from the entry point {entry.id} to {chosen} or to a stop that has one, the BTR goes to its first stop.");
					return;
				}
			}

			_vehicle = btr;
			_kind = Kind.FirstLeg;
			_then = first == chosen ? null : chosen;
			if (!Hop(btr, entry.id, first, keepPause: false))
			{
				Clear();
				return;
			}
			btr.BtrState = EBtrState.Running;
			Plugin.Log.LogInfo($"BTR first stop: {chosen} (route {route.id}: {string.Join(",", route.pathPoints)}), from the entry point {entry.id}{(first == chosen ? "" : " via " + first)}.");
		}

		// ---- checks on the route the game picked ---------------------------------------------------------------------------------------------------

		internal static bool RoadFrom(MapPathConfig map, string stop) => map.PathDestinations.Any(other => other != null && other.id != stop && Joined(map, stop, other.id));

		internal static bool RoadBetween(MapPathConfig map, string a, string b) => Joined(map, a, b);

		// Each stop of the route joined to the next by road, and for a circle the last to the first (that is how the game starts the next lap). The BTR comes
		// in from the entry point by road to the first stop, or with a random first stop from wherever the entry point has a road. The exit point is not
		// checked: on Woods the last stop has no road to it, and the BTR only goes there after its laps, which a raid does not last for.
		internal static bool RouteIsDrivable(MapPathConfig map, PathConfig route)
		{
			var stops = route.pathPoints;
			for (var i = 0; i < stops.Count - 1; i++)
			{
				if (!Joined(map, stops[i], stops[i + 1]))
				{
					return false;
				}
			}
			if (route.circle && !route.once && stops.Count > 1 && !Joined(map, stops[stops.Count - 1], stops[0]))
			{
				return false;
			}
			return Plugin.BtrRandomStart.Value ? RoadFrom(map, route.enterPoint) : Joined(map, route.enterPoint, stops[0]);
		}

		internal static void DumpMap(MapPathConfig map, PathConfig route)
		{
			Plugin.Log.LogInfo($"BTR map: route {route.id} {route.enterPoint} > {string.Join(",", route.pathPoints)} > {route.exitPoint} (circle {route.circle}, once {route.once}, laps {route.circleCount}); "
				+ $"stops {string.Join(" ", map.PathDestinations.Where(d => d != null).Select(d => $"{d.id}@{d.transform.position:0}"))}; "
				+ $"roads {string.Join(" ", map.PathSplines.Where(sp => sp != null).Select(sp => sp.id))}");
		}
	}

	internal class BtrFlareSuccessPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(Player).GetMethod(nameof(Player.HandleFlareSuccessEvent), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(Player __instance, Vector3 position, AmmoTemplate ammoTemplate)
		{
			if (ammoTemplate == null || !BtrCall.IsBtrAmmo(ammoTemplate._id))
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

	// What the BTR does at the start of a raid. The routes are fixed lists of stops, and on Woods the BTR comes in at the same place and drives the same
	// circle every raid. Before the game builds the route: the route is checked (a stop not joined by road to the next would leave the BTR stuck; a
	// route from the server is replaced by a drivable one), may be reversed, and may use another of the map's entry points.
	internal class BtrRoutePatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(VehicleBase).GetMethod(nameof(VehicleBase.Initialization), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPrefix]
		private static void Prefix(VehicleBase __instance, MapPathConfig mapPathsConfig)
		{
			try
			{
				var route = __instance.CurrentPathConfig;
				if (route?.pathPoints == null || mapPathsConfig == null)
				{
					return;
				}

				if (!BtrCall.RouteIsDrivable(mapPathsConfig, route))
				{
					var alternatives = (mapPathsConfig.PathsConfiguration.pathsConfigurations ?? new PathConfig[0])
						.Where(r => r != null && r.active && r != route && r.pathPoints != null && BtrCall.RouteIsDrivable(mapPathsConfig, r)).ToList();
					Plugin.Log.LogWarning($"BTR route {route.id} ({route.enterPoint} > {string.Join(",", route.pathPoints)} > {route.exitPoint}) has stops that are not joined by road; "
						+ (alternatives.Count > 0 ? "using another route." : "no drivable route to use instead, keeping it."));
					if (alternatives.Count > 0)
					{
						route = alternatives[Random.Range(0, alternatives.Count)];
						__instance.CurrentPathConfig = route;
					}
				}

				BtrCall.DumpMap(mapPathsConfig, route);
				if (Plugin.BtrRandomDirection.Value)
				{
					Reverse(route, mapPathsConfig);
				}
				if (Plugin.BtrRandomEntry.Value)
				{
					PickEntry(route, mapPathsConfig);
				}
			}
			catch (System.Exception e)
			{
				Plugin.Log.LogError("BTR route check failed: " + e);
			}
		}

		// A circle route is driven the other way round half the time
		private static void Reverse(PathConfig route, MapPathConfig map)
		{
			if (route.pathPoints.Count < 3 || route.once || !route.circle || route.enterPoint != route.exitPoint || Random.value < 0.5f)
			{
				return;
			}
			var reversed = route.pathPoints.AsEnumerable().Reverse().ToList();
			var candidate = new PathConfig { enterPoint = route.enterPoint, exitPoint = route.exitPoint, pathPoints = reversed, circle = true };
			if (!BtrCall.RouteIsDrivable(map, candidate))
			{
				Plugin.Verbose($"BTR route {route.id} cannot be reversed: a stop is not joined to the next by road.");
				return;
			}
			route.pathPoints = reversed;
			Plugin.Log.LogInfo($"BTR route {route.id} is driven the other way round this raid: {string.Join(",", reversed)}");
		}

		// The route may start and end at another of the map's entry points (those the map's routes use). Each entry point has a road to a single stop (on
		// Woods p10 only to p5, which is why the BTR nearly always goes to the same camp first); with a random first stop any of them will do, otherwise
		// the entry point must have a road to the route's first stop
		private static void PickEntry(PathConfig route, MapPathConfig map)
		{
			if (route.once || !route.circle || route.enterPoint != route.exitPoint)
			{
				return;
			}
			var first = route.pathPoints[0];
			var options = (map.PathsConfiguration.pathsConfigurations ?? new PathConfig[0])
				.Where(r => r != null && r.enterPoint == r.exitPoint)
				.Select(r => r.enterPoint)
				.Concat(new[] { route.enterPoint })
				.Distinct()
				.Where(entry => Plugin.BtrRandomStart.Value ? BtrCall.RoadFrom(map, entry) : BtrCall.RoadBetween(map, entry, first))
				.ToList();
			if (options.Count == 0)
			{
				return;
			}
			var pick = options[Random.Range(0, options.Count)];
			if (pick != route.enterPoint)
			{
				Plugin.Log.LogInfo($"BTR route {route.id} enters from {pick} instead of {route.enterPoint} this raid (entry points that fit: {string.Join(", ", options)}).");
				route.enterPoint = pick;
				route.exitPoint = pick;
			}
		}
	}

	internal class BtrRandomStartPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(VehicleBase).GetMethod(nameof(VehicleBase.MoveEnable), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(VehicleBase __instance)
		{
			try
			{
				BtrCall.StartAtRandomStop(__instance);
			}
			catch (System.Exception e)
			{
				Plugin.Log.LogError("BTR first stop failed: " + e);
			}
		}
	}
}
