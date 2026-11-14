using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.Trading;
using EFT.UI;
using EFT.UI.DragAndDrop;
using EFT.UI.Ragfair;
using SPT.Reflection.Patching;
using TMPro;
using UnityEngine;
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
			private static void Postfix(TradingItemView __instance, Trader.ItemPrice? price, Trader ___Trader, EOwnerType ____ownerType)
			{
				if (!OverstockPrices.Active || price == null || ____ownerType == EOwnerType.Profile || !__instance._price.gameObject.activeSelf)
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
			private static void Postfix(TradingItemView __instance, Trader ___Trader, bool ___ModSlotView, ItemUiContext ___ItemUiContext, EOwnerType ____ownerType)
			{
				if (!OverstockPrices.Active || !Plugin.PriceIndicatorTooltip.Value || ___ModSlotView || ____ownerType == EOwnerType.Profile)
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

		// The game reloads a trader's offers (after a purchase, say) and keeps the old ones in memory until then: whatever got dearer between the
		// two is told to the trader screen with its old price as the base, so it is judged against what it cost, not against what it cost when
		// the screen first drew it. No request: both lists are the client's own.
		internal class TraderReloadPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(Assortment).GetMethod(nameof(Assortment.UpdateAssortment), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPrefix]
			private static void Prefix(Assortment __instance, out Dictionary<string, BarterScheme> __state)
			{
				__state = __instance._schemes;
			}

			[PatchPostfix]
			private static void Postfix(Assortment __instance, ref Task __result, Dictionary<string, BarterScheme> __state)
			{
				if (OverstockPrices.Active && __state != null)
				{
					__result = NoteAfter(__result, __instance, __state);
				}
			}

			private static async Task NoteAfter(Task task, Assortment assortment, Dictionary<string, BarterScheme> before)
			{
				await task;
				try
				{
					var after = assortment._schemes;
					var traderId = assortment._trader?.Id;
					if (after == null || traderId == null || ReferenceEquals(before, after))
					{
						return;
					}

					foreach (var pair in after)
					{
						if (!before.TryGetValue(pair.Key, out var old))
						{
							continue;
						}

						// What the trader grid shows for it: the counts added up, rounded up
						int oldPrice = (int)Math.Ceiling(old.Sum(variant => variant.Sum(template => template.count)));
						int newPrice = (int)Math.Ceiling(pair.Value.Sum(variant => variant.Sum(template => template.count)));
						if (newPrice > oldPrice)
						{
							OverstockPrices.NoteBase('t', traderId, pair.Key, oldPrice);
						}

						// What the barter panel shows: the count of each item asked for
						var oldCounts = old.SelectMany(variant => variant).GroupBy(template => template._tpl).ToDictionary(group => group.Key, group => (int)Math.Ceiling(group.Sum(template => template.count)));
						foreach (var group in pair.Value.SelectMany(variant => variant).GroupBy(template => template._tpl))
						{
							int newCount = (int)Math.Ceiling(group.Sum(template => template.count));
							if (oldCounts.TryGetValue(group.Key, out int oldCount) && newCount > oldCount)
							{
								OverstockPrices.NoteBase('b', traderId, pair.Key + ":" + group.Key, oldCount);
							}
						}
					}
				}
				catch (Exception e)
				{
					Plugin.Log.LogWarning("Price indicator: couldn't compare the trader's offers before and after the reload: " + e.Message);
				}
			}
		}

		// Trader screen, the items a barter asks for ("3/11"): the server raises the count of each item, so a raised one is drawn in the colour
		internal class TraderBarterCountPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(TradingRequisitePanel).GetMethod(nameof(TradingRequisitePanel.UpdateValues), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPostfix]
			private static void Postfix(TradingRequisitePanel __instance, Requisite ____requisite, Assortment ____traderAssortment)
			{
				if (!OverstockPrices.Active || ____requisite == null || ____traderAssortment == null)
				{
					return;
				}

				var selected = ____traderAssortment.SelectedItem;
				var trader = ____traderAssortment._trader;
				int required = ____requisite.RequiredItemsCount;
				if (selected == null || trader == null || required < 1 || required >= int.MaxValue || ____requisite.IsDogtagRequired || !__instance._count.gameObject.activeSelf)
				{
					return;
				}

				string offerId = selected.Id.ToString();
				if (OverstockPrices.IsRaised('b', trader.Id, offerId + ":" + ____requisite.RequiredItem.TemplateId, required, out _, offerId))
				{
					__instance._count.text = "<color=#dadabc>" + ____requisite.PreparedItemsCount + "/" + Raised(required.ToString()) + "</color>";
				}
			}
		}

		// Flea market, a trader's barter offer: the same for the counts of the items it asks for. UI Fixes draws these ("3/11") after the game
		// has made the row, so the count is recoloured a frame later, whichever mod's code ran first.
		internal class FleaBarterCountPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(OfferItemPriceBarter).GetMethod(nameof(OfferItemPriceBarter.Show), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPostfix]
			private static void Postfix(OfferItemPriceBarter __instance, IExchangeRequirement requirement, Offer offer, bool expanded)
			{
				if (!OverstockPrices.Active || !expanded || offer == null || offer.NotAvailable || offer.OnlyMoney || offer.MemberType != EMemberCategory.Trader || offer.Item == null)
				{
					return;
				}

				string offerId = offer.Item.Id.ToString();
				if (OverstockPrices.IsRaised('f', offer.User.Id.ToString(), offerId + ":" + requirement.TemplateId, requirement.IntCount, out _, offerId))
				{
					Plugin.Instance.StartCoroutine(Recolor(__instance));
				}
			}

			private static IEnumerator Recolor(OfferItemPriceBarter view)
			{
				yield return null;
				if (view == null)
				{
					yield break;
				}

				foreach (var label in view.GetComponentsInChildren<TMP_Text>(true))
				{
					// "<color=#C5C3B2><b>3</b></color>/11": the part after the slash is the count asked for
					string text = label.text;
					int slash = text == null ? -1 : text.IndexOf("</color>/", StringComparison.Ordinal);
					if (slash < 0 || text.IndexOf("<color=", slash, StringComparison.Ordinal) >= 0)
					{
						continue;
					}

					label.text = text.Substring(0, slash + 9) + Raised(text.Substring(slash + 9));
				}
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
