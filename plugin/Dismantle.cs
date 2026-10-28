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
using EFT.UI.DragAndDrop;

namespace CustomRpTweaks.Client
{
	// Hotkey on a hovered item: detaches the parts attached to it and puts them next to it, in the container the item lies in.
	// It goes on with the parts that came off (a scope on a mount on a rail), until nothing more can be taken off.
	// Every move is the game's own, so what it refuses (in a raid: parts that can't be modded, vital parts, locked armor slots)
	// stays attached, but the parts attached to it can still come off. Magazines keep their ammo and containers keep their contents,
	// but a round in the chamber comes out.
	// A second hotkey does the same but leaves the vital parts (the ones in required slots, a receiver for instance) attached wherever it is
	// used, not only in a raid, so what comes off is the rest of the gun.
	internal static class Dismantle
	{
		private const int MaxMoves = 300;
		private const int RetrySweeps = 4;

		private static readonly FieldInfo ControllerField = typeof(ItemUiContext).GetField("_inventoryController", BindingFlags.Instance | BindingFlags.NonPublic);

		private static bool _running;

		// Whether the hotkey would do something for the item: it has parts attached (or, with the setting on, is one itself). hasVitalParts: some of them are
		// in required slots, which the keep-vital hotkey leaves on
		internal static bool IsApplicable(Item item, out bool hasVitalParts)
		{
			hasVitalParts = false;
			if (!(item is CompoundItem root) || (InGameStatus.InRaid && !Plugin.DismantleInRaid.Value))
			{
				return false;
			}

			var any = Plugin.DismantleSelf.Value && IsAttachedPart(root);
			var queue = new Queue<CompoundItem>();
			queue.Enqueue(root);
			for (var visited = 0; queue.Count > 0 && visited < 200; visited++)
			{
				foreach (var slot in queue.Dequeue().Slots)
				{
					var part = slot.ContainedItem;
					if (part == null || part is Ammo)
					{
						continue;
					}

					any = true;
					hasVitalParts |= slot.Required;
					if (part is CompoundItem compound && !(part is Magazine))
					{
						queue.Enqueue(compound);
					}
				}
			}

			return any;
		}

		internal static void Update()
		{
			if (_running)
			{
				return;
			}

			// Checked first: its keys include the other hotkey's by default, which would match as well
			var keepVital = Plugin.DismantleNonVitalHotkey.Value.IsDown();
			if (!keepVital && !Plugin.DismantleHotkey.Value.IsDown())
			{
				return;
			}

			var uiContext = ItemUiContext.Instance;
			var hovered = uiContext?.CurrentItemContext?.Item;
			var item = hovered as CompoundItem;
			var controller = uiContext == null ? null : ControllerField.GetValue(uiContext) as InventoryController;
			if (item == null || controller == null || (InGameStatus.InRaid && !Plugin.DismantleInRaid.Value))
			{
				Plugin.Log.LogInfo($"Dismantle: nothing done, hovered item: {(hovered == null ? "none" : NameOf(hovered) + " (" + hovered.GetType().Name + ")")}, controller: {controller != null}, in raid: {InGameStatus.InRaid}");
				return;
			}

			Run(controller, item, keepVital);
		}

		private static async void Run(InventoryController controller, CompoundItem item, bool keepVital)
		{
			_running = true;
			try
			{
				var targets = Targets(controller, item);
				if (targets.Count == 0)
				{
					Plugin.Log.LogInfo($"Dismantle: {NameOf(item)} ({item.GetType().Name}): no container to put the parts in");
					NotificationManager.DisplayMessageNotification($"Dismantle: no container to put the parts of {NameOf(item)} in.");
					return;
				}

				var tally = new Tally { KeepVital = keepVital };
				var queue = new Queue<CompoundItem>();
				if (Plugin.DismantleSelf.Value && IsAttachedPart(item))
				{
					// A part attached to something else (not worn gear): it comes off itself, and its parts after it
					var moved = await TakeOff(controller, item, targets, tally);
					if (moved || controller.Examined(item))
					{
						queue.Enqueue(item);
					}
				}
				else
				{
					// Only what is attached to the hovered item comes off, not the item itself (UI Fixes' uninstall does that)
					queue.Enqueue(item);
				}

				var holders = 0;
				async Task Drain()
				{
					while (queue.Count > 0 && tally.Attempts < MaxMoves && holders++ < MaxMoves)
					{
						var holder = queue.Dequeue();

						// A round in the chamber is not in a mod slot (Weapon.Chambers is separate), so it goes first, next to the parts
						if (holder is Weapon weapon)
						{
							foreach (var chamber in weapon.Chambers.ToList())
							{
								if (chamber.ContainedItem is Ammo round)
								{
									await TakeOff(controller, round, targets, tally);
								}
							}
						}
						foreach (var slot in holder.Slots.ToList())
						{
							var part = slot.ContainedItem;
							if (part == null || part is Ammo)
							{
								continue;
							}

							// A part that has to stay (vital, not moddable in a raid, no room) still gets its own parts taken off:
							// a scope comes off a receiver that can't. Only a part that was not examined is left alone.
							var moved = await TakeOff(controller, part, targets, tally);
							if (part is CompoundItem compound && !(part is Magazine) && (moved || controller.Examined(part)))
							{
								queue.Enqueue(compound);
							}
						}
					}
				}

				await Drain();

				// In a raid the game treats the parts of a part that has just been moved as not existing for a moment (UnknownItemError),
				// so they were refused. Try those again after a short wait, with what comes off them.
				for (var sweep = 1; sweep <= RetrySweeps && tally.Unknown.Count > 0 && tally.Attempts < MaxMoves; sweep++)
				{
					await Task.Delay(300 * sweep);
					var retry = tally.Unknown.ToList();
					tally.Unknown.Clear();
					foreach (var part in retry)
					{
						if (!(part.Parent is SlotItemAddress))
						{
							continue;
						}

						var moved = await TakeOff(controller, part, targets, tally);
						if (moved)
						{
							Plugin.Log.LogInfo($"Dismantle: {NameOf(part)} came off on retry {sweep}");
						}

						if (part is CompoundItem compound && !(part is Magazine) && (moved || controller.Examined(part)))
						{
							queue.Enqueue(compound);
						}
					}

					await Drain();
				}

				if (tally.Unknown.Count > 0)
				{
					tally.Refused += tally.Unknown.Count;
					tally.FirstRefusal = tally.FirstRefusal ?? "not known to the game yet";
				}

				Report(item, tally);
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Dismantle failed: {e}");
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
			public bool KeepVital;
			public string FirstRefusal;
			// Parts the game refused as unknown right after a move, to try again
			public readonly List<Item> Unknown = new List<Item>();
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

			// The game's raid rules for taking a part off, checked first: they hold wherever the part would go, and without this
			// a part the rules forbid could be reported as "no room" whenever it is too big for the free space
			var forbidden = RuleViolation(part, tally.KeepVital);
			if (forbidden != null)
			{
				tally.Refused++;
				tally.FirstRefusal = tally.FirstRefusal ?? forbidden;
				Plugin.Log.LogInfo($"Dismantle: {NameOf(part)} stays ({forbidden})");
				return false;
			}

			tally.Attempts++;
			bool anySpace = false;
			foreach (var target in targets)
			{
				foreach (var grid in target.Grids)
				{
					var address = FindFreePlace(grid, part);
					if (address == null)
					{
						continue;
					}

					anySpace = true;
					var operation = ItemManipulator.Move(part, address, controller, true);
					if (operation.Failed && operation.Error is UnknownItemError)
					{
						tally.Unknown.Add(part);
						Plugin.Log.LogInfo($"Dismantle: {NameOf(part)} is not known to the game yet, will try again");
						return false;
					}

					if (operation.Failed)
					{
						// A rule of the game (not a lack of space), the same for every place
						tally.Refused++;
						tally.FirstRefusal = tally.FirstRefusal ?? operation.Error?.ToString();
						Plugin.Log.LogInfo($"Dismantle: {NameOf(part)} stays ({operation.Error})");
						return false;
					}

					var outcome = await controller.TryRunNetworkTransaction(operation);
					if (outcome.Failed)
					{
						tally.Refused++;
						tally.FirstRefusal = tally.FirstRefusal ?? outcome.Error?.ToString();
						Plugin.Log.LogWarning($"Dismantle: moving {NameOf(part)} failed ({outcome.Error})");
						return false;
					}

					tally.Moved++;
					Plugin.Log.LogInfo($"Dismantle: {NameOf(part)} taken off into {target.ShortName.Localized()}");
					return true;
				}
			}

			if (!anySpace)
			{
				tally.NoRoom++;
			}

			return false;
		}

		// The raid-only part of the game's move rules (ItemManipulator.MovePathCheck): parts that can't be modded in a raid, and vital slots.
		// With keepVital the vital slots are kept anywhere, not only in a raid.
		private static string RuleViolation(Item part, bool keepVital)
		{
			if (!(part.Parent is SlotItemAddress address))
			{
				return null;
			}

			if (InGameStatus.InRaid && part is Mod mod && !mod.RaidModdable)
			{
				return "can't be modded in a raid";
			}

			return (InGameStatus.InRaid || keepVital) && address.Slot.Required ? "vital part" : null;
		}

		// Attached in a slot of an item that is not the character's equipment (a mod on a weapon, a plate in armor, an insert)
		private static bool IsAttachedPart(Item item)
		{
			return item.Parent is SlotItemAddress address && !(address.Container?.ParentItem is InventoryEquipment);
		}

		// Where the parts may go, best first, the same both ways round: the container the item (or what it is attached to) lies in, then
		// any container its owner carries (no exclusions, in no particular order: any free place is fair game), then any other
		// inventory open on screen (a bag you opened, a body), and last the other side: for your own gear in the menu the stash, for
		// someone else's gear (a body, another character) or something lying in a bag that is not yours, what you carry. Stash items
		// only go to the stash grid. Used for the parts taken off an item, for the round taken out of a chamber and for the new stack
		// of a split.
		internal static List<CompoundItem> Targets(InventoryController controller, Item item)
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
				foreach (var carried in CarriedContainers(equipment, equipment == controller.Inventory.Equipment))
				{
					Add(carried);
				}

				// An inventory you have open counts too (a bag on the ground, a body), before the other side
				foreach (var open in OpenContainers(controller))
				{
					Add(open);
				}

				if (equipment == controller.Inventory.Equipment)
				{
					// Your own gear, in the menu: the stash is part of your inventory too, but what you carry stays on you first
					Add(controller.Inventory.Stash);
				}
				else
				{
					// Someone else's gear (a dead body, another character): when theirs and the open ones are full, what you carry
					foreach (var carried in CarriedContainers(controller.Inventory.Equipment))
					{
						Add(carried);
					}
				}
			}
			else if (top != controller.Inventory.Stash)
			{
				// Something that lies in a container that is neither yours nor the stash (a bag on the ground): its own container, then
				// any other inventory open, then what you carry
				foreach (var open in OpenContainers(controller))
				{
					Add(open);
				}

				foreach (var carried in CarriedContainers(controller.Inventory.Equipment))
				{
					Add(carried);
				}
			}

			targets.RemoveAll(target => target == item || IsInside(target, item));
			return targets;
		}

		// Every container a character carries (pockets, vests, armored rigs, backpacks, the secured container...) in whatever order the
		// equipment lists them: any free place is fair game, as it is for the game itself. The secured container only counts for your own
		// gear: on a body (a boss has one too) it can't be opened, so whatever goes in is gone.
		internal static List<CompoundItem> CarriedContainers(InventoryEquipment equipment, bool includeSecured = true) =>
			equipment.Slots
				.Where(slot => includeSecured || slot.ID != nameof(EquipmentSlot.SecuredContainer))
				.Select(slot => slot.ContainedItem as CompoundItem)
				.Where(container => container != null && container.Grids != null && container.Grids.Length > 0)
				.ToList();

		// The containers of OTHER inventories shown on screen right now (a bag you opened, a dead body's pockets): their grids are on
		// display. Only characters' and loot inventories, not a trader's, the flea market's or the mail's, and not your own (those are
		// handled by the order of the owner and you, even though your backpack is on screen in a raid too).
		internal static List<CompoundItem> OpenContainers(InventoryController controller) =>
			UnityEngine.Object.FindObjectsOfType<GridView>()
				.Select(view => view.Grid?.ParentItem as CompoundItem)
				.Where(container => container != null && container.Owner != null && container.Owner.OwnerType == EOwnerType.Profile && !IsYours(controller, container))
				.Distinct()
				.ToList();

		// Lies in your equipment or your stash
		private static bool IsYours(InventoryController controller, Item item)
		{
			var top = item;
			for (var depth = 0; depth < 64 && HolderOf(top) != null; depth++)
			{
				top = HolderOf(top);
			}

			return top == controller.Inventory.Equipment || top == controller.Inventory.Stash;
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

		// A free place in the grid for the item, not counting the cell the item itself lies in: Grid.FindLocationForItem lifts an item
		// that is already in the grid out first, so it answers with the item's own cell, which a split can't use.
		internal static GridItemAddress FindFreePlace(Grid grid, Item item)
		{
			if (!grid.CheckCompatibility(item))
			{
				return null;
			}

			var size = item.CalculateCellSize();
			var location = grid.FindFreeSpaceInGrid(size.X, size.Y, ItemRotation.Horizontal);
			if (location == null && size.X != size.Y)
			{
				location = grid.FindFreeSpaceInGrid(size.Y, size.X, ItemRotation.Vertical);
			}

			return location == null ? null : grid.CreateItemAddress(location);
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
			Plugin.Log.LogInfo($"Dismantle: {NameOf(item)}: {tally.Moved} removed, {tally.NoRoom} without room, {tally.Refused} refused, {tally.Unexamined} not examined, in raid: {InGameStatus.InRaid}");
			if (tally.Moved > 0)
			{
				Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.MenuWeaponDisassemble);
			}

			// The sound says it worked; the only thing worth a message is a part that had nowhere to go
			if (tally.NoRoom > 0)
			{
				NotificationManager.DisplayMessageNotification($"Dismantle: no room for {tally.NoRoom} part{(tally.NoRoom == 1 ? "" : "s")} of {NameOf(item)}.");
			}
		}
	}
}
