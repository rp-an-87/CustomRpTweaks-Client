using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI;

namespace CustomRpTweaks.Client
{
	// Hotkey (and context menu entry) on a hovered container: moves everything lying directly in its grids out, one item at a time, to the first
	// place that takes it. The places are the ones Dismantle uses (Dismantle.Targets): the container it lies in, whatever its owner carries,
	// open inventories, and last the stash (your own gear) or what you carry (someone else's gear). Items that hold items (a case in a backpack)
	// move whole, with their contents. Every move is the game's own, so what it refuses stays where it is.
	internal static class Empty
	{
		private const int MaxMoves = 300;

		private static bool _running;

		// A container (it has grids) that is yours to move things in (not a trader's or the flea market's)
		internal static bool IsContainer(Item item)
		{
			return item is CompoundItem container && container.Grids != null && container.Grids.Length > 0 && Allowed(container);
		}

		// A container with something in its grids
		internal static bool IsApplicable(Item item)
		{
			return IsContainer(item) && ((CompoundItem)item).Grids.Any(grid => grid.Items.Any());
		}

		private static bool Allowed(Item item) => item.Owner != null && item.Owner.OwnerType == EOwnerType.Profile && (!InGameStatus.InRaid || Plugin.EmptyInRaid.Value);

		internal static void Update()
		{
			if (_running || !Plugin.EmptyHotkey.Value.IsDown())
			{
				return;
			}

			var uiContext = ItemUiContext.Instance;
			var hovered = uiContext?.CurrentItemContext?.Item;
			var controller = Dismantle.ControllerOf(uiContext);
			if (!(hovered is CompoundItem item) || controller == null)
			{
				Plugin.Verbose($"Empty: nothing done, hovered item: {(hovered == null ? "none" : hovered.GetType().Name)}, controller: {controller != null}");
				return;
			}

			Run(controller, item);
		}

		internal static async void Run(InventoryController controller, CompoundItem item)
		{
			if (_running)
			{
				return;
			}

			_running = true;
			try
			{
				if (item.Grids == null || item.Grids.Length == 0 || !Allowed(item))
				{
					Plugin.Verbose($"Empty: {Dismantle.NameOf(item)} ({item.GetType().Name}): not a container here, in raid: {InGameStatus.InRaid}");
					return;
				}

				// Biggest first: they are the hardest to find room for
				var contents = item.Grids.SelectMany(grid => grid.Items).Distinct().OrderByDescending(thing =>
				{
					var size = thing.CalculateCellSize();
					return size.X * size.Y;
				}).ToList();
				if (contents.Count == 0)
				{
					NotificationManager.DisplayMessageNotification($"Empty: {Dismantle.NameOf(item)} has nothing in it.");
					return;
				}

				var targets = Dismantle.Targets(controller, item);
				if (targets.Count == 0)
				{
					Plugin.Verbose($"Empty: {Dismantle.NameOf(item)}: no container to put the contents in");
					NotificationManager.DisplayMessageNotification($"Empty: no container to put the contents of {Dismantle.NameOf(item)} in.");
					return;
				}

				var tally = new Tally();
				foreach (var thing in contents)
				{
					if (tally.Attempts++ >= MaxMoves)
					{
						break;
					}

					await MoveOut(controller, thing, targets, tally);
				}

				Report(item, tally);
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Empty failed: {e}");
			}
			finally
			{
				_running = false;
			}
		}

		private class Tally
		{
			public int Attempts;
			public int Moved;
			public int NoRoom;
			public int Refused;
			public string FirstRefusal;
		}

		private static async Task MoveOut(InventoryController controller, Item thing, List<CompoundItem> targets, Tally tally)
		{
			if (thing.PinLockState == EItemPinLockState.Locked)
			{
				tally.Refused++;
				tally.FirstRefusal = tally.FirstRefusal ?? "locked";
				return;
			}

			var anySpace = false;
			string refusal = null;
			foreach (var target in targets)
			{
				// Never into itself (a case that is itself one of the places)
				if (Dismantle.IsInside(target, thing))
				{
					continue;
				}

				foreach (var grid in target.Grids)
				{
					var address = Dismantle.FindFreePlace(grid, thing);
					if (address == null)
					{
						continue;
					}

					anySpace = true;
					var error = await TryMove(controller, thing, address);
					if (error == null)
					{
						tally.Moved++;
						Plugin.Verbose($"Empty: {Dismantle.NameOf(thing)} moved into {target.ShortName.Localized()}");
						return;
					}

					refusal = refusal ?? error;
				}
			}

			if (anySpace)
			{
				tally.Refused++;
				tally.FirstRefusal = tally.FirstRefusal ?? refusal;
				Plugin.Verbose($"Empty: {Dismantle.NameOf(thing)} stays ({refusal})");
			}
			else
			{
				tally.NoRoom++;
			}
		}

		// null when the item moved, else why not. In a raid the game sometimes doesn't know an item yet right after another move: try again after a short wait.
		private static async Task<string> TryMove(InventoryController controller, Item thing, GridItemAddress address)
		{
			for (var attempt = 0; ; attempt++)
			{
				var operation = ItemManipulator.Move(thing, address, controller, true);
				if (operation.Failed)
				{
					if (operation.Error is UnknownItemError && attempt < 3)
					{
						await Task.Delay(300 * (attempt + 1));
						continue;
					}

					return operation.Error?.ToString() ?? "refused";
				}

				var outcome = await controller.TryRunNetworkTransaction(operation);
				if (outcome.Failed)
				{
					Plugin.Log.LogWarning($"Empty: moving {Dismantle.NameOf(thing)} failed ({outcome.Error})");
					return outcome.Error?.ToString() ?? "refused";
				}

				return null;
			}
		}

		private static void Report(Item item, Tally tally)
		{
			Plugin.Verbose($"Empty: {Dismantle.NameOf(item)}: {tally.Moved} moved, {tally.NoRoom} without room, {tally.Refused} refused ({tally.FirstRefusal}), in raid: {InGameStatus.InRaid}");
			if (tally.Moved > 0)
			{
				Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.BackpackOpen);
			}

			var left = new List<string>();
			if (tally.NoRoom > 0)
			{
				left.Add($"{tally.NoRoom} without room");
			}

			if (tally.Refused > 0)
			{
				left.Add($"{tally.Refused} refused by the game ({tally.FirstRefusal})");
			}

			if (left.Count > 0)
			{
				NotificationManager.DisplayMessageNotification($"Empty: {tally.NoRoom + tally.Refused} item{(tally.NoRoom + tally.Refused == 1 ? "" : "s")} stayed in {Dismantle.NameOf(item)}: {string.Join(", ", left)}.");
			}
		}
	}
}
