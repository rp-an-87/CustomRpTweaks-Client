using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;

namespace CustomRpTweaks.Client
{
	// Hotkey on a hovered armor plate in the stash: puts it into the first empty plate slot of the armor, rig or helmet you wear
	// that accepts it. It is the game's own move into that slot, so its filters and conflict rules decide what fits. Not in a raid,
	// where the game doesn't let you do it either.
	internal static class EquipPlate
	{
		private static readonly FieldInfo ControllerField = typeof(ItemUiContext).GetField("_inventoryController", BindingFlags.Instance | BindingFlags.NonPublic);

		private static readonly EquipmentSlot[] WornOrder = { EquipmentSlot.ArmorVest, EquipmentSlot.TacticalVest, EquipmentSlot.Headwear };

		private static bool _running;

		// Whether the hotkey would do something for the item: a plate that isn't worn yet and a worn armor with a slot that takes it
		internal static bool IsApplicable(InventoryController controller, Item item)
		{
			if (!(item is ArmorPlate plate) || controller?.Inventory.Stash == null || InGameStatus.InRaid || IsWorn(plate))
			{
				return false;
			}

			foreach (var armor in WornArmor(controller.Inventory.Equipment))
			{
				foreach (var slot in armor.AllSlots)
				{
					if (slot.CanAccept(plate) && !ItemManipulator.Move(plate, slot.CreateItemAddress(), controller, true).Failed)
					{
						return true;
					}
				}
			}

			return false;
		}

		internal static void Update()
		{
			if (_running || !Plugin.EquipPlateHotkey.Value.IsDown())
			{
				return;
			}

			var uiContext = ItemUiContext.Instance;
			var plate = uiContext?.CurrentItemContext?.Item as ArmorPlate;
			var controller = uiContext == null ? null : ControllerField.GetValue(uiContext) as InventoryController;
			if (plate == null || controller?.Inventory.Stash == null || InGameStatus.InRaid || IsWorn(plate))
			{
				return;
			}

			Run(controller, plate);
		}

		private static async void Run(InventoryController controller, ArmorPlate plate)
		{
			_running = true;
			try
			{
				foreach (var armor in WornArmor(controller.Inventory.Equipment))
				{
					foreach (var slot in armor.AllSlots)
					{
						if (!slot.CanAccept(plate))
						{
							continue;
						}

						var operation = ItemManipulator.Move(plate, slot.CreateItemAddress(), controller, true);
						if (operation.Failed)
						{
							continue;
						}

						var outcome = await ItemUiContext.RunWithSound(controller, plate, operation);
						if (outcome.Failed)
						{
							Plugin.Log.LogWarning($"Equip plate: {plate.ShortName.Localized()} into {armor.ShortName.Localized()} failed ({outcome.Error})");
							return;
						}

						Plugin.Log.LogInfo($"Equip plate: {plate.ShortName.Localized()} into {armor.ShortName.Localized()}");
						return;
					}
				}

				Plugin.Log.LogInfo($"Equip plate: no worn armor has a free slot for {plate.ShortName.Localized()}");
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Equip plate failed: {e}");
			}
			finally
			{
				_running = false;
			}
		}

		// Body armor, then the rig, then the helmet, then whatever else you wear that has slots
		private static IEnumerable<CompoundItem> WornArmor(InventoryEquipment equipment)
		{
			var worn = equipment.Slots.Select(slot => slot.ContainedItem).OfType<CompoundItem>().ToList();
			return WornOrder.Select(slot => equipment.GetSlot(slot).ContainedItem as CompoundItem)
				.Where(armor => armor != null)
				.Concat(worn)
				.Distinct()
				.Where(armor => armor.Slots.Length > 0);
		}

		// Already in a slot of something you wear (not just carried in a backpack)
		private static bool IsWorn(Item item)
		{
			for (var depth = 0; item != null && depth < 64; depth++)
			{
				if (item.Parent?.Container is Grid)
				{
					return false;
				}

				if (item.Parent is SlotItemAddress address && address.Container?.ParentItem is InventoryEquipment)
				{
					return true;
				}

				item = item.Parent is OwnerItself ? null : item.Parent?.Container?.ParentItem;
			}

			return false;
		}
	}
}
