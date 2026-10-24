using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Trading;
using EFT.UI;
using EFT.UI.DragAndDrop;
using EFT.UI.Ragfair;
using SPT.Reflection.Patching;
using SPT.Reflection.Utils;

namespace CustomRpTweaks.Client.Patches
{
	// Colours the price of trader offers the server's trader overstock has raised (see OverstockPrices). Two hooks make the one
	// request, two patches draw the prices, one patch keeps the trader screen current after a flea market purchase.
	internal static class OverstockPricePatches
	{
		private static string Raised(string text, int sizePercent = 100) => (sizePercent == 100 ? "" : "<size=" + sizePercent + "%>") + "<color=" + Plugin.PriceIndicatorNewColor.Value + ">" + text + "</color>" + (sizePercent == 100 ? "" : "</size>");

		// Trader screen: the game has loaded a trader's offers
		internal class TraderOffersHook : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(ClientBackendSession).GetMethod(nameof(ClientBackendSession.GetTraderAssortment), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPostfix]
			private static void Postfix(ref Task<Result<TraderAssortment>> __result)
			{
				if (OverstockPrices.NeedsSeed)
				{
					__result = OverstockPrices.SeedAfter(__result);
				}
			}
		}

		// Flea market: the game has loaded search results
		internal class FleaOffersHook : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(EftClientBackendSession).GetMethod(nameof(EftClientBackendSession.GetOffers), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPostfix]
			private static void Postfix(ref Task<Result<OffersList>> __result)
			{
				if (OverstockPrices.NeedsSeed)
				{
					__result = OverstockPrices.SeedAfter(__result);
				}
			}
		}

		// The game keeps a trader's offers from when the trader screen last loaded them, and only reloads them after a purchase
		// on that screen. A flea market purchase that uses up the whole limit of a trader's offer starts a new, dearer round on
		// the server, so reload that trader's offers once the purchase is through. Other purchases don't change prices.
		internal class FleaPurchaseRefreshPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(RagFair).GetMethod(nameof(RagFair.Purchase), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPostfix]
			private static void Postfix(CommoditiesToPurchase purchases, ref Task<IResult> __result)
			{
				if (!OverstockPrices.Active)
				{
					return;
				}

				// The offers still hold their numbers from before this purchase
				var traderIds = purchases
					.Where(c => c.Exchangeable is Offer offer && offer.User.MemberType == EMemberCategory.Trader && offer.BuyRestrictionMax > 0
						&& offer.BuyRestrictionCurrent + c.Count >= offer.BuyRestrictionMax)
					.Select(c => ((Offer)c.Exchangeable).User.Id.ToString())
					.Distinct()
					.ToList();
				if (traderIds.Count > 0)
				{
					__result = RefreshTradersAfter(__result, traderIds);
				}
			}

			private static async Task<IResult> RefreshTradersAfter(Task<IResult> task, System.Collections.Generic.List<string> traderIds)
			{
				IResult result = await task;
				try
				{
					var traders = ClientAppUtils.GetMainApp()?.GetClientBackEndSession()?.Traders;
					if (traders != null)
					{
						foreach (var trader in traders.Where(t => traderIds.Contains(t.Id) && t.CurrentAssortment != null).ToList())
						{
							await trader.CurrentAssortment.UpdateAssortment(true);
						}
					}
				}
				catch (Exception e)
				{
					Plugin.Log.LogWarning("Couldn't reload the trader offers after a flea market purchase: " + e.Message);
				}

				return result;
			}
		}

		// Price under an item in the trader's grid
		internal class TraderGridPricePatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(TradingItemView).GetMethod(nameof(TradingItemView.SetPrice), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPostfix]
			private static void Postfix(TradingItemView __instance, Trader.ItemPrice? price, Trader ___Trader)
			{
				if (!OverstockPrices.Active || price == null || !__instance._price.gameObject.activeSelf)
				{
					return;
				}

				if (OverstockPrices.IsRaised('t', ___Trader.Id, __instance.Item.Id.ToString(), price.Value.Amount, out _))
				{
					__instance._price.enableWordWrapping = false;
					__instance._price.text = Raised(__instance._price.text, Plugin.PriceIndicatorTraderSize.Value);
				}
			}
		}

		// Tooltip of an item in the trader's grid: there is room for the base price, struck through, before the raised price
		internal class TraderTooltipPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(TradingItemView).GetMethod(nameof(TradingItemView.ShowTooltip), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPostfix]
			private static void Postfix(TradingItemView __instance, Trader ___Trader, bool ___ModSlotView, ItemUiContext ___ItemUiContext)
			{
				if (!OverstockPrices.Active || !Plugin.PriceIndicatorTooltip.Value || ___ModSlotView)
				{
					return;
				}

				Trader.ItemPrice? price = __instance.ItemPrice;
				if (price == null || !OverstockPrices.IsRaised('t', ___Trader.Id, __instance.Item.Id.ToString(), price.Value.Amount, out int basePrice))
				{
					return;
				}

				var label = ___ItemUiContext.PriceTooltip._price;
				label.enableWordWrapping = false;
				label.text = "<size=75%><color=" + Plugin.PriceIndicatorOldColor.Value + "><s>" + basePrice + "</s></color></size> " + Raised(price.Value.Amount.ToString());
			}
		}

		// Price on a flea market row, for offers that cost money only
		internal class FleaRowPricePatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(OfferItemPrice).GetMethod(nameof(OfferItemPrice.Show), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPostfix]
			private static void Postfix(OfferItemPrice __instance, Offer offer)
			{
				if (!OverstockPrices.Active || offer.NotAvailable || !offer.OnlyMoney)
				{
					return;
				}

				if (OverstockPrices.IsRaised('f', offer.User.Id.ToString(), offer.Item.Id.ToString(), offer.Requirements[0].IntCount, out _))
				{
					__instance._priceLabel.text = Raised(__instance._priceLabel.text);
				}
			}
		}
	}
}
