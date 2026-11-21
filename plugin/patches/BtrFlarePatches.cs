using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
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
	// on its own. Like the artillery flare, it is recognised when the flare succeeds (the game calls Player.HandleFlareSuccessEvent with the position
	// and the ammo when the flare is high enough).
	//
	// The BTR then goes to the stop nearest to where the flare went up, on its route or not. It drives along splines between neighbouring stops of the
	// map, so the way there is found on the graph of all stops (breadth first) and driven stop by stop with the game's own "take me to" call
	// (BTRVehicle.MoveToDestination). If it is waiting at a stop it leaves at once, otherwise it finishes the segment it is on and carries on at once;
	// at the stops on the way it waits a second. At the called stop it waits its normal pause.
	// If that stop is on the route the game carries on from there. If not, the game cannot (it finds the next stop from the place of the current one in
	// the route), so the BTR drives back to the route stop nearest to it, and the game carries on from that one.
	internal static class BtrCall
	{
		// ammo id of the cartridge, same as BtrFlareItems.AmmoId in the server mod
		internal const string AmmoId = "6a1b7c0d4e5f60718293a4b6";
		internal const string BtrTraderId = "656f0f98d80a697f855d34b1";
		// seconds the BTR waits at a stop it only passes through on the way
		private const float PassThroughWait = 1f;

		private enum Phase { None, Going, Returning }

		private static BTRVehicle _vehicle;
		private static string _target;          // the stop the BTR was called to; null = no call pending
		private static bool _targetOnRoute;
		private static Phase _phase;
		private static string _at;              // the stop the BTR is at, or is on its way to, in the plan
		private static readonly Queue<string> _plan = new Queue<string>();   // the stops still to drive to, in order

		// a paid ride (the driver's "take me to") that needs more than one road: the stops still to drive to, with you aboard
		private static BTRVehicle _rideVehicle;
		private static string _rideAt;
		private static readonly Queue<string> _ridePlan = new Queue<string>();

		internal static void Notify(string message)
		{
			Plugin.Log.LogInfo("BTR signal: " + message);
			NotificationManager.DisplayMessageNotification("BTR signal: " + message);
		}

		private static void Clear()
		{
			_vehicle = null;
			_target = null;
			_at = null;
			_phase = Phase.None;
			_plan.Clear();
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

			if (_target != null && ReferenceEquals(vehicle, _vehicle))
			{
				Notify($"the BTR is already on its way to {_target}.");
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

			var graph = BuildGraph(map, route);
			var parents = Search(graph, start.id);
			var stops = parents.Keys.Select(id => map.GetPartPathByID(id) as PathDestination).Where(stop => stop != null).ToList();
			if (Plugin.VerboseLogging.Value)
			{
				Plugin.Log.LogInfo($"BTR signal: splines of the map: {string.Join(", ", map.PathSplines.Where(sp => sp != null).Select(sp => sp.id))}");
				Plugin.Log.LogInfo($"BTR signal: flare at {flarePosition:0}, route {route.id} {string.Join(",", route.pathPoints)}, BTR {(paused ? "waiting at" : "heading for")} {start.id}; stops it can reach: "
					+ string.Join(", ", stops.Select(s => (s, d: Flat(s.transform.position, flarePosition))).OrderBy(x => x.d).Select(x => $"{x.s.id}{(route.pathPoints.Contains(x.s.id) ? "" : " (off route)")} {x.d:0} m")));
			}

			var best = stops.OrderBy(stop => Flat(stop.transform.position, flarePosition)).FirstOrDefault();
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
			_target = best.id;
			_targetOnRoute = route.pathPoints.Contains(best.id);
			_phase = Phase.Going;
			_at = start.id;
			_plan.Clear();
			foreach (var id in PathTo(parents, best.id))
			{
				_plan.Enqueue(id);
			}
			Notify($"the BTR will stop at {best.id}{(_targetOnRoute ? "" : " (off its route)")}, {distance:0} m from the flare{(_plan.Count > 0 ? ", via " + string.Join(", ", _plan.Where(id => id != best.id)) : "")}.".Replace(", via .", "."));

			// waiting at a stop already: leave at once
			if (paused)
			{
				ContinuePlan(vehicle, start, false);
			}
		}

		// ---- the BTR reaches a stop (the game calls this once its pause there has started) -----------------------------------------------------

		internal static void OnStopReached(VehicleBase vehicle, PathDestination stop)
		{
			if (_rideVehicle != null)
			{
				ContinueRide(vehicle, stop);
				return;
			}
			if (_target == null)
			{
				return;
			}
			if (!ReferenceEquals(vehicle, _vehicle) || stop.id != _at)
			{
				Plugin.Log.LogWarning($"BTR signal: the BTR reached {stop.id}, expected {_at}; the call is dropped.");
				Clear();
				return;
			}

			if (_plan.Count > 0)
			{
				ContinuePlan(vehicle, stop, false);
				return;
			}

			if (_phase == Phase.Returning)
			{
				Notify($"the BTR is back on its route at {stop.id}.");
				ShortPause(vehicle, PassThroughWait);
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

			// off its route: wait the normal pause here, then drive back to the nearest route stop
			var map = ((BTRVehicle)vehicle).MapPathConfig;
			var route = vehicle.CurrentPathConfig;
			var parents = Search(BuildGraph(map, route), stop.id);
			var back = route.pathPoints.Distinct()
				.Where(id => parents.ContainsKey(id))
				.Select(id => map.GetPartPathByID(id) as PathDestination)
				.Where(s => s != null)
				.OrderBy(s => Flat(s.transform.position, stop.transform.position))
				.FirstOrDefault();
			if (back == null)
			{
				Plugin.Log.LogWarning($"BTR signal: no route stop to drive back to from {stop.id}.");
				Clear();
				return;
			}
			_phase = Phase.Returning;
			_plan.Clear();
			foreach (var id in PathTo(parents, back.id))
			{
				_plan.Enqueue(id);
			}
			Plugin.Log.LogInfo($"BTR signal: after the wait the BTR drives back to {back.id}.");
			ContinuePlan(vehicle, stop, true);
		}

		// Drives on to the next stop of the plan from the stop the BTR is at now
		private static void ContinuePlan(VehicleBase vehicle, PathDestination from, bool keepPause)
		{
			var next = _plan.Dequeue();
			var map = ((BTRVehicle)vehicle).MapPathConfig;
			if (map.GetPathSplineByPoints(from.id, next) == null)
			{
				Plugin.Log.LogWarning($"BTR signal: no spline from {from.id} to {next}; the call is dropped.");
				Clear();
				return;
			}

			var traverse = Traverse.Create(vehicle);
			var elapsed = traverse.Field("_tempPauseDuration").GetValue<float>();
			var total = traverse.Field("_pauseDuration").GetValue<float>();
			vehicle.MoveToDestination(next);   // also cuts the pause to its last seconds
			if (keepPause)
			{
				// the BTR waits the whole pause here
				traverse.Field("_tempPauseDuration").SetValue(elapsed);
				vehicle.timeToEndPause = total - elapsed;
			}
			else
			{
				ShortPause(vehicle, PassThroughWait);
			}
			_at = next;
			Plugin.Verbose($"BTR signal: {from.id} -> {next}{(keepPause ? " after the normal wait" : "")}.");
		}

		// ---- a paid ride over more than one road ------------------------------------------------------------------------------------------------

		// The driver's "take me to" asks the BTR to drive one spline from the stop it is at to the destination. When no spline joins them (typically from
		// an off-route stop, where nothing in the route leads on) the game would fail, so the way is found on the graph of stops instead: the first stop
		// replaces the destination in the game's call, and the rest is driven stop by stop with you aboard. Returns false to refuse the purchase.
		internal static bool PlanRide(BtrController controller, ref string idPathDestination)
		{
			var vehicle = controller.BtrVehicle;
			var parts = idPathDestination?.Split('/');
			if (vehicle == null || parts == null || parts.Length < 2)
			{
				return true;
			}
			var current = Traverse.Create(vehicle).Field("_currentPathDestination").GetValue<PathDestination>();
			var map = vehicle.MapPathConfig;
			var destination = parts[1];
			if (current == null || map == null || current.id == destination || map.GetPathSplineByPoints(current.id, destination) != null)
			{
				return true;
			}

			var parents = Search(BuildGraph(map, vehicle.CurrentPathConfig), current.id);
			if (!parents.ContainsKey(destination))
			{
				Notify($"there is no road from {current.id} to {destination}.");
				return false;
			}

			var path = PathTo(parents, destination);
			Clear();   // a call or a drive back that was waiting is replaced by the ride
			_ridePlan.Clear();
			foreach (var id in path.Skip(1))
			{
				_ridePlan.Enqueue(id);
			}
			_rideVehicle = vehicle;
			_rideAt = path[0];
			idPathDestination = parts[0] + "/" + path[0];
			Notify($"the BTR takes you to {destination} via {string.Join(", ", path.Take(path.Count - 1))}.".Replace(" via .", "."));
			return true;
		}

		// The BTR reached a stop of the ride: on to the next one with you still aboard (the game resets "paid" at every stop and puts passengers off
		// when it leaves an unpaid one), or, at the destination, back to normal
		private static void ContinueRide(VehicleBase vehicle, PathDestination stop)
		{
			if (!ReferenceEquals(vehicle, _rideVehicle) || stop.id != _rideAt)
			{
				Plugin.Log.LogWarning($"BTR signal: the ride reached {stop.id}, expected {_rideAt}; dropped.");
				_rideVehicle = null;
				_ridePlan.Clear();
				return;
			}
			if (_ridePlan.Count == 0)
			{
				_rideVehicle = null;
				return;
			}

			var next = _ridePlan.Dequeue();
			var map = ((BTRVehicle)vehicle).MapPathConfig;
			if (map.GetPathSplineByPoints(stop.id, next) == null)
			{
				Plugin.Log.LogWarning($"BTR signal: no spline from {stop.id} to {next}; the ride ends here.");
				_rideVehicle = null;
				_ridePlan.Clear();
				return;
			}
			vehicle.IsPaid = true;
			vehicle.MoveToDestination(next);
			ShortPause(vehicle, PassThroughWait);
			_rideAt = next;
			Plugin.Verbose($"BTR signal: ride {stop.id} -> {next}.");
		}

		// The pause at the current stop ends in this many seconds
		private static void ShortPause(VehicleBase vehicle, float seconds)
		{
			var traverse = Traverse.Create(vehicle);
			var total = traverse.Field("_pauseDuration").GetValue<float>();
			traverse.Field("_tempPauseDuration").SetValue(Mathf.Max(0f, total - seconds));
			vehicle.timeToEndPause = Mathf.Min(seconds, total);
		}

		// ---- the graph of stops ---------------------------------------------------------------------------------------------------------------

		// Stops of the map joined by a spline (spline ids are "s_<stop>_<stop>"); the route's entry and exit points are left out: arriving at the exit point sends the BTR away
		private static Dictionary<string, List<string>> BuildGraph(MapPathConfig map, PathConfig route)
		{
			var stopIds = new HashSet<string>(map.PathDestinations.Where(stop => stop != null).Select(stop => stop.id));
			var graph = new Dictionary<string, List<string>>();
			void Join(string from, string to)
			{
				if (!graph.TryGetValue(from, out var list))
				{
					graph[from] = list = new List<string>();
				}
				if (!list.Contains(to))
				{
					list.Add(to);
				}
			}
			foreach (var spline in map.PathSplines)
			{
				var parts = spline?.id?.Split('_');
				if (parts == null || parts.Length < 3)
				{
					continue;
				}
				string a = parts[1], b = parts[2];
				if (!stopIds.Contains(a) || !stopIds.Contains(b) || a == route.enterPoint || a == route.exitPoint || b == route.enterPoint || b == route.exitPoint)
				{
					continue;
				}
				Join(a, b);
				Join(b, a);
			}
			return graph;
		}

		// Breadth first from one stop: every stop reachable, with the stop before it on the shortest way
		private static Dictionary<string, string> Search(Dictionary<string, List<string>> graph, string from)
		{
			var parents = new Dictionary<string, string> { [from] = null };
			var queue = new Queue<string>();
			queue.Enqueue(from);
			while (queue.Count > 0)
			{
				var id = queue.Dequeue();
				if (!graph.TryGetValue(id, out var neighbours))
				{
					continue;
				}
				foreach (var next in neighbours)
				{
					if (!parents.ContainsKey(next))
					{
						parents[next] = id;
						queue.Enqueue(next);
					}
				}
			}
			return parents;
		}

		// The stops to drive to, in order, without the one it starts at
		private static List<string> PathTo(Dictionary<string, string> parents, string to)
		{
			var path = new List<string>();
			for (var id = to; id != null && parents[id] != null; id = parents[id])
			{
				path.Add(id);
			}
			path.Reverse();
			return path;
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

	// The driver's "take me to": see BtrCall.PlanRide
	internal class BtrRidePatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(BtrController).GetMethod(nameof(BtrController.MoveToDestination), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPrefix]
		private static bool Prefix(BtrController __instance, ref string idPathDestination)
		{
			try
			{
				return BtrCall.PlanRide(__instance, ref idPathDestination);
			}
			catch (System.Exception e)
			{
				Plugin.Log.LogError("BTR ride failed: " + e);
				return true;
			}
		}
	}

	// Where the BTR comes from and which way round it goes. The routes are fixed lists of stops, and on Woods the BTR starts at the same place and
	// drives the same circle every raid. These two make it less predictable: it may appear at a random stop of its route (not close to you), and a
	// circle route is driven the other way round half the time.
	internal class BtrRouteReversePatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(VehicleBase).GetMethod(nameof(VehicleBase.Initialization), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		// runs before the game builds the route's stops and splines from the route's list
		[PatchPrefix]
		private static void Prefix(VehicleBase __instance, MapPathConfig mapPathsConfig)
		{
			if (!Plugin.BtrRandomDirection.Value)
			{
				return;
			}
			var route = __instance.CurrentPathConfig;
			if (route?.pathPoints == null || route.pathPoints.Count < 3 || route.once || !route.circle || route.enterPoint != route.exitPoint)
			{
				return;
			}
			if (Random.value < 0.5f)
			{
				Plugin.Verbose($"BTR route {route.id}: driven the original way round: {string.Join(",", route.pathPoints)}");
				return;
			}

			var reversed = route.pathPoints.AsEnumerable().Reverse().ToList();
			// the entry point has to be joined to the new first stop and the exit to the new last one (same point, joined to both ends of the circle)
			var sequence = new List<string> { route.enterPoint };
			sequence.AddRange(reversed);
			sequence.Add(route.exitPoint);
			for (var i = 0; i < sequence.Count - 1; i++)
			{
				if (mapPathsConfig.GetPathSplineByPoints(sequence[i], sequence[i + 1]) == null)
				{
					Plugin.Log.LogWarning($"BTR route {route.id} cannot be reversed: no spline between {sequence[i]} and {sequence[i + 1]}.");
					return;
				}
			}
			route.pathPoints = reversed;
			Plugin.Log.LogInfo($"BTR route {route.id} is driven the other way round this raid: {string.Join(",", reversed)}");
		}
	}

	internal class BtrRandomStartPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(VehicleBase).GetMethod(nameof(VehicleBase.SpawnInEnterPoint), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		// Replaces putting the BTR on its entry point with putting it on a random stop of its route, at least a set distance from you
		[PatchPrefix]
		private static bool Prefix(VehicleBase __instance)
		{
			if (!Plugin.BtrRandomStart.Value)
			{
				return true;
			}
			var route = __instance.CurrentPathConfig;
			var map = __instance.MapPathConfig;
			if (route?.pathPoints == null || route.pathPoints.Count < 2 || route.once || !route.circle || map == null)
			{
				return true;
			}

			var player = Singleton<GameWorld>.Instance?.MainPlayer;
			var minDistance = Plugin.BtrRandomStartMinDistance.Value;
			var options = route.pathPoints
				.Select(id => map.GetPartPathByID(id) as PathDestination)
				.Where(stop => stop != null && (player == null || Vector3.Distance(stop.transform.position, player.Transform.position) >= minDistance))
				.ToList();
			if (options.Count == 0)
			{
				Plugin.Verbose("BTR random start: every stop is closer than the minimum distance, the BTR starts at its entry point.");
				return true;
			}

			var stop = options[Random.Range(0, options.Count)];
			var traverse = Traverse.Create(__instance);
			__instance.transform.position = stop.transform.position;
			__instance.transform.rotation = stop.transform.rotation;
			traverse.Field("_currentPathDestination").SetValue(stop);
			traverse.Field("_targetPathDestination").SetValue(stop);
			traverse.Field("_changeSpeedMultiplier").SetValue(0.1f);
			GlobalEventsController.CreateEvent<BtrSpawnOnThePathEvent>().Invoke(stop.transform.position, stop.transform.rotation, string.Empty);
			Plugin.Log.LogInfo($"BTR random start: the BTR appears at stop {stop.id} of route {route.id} ({string.Join(",", route.pathPoints)}).");
			return false;
		}
	}
}
