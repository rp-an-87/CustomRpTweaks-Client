using System;
using System.Reflection;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI;

namespace CustomRpTweaks.Client
{
	// Hotkey on a hovered stack (ammo, money, grenades, anything that stacks): splits it in half without the split dialog. The new
	// stack goes to the first free place next to it, in the container it lies in. It is the game's own split, so what the game
	// refuses (locked items, containers that can't take it) is refused here too.
	internal static class SplitStack
	{
		private static readonly FieldInfo ControllerField = typeof(ItemUiContext).GetField("_inventoryController", BindingFlags.Instance | BindingFlags.NonPublic);

		private static bool _running;

		// A stack of at least two that lies in a grid (not one inside a magazine, say)
		internal static bool IsApplicable(Item item) =>
			item != null && item.StackObjectsCount > 1 && item.StackMaxSize > 1 && item.Parent?.Container is Grid;

		internal static void Update()
		{
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
				foreach (var target in Dismantle.Targets(controller, item))
				{
					foreach (var grid in target.Grids)
					{
						var address = grid.FindLocationForItem(item);
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

						var outcome = await ItemUiContext.RunWithSound(controller, item, operation);
						if (outcome.Failed)
						{
							Plugin.Log.LogWarning($"Split: {item.ShortName.Localized()} failed ({outcome.Error})");
						}

						return;
					}
				}

				Plugin.Log.LogInfo($"Split: {item.ShortName.Localized()} not split ({refusal ?? "no free place"})");
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
