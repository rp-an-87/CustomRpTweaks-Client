using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI;

namespace CustomRpTweaks.Client
{
	// Hotkey on a hovered weapon: takes the round (or the spent case) out of its chamber and puts it in a container, without unloading the
	// magazine or taking the weapon apart (UI Fixes' unload key leaves the chamber alone, and an unloaded weapon is what the flea market
	// wants). It goes where the parts of a dismantled item go (Dismantle.Targets): the container the weapon lies in first, then the
	// containers whoever wears it carries, in no particular order (yours, or a corpse's and then yours), then, in the menu, the stash. In a raid
	// there is no stash: when nothing takes the round, nothing is moved.
	internal static class Unchamber
	{
		private static readonly FieldInfo ControllerField = typeof(ItemUiContext).GetField("_inventoryController", BindingFlags.Instance | BindingFlags.NonPublic);

		private static bool _running;

		// A weapon with something in its chamber
		internal static bool IsApplicable(Item item) => item is Weapon weapon && weapon.Chambers.Any(chamber => chamber.ContainedItem is Ammo);

		internal static void Update()
		{
			if (_running || !Plugin.UnchamberHotkey.Value.IsDown())
			{
				return;
			}

			var uiContext = ItemUiContext.Instance;
			var weapon = uiContext?.CurrentItemContext?.Item as Weapon;
			var controller = uiContext == null ? null : ControllerField.GetValue(uiContext) as InventoryController;
			if (weapon != null && controller != null && IsApplicable(weapon))
			{
				Run(controller, weapon);
			}
		}

		private static string NameOf(Item item) => item.ShortName.Localized();

		private static async void Run(InventoryController controller, Weapon weapon)
		{
			_running = true;
			try
			{
				var rounds = weapon.Chambers.Select(chamber => chamber.ContainedItem as Ammo).Where(ammo => ammo != null).ToList();
				if (rounds.Count == 0)
				{
					return;
				}

				var targets = Dismantle.Targets(controller, weapon);
				var stash = controller.Inventory.Stash;
				if (!InGameStatus.InRaid && stash != null && !targets.Contains(stash))
				{
					targets.Add(stash);
				}

				foreach (var round in rounds)
				{
					var placed = await Place(controller, round, targets);
					if (placed.Error != null)
					{
						Plugin.Log.LogWarning($"Unchamber: {NameOf(round)} did not come out of {NameOf(weapon)}: {placed.Error}");
						NotificationManager.DisplayMessageNotification($"Unchamber: {NameOf(round)} stays in {NameOf(weapon)} ({placed.Error}).");
						return;
					}

				}

				NotificationManager.DisplayMessageNotification(rounds.Count == 1
					? $"Unchambered {NameOf(rounds[0])} from {NameOf(weapon)}."
					: $"Unchambered {rounds.Count} rounds from {NameOf(weapon)}.");
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Unchamber failed: {e}");
			}
			finally
			{
				_running = false;
			}
		}

		private class Placed
		{
			public string Error;
		}

		// The first free place in the targets, in order; a rule of the game that refuses the move stops it (it holds for every place)
		private static async Task<Placed> Place(InventoryController controller, Ammo round, System.Collections.Generic.List<CompoundItem> targets)
		{
			foreach (var target in targets)
			{
				foreach (var grid in target.Grids)
				{
					var address = Dismantle.FindFreePlace(grid, round);
					if (address == null)
					{
						continue;
					}

					var operation = ItemManipulator.Move(round, address, controller, true);
					if (operation.Failed)
					{
						return new Placed { Error = operation.Error?.ToString() };
					}

					var outcome = await controller.TryRunNetworkTransaction(operation);
					if (outcome.Failed)
					{
						return new Placed { Error = outcome.Error?.ToString() };
					}

					return new Placed();
				}
			}

			return new Placed { Error = "no room, nothing was moved" };
		}
	}
}
