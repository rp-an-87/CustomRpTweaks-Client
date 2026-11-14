using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.Hideout;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.Ragfair;
using SPT.Reflection.Patching;

namespace CustomRpTweaks.Client
{
	internal enum QuickBuyMode
	{
		[System.ComponentModel.Description("Buy all")]
		BuyAll,

		[System.ComponentModel.Description("Buy missing")]
		BuyMissing,

		[System.ComponentModel.Description("Buy missing, found-in-raid items don't count")]
		BuyMissingNoFir
	}

	// "Quick buy missing" / "Quick buy all" / "Barter cost" in the item menu of a flea market barter offer (the buttons beside the offer:
	// Inspect, Filter by item, ...). For every item the barter asks for, the cheapest player offers for roubles are bought (the game's own
	// search, sorted by price, and the game's own purchase request), then the barter itself is bought with those items. Everything is
	// worked out first and nothing is bought unless every item can be had and the roubles are there.
	internal static class FleaBarterBuy
	{
		internal static string ButtonText => Plugin.FleaBarterBuyComplete.Value ? "Buy & fulfil" : "Buy items";

		// What a quick buy is about: the requirements, how what you hold is counted, and the rules (from the settings of the screen it is on)
		internal class Source
		{
			public Offer Offer;                                                          // the barter offer; null for a hideout craft
			public IExchangeRequirement[] Requirements;
			public Func<IExchangeRequirement, int> Required;
			public Func<Item, IExchangeRequirement, bool> Suitable;                      // can this item of the stash be used for the requirement
			public Func<IExchangeRequirement, bool> Consumed = requirement => true;      // false: a tool, only bought when you don't have it
			public Func<IExchangeRequirement, bool> NeedsFir = requirement => false;     // only found-in-raid items will do: not for sale
			public Func<List<Item>> Stash;
			public bool UnbuyableBlocks;                                                 // something the flea market can't supply stops the buy (barter) or is left to the stash (craft)
			public QuickBuyMode Setting;
			public Mode Buying => Setting == QuickBuyMode.BuyAll ? Mode.All : Mode.Missing;
			public bool FirAsMissing => Setting == QuickBuyMode.BuyMissingNoFir;
		}

		internal static Source BarterSource(RagFair ragfair, Offer offer) => new Source
		{
			Offer = offer,
			Requirements = offer.Requirements,
			Required = requirement => Required(offer, requirement),
			Suitable = (item, requirement) => HandoverExtensions.AvailableToHandover(item, requirement),
			Stash = () => ragfair._tradingSession.Profile.Inventory.Stash.GetAllItemsFromCollection().ToList(),
			UnbuyableBlocks = true,
			Setting = Plugin.FleaBarterBuyMode.Value
		};

		internal static Source HideoutSource(ProduceView view) => new Source
		{
			Requirements = view.Scheme.RequiredItems.Where(requirement => Plugin.HideoutBuyTools.Value || !(requirement is ToolRequirement)).Cast<IExchangeRequirement>().ToArray(),
			Required = requirement => requirement.IntCount,
			Suitable = (item, requirement) => ItemRequirement.IsSuitableItem(requirement, item),
			Consumed = requirement => !(requirement is ToolRequirement),
			NeedsFir = requirement => requirement.IsSpawnedInSession,
			Stash = () => Singleton<HideoutRepresentation>.Instance.AllStashItems.Where(item => item != null).ToList(),
			UnbuyableBlocks = false,
			Setting = Plugin.HideoutBuyMode.Value
		};

		internal static RagFair RagfairOf(ProduceView view) => view.ItemUiContext.Session.RagFair;
		internal enum Mode { Missing, All }

		// What one requirement of the barter needs: how many to buy, and the offers they come from
		private class Need
		{
			public IExchangeRequirement Requirement;
			public string Name;
			public int Required;
			public int ToBuy;
			public int Shortfall;
			public int Spare;
			public int Owned;
			public int FirHeld;
			public int Held;
			public int Busy;
			public bool CannotBuy;
			public readonly List<Pick> Picks = new List<Pick>();
			public int Bought => Picks.Sum(pick => pick.Count);
		}

		private class Pick
		{
			public Offer Offer;
			public int Count;
			public long Cost;
			public ECurrencyType Currency;
		}

		private class Plan
		{
			public readonly List<Need> Needs = new List<Need>();
			public readonly List<string> Problems = new List<string>();
			public Dictionary<ECurrencyType, long> Totals()
			{
				var totals = new Dictionary<ECurrencyType, long>();
				foreach (var pick in Needs.SelectMany(need => need.Picks))
				{
					totals.TryGetValue(pick.Currency, out var sum);
					totals[pick.Currency] = sum + pick.Cost;
				}

				return totals;
			}

			public string TotalText => string.Join(" + ", Totals().Where(pair => pair.Value > 0).Select(pair => Amount(pair.Key, pair.Value)));
			public int Bought => Needs.Sum(need => need.Bought);
		}

		private const int PageSize = 50;
		private const int MaxPages = 6;

		private static bool _running;

		private static void Notify(string message) => NotificationManager.DisplayMessageNotification(message);

		private static string Amount(ECurrencyType currency, long amount) =>
			amount.ToString("N0", CultureInfo.InvariantCulture).Replace(",", " ") + (currency == ECurrencyType.RUB ? " roubles" : currency == ECurrencyType.USD ? " USD" : currency == ECurrencyType.EUR ? " EUR" : " " + currency);

		private static readonly ECurrencyType[] Currencies = { ECurrencyType.RUB, ECurrencyType.USD, ECurrencyType.EUR };

		// What you hold of each currency the flea market is paid in (money that is locked doesn't count)
		private static Dictionary<ECurrencyType, long> Wallet(IEnumerable<Item> items)
		{
			var money = items.OfType<Money>().Where(coin => coin.PinLockState != EItemPinLockState.Locked).ToList();
			return Currencies.ToDictionary(currency => currency, currency =>
			{
				var id = CurrencyUtil.GetCurrencyId(currency);
				return money.Where(coin => coin.TemplateId == id).Sum(coin => (long)coin.StackObjectsCount);
			});
		}

		private static string NameOf(Item item) => item == null ? "?" : item.ShortName.Localized(null);

		// Called when the game makes the interactions of an item: a flea market barter gets our entries
		internal static void Add(ItemContext itemContext, ContextInteractions<EItemInfoButton> interactions)
		{
			if (!Plugin.FleaBarterBuyMenu.Value || HandIn.AddDynamic == null || interactions == null || itemContext?.Item == null)
			{
				return;
			}

			var ragfairInteractions = interactions as RagfairContextInteractions;
			var ragfair = ragfairInteractions?._ragfair;
			if (ragfair == null)
			{
				return;
			}

			var offer = FindOffer(ragfair, itemContext.Item);
			// The label depends on a setting: both are taken out
			interactions.RemoveDynamicInteraction("Buy & fulfil");
			interactions.RemoveDynamicInteraction("Buy items");
			if (offer == null || offer.NotAvailable || offer.OnlyMoney || offer.Requirements == null || offer.Requirements.Length == 0 || ragfair.Disabled)
			{
				return;
			}

			HandIn.AddDynamic.Invoke(interactions, new object[] { ButtonText, ButtonText, (Action)(() => Start(ragfair, offer)), null });
		}

		private static Offer FindOffer(RagFair ragfair, Item item)
		{
			foreach (var offer in ragfair.Offers)
			{
				if (offer != null && (offer.Item == item || (offer.Item != null && offer.Item.Id == item.Id)))
				{
					return offer;
				}
			}

			return null;
		}

		// One quick buy at a time
		private static async Task Guard(Func<Task> work)
		{
			if (_running)
			{
				Notify("Quick buy: still working on the last one.");
				return;
			}

			_running = true;
			try
			{
				await work();
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Quick buy failed: {e}");
				Notify("Quick buy failed: " + e.Message + " (see the BepInEx log)");
			}
			finally
			{
				_running = false;
				QuoteHover.ClearCache();
			}
		}

		internal static async void Start(RagFair ragfair, Offer offer) => await Guard(() => Run(ragfair, BarterSource(ragfair, offer)));

		internal static async void StartHideout(ProduceView view) => await Guard(() => Run(RagfairOf(view), HideoutSource(view)));

		private static async Task Run(RagFair ragfair, Source src)
		{
			var offer = src.Offer;
			var profile = ragfair._tradingSession.Profile;
			var inventory = profile.Inventory;
			var search = new Dictionary<string, List<Offer>>();

			if (offer != null && (!offer.CanBeBought || offer.IsOutOfStockNow()))
			{
				Notify("Quick buy: this offer can't be bought right now.");
				return;
			}

			// Everything is worked out first: nothing is bought unless all of it can be had and paid for
			var plan = await MakePlan(ragfair, profile, src, search, Wallet(inventory.Stash.GetAllItemsFromCollection()));
			if (plan.Problems.Count > 0)
			{
				Notify("Quick buy: nothing bought. " + string.Join(" ", plan.Problems));
				return;
			}

			var stash = inventory.Stash.GetAllItemsFromCollection().ToList();
			if (plan.Bought > 0)
			{
				var goods = new Dictionary<IExchangeable, int>();
				foreach (var pick in plan.Needs.SelectMany(need => need.Picks))
				{
					goods[pick.Offer] = pick.Count;
				}

				var references = HandoverExtensions.RecalculateReferences(stash, goods);
				var commodities = new CommoditiesToPurchase();
				foreach (var pair in goods)
				{
					var money = references[pair.Key].ItemsToHandover
						.Where(entry => entry.Value > 0 && CurrencyUtil.IsCurrencyId(entry.Key.TemplateId))
						.Select(entry => new ItemReference { Id = entry.Key.Id, TemplateId = entry.Key.TemplateId, ExistingCount = entry.Value })
						.ToList();
					commodities.Add(new CommodityToPurchase(pair.Key, pair.Key.SellInOnePiece ? pair.Key.CurrentItemCount : pair.Value, money));
				}

				var result = await ragfair.Purchase(commodities);
				if (!result.Succeed)
				{
					Notify($"Quick buy: the flea market refused the purchase (code {result.ErrorCode}), the offers have probably changed. Try again.");
					return;
				}
			}

			var spare = plan.Needs.Sum(need => need.Spare);
			var summary = plan.Bought == 0 ? "nothing needed buying" : $"bought {plan.Bought} item{(plan.Bought == 1 ? "" : "s")} for {plan.TotalText}" + (spare > 0 ? $" ({spare} spare in a pack)" : "");

			// A craft: the items are in the stash, starting it is up to you
			if (offer == null || !Plugin.FleaBarterBuyComplete.Value)
			{
				Notify("Quick buy: " + summary + ".");
				return;
			}

			// The items should be in the stash as the purchase returns; give the game a moment if not
			List<ItemReference> handover = null;
			for (var attempt = 0; attempt < 10; attempt++)
			{
				stash = inventory.Stash.GetAllItemsFromCollection().ToList();
				handover = Handover(src, stash);
				if (handover != null)
				{
					break;
				}

				await Task.Delay(200);
			}

			if (handover == null)
			{
				Notify("Quick buy: " + summary + ", but the items are not all in your stash, so the barter was not bought.");
				return;
			}

			var screen = UnityEngine.Object.FindObjectOfType<RagfairScreen>();
			if (screen == null)
			{
				Notify("Quick buy: " + summary + ", but the flea market screen is gone, so the barter was not bought.");
				return;
			}

			var barter = new CommoditiesToPurchase();
			barter.Add(new CommodityToPurchase(offer, offer.SellInOnePiece ? offer.Quantity : 1, handover));
			Notify("Quick buy: " + summary + ", buying the barter.");
			await screen.PerformPurchase(barter);
		}
		private static bool IsOutOfStockNow(this Offer offer) => offer.TotalItemCount <= 0;

		// What a quote depends on besides the offers: what is asked for now (the server may raise a barter) and what you hold. A quote is
		// only reused while this stays the same.
		internal static string StateKey(Source src)
		{
			var stash = src.Stash().Where(item => item != null).ToList();
			var key = new StringBuilder();
			foreach (var requirement in src.Requirements)
			{
				var held = stash.Where(item => item.StringTemplateId == requirement.TemplateId).ToList();
				key.Append(requirement.TemplateId).Append('=').Append(src.Required(requirement)).Append('/').Append(held.Sum(item => item.StackObjectsCount))
					.Append('/').Append(held.Where(item => item.MarkedAsSpawnedInSession).Sum(item => item.StackObjectsCount)).Append(';');
			}

			foreach (var amount in Wallet(stash))
			{
				key.Append(amount.Value).Append(',');
			}

			return key.ToString();
		}
		private const string Yellow = "#F2C94C", Green = "#6FCF6F", Blue = "#5AA9F2", Red = "#E0554A", Grey = "#9A9A9A";

		private static string Number(long amount) => amount.ToString("N0", CultureInfo.InvariantCulture).Replace(",", " ");

		private static string Symbol(ECurrencyType currency, bool colour)
		{
			var symbol = currency == ECurrencyType.RUB ? "\u20bd" : currency == ECurrencyType.USD ? "$" : "\u20ac";
			var tint = currency == ECurrencyType.RUB ? Yellow : currency == ECurrencyType.USD ? Green : Blue;
			return colour ? $"<color={tint}><b>{symbol}</b></color>" : "<b>" + symbol + "</b>";
		}

		// "P 21 321, $ 12 346, E 1 234" with the symbols in their colours (or plain, to be drawn in one colour)
		private static string Price(Dictionary<ECurrencyType, long> amounts, bool colour) =>
			string.Join(", ", Currencies.Where(currency => amounts.TryGetValue(currency, out var value) && value > 0).Select(currency => $"{Symbol(currency, colour)} <b>{Number(amounts[currency])}</b>"));

		private static string Note(string text) => $"<size=85%><color={Grey}>{text}</color></size>";

		// Notes under the tooltip: found-in-raid items that are held but won't be used, and what the flea market can't supply
		private static IEnumerable<string> Notes(Plan plan, Source src)
		{
			// What you hold of each requirement, counted the way the mode counts (found-in-raid items or not): it says why something is bought,
			// or isn't. Green when what you hold covers it.
			var have = new List<string>();
			foreach (var need in plan.Needs)
			{
				if (need.CannotBuy && src.UnbuyableBlocks)
				{
					continue;
				}

				var counted = need.Owned >= need.Required ? need.Owned : (need.ToBuy == 0 || need.CannotBuy ? need.Held : need.Owned);
				var text = $"{need.Name} <color={(counted >= need.Required ? Green : Yellow)}>{counted}/{need.Required}</color>";
				var remarks = new List<string>();
				if (need.FirHeld > 0 && src.Buying == Mode.Missing && src.FirAsMissing && src.Consumed(need.Requirement) && !src.NeedsFir(need.Requirement))
				{
					remarks.Add($"+{need.FirHeld} FiR" + (need.Shortfall > 0 && need.FirHeld >= need.Shortfall ? ", would cover the gap" : ""));
				}

				if (need.Busy > 0)
				{
					remarks.Add($"{need.Busy} not empty");
				}

				if (remarks.Count > 0)
				{
					text += " (" + string.Join(", ", remarks) + ")";
				}

				have.Add(text);
			}

			if (have.Count > 0)
			{
				// One line per requirement, so a long list stays readable
				yield return Note("<b>You have:</b>\n" + string.Join("\n", have));
			}

			if (!src.UnbuyableBlocks)
			{
				foreach (var need in plan.Needs.Where(need => need.CannotBuy))
				{
					yield return Note($"{need.Name} can't be bought on the flea market, you have {need.Held} of {need.Required}.");
				}
			}
		}

		// The tooltip of a button, from the offers as they are now
		internal static async Task<string> Quote(RagFair ragfair, Source src)
		{
			var profile = ragfair._tradingSession.Profile;
			var search = new Dictionary<string, List<Offer>>();
			var plan = await MakePlan(ragfair, profile, src, search, null);
			var lines = new List<string>();

			var unsellable = src.UnbuyableBlocks ? plan.Needs.Where(need => need.CannotBuy).ToList() : new List<Need>();
			var lacking = plan.Needs.Where(need => !need.CannotBuy && need.Shortfall > 0).ToList();
			if (unsellable.Count > 0 || lacking.Count > 0)
			{
				if (lacking.Count > 0)
				{
					lines.Add($"<b><color={Red}>Can't fulfill:</color></b> not enough offers for " + string.Join(", ", lacking.Select(need => $"<b>{need.Name}</b> ({need.ToBuy - need.Shortfall}/{need.ToBuy})")) + " in the flea market");
				}

				if (unsellable.Count > 0)
				{
					lines.Add($"<b><color={Red}>Can't fulfill:</color></b> " + string.Join(", ", unsellable.Select(need => $"<b>{need.Name}</b>")) + " can't be bought on the flea market");
				}

				lines.AddRange(Notes(plan, src));
				return string.Join("\n", lines);
			}

			if (plan.Bought == 0)
			{
				lines.Add("<b>Nothing to buy:</b> you hold everything it asks for");
				lines.AddRange(Notes(plan, src));
				return string.Join("\n", lines);
			}

			lines.Add("<b>Will buy:</b> " + string.Join(", ", plan.Needs.Where(need => need.Bought > 0).Select(need => $"<b>{need.Bought}</b> {need.Name}" + (need.Spare > 0 ? $" (+{need.Spare} spare)" : ""))));

			var wallet = Wallet(profile.Inventory.Stash.GetAllItemsFromCollection());
			var held = await MakePlan(ragfair, profile, src, search, new Dictionary<ECurrencyType, long>(wallet));
			var totals = plan.Totals();
			if (held.Problems.Count == 0)
			{
				lines.Add("<b>Buy for:</b> " + Price(totals, true));
			}
			else
			{
				lines.Add($"<b><color={Red}>Can't buy: {Price(totals, false)}</color></b>");
				var missing = totals.Where(pair => pair.Value > wallet[pair.Key]).ToDictionary(pair => pair.Key, pair => pair.Value - wallet[pair.Key]);
				if (missing.Count > 0)
				{
					lines.Add(Note("You are short by " + Price(missing, true)));
				}
			}

			lines.AddRange(Notes(plan, src));
			return string.Join("\n", lines);
		}
		private static int Required(Offer offer, IExchangeRequirement requirement) =>
			offer.SellInOnePiece ? requirement.IntCount : (int)Math.Ceiling(requirement.PreciseCount);

		// Items of the stash that can be used for a requirement, the way the game's own hand-over picks them: not found in raid first
		// (so a found-in-raid item is only used when nothing else is left), then the smaller stacks
		private static IEnumerable<Item> Candidates(Source src, IEnumerable<Item> stash, IExchangeRequirement requirement, bool includeFir) =>
			stash.Where(item => item != null && item.StringTemplateId == requirement.TemplateId && src.Suitable(item, requirement) && (includeFir || !item.MarkedAsSpawnedInSession))
				.OrderBy(item => item.MarkedAsSpawnedInSession)
				.ThenBy(item => item.StackObjectsCount);

		// The references of the items to hand over for the barter, or null when the stash doesn't hold everything it asks for
		private static List<ItemReference> Handover(Source src, List<Item> stash)
		{
			var references = new List<ItemReference>();
			foreach (var requirement in src.Requirements)
			{
				var remaining = src.Required(requirement);
				foreach (var item in Candidates(src, stash, requirement, true))
				{
					if (remaining <= 0)
					{
						break;
					}

					var take = Math.Min(remaining, item.StackObjectsCount);
					references.Add(new ItemReference { Id = item.Id, TemplateId = item.TemplateId, ExistingCount = take });
					remaining -= take;
				}

				if (remaining > 0)
				{
					return null;
				}
			}

			return references;
		}

		private static async Task<Plan> MakePlan(RagFair ragfair, Profile profile, Source src, Dictionary<string, List<Offer>> search, Dictionary<ECurrencyType, long> budget)
		{
			var plan = new Plan();
			var stash = src.Stash();
			var mode = src.Buying;

			foreach (var requirement in src.Requirements)
			{
				var need = new Need { Requirement = requirement, Name = NameOf(requirement.Item), Required = src.Required(requirement) };
				plan.Needs.Add(need);

				// A tool isn't used up, so it is only bought when you don't have it; a found-in-raid requirement only takes found-in-raid items
				var consumed = src.Consumed(requirement);
				var needsFir = src.NeedsFir(requirement);
				need.Held = Candidates(src, stash, requirement, true).Sum(item => item.StackObjectsCount);
				// Containers of this kind with something in them: held, but of no use here
				need.Busy = stash.Where(item => item != null && item.StringTemplateId == requirement.TemplateId && item is CompoundItem compound && !compound.IsEmpty).Sum(item => item.StackObjectsCount);
				need.FirHeld = Candidates(src, stash, requirement, true).Where(item => item.MarkedAsSpawnedInSession).Sum(item => item.StackObjectsCount);
				var owned = Candidates(src, stash, requirement, mode == Mode.All || !src.FirAsMissing || !consumed || needsFir).Sum(item => item.StackObjectsCount);
				need.Owned = owned;
				need.ToBuy = mode == Mode.All && consumed ? need.Required : Math.Max(0, need.Required - owned);
				if (need.ToBuy <= 0)
				{
					continue;
				}

				// Money, dog tags and found-in-raid items can't be bought on the flea market: what you hold is what there is
				if (needsFir || CurrencyUtil.IsCurrencyId(requirement.TemplateId) || requirement.Item.GetItemComponent<DogtagComponent>() != null)
				{
					need.ToBuy = 0;
					need.CannotBuy = need.Held < need.Required;
					if (need.CannotBuy && src.UnbuyableBlocks)
					{
						plan.Problems.Add($"{need.Name} can't be bought on the flea market and you don't have {need.Required}.");
					}

					continue;
				}

				if (!search.TryGetValue(requirement.TemplateId, out var offers))
				{
					offers = await FindOffers(ragfair, profile, requirement);
					search[requirement.TemplateId] = offers;
				}

				if (!Allocate(need, offers, budget))
				{
					// Is it the offers that run out, or only the money?
					var enoughOffers = Allocate(new Need { ToBuy = need.ToBuy }, offers, null);
					plan.Problems.Add(enoughOffers
						? $"Only {need.ToBuy - need.Shortfall} of {need.ToBuy} {need.Name} can be had with the money you hold."
						: $"Only {need.ToBuy - need.Shortfall} of {need.ToBuy} {need.Name} are for sale on the flea market.");
				}
			}

			return plan;
		}
		private static double UnitPrice(Offer offer) => offer.SellInOnePiece ? offer.Requirements[0].PreciseCount / Math.Max(1, offer.Quantity) : offer.Requirements[0].PreciseCount;

		private static void Take(Need need, Offer offer, int count)
		{
			var cost = offer.SellInOnePiece ? offer.Requirements[0].IntCount : (long)Math.Ceiling(offer.Requirements[0].PreciseCount * count);
			need.Picks.Add(new Pick { Offer = offer, Count = count, Cost = cost, Currency = offer.MoneyType });
		}

		// The cheapest offers first, per piece. A pack sold in one piece is only taken whole, and only when it fits in what is still needed;
		// if packs are all that is left, the cheapest pack that covers it is bought whole (the rest of it is spare).
		// Roubles first, then dollars, then euro: the others only for what the roubles can't cover. budget is what you hold of each
		// currency (spent as offers are taken), or null for no limit.
		private static bool Allocate(Need need, List<Offer> offers, Dictionary<ECurrencyType, long> budget)
		{
			var remaining = need.ToBuy;
			var sorted = offers.OrderBy(offer => Array.IndexOf(Currencies, offer.MoneyType)).ThenBy(UnitPrice).ThenBy(offer => offer.IntId).ToList();
			foreach (var offer in sorted)
			{
				if (remaining <= 0)
				{
					break;
				}

				var supply = offer.CurrentItemCount;
				if (offer.SellInOnePiece)
				{
					if (supply <= remaining && Pay(budget, offer, supply))
					{
						Take(need, offer, supply);
						remaining -= supply;
					}
				}
				else
				{
					var count = Math.Min(supply, remaining);
					if (budget != null)
					{
						count = (int)Math.Min(count, budget[offer.MoneyType] / Math.Max(1, (long)Math.Ceiling(offer.Requirements[0].PreciseCount)));
					}

					if (count > 0 && Pay(budget, offer, count))
					{
						Take(need, offer, count);
						remaining -= count;
					}
				}
			}

			if (remaining > 0)
			{
				var pack = sorted.Where(offer => offer.SellInOnePiece && offer.CurrentItemCount >= remaining && need.Picks.All(pick => pick.Offer != offer))
					.OrderBy(offer => Array.IndexOf(Currencies, offer.MoneyType)).ThenBy(offer => offer.Requirements[0].PreciseCount)
					.FirstOrDefault(offer => Pay(budget, offer, offer.CurrentItemCount));
				if (pack != null)
				{
					Take(need, pack, pack.CurrentItemCount);
					need.Spare = pack.CurrentItemCount - remaining;
					remaining = 0;
				}
			}

			need.Shortfall = remaining;
			return remaining == 0;
		}

		// Takes the price of count pieces of the offer out of the budget; false (nothing taken) when it isn't there
		private static bool Pay(Dictionary<ECurrencyType, long> budget, Offer offer, int count)
		{
			if (budget == null)
			{
				return true;
			}

			var price = offer.SellInOnePiece ? offer.Requirements[0].IntCount : (long)Math.Ceiling(offer.Requirements[0].PreciseCount * count);
			if (budget[offer.MoneyType] < price)
			{
				return false;
			}

			budget[offer.MoneyType] -= price;
			return true;
		}

		// The offers for one item that can be bought right now: other players' offers for roubles, dollars or euro, found by the game's own search
		private static async Task<List<Offer>> FindOffers(RagFair ragfair, Profile profile, IExchangeRequirement requirement)
		{
			var found = new List<Offer>();
			var session = ragfair._tradingSession;
			var delay = Singleton<GlobalConfiguration>.Instance.RagFair.delaySinceOfferAdd;
			var me = profile.Id.ToString();

			for (var page = 0; page < MaxPages; page++)
			{
				var response = await session.GetOffers(page, PageSize, (int)ESortType.Price, false, 0, 0, 0, 0, 0, 0, 100, false, true, (int)EOfferOwnerType.PlayerOwnerType,
					true, requirement.TemplateId, string.Empty, string.Empty, new Dictionary<string, int>(), 0, false);
				if (response.Failed || response.Value == null || response.Value.offers == null)
				{
					break;
				}

				foreach (var candidate in response.Value.offers)
				{
					if (candidate != null && !candidate.NotAvailable && candidate.CanBeBought && !candidate.Locked && candidate.CurrentItemCount > 0 && candidate.OnlyMoney
						&& Array.IndexOf(Currencies, candidate.MoneyType) >= 0 && candidate.MemberType != EMemberCategory.Trader && candidate.User != null && candidate.User.Id.ToString() != me
						&& (DateTimeExtensions.UtcNow - candidate.StartTime).TotalSeconds >= delay && candidate.Item != null && candidate.Item.StringTemplateId == requirement.TemplateId)
					{
						found.Add(candidate);
					}
				}

				if (response.Value.offers.Length == 0 || (page + 1) * PageSize >= response.Value.offersCount)
				{
					break;
				}
			}

			return found;
		}
	}

	// The item menu of a flea market offer is built here (for the buttons beside the offer); our entries are added to it
	internal class FleaBarterBuyMenuPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(ItemUiContext).GetMethod(nameof(ItemUiContext.GetItemContextInteractions), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(ItemContext itemContext, ContextInteractions<EItemInfoButton> __result)
		{
			// Never let a problem with our entries break the menu
			try
			{
				FleaBarterBuy.Add(itemContext, __result);
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Quick buy menu entries: {e}");
			}
		}
	}
}
