using System.Reflection;
using EFT.Hideout;
using EFT.UI;
using EFT.UI.Ragfair;
using SPT.Reflection.Patching;

namespace CustomRpTweaks.Client.Patches
{
	// The flea market buy button of a craft buys what the craft needs itself (the same engine as Buy & fulfil, see FleaBarterBuy) instead of
	// opening a flea market search, and hovering it says what it would buy and cost, or why it can't be done.
	internal static class HideoutQuickBuy
	{
		private static bool Available(ProduceView view)
		{
			var ragfair = view.ItemUiContext.Session.RagFair;
			return !ragfair.Disabled && ragfair.Status != RagFair.ERagFairStatus.Banned;
		}

		internal class BuyMaterialsPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(ProduceView).GetMethod(nameof(ProduceView.BuyMaterials), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPrefix]
			private static bool Prefix(ProduceView __instance)
			{
				if (!Plugin.HideoutQuickBuy.Value || !Available(__instance))
				{
					return true;
				}

				// What the tooltip says is about to be out of date
				QuoteHover.ClearCache();
				var tooltip = ItemUiContext.Instance == null ? null : ItemUiContext.Instance.Tooltip;
				if (tooltip != null)
				{
					tooltip.Close();
				}

				FleaBarterBuy.StartHideout(__instance);
				return false;
			}
		}

		// The button is open whenever the flea market is (there is always something to look at), and carries our tooltip
		internal class StartButtonPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(ProduceView).GetMethod(nameof(ProduceView.SetStartButtonActive), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPostfix]
			private static void Postfix(ProduceView __instance)
			{
				if (__instance._buyButton == null)
				{
					return;
				}

				var hover = __instance._buyButton.GetComponent<QuoteHover>();
				if (!Plugin.HideoutQuickBuy.Value)
				{
					if (hover != null)
					{
						hover.enabled = false;
					}

					return;
				}

				bool available = Available(__instance);
				__instance._buyButton.SetUnlockStatus(available);
				if (hover == null)
				{
					hover = __instance._buyButton.gameObject.AddComponent<QuoteHover>();
				}

				hover.enabled = available;
				hover.CacheKey = () => "hideout:" + __instance.Scheme._id + ":" + Plugin.HideoutBuyMode.Value + ":" + Plugin.HideoutBuyTools.Value + ":" + FleaBarterBuy.StateKey(FleaBarterBuy.HideoutSource(__instance));
				hover.Quote = () => FleaBarterBuy.Quote(FleaBarterBuy.RagfairOf(__instance), FleaBarterBuy.HideoutSource(__instance));
			}
		}

		// The game's own tooltip of the button says things that are no longer true (a flea market search); ours replaces it
		internal class BuyTooltipPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(ProduceView).GetMethod(nameof(ProduceView.GetBuyButtonTooltipText), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPostfix]
			private static void Postfix(ProduceView __instance, ref string __result)
			{
				if (Plugin.HideoutQuickBuy.Value && Available(__instance))
				{
					__result = string.Empty;
				}
			}
		}
	}
}
