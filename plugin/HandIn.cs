using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.Quests;
using EFT.UI;

namespace CustomRpTweaks.Client
{
	// "Hand in" in the context menu of an item in the stash: when a started quest still wants this very item (its template, found in
	// raid, durability and so on, the same test the game uses for its own hand-over window), it goes to that quest with one click.
	// Only in the stash screens, never in a raid. Every step is the game's own: the quest controller's HandoverItem.
	internal static class HandIn
	{
		internal const string MenuKey = "Hand in";
		internal const string MenuId = "HandIn";

		// The yellow of the button, like Discard's red
		internal static readonly UnityEngine.Color Yellow = new UnityEngine.Color(0.91f, 0.72f, 0.19f);

		// The quest icon of the task list (the "bring an item" one), or none if the game's icons are not there
		private static UnityEngine.Sprite QuestIcon()
		{
			try
			{
				return EFTHardSettings.Instance.StaticIcons.QuestIconTypeSprites[EQuestIconType.PickUp];
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static readonly FieldInfo QuestControllerField = typeof(ItemUiContext).GetField("_questController", BindingFlags.Instance | BindingFlags.NonPublic);
		private static readonly FieldInfo InventoryControllerField = typeof(ItemUiContext).GetField("_inventoryController", BindingFlags.Instance | BindingFlags.NonPublic);

		// ContextInteractions<T>.method_2(id, key, callback, icon) adds a button of our own (the game's obfuscated name, found by its shape)
		private static readonly MethodInfo AddDynamic = typeof(ContextInteractions<EItemInfoButton>).GetMethods(BindingFlags.Instance | BindingFlags.Public)
			.FirstOrDefault(method =>
			{
				var parameters = method.GetParameters();
				return parameters.Length == 4 && parameters[0].ParameterType == typeof(string) && parameters[1].ParameterType == typeof(string)
					&& parameters[2].ParameterType == typeof(Action) && parameters[3].ParameterType == typeof(UnityEngine.Sprite);
			});

		private static bool _running;

		private static QuestController QuestControllerOf(ItemUiContext uiContext) => uiContext == null ? null : QuestControllerField.GetValue(uiContext) as QuestController;

		// One objective that takes the item: the quest, its condition, and how many of the item it still wants
		internal class Target
		{
			public Quest Quest;
			public ConditionHandoverItem Condition;
			public int Remaining;
		}

		// Every objective of a started quest that takes this item whole (usually none or one)
		internal static List<Target> FindAll(QuestController questController, Inventory inventory, Item item)
		{
			var found = new List<Target>();

			// The game's hand-over window only lets you pick items that are empty (no ammo in a magazine, nothing in a case, no mods on
			// a weapon) and not locked; quest items and money skip the window and are always taken
			if (item.PinLockState == EItemPinLockState.Locked || !(item.QuestItem || CurrencyUtil.IsCurrencyId(item.TemplateId) || item.IsEmpty()))
			{
				return found;
			}

			foreach (var candidate in questController.Quests)
			{
				if (candidate.QuestStatus != EQuestStatus.Started || !candidate.Conditions.TryGetValue(EQuestStatus.AvailableForFinish, out var conditions))
				{
					continue;
				}

				foreach (var entry in conditions)
				{
					var handover = entry as ConditionHandoverItem;
					if (handover == null || candidate.IsConditionDone(handover) || !handover.target.Contains(item.StringTemplateId))
					{
						continue;
					}

					// All the conditions of the quest line (found in raid, durability, dogtag level) are in the game's own list
					if (!ConditionalController<Quest>.GetItemsForCondition(inventory, handover).Contains(item))
					{
						continue;
					}

					ConditionProgressChecker checker;
					var done = candidate.ProgressCheckers.TryGetValue(handover, out checker) ? checker.CurrentValue : 0d;
					var remaining = (int)(handover.value - done);
					if (remaining > 0)
					{
						found.Add(new Target { Quest = candidate, Condition = handover, Remaining = remaining });
					}
				}
			}

			return found;
		}

		// The same objective again in a fresh look at the item (the quest may have moved on), or null
		private static Target Again(QuestController questController, Inventory inventory, Item item, Target wanted) =>
			FindAll(questController, inventory, item).FirstOrDefault(target => target.Quest == wanted.Quest && target.Condition == wanted.Condition);

		// Called for every context menu the game builds: adds the button when a quest wants the item
		internal static void AddButton(ItemUiContext uiContext, ContextInteractions<EItemInfoButton> interactions, ItemContext itemContext)
		{
			if (!Plugin.HandInMenu.Value || AddDynamic == null || interactions == null || itemContext?.Item == null || InGameStatus.InRaid)
			{
				return;
			}

			var item = itemContext.Item;
			var questController = QuestControllerOf(uiContext);
			var inventoryController = InventoryControllerField.GetValue(uiContext) as InventoryController;
			if (questController == null || inventoryController == null || item.Owner != inventoryController)
			{
				return;
			}

			// One button, or "Hand in: quest name" for each when several quests want it. Each new button goes to the top, so they are added
			// last to first.
			var targets = FindAll(questController, inventoryController.Inventory, item);
			for (var i = targets.Count - 1; i >= 0; i--)
			{
				var target = targets[i];
				var label = targets.Count == 1 ? MenuKey : $"{MenuKey}: {target.Quest.Template.Name.Localized()}";
				interactions.RemoveDynamicInteraction(label);
				AddDynamic.Invoke(interactions, new object[] { MenuId, label, (Action)(() => Run(questController, inventoryController, item, target)), QuestIcon() });
			}
		}

		// Splits count off the stack into the first free place (the same places as the split hotkey) and returns the new stack, or null
		private static async Task<Item> SplitOff(InventoryController controller, Item item, int count)
		{
			var template = item.TemplateId;
			var before = new HashSet<string>(controller.Inventory.GetPlayerItems(EPlayerItems.AllExceptHideoutStashes).Where(other => other.TemplateId == template).Select(other => other.Id.ToString()));
			string refusal = null;
			foreach (var target in Dismantle.Targets(controller, item))
			{
				foreach (var grid in target.Grids)
				{
					var address = Dismantle.FindFreePlace(grid, item);
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
						Plugin.Log.LogWarning($"Hand in: split of {item.ShortName.Localized()} failed ({outcome.Error})");
						NotificationManager.DisplayMessageNotification("Hand in: the game refused to split the stack.");
						return null;
					}

					// The new stack is the one that was not there before
					for (var attempt = 0; attempt < 10; attempt++)
					{
						var created = controller.Inventory.GetPlayerItems(EPlayerItems.AllExceptHideoutStashes)
							.FirstOrDefault(other => other.TemplateId == template && !before.Contains(other.Id.ToString()));
						if (created != null)
						{
							return created;
						}

						await Task.Delay(100);
					}

					NotificationManager.DisplayMessageNotification("Hand in: split the stack but could not find the new one.");
					return null;
				}
			}

			Plugin.Log.LogInfo($"Hand in: no split ({refusal ?? "no free place"})");
			NotificationManager.DisplayMessageNotification(refusal == null ? "Hand in: no room for the part to split off." : "Hand in: the game doesn't allow splitting it here.");
			return null;
		}

		private static async void Run(QuestController questController, InventoryController inventoryController, Item item, Target wanted)
		{
			if (_running)
			{
				return;
			}

			_running = true;
			try
			{
				// Looked up again: the quest may have moved on since the menu was drawn
				var target = Again(questController, inventoryController.Inventory, item, wanted);
				if (target == null)
				{
					NotificationManager.DisplayMessageNotification("Hand in: that quest doesn't want this any more.");
					return;
				}

				var name = item.ShortName.Localized();
				if (item.StackObjectsCount > target.Remaining)
				{
					// The window never lets you pick more than the quest still needs, so the exact amount is split off first
					var piece = await SplitOff(inventoryController, item, target.Remaining);
					if (piece == null)
					{
						return;
					}

					item = piece;
					target = Again(questController, inventoryController.Inventory, item, wanted);
					if (target == null)
					{
						NotificationManager.DisplayMessageNotification($"Hand in: split off {item.StackObjectsCount} {name}, but the quest doesn't take it. It lies next to the stack.");
						return;
					}
				}

				var count = item.StackObjectsCount;
				var result = await questController.HandoverItem(target.Quest, target.Condition, new[] { item }, true);
				if (result.Succeed)
				{
					Plugin.Log.LogInfo($"Hand in: {count}x {name} to {target.Quest.Template.Name}");
					NotificationManager.DisplayMessageNotification($"Handed in {name} for {target.Quest.Template.Name.Localized()}.");
				}
				else
				{
					Plugin.Log.LogWarning($"Hand in: {name} failed ({result.Error})");
				}
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Hand in failed: {e}");
			}
			finally
			{
				_running = false;
			}
		}
	}
}
