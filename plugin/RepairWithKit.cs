using System;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI;
using UnityEngine;

namespace CustomRpTweaks.Client
{
	// Hotkey on a hovered item in the stash: repairs it fully with a repair kit, without the repair window. It runs the game's own
	// repair strategy (the one the window's Repair button calls), with the kit that has the least resource left that can repair it,
	// and as much as the kits of that type can pay for.
	internal static class RepairWithKit
	{
		private static readonly FieldInfo ControllerField = typeof(ItemUiContext).GetField("_inventoryController", BindingFlags.Instance | BindingFlags.NonPublic);

		private static bool _running;

		// The controller raises this after every repair without checking for listeners; only the repair window normally has one
		private static void NoListener()
		{
		}

		internal static void Update()
		{
			if (_running || !Plugin.RepairWithKitHotkey.Value.IsDown())
			{
				return;
			}

			var uiContext = ItemUiContext.Instance;
			var itemContext = uiContext?.CurrentItemContext;
			var controller = uiContext == null ? null : ControllerField.GetValue(uiContext) as InventoryController;
			var repairController = uiContext?.Session?.RepairController;
			if (itemContext == null || repairController == null || controller?.Inventory.Stash == null || itemContext.Item is RepairKit)
			{
				return;
			}

			var item = itemContext.Item;
			var repairables = item.GetItemComponentsInChildren<RepairableComponent>(true).ToList();
			var kit = controller.Inventory.GetPlayerItems(EPlayerItems.Stash | EPlayerItems.Equipment)
				.OfType<RepairKit>()
				.Where(candidate => candidate.Resource > 0f && repairables.Any(repairable => candidate.CanRepair(repairable.Item)))
				.OrderBy(candidate => candidate.Resource)
				.FirstOrDefault();
			if (kit == null)
			{
				return;
			}

			// The game's own check, so locked armor slots and the like are left alone
			var interactions = uiContext.GetItemContextInteractions(new RepairItemContext(itemContext, kit), null);
			if (!interactions.IsInteractionAvailable(EItemInfoButton.Repair).Succeed)
			{
				return;
			}

			Run(repairController, item, kit);
		}

		private static async void Run(EFT.Repairing.RepairController repairController, Item item, RepairKit kit)
		{
			_running = true;
			try
			{
				IRepairStrategy strategy = item.GetItemComponent<ArmorHolderComponent>() != null
					? (IRepairStrategy)new ArmorRepairStrategy(item, repairController)
					: new DefaultRepairStrategy(item, repairController);
				var collection = strategy.RepairKitsCollections.FirstOrDefault(candidate => candidate._repairKits.Contains(kit));
				if (collection == null)
				{
					return;
				}

				strategy.CurrentRepairer = collection;
				float amount = strategy.HowMuchRepairScoresCanAccept();
				double price = strategy.GetRepairPrice(amount, collection);
				if (price > collection.GetRepairPoints())
				{
					amount *= (float)(collection.GetRepairPoints() / price);
				}

				if (amount < 0.1f)
				{
					return;
				}

				repairController.OnSuccessfulRepairChangedEvent += NoListener;
				IResult result;
				try
				{
					result = await strategy.RepairItem(amount, kit);
				}
				finally
				{
					repairController.OnSuccessfulRepairChangedEvent -= NoListener;
				}

				if (result.Succeed)
				{
					Singleton<GUISounds>.Instance.PlayUISound(EUISoundType.RepairComplete);
					NotificationManager.DisplayMessageNotification($"Repaired {item.ShortName.Localized()} to {strategy.Durability():F1}.");
				}
				else
				{
					Plugin.Log.LogWarning($"Repair with kit failed: {result.Error}");
				}
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Repair with kit failed: {e}");
			}
			finally
			{
				_running = false;
			}
		}
	}
}
