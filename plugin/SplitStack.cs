using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI;
using UnityEngine;

namespace CustomRpTweaks.Client
{
	// Hotkey on a hovered stack (ammo, money, grenades, anything that stacks): splits it in half without the split dialog. The new
	// stack goes to the first free place next to it, in the container it lies in. It is the game's own split, so what the game
	// refuses (locked items, containers that can't take it) is refused here too.
	internal static class SplitStack
	{
		private static readonly FieldInfo ControllerField = typeof(ItemUiContext).GetField("_inventoryController", BindingFlags.Instance | BindingFlags.NonPublic);

		private static bool _running;

		// Timing of the last split (one [perf] line each): our own steps, then the worst frame of the next 60, which is where the game's own
		// work (the new stack's view, the sound) shows up
		private static string _perf;
		private static int _framesLeft;
		private static float _worstFrame;
		private static int _worstAt;
		private static int _frame;

		// A stack of at least two that lies in a grid (not one inside a magazine, say)
		internal static bool IsApplicable(Item item) =>
			item != null && item.StackObjectsCount > 1 && item.StackMaxSize > 1 && item.Parent?.Container is Grid;

		internal static void Update()
		{
			if (_framesLeft > 0)
			{
				_frame++;
				if (Time.unscaledDeltaTime > _worstFrame)
				{
					_worstFrame = Time.unscaledDeltaTime;
					_worstAt = _frame;
				}

				if (--_framesLeft == 0)
				{
					Plugin.Log.LogInfo($"[perf] split: {_perf ?? "transaction still running"}, worst frame after it {_worstFrame * 1000f:0.0} ms (frame +{_worstAt})");
				}
			}

			if (_running || !Plugin.SplitHotkey.Value.IsDown())
			{
				return;
			}

			var uiContext = ItemUiContext.Instance;
			var item = uiContext?.CurrentItemContext?.Item;
			var controller = uiContext == null ? null : ControllerField.GetValue(uiContext) as InventoryController;
			if (controller != null && IsApplicable(item))
			{
				Run(controller, item);
			}
		}

		private static async void Run(InventoryController controller, Item item)
		{
			_running = true;
			try
			{
				// The new stack gets the lower half, so an odd stack keeps the extra one
				var count = item.StackObjectsCount / 2;
				string refusal = null;
				var clock = Stopwatch.StartNew();
				double searchMs = 0;
				foreach (var target in Dismantle.TargetsInOrder(controller, item))
				{
					foreach (var grid in target.Grids)
					{
						var address = Dismantle.FindFreePlace(grid, item);
						searchMs = clock.Elapsed.TotalMilliseconds;
						if (address == null)
						{
							continue;
						}

						var operation = ItemManipulator.SplitExact(item, count, address, controller, controller, true);
						if (operation.Failed)
						{
							refusal = refusal ?? operation.Error?.ToString();
							continue;
						}

						Plugin.Verbose($"Split: {item.ShortName.Localized()} x{count} into {target.ShortName.Localized()}");
						var opMs = clock.Elapsed.TotalMilliseconds;
						_worstFrame = 0f;
						_worstAt = 0;
						_frame = 0;
						_framesLeft = 60;
						_perf = null;
						var outcome = await ItemUiContext.RunWithSound(controller, item, operation);
						_perf = $"{item.ShortName.Localized()} in {target.ShortName.Localized()}, free place found after {searchMs:0.0} ms, split op built after {opMs:0.0} ms, transaction + sound {clock.Elapsed.TotalMilliseconds - opMs:0.0} ms";
						if (outcome.Failed)
						{
							Plugin.Log.LogWarning($"Split: {item.ShortName.Localized()} failed ({outcome.Error})");
						}

						return;
					}
				}

				Plugin.Verbose($"Split: {item.ShortName.Localized()} not split ({refusal ?? "no free place"})");
				NotificationManager.DisplayMessageNotification(refusal == null ? "Split: no room for the new stack." : "Split: the game doesn't allow it here.");
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Split failed: {e}");
			}
			finally
			{
				_running = false;
			}
		}
	}
}
