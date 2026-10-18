using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT.Hideout;
using EFT.InventoryLogic;
using EFT.UI.Ragfair;
using SPT.Reflection.Patching;

namespace CustomRpTweaks.Client.Patches
{
	// A non-FiR requirement accepts FiR items too, so crafts can quietly use FiR items kept for the hideout.
	// With the toggle on, crafts stay startable with FiR items, but the UI shows them and the buy button replaces them:
	//  - requirement counters read "non-FiR (FiR) / required" when FiR items are owned
	//  - the buy button buys what the craft would take from FiR items (the item picker prefers non-FiR) plus anything truly missing
	// Tools and requirements that explicitly need FiR items are left alone.
	internal static class HideoutCraftFirPatches
	{
		private const string HaveAllItemsMessage = "Hideout/Craft/HaveAllCraftItems";
		private const string CantBuyFirMessage = "Hideout/Craft/CantBuyFIRItems";
		private const string CounterColor = "C5C3B2";
		private const string FirColor = "7F7F7F";

		private static bool Applies(ItemRequirement requirement) => Plugin.CraftFirHandling.Value && !requirement.IsSpawnedInSession && !(requirement is ToolRequirement);

		private static int NonFirOwned(ItemRequirement requirement, IEnumerable<Item> items) =>
			items.Where(item => !item.SpawnedInSession && ItemRequirement.IsSuitableItem(requirement, item)).Sum(item => item.StackObjectsCount);

		// Template id -> amount to buy: what the requirements need beyond the non-FiR items owned (FiR items that would be used + truly missing)
		private static Dictionary<string, int> MissingNonFir(ProduceView view)
		{
			var stashItems = Singleton<HideoutRepresentation>.Instance.AllStashItems.ToList();
			var missing = new Dictionary<string, int>();
			foreach (var requirement in view.Scheme.RequiredItems.Where(Applies))
			{
				int needed = requirement.IntCount - NonFirOwned(requirement, stashItems);
				if (needed > 0)
				{
					missing[requirement.TemplateId] = missing.TryGetValue(requirement.TemplateId, out int already) ? already + needed : needed;
				}
			}

			return missing;
		}

		// Requirement icons: counter only. The checkmark and IsFulfilled (which gates Start) keep the vanilla values.
		internal class RequirementViewPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(HideoutProductionRequirementView).GetMethod(nameof(HideoutProductionRequirementView.Show), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPostfix]
			private static void Postfix(HideoutProductionRequirementView __instance, ItemRequirement requirement, BaseHideoutScheme scheme, IEnumerable<Item> allItems, bool canStartProduction)
			{
				if (!(scheme is ProductionScheme) || !canStartProduction || !Applies(requirement))
				{
					return;
				}

				int nonFir = NonFirOwned(requirement, allItems);
				int fir = requirement.UserItemsCount - nonFir;
				if (fir <= 0)
				{
					return;
				}

				__instance._itemViewFactory.SetCounterText($"<color=#{CounterColor}><b>{nonFir}</b></color> <color=#{FirColor}>({fir})</color> / {requirement.IntCount}");
			}
		}

		internal class BuyMaterialsPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(ProduceView).GetMethod(nameof(ProduceView.BuyMaterials), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPrefix]
			private static bool Prefix(ProduceView __instance)
			{
				if (!Plugin.CraftFirHandling.Value)
				{
					return true;
				}

				var missing = MissingNonFir(__instance);
				if (missing.Count > 0)
				{
					var search = new BuildItemSearchValue { BuildName = "", BuildItems = missing, BuildCount = 1 };
					__instance.ItemUiContext.ExternalRagfairSearch(new RagfairSearch(EFilterType.BuildItems, search, true));
				}

				return false;
			}
		}

		// Enables the buy button whenever the craft would use FiR items or is missing items
		internal class StartButtonPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(ProduceView).GetMethod(nameof(ProduceView.SetStartButtonActive), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPostfix]
			private static void Postfix(ProduceView __instance)
			{
				if (!Plugin.CraftFirHandling.Value || __instance._buyButton == null)
				{
					return;
				}

				var ragfair = __instance.ItemUiContext.Session.RagFair;
				bool fleaAvailable = !ragfair.Disabled && ragfair.Status != RagFair.ERagFairStatus.Banned;
				__instance._buyButton.SetUnlockStatus(fleaAvailable && MissingNonFir(__instance).Count > 0);
			}
		}

		internal class BuyTooltipPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(ProduceView).GetMethod(nameof(ProduceView.GetBuyButtonTooltipText), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPostfix]
			private static void Postfix(ProduceView __instance, ref string __result)
			{
				// Only adjust the messages about items; the flea market unavailable message stays
				if (!Plugin.CraftFirHandling.Value || (__result != string.Empty && __result != HaveAllItemsMessage && __result != CantBuyFirMessage))
				{
					return;
				}

				__result = MissingNonFir(__instance).Count > 0 ? string.Empty : HaveAllItemsMessage;
			}
		}
	}
}
