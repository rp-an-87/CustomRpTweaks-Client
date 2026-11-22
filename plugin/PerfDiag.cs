using System;
using System.Diagnostics;
using Comfort.Common;
using EFT;
using EFT.UI.DragAndDrop;
using EFT.UI.Screens;
using UnityEngine;
using UnityEngine.Profiling;

namespace CustomRpTweaks.Client
{
	// Troubleshooting aid for the slowdown that builds up over a few raids: writes one [Perf] line to the BepInEx log at the start and end of every
	// raid and when the stash screen opens (and again 3 s later, when the frame time has settled): memory (managed heap, Unity allocated and
	// reserved), the number of live item views, grid views, textures, sprites and game objects (Resources.FindObjectsOfTypeAll, so inactive and
	// pooled ones count too), the smoothed frame time, and the sizes of this mod's own caches. Read down the lines across raids: a count that
	// keeps climbing is what leaks, a flat count with a rising frame time is per-frame cost. Each snapshot costs a few ms, only at those moments.
	internal static class PerfDiag
	{
		private static bool _inRaid;
		private static int _raids;
		private static EEftScreenType _screen;
		private static float _smoothedMs = 16f;
		private static float _stashFollowUp = -1f;

		internal static void Update()
		{
			// Always tracked, so a snapshot shows what the frame time was, not only the moment it was taken
			var ms = Time.unscaledDeltaTime * 1000f;
			if (ms > 0f && ms < 1000f)
			{
				_smoothedMs += (ms - _smoothedMs) * 0.05f;
			}

			if (Plugin.LogPerformance == null || !Plugin.LogPerformance.Value)
			{
				return;
			}

			try
			{
				var inRaid = Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance.MainPlayer != null;
				if (inRaid != _inRaid)
				{
					_inRaid = inRaid;
					if (inRaid)
					{
						_raids++;
					}

					Snapshot(inRaid ? "raid " + _raids + " start" : "raid " + _raids + " end");
				}

				if (inRaid)
				{
					_screen = EEftScreenType.None;
					_stashFollowUp = -1f;
					return;
				}

				var manager = EftScreenManager.Instance;
				var current = manager == null ? null : manager.CurrentScreenController;
				var screen = current == null ? EEftScreenType.None : current.ScreenType;
				if (screen != _screen)
				{
					_screen = screen;
					_stashFollowUp = -1f;
					if (screen == EEftScreenType.Inventory)
					{
						Snapshot("stash opened");
						_stashFollowUp = Time.unscaledTime + 3f;
					}
				}
				else if (_stashFollowUp > 0f && Time.unscaledTime >= _stashFollowUp)
				{
					_stashFollowUp = -1f;
					Snapshot("stash open for 3 s");
				}
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning("[Perf] snapshot failed: " + e.Message);
			}
		}

		private static void Snapshot(string when)
		{
			var timer = Stopwatch.StartNew();
			const double Mb = 1024.0 * 1024.0;
			var line = $"[Perf] {when}: frame {_smoothedMs:0.0} ms"
				+ $", heap {GC.GetTotalMemory(false) / Mb:0} MB, unity {Profiler.GetTotalAllocatedMemoryLong() / Mb:0}/{Profiler.GetTotalReservedMemoryLong() / Mb:0} MB (allocated/reserved)"
				+ $", item views {Count<ItemView>()}, grid views {Count<GridView>()}, textures {Count<Texture2D>()}, sprites {Count<Sprite>()}, game objects {Count<GameObject>()}"
				+ $", own caches: menu entries {ItemMenu.PendingCount}, flea panels {FleaAmmoBoxSearch.ShowTasks.Count}";
			timer.Stop();
			Plugin.Log.LogInfo(line + $" (snapshot took {timer.Elapsed.TotalMilliseconds:0} ms)");
		}

		private static float _nextSlowLine;

		// A piece of work done on hover that took long enough to be felt: one [Perf] line, at most one every 2 s, naming the item
		internal static void NoteSlow(string what, double ms, EFT.InventoryLogic.Item item)
		{
			if (ms < 3.0 || Plugin.LogPerformance == null || !Plugin.LogPerformance.Value || Time.unscaledTime < _nextSlowLine)
			{
				return;
			}

			_nextSlowLine = Time.unscaledTime + 2f;
			Plugin.Log.LogInfo($"[Perf] slow {what}: {ms:0.0} ms for {(item == null ? "?" : item.ShortName.Localized())} ({(item == null ? "" : item.GetType().Name)})");
		}

		private static int Count<T>() where T : UnityEngine.Object => Resources.FindObjectsOfTypeAll<T>().Length;
	}
}
