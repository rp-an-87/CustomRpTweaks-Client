using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI;

namespace CustomRpTweaks.Client
{
	// Hotkey on a hovered item: detaches the parts attached to it and puts them next to it, in the container the item lies in.
	// Recursive mode goes on with the parts that came off (a scope on a mount on a rail), until nothing more can be taken off.
	// Every move is the game's own, so what it refuses (in a raid: parts that can't be modded, vital parts, locked armor slots)
	// stays attached, and nothing below such a part is touched. Magazines keep their ammo and containers keep their contents.
	internal static class Disassemble
	{
		private const int MaxMoves = 300;

		private static readonly FieldInfo ControllerField = typeof(ItemUiContext).GetField("_inventoryController", BindingFlags.Instance | BindingFlags.NonPublic);

		private static bool _running;

		internal static void Update()
		{
			if (_running || !Plugin.DisassembleHotkey.Value.IsDown())
			{
				return;
			}

			var uiContext = ItemUiContext.Instance;
			var hovered = uiContext?.CurrentItemContext?.Item;
			var item = hovered as CompoundItem;
			var controller = uiContext == null ? null : ControllerField.GetValue(uiContext) as InventoryController;
			if (item == null || controller == null || (InGameStatus.InRaid && !Plugin.DisassembleInRaid.Value))
			{
				Plugin.Log.LogInfo($"Disassemble: nothing done, hovered item: {(hovered == null ? "none" : NameOf(hovered) + " (" + hovered.GetType().Name + ")")}, controller: {controller != null}, in raid: {InGameStatus.InRaid}");
				return;
			}

			Run(controller, item);
		}

		private static async void Run(InventoryController controller, CompoundItem item)
		{
			_running = true;
			try
			{
				var targets = Targets(controller, item);
				if (targets.Count == 0)
				{
					Plugin.Log.LogInfo($"Disassemble: {NameOf(item)} ({item.GetType().Name}): no container to put the parts in");
					NotificationManager.DisplayMessageNotification($"Disassemble: no container to put the parts of {NameOf(item)} in.");
					return;
				}

				var tally = new Tally();
				var queue = new Queue<CompoundItem>();
				var recursive = Plugin.DisassembleRecursive.Value;
				if (IsAttachedPart(item))
				{
					// A part attached to something else (not worn gear): it comes off itself, and, recursively, its parts after it
					if (await TakeOff(controller, item, targets, tally) && recursive)
					{
						queue.Enqueue(item);
					}
				}
				else
				{
					queue.Enqueue(item);
				}

				while (queue.Count > 0 && tally.Attempts < MaxMoves)
				{
					var holder = queue.Dequeue();
					foreach (var slot in holder.Slots.ToList())
					{
						var part = slot.ContainedItem;
						if (part == null || part is Ammo)
						{
							continue;
						}

						if (await TakeOff(controller, part, targets, tally) && recursive && part is CompoundItem compound && !(part is Magazine))
						{
							queue.Enqueue(compound);
						}
					}
				}

				Report(item, tally);
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Disassemble failed: {e}");
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
			public int Unexamined;
			public string FirstRefusal;
		}

		private static async Task<bool> TakeOff(InventoryController controller, Item part, List<CompoundItem> targets, Tally tally)
		{
			if (part.PinLockState == EItemPinLockState.Locked)
			{
				tally.Refused++;
				tally.FirstRefusal = tally.FirstRefusal ?? "locked";
				return false;
			}

			if (!controller.Examined(part))
			{
				tally.Unexamined++;
				return false;
			}

			tally.Attempts++;
			bool anySpace = false;
			foreach (var target in targets)
			{
				foreach (var grid in target.Grids)
				{
					var address = grid.FindLocationForItem(part);
					if (address == null)
					{
						continue;
					}

					anySpace = true;
					var operation = ItemManipulator.Move(part, address, controller, true);
					if (operation.Failed)
					{
						// A rule of the game (not a lack of space), the same for every place
						tally.Refused++;
						tally.FirstRefusal = tally.FirstRefusal ?? operation.Error?.ToString();
						Plugin.Log.LogInfo($"Disassemble: {NameOf(part)} stays ({operation.Error})");
						return false;
					}

					var outcome = await controller.TryRunNetworkTransaction(operation);
					if (outcome.Failed)
					{
						tally.Refused++;
						tally.FirstRefusal = tally.FirstRefusal ?? outcome.Error?.ToString();
						Plugin.Log.LogWarning($"Disassemble: moving {NameOf(part)} failed ({outcome.Error})");
						return false;
					}

					tally.Moved++;
					Plugin.Log.LogInfo($"Disassemble: {NameOf(part)} taken off into {target.ShortName.Localized()}");
					return true;
				}
			}

			if (!anySpace)
			{
				tally.NoRoom++;
			}

			return false;
		}

		// Attached in a slot of an item that is not the character's equipment (a mod on a weapon, a plate in armor, an insert)
		private static bool IsAttachedPart(Item item)
		{
			return item.Parent is SlotItemAddress address && !(address.Container?.ParentItem is InventoryEquipment);
		}

		// Where the parts may go, best first: the container the item (or what it is attached to) lies in, then, for something
		// carried by a player or a dead body, their pockets, vest and backpack. Stash items only go to the stash grid.
		private static List<CompoundItem> Targets(InventoryController controller, Item item)
		{
			var targets = new List<CompoundItem>();
			void Add(CompoundItem container)
			{
				if (container != null && container.Grids != null && container.Grids.Length > 0 && !targets.Contains(container))
				{
					targets.Add(container);
				}
			}

			// The first ancestor (or the item itself) that lies in a grid
			for (var current = item; current != null; current = HolderOf(current))
			{
				if (current.Parent?.Container is Grid grid)
				{
					Add(grid.ParentItem);
					break;
				}
			}

			// The top of the chain the item hangs on: the stash, or the equipment of a character (worn gear and everything in it)
			var top = item;
			for (var depth = 0; depth < 64 && HolderOf(top) != null; depth++)
			{
				top = HolderOf(top);
			}

			if (top is InventoryEquipment equipment)
			{
				// Your own gear, in the menu: the stash is part of your inventory too
				if (equipment == controller.Inventory.Equipment)
				{
					Add(controller.Inventory.Stash);
				}

				foreach (var slot in new[] { EquipmentSlot.Pockets, EquipmentSlot.TacticalVest, EquipmentSlot.Backpack })
				{
					Add(equipment.GetSlot(slot).ContainedItem as CompoundItem);
				}
			}

			targets.RemoveAll(target => target == item || IsInside(target, item));
			return targets;
		}

		// The item this one is attached to or lies in. A root item (the stash, the equipment) belongs to its owner, whose
		// parent item is the root itself, so that is where the chain ends.
		private static Item HolderOf(Item item)
		{
			if (item.Parent == null || item.Parent is OwnerItself)
			{
				return null;
			}

			var holder = item.Parent.Container?.ParentItem;
			return holder == item ? null : holder;
		}

		private static bool IsInside(Item candidate, Item ancestor)
		{
			for (var current = candidate; current != null; current = HolderOf(current))
			{
				if (current == ancestor)
				{
					return true;
				}
			}

			return false;
		}

		// Some templates (armor plates among them) have no short name in the locale
		private static string NameOf(Item item)
		{
			var name = item.ShortName.Localized();
			if (string.IsNullOrEmpty(name))
			{
				name = item.Name.Localized();
			}

			return string.IsNullOrEmpty(name) ? item.TemplateId.ToString() : name;
		}

		private static void Report(Item item, Tally tally)
		{
			Plugin.Log.LogInfo($"Disassemble: {NameOf(item)}: {tally.Moved} removed, {tally.NoRoom} without room, {tally.Refused} refused, {tally.Unexamined} not examined, in raid: {InGameStatus.InRaid}");
			if (tally.Moved > 0)
			{
				Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.MenuWeaponDisassemble);
			}

			// The sound says it worked; the only thing worth a message is a part that had nowhere to go
			if (tally.NoRoom > 0)
			{
				NotificationManager.DisplayMessageNotification($"Disassemble: no room for {tally.NoRoom} part{(tally.NoRoom == 1 ? "" : "s")} of {NameOf(item)}.");
			}
		}
	}
}
