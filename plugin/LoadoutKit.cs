using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI;

namespace CustomRpTweaks.Client
{
	// Brings a preset list of consumables from the stash into pockets, then vest. Each group has a target; what you already
	// carry counts towards it, and the stash fills the rest in priority order. A target of 0 disables the group.
	// Meds are counted in health resource, bandages and heavy bleed meds in "points", the others in items.
	internal static class LoadoutKit
	{
		private class Entry
		{
			public string Template;
			public Func<Item, float> Weight;
		}

		private class Group
		{
			public Func<int> Target;
			public Entry[] Entries; // in priority order
		}

		private static Entry Count(string template) => new Entry { Template = template, Weight = item => 1f };
		private static Entry Points(string template, float points) => new Entry { Template = template, Weight = item => points };
		private static Entry Resource(string template) => new Entry { Template = template, Weight = ResourceOf };

		private static Group[] Groups => _groups ?? (_groups = new[]
		{
			new Group
			{
				Target = () => Plugin.KitHealthResource.Value,
				Entries = new[]
				{
					Resource("60098ad7c2240c0fe85c570a"), // AFAK
					Resource("544fb45d4bdc2dee738b4568"), // Salewa
					Resource("590c678286f77426c9660122"), // IFAK
					Resource("590c661e86f7741e566b646a"), // Car
					Resource("5755356824597772cb798962")  // AI-2
				}
			},
			new Group
			{
				Target = () => Plugin.KitBandageResource.Value,
				Entries = new[]
				{
					Points("5751a25924597722c463c472", 2f), // Army bandage
					Points("544fb25a4bdc2dfb738b4567", 1f)  // Aseptic bandage
				}
			},
			new Group
			{
				Target = () => Plugin.KitHeavyBleedResource.Value,
				Entries = new[]
				{
					Points("5e8488fa988a8701445df1e4", 2f), // CALOK-B
					Points("60098af40accd37ef2175f27", 1f), // CAT
					Points("5e831507ea0a7c419c2f9bd9", 1f)  // Esmarch
				}
			},
			new Group
			{
				Target = () => Plugin.KitPropital.Value,
				Entries = new[] { Count("5c0e530286f7747fa1419862") }
			},
			new Group
			{
				Target = () => Plugin.KitGrenades.Value,
				Entries = new[]
				{
					Count("58d3db5386f77426186285a0"), // M67
					Count("5448be9a4bdc2dfd2f8b456a"), // RGD-5
					Count("5710c24ad2720bc3458b45a3"), // F-1
					Count("67b49e7335dec48e3e05e057"), // F-1 (reduced delay)
					Count("5e32f56fcb6d5863cc5e5ee4"), // VOG-17
					Count("5e340dcdcb6d5863cc5e5efb")  // VOG-25
				}
			}
		});

		private static Group[] _groups;
		private static bool _running;
		private static int _swapped;

		internal static bool AnyGroupEnabled => Groups.Any(group => group.Target() > 0);

		internal static async void Run(InventoryController controller)
		{
			Plugin.Log.LogInfo($"Kit: button pressed (running: {_running})");
			if (_running)
			{
				return;
			}

			_running = true;
			try
			{
				_swapped = 0;
				int moved = await Fetch(controller);
				var message = moved + _swapped > 0 ? $"Kit: brought {moved} item(s), swapped {_swapped}." : "Kit: nothing to bring.";
				NotificationManager.DisplayMessageNotification(message);
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Kit failed: {e}");
			}
			finally
			{
				_running = false;
			}
		}

		private static async Task<int> Fetch(InventoryController controller)
		{
			var inventory = controller.Inventory;
			var stash = inventory.Stash;
			if (stash == null)
			{
				Plugin.Log.LogInfo("Kit: no stash");
				return 0;
			}

			var targets = new[] { EquipmentSlot.Pockets, EquipmentSlot.TacticalVest, EquipmentSlot.ArmorVest }
				.Select(slot => inventory.Equipment.GetSlot(slot).ContainedItem as CompoundItem)
				.Where(container => container != null)
				.ToList();

			Plugin.Log.LogInfo($"Kit: {targets.Count} target containers, {inventory.GetPlayerItems(EPlayerItems.Stash).Count()} items in stash");
			int moved = 0;
			foreach (var group in Groups.Where(group => group.Target() > 0))
			{
				if (Plugin.KitSwapWorse.Value)
				{
					await SwapWorse(controller, inventory, group, targets);
				}

				// Carried and stash items are re-read per group, since earlier groups and the swaps changed the inventory
				var carried = CarriedItems(inventory).ToList();
				var inStash = inventory.GetPlayerItems(EPlayerItems.Stash).ToList();

				float need = group.Target() - carried.Sum(item => WeightOf(group, item));
				Plugin.Log.LogInfo($"Kit: group {group.Entries[0].Template}: target {group.Target()}, carrying {group.Target() - need}, {Candidates(inStash, group).Count()} candidates in stash");
				foreach (var item in Candidates(inStash, group))
				{
					if (need <= 0)
					{
						break;
					}

					float weight = WeightOf(group, item);
					if (weight <= 0 || (!Plugin.KitAllowOvershoot.Value && weight > need))
					{
						Plugin.Log.LogInfo($"Kit: skipping {item.ShortName.Localized()} (weight {weight}, need {need})");
						continue;
					}

					if (await MoveToTargets(controller, item, targets))
					{
						need -= weight;
						moved++;
					}
				}
			}

			return moved;
		}

		// Replaces carried items by better ones from the stash: the carried one goes to the stash first, and goes back if
		// the better one can't be brought. Only items needed to reach the target are swapped: the best carried items are
		// taken until the target is met, and anything beyond that is a spare that is left alone. A swap must not lower the
		// total of the needed items (or the target).
		private static async Task SwapWorse(InventoryController controller, Inventory inventory, Group group, List<CompoundItem> targets)
		{
			var stash = inventory.Stash;
			var stashTargets = new List<CompoundItem> { stash };
			var handled = new HashSet<string>();

			for (int guard = 0; guard < 20; guard++)
			{
				var needed = new List<Item>();
				float total = 0f;
				foreach (var item in CarriedItems(inventory).Where(item => WeightOf(group, item) > 0).OrderBy(item => PriorityOf(group, item)).ThenByDescending(ResourceOf))
				{
					if (total >= group.Target())
					{
						break;
					}

					needed.Add(item);
					total += WeightOf(group, item);
				}

				var carried = needed.Where(item => !handled.Contains(item.Id.ToString())).ToList();
				var inStash = inventory.GetPlayerItems(EPlayerItems.Stash).Where(item => !handled.Contains(item.Id.ToString())).ToList();
				var candidates = Candidates(inStash, group).ToList();

				Item worse = null;
				Item better = null;
				foreach (var current in carried.OrderByDescending(item => PriorityOf(group, item)).ThenBy(ResourceOf))
				{
					better = candidates.FirstOrDefault(candidate => IsBetter(group, candidate, current)
						&& total - WeightOf(group, current) + WeightOf(group, candidate) >= Math.Min(total, group.Target()));
					if (better != null)
					{
						worse = current;
						break;
					}
				}

				if (worse == null)
				{
					return;
				}

				handled.Add(worse.Id.ToString());
				handled.Add(better.Id.ToString());

				if (!await MoveToTargets(controller, worse, stashTargets))
				{
					continue;
				}

				if (await MoveToTargets(controller, better, targets))
				{
					_swapped++;
				}
				else
				{
					Plugin.Log.LogWarning($"Kit: could not swap in {better.ShortName.Localized()}, putting {worse.ShortName.Localized()} back");
					await MoveToTargets(controller, worse, targets);
				}
			}
		}

		// Higher priority template, or the same template with more resource, or non-FiR instead of FiR at no less resource
		private static bool IsBetter(Group group, Item candidate, Item current)
		{
			int candidatePriority = PriorityOf(group, candidate);
			int currentPriority = PriorityOf(group, current);
			if (candidatePriority != currentPriority)
			{
				return candidatePriority < currentPriority;
			}

			if (candidate.MarkedAsSpawnedInSession == current.MarkedAsSpawnedInSession)
			{
				return ResourceOf(candidate) > ResourceOf(current);
			}

			return current.MarkedAsSpawnedInSession && ResourceOf(candidate) >= ResourceOf(current);
		}

		private static int PriorityOf(Group group, Item item)
		{
			var template = item.TemplateId.ToString();
			return Array.FindIndex(group.Entries, entry => entry.Template == template);
		}

		private static async Task<bool> MoveToTargets(InventoryController controller, Item item, List<CompoundItem> targets)
		{
			var result = ItemManipulator.QuickFindAppropriatePlace(item, controller, targets, ItemManipulator.EMoveItemOrder.PrioritizeTargetsOrder, true);
			if (result.Failed)
			{
				Plugin.Log.LogInfo($"Kit: no room for {item.ShortName.Localized()} ({result.Error})");
				return false;
			}

			Plugin.Log.LogInfo($"Kit: moving {item.ShortName.Localized()}");
			var outcome = await controller.TryRunNetworkTransaction(result);
			if (outcome.Failed)
			{
				Plugin.Log.LogWarning($"Kit: moving {item.ShortName.Localized()} failed ({outcome.Error})");
				return false;
			}

			return true;
		}

		// Pockets, vest, armored vest and backpack; not the secured container
		private static IEnumerable<Item> CarriedItems(Inventory inventory)
		{
			foreach (var slot in new[] { EquipmentSlot.Pockets, EquipmentSlot.TacticalVest, EquipmentSlot.ArmorVest, EquipmentSlot.Backpack })
			{
				var contained = inventory.Equipment.GetSlot(slot).ContainedItem;
				if (contained == null)
				{
					continue;
				}

				foreach (var item in contained.GetAllItems())
				{
					yield return item;
				}
			}
		}

		private static float WeightOf(Group group, Item item)
		{
			var template = item.TemplateId.ToString();
			var entry = group.Entries.FirstOrDefault(candidate => candidate.Template == template);
			return entry == null ? 0f : entry.Weight(item);
		}

		// Group entries in priority order; within one template, non-FiR first, then the fullest resource
		private static IEnumerable<Item> Candidates(List<Item> items, Group group)
		{
			foreach (var entry in group.Entries)
			{
				foreach (var item in items
					.Where(item => item.TemplateId.ToString() == entry.Template)
					.OrderBy(item => item.MarkedAsSpawnedInSession ? 1 : 0)
					.ThenByDescending(ResourceOf))
				{
					yield return item;
				}
			}
		}

		private static float ResourceOf(Item item) => item.GetItemComponent<MedKitComponent>()?.HpResource ?? 0f;
	}
}
