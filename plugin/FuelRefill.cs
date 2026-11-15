using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Hideout;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.Ragfair;
using SPT.Reflection.Patching;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CustomRpTweaks.Client
{
	// "Refill" on the generator's fuel slots (and the water collector's filter slot, which is the same kind of slot): a button over a slot that is empty or whose fuel ran out. It buys one full fuel tank on the
	// flea market (traders' offers included, the cheapest full one) and puts it in the slot the way the slot's own dropdown would: the empty
	// tank goes back to the stash, the new one takes its place.
	// First it uses a tank that is already in the stash (any with fuel left: Metal before Expeditionary, the fullest first). Otherwise it buys, in order: a Metal fuel tank at a normal price, an Expeditionary fuel tank at a normal price, then (nothing is for sale at
	// a normal price) a Metal tank at an overstock price (a price the server's trader overstock raised), then an Expeditionary one.
	// The button's text is white when the purchase is at a normal price, orange at an overstock price, red when nothing can be bought.
	internal static class FuelRefill
	{
		private const string ButtonName = "RefillFuel";

		// In order of preference
		private static readonly string[] FuelTanks = { "5d1b36a186f7742523398433", "5d1b371186f774253763a656" };
		private const string WaterFilter = "5d1b385e86f774252167b98a";

		private static string NameOf(string id) => id == FuelTanks[0] ? "Metal fuel tank" : id == FuelTanks[1] ? "Expeditionary fuel tank" : id == WaterFilter ? "Water filter" : (id + " Name").Localized(null);

		// What a slot takes, in order of preference: the generator's two kinds of tank, the water collector's filter. Null: not a slot this is for.
		internal static string[] KindsFor(IConsumer consumer)
		{
			if (consumer is GeneratorBehaviour)
			{
				return FuelTanks;
			}

			var filters = consumer == null || consumer.Slots == null || consumer.Slots.Filters == null ? null : consumer.Slots.Filters.Select(id => id.ToString()).ToList();
			return consumer is PermanentProducer && filters != null && filters.Contains(WaterFilter) ? new[] { WaterFilter } : null;
		}

		private class Choice
		{
			public Offer Offer;
			public Item Stock;                                            // a tank from the stash, nothing is bought
			public int Tank;
			public string[] Kinds;
			public long Price;
			public long BasePrice;
			public ECurrencyType Currency;
			public float Capacity;
			public bool Overstock;
			public string Problem;
			public readonly List<string> Notes = new List<string>();      // why a tank that comes earlier in the order was not bought
			public bool CanBuy => Offer != null || Stock != null;
			public string Name => NameOf(Kinds[Tank]);
		}

		private static RagFair RagfairNow()
		{
			var context = ItemUiContext.Instance;
			return context == null || context.Session == null ? null : context.Session.RagFair;
		}

		private static bool Available(RagFair ragfair) => ragfair != null && !ragfair.Disabled && ragfair.Status != RagFair.ERagFairStatus.Banned;

		private static long PriceOf(Offer offer) => offer.SellInOnePiece ? offer.Requirements[0].IntCount : (long)Math.Ceiling(offer.Requirements[0].PreciseCount);

		// Only a full tank is worth buying: a player's offer can be a half used one
		private static bool IsFull(Offer offer)
		{
			var resource = offer.Item == null ? null : offer.Item.GetItemComponent<ResourceComponent>();
			return resource == null || resource.Value >= resource.MaxResource - 0.01f;
		}

		private static float CapacityOf(Offer offer)
		{
			var resource = offer.Item == null ? null : offer.Item.GetItemComponent<ResourceComponent>();
			return resource == null ? 0f : resource.MaxResource;
		}

		private static float FuelOf(Item item)
		{
			var resource = item.GetItemComponent<ResourceComponent>();
			return resource == null ? 0f : resource.Value;
		}

		// Tanks in the stash with fuel left: Metal before Expeditionary, the fullest first
		private static Item StockedTank(List<Item> stash, string[] kinds, out int tank)
		{
			for (tank = 0; tank < kinds.Length; tank++)
			{
				var id = kinds[tank];
				var found = stash.Where(item => item != null && item.StringTemplateId == id && FuelOf(item) > Mathf.Epsilon).OrderByDescending(FuelOf).FirstOrDefault();
				if (found != null)
				{
					return found;
				}
			}

			tank = 0;
			return null;
		}

		// A trader's offer whose price the server's overstock raised (the same test as the flea market rows use); other players' offers never are
		private static bool Overstocked(Offer offer, out int basePrice)
		{
			basePrice = (int)PriceOf(offer);
			if (offer.MemberType != EMemberCategory.Trader || offer.User == null || offer.Item == null)
			{
				return false;
			}

			return OverstockPrices.IsRaised('f', offer.User.Id.ToString(), offer.Item.Id.ToString(), offer.Requirements[0].IntCount, out basePrice);
		}

		private static string Money(ECurrencyType currency, long amount, bool colour) => FleaBarterBuy.Price(new Dictionary<ECurrencyType, long> { { currency, amount } }, colour);

		private static string OrangeHex => Plugin.PriceIndicatorNewColor != null && !string.IsNullOrEmpty(Plugin.PriceIndicatorNewColor.Value) ? Plugin.PriceIndicatorNewColor.Value : "#FF9A3C";

		private static string WhyNot(string[] kinds, int tank, List<Offer> normal, List<Offer> raised, Dictionary<ECurrencyType, long> wallet)
		{
			if (normal.Count == 0)
			{
				return raised.Count == 0 ? $"No full {NameOf(kinds[tank])} for sale" : $"{NameOf(kinds[tank])} is only for sale at an overstock price";
			}

			var cheapest = normal[0];
			var missing = PriceOf(cheapest) - wallet[cheapest.MoneyType];
			return $"The cheapest {NameOf(kinds[tank])} costs {Money(cheapest.MoneyType, PriceOf(cheapest), false)} and you are short by {Money(cheapest.MoneyType, missing, false)}";
		}

		private static Choice Made(string[] kinds, Offer offer, int tank, bool overstock, List<string> notes, Dictionary<Offer, int> bases)
		{
			var choice = new Choice
			{
				Offer = offer, Kinds = kinds, Tank = tank, Price = PriceOf(offer), Currency = offer.MoneyType, Capacity = CapacityOf(offer), Overstock = overstock,
				BasePrice = bases.TryGetValue(offer, out var basePrice) ? basePrice : PriceOf(offer)
			};
			choice.Notes.AddRange(notes);
			return choice;
		}

		// The cheapest full tank at a price you can pay, by the order in the header comment. Each tank's offers are split into those at a
		// normal price and those the overstock raised.
		private static async Task<Choice> Choose(RagFair ragfair, List<Item> stash, string[] kinds)
		{
			var stocked = StockedTank(stash, kinds, out var stockedTank);
			if (stocked != null)
			{
				return new Choice { Stock = stocked, Kinds = kinds, Tank = stockedTank, Capacity = stocked.GetItemComponent<ResourceComponent>().MaxResource };
			}

			var wallet = FleaBarterBuy.Wallet(stash);
			var profile = ragfair._tradingSession.Profile;
			FleaBarterBuy.UpdateRates(ragfair);
			var normal = new List<Offer>[kinds.Length];
			var raised = new List<Offer>[kinds.Length];
			var bases = new Dictionary<Offer, int>();
			for (var tank = 0; tank < kinds.Length; tank++)
			{
				normal[tank] = new List<Offer>();
				raised[tank] = new List<Offer>();
				var offers = (await FleaBarterBuy.FindOffers(ragfair, profile, kinds[tank]))
					.Where(offer => IsFull(offer) && (!offer.SellInOnePiece || offer.CurrentItemCount == 1))
					.OrderBy(FleaBarterBuy.Value).ThenBy(offer => offer.IntId);
				foreach (var offer in offers)
				{
					if (Overstocked(offer, out var basePrice))
					{
						bases[offer] = basePrice;
						raised[tank].Add(offer);
					}
					else
					{
						normal[tank].Add(offer);
					}
				}
			}

			Func<Offer, bool> affordable = offer => wallet[offer.MoneyType] >= PriceOf(offer);
			var notes = new List<string>();
			for (var tank = 0; tank < kinds.Length; tank++)
			{
				var pick = normal[tank].FirstOrDefault(affordable);
				if (pick != null)
				{
					return Made(kinds, pick, tank, false, notes, bases);
				}

				notes.Add(WhyNot(kinds, tank, normal[tank], raised[tank], wallet));
			}

			for (var tank = 0; tank < kinds.Length; tank++)
			{
				var pick = raised[tank].FirstOrDefault(affordable);
				if (pick != null)
				{
					return Made(kinds, pick, tank, true, notes, bases);
				}
			}

			var everything = normal.SelectMany(list => list).Concat(raised.SelectMany(list => list)).OrderBy(FleaBarterBuy.Value).ToList();
			if (everything.Count == 0)
			{
				return new Choice { Problem = "There is no full " + string.Join(" or ", kinds.Select(NameOf)) + " for sale on the flea market" };
			}

			var cheapest = everything[0];
			var tankOfCheapest = Array.IndexOf(kinds, cheapest.Item.StringTemplateId);
			return new Choice
			{
				Problem = $"The cheapest full one ({NameOf(kinds[Math.Max(0, tankOfCheapest)])}) costs {Money(cheapest.MoneyType, PriceOf(cheapest), false)} and you are short by {Money(cheapest.MoneyType, PriceOf(cheapest) - wallet[cheapest.MoneyType], false)}"
			};
		}

		private static List<Item> StashOf(RagFair ragfair) => ragfair._tradingSession.Profile.Inventory.Stash.GetAllItemsFromCollection().ToList();

		// One search serves every slot's button and tooltip for a few seconds, as long as what you hold is the same
		private static Task<Choice> _current;
		private static string _currentKey;
		private static float _currentAt;

		private static Task<Choice> Current(RagFair ragfair, string[] kinds)
		{
			var key = string.Join("+", kinds) + StateKey(false, kinds);
			if (_current != null && key == _currentKey && Time.realtimeSinceStartup - _currentAt < 10f && !_current.IsFaulted && !_current.IsCanceled)
			{
				return _current;
			}

			_currentKey = key;
			_currentAt = Time.realtimeSinceStartup;
			_current = Choose(ragfair, StashOf(ragfair), kinds);
			return _current;
		}

		// The tooltip of the button
		internal static async Task<string> Quote(bool replacing, string[] kinds)
		{
			var ragfair = RagfairNow();
			if (!Available(ragfair))
			{
				return $"<b><color={FleaBarterBuy.Red}>Can't refill:</color></b> the flea market is not available";
			}

			var choice = await Current(ragfair, kinds);
			if (!choice.CanBuy)
			{
				return $"<b><color={FleaBarterBuy.Red}>Can't refill:</color></b> " + choice.Problem;
			}

			if (choice.Stock != null)
			{
				return $"<b>Will use:</b> the <b>{choice.Name}</b> in your stash ({Mathf.CeilToInt(FuelOf(choice.Stock))}/{choice.Capacity:0})\n<size=85%><color={FleaBarterBuy.Grey}>Nothing is bought." + (replacing ? " What is in the slot now goes to your stash." : string.Empty) + "</color></size>";
			}

			var lines = new List<string>();
			foreach (var note in choice.Notes)
			{
				lines.Add($"<color={(choice.Overstock ? OrangeHex : FleaBarterBuy.Yellow)}>{note}.</color>");
			}

			lines.Add(choice.Tank > 0
				? $"<b>Will buy:</b> a full <b>{choice.Name}</b> instead (holds {choice.Capacity:0})"
				: $"<b>Will buy:</b> a full <b>{choice.Name}</b>");
			if (choice.Overstock)
			{
				lines.Add($"<b>Buy for:</b> <color={OrangeHex}>{Money(choice.Currency, choice.Price, false)}</color>");
				lines.Add($"<size=85%><color={FleaBarterBuy.Grey}>An overstock price, normally {Money(choice.Currency, choice.BasePrice, false)}.</color></size>");
			}
			else
			{
				lines.Add("<b>Buy for:</b> " + Money(choice.Currency, choice.Price, true));
			}

			if (replacing)
			{
				lines.Add($"<size=85%><color={FleaBarterBuy.Grey}>What is in the slot now goes to your stash.</color></size>");
			}

			return string.Join("\n", lines);
		}

		// What a choice depends on besides the offers: what you hold (and, for a tooltip, whether there is a tank to replace)
		internal static string StateKey(bool replacing, string[] kinds)
		{
			var ragfair = RagfairNow();
			if (ragfair == null)
			{
				return "none";
			}

			var stash = StashOf(ragfair);
			var tanks = stash.Where(item => item != null && Array.IndexOf(kinds, item.StringTemplateId) >= 0 && FuelOf(item) > Mathf.Epsilon).Select(item => item.Id + "=" + Mathf.CeilToInt(FuelOf(item)));
			return replacing + ":" + string.Join(",", FleaBarterBuy.Wallet(stash).Select(pair => pair.Key + "=" + pair.Value)) + "|" + string.Join(",", tanks);
		}

		internal static void Start(FuelSelectionCell cell, string[] kinds)
		{
			// What the tooltip says is about to be out of date
			QuoteHover.ClearCache();
			var tooltip = ItemUiContext.Instance == null ? null : ItemUiContext.Instance.Tooltip;
			if (tooltip != null)
			{
				tooltip.Close();
			}

			FleaBarterBuy.RunGuarded(() => Run(cell, kinds));
		}

		private static async Task Run(FuelSelectionCell cell, string[] kinds)
		{
			var ragfair = RagfairNow();
			if (!Available(ragfair))
			{
				FleaBarterBuy.Notify("Refill: the flea market is not available.");
				return;
			}

			// Always from fresh offers: this one spends money
			_current = null;
			var stash = StashOf(ragfair);
			var choice = await Choose(ragfair, stash, kinds);
			if (!choice.CanBuy)
			{
				FleaBarterBuy.Notify("Refill: nothing bought. " + choice.Problem + ".");
				return;
			}

			Item bought = choice.Stock;
			if (bought == null)
			{
				var before = new HashSet<string>(stash.Select(item => item.Id.ToString()));
				var failure = await FleaBarterBuy.Purchase(ragfair, stash, new Dictionary<IExchangeable, int> { { choice.Offer, 1 } });
				if (failure != null)
				{
					FleaBarterBuy.Notify(failure.Replace("Quick buy:", "Refill:"));
					return;
				}

				_current = null;

				// The tank should be in the stash as the purchase returns; give the game a moment if not
				for (var attempt = 0; attempt < 15 && bought == null; attempt++)
				{
					bought = StashOf(ragfair).FirstOrDefault(item => item.StringTemplateId == kinds[choice.Tank] && !before.Contains(item.Id.ToString()));
					if (bought == null)
					{
						await Task.Delay(200);
					}
				}
			}

			var stocked = choice.Stock != null;
			var price = stocked ? string.Empty : " for " + Money(choice.Currency, choice.Price, false);
			var what = choice.Name + (choice.Tank > 0 ? $" instead of the {NameOf(kinds[0])}" : string.Empty) + (choice.Overstock ? " at an overstock price" : string.Empty);
			if (bought == null)
			{
				FleaBarterBuy.Notify($"Refill: bought a {what}{price}, but it is not in your stash yet. Put it in the slot yourself.");
				return;
			}

			RefreshHideout();
			if (cell == null || !cell.isActiveAndEnabled)
			{
				FleaBarterBuy.Notify($"Refill: {(stocked ? "took" : "bought")} a {what}{price}. The screen was closed, so it is in your stash.");
				return;
			}

			// The same call the slot's dropdown makes: the empty tank is taken out to the stash, the new one is put in
			cell.ItemSelected(bought);
			var filled = cell.FuelItem != null && cell.FuelItem.Value > Mathf.Epsilon;
			FleaBarterBuy.Notify(filled
				? (stocked ? $"Refill: put the {what} from your stash in the slot." : $"Refill: bought a {what}{price} and put it in the slot.")
				: $"Refill: {(stocked ? "took" : "bought")} a {what}{price}, but the slot could not take it (is the stash full?). It is in your stash.");

			// What the other buttons say has changed (the money, the offers)
			foreach (var placer in UnityEngine.Object.FindObjectsOfType<FuelRefillPlacer>())
			{
				UpdateLook(placer.Cell);
			}
		}

		// The hideout only learns of items that arrive in the stash through the game's own events, which a flea market purchase doesn't raise
		private static void RefreshHideout()
		{
			try
			{
				var hideout = Singleton<HideoutRepresentation>.Instance;
				if (hideout != null)
				{
					hideout.method_25();
				}
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning("Refill: couldn't refresh the hideout stash: " + e.Message);
			}
		}

		private static Color Tint(string hex, Color fallback) => ColorUtility.TryParseHtmlString(hex, out var colour) ? colour : fallback;

		// The text of the button: white at a normal price, orange at an overstock price, red when nothing can be bought
		private static async void UpdateLook(FuelSelectionCell cell)
		{
			try
			{
				var button = cell == null ? null : cell.transform.Find(ButtonName);
				var placer = button == null ? null : button.GetComponent<FuelRefillPlacer>();
				if (placer == null || placer.Label == null)
				{
					return;
				}

				var ragfair = RagfairNow();
				Color colour;
				if (!Available(ragfair))
				{
					colour = Tint(FleaBarterBuy.Red, Color.red);
				}
				else
				{
					var choice = await Current(ragfair, placer.Kinds);
					colour = !choice.CanBuy ? Tint(FleaBarterBuy.Red, Color.red) : choice.Overstock ? Tint(OrangeHex, new Color(1f, 0.6f, 0.24f)) : Color.white;
				}

				if (placer != null && placer.Label != null)
				{
					placer.Label.color = colour;
				}
			}
			catch (Exception e)
			{
				Plugin.Verbose($"Refill button colour: {e.Message}");
			}
		}

		// Called when a slot is shown: the button is made once per slot (generator only; the air filter unit uses the same screen)
		internal static void Attach(FuelSelectionCell cell, IConsumer consumer)
		{
			var kinds = KindsFor(consumer);
			if (kinds == null)
			{
				return;
			}

			if (cell.transform.Find(ButtonName) == null)
			{
				Build(cell, kinds);
			}

			Refresh(cell);
		}

		// Shown on a slot with no fuel or fuel that ran out, as long as the slot can be changed
		internal static void Refresh(FuelSelectionCell cell)
		{
			var button = cell.transform.Find(ButtonName);
			if (button == null)
			{
				return;
			}

			var resource = cell.FuelItem;
			var empty = resource == null || resource.Value <= Mathf.Epsilon;
			var show = Plugin.HideoutFuelRefill.Value && empty && cell.DropdownButton != null && cell.DropdownButton.gameObject.activeSelf;
			if (button.gameObject.activeSelf != show)
			{
				button.gameObject.SetActive(show);
			}

			if (show)
			{
				UpdateLook(cell);
			}
		}

		private static void Build(FuelSelectionCell cell, string[] kinds)
		{
			var go = new GameObject(ButtonName, typeof(RectTransform), typeof(Image), typeof(Button), typeof(QuoteHover), typeof(FuelRefillPlacer), typeof(LayoutElement));
			go.transform.SetParent(cell.transform, false);
			// The slot may arrange its children itself (that put the button under the box); this one is placed by the placer
			go.GetComponent<LayoutElement>().ignoreLayout = true;
			// Its own canvas, one above the slot's, so it is drawn over the box and gets the clicks
			var parentCanvas = cell.GetComponentInParent<Canvas>();
			var canvas = go.AddComponent<Canvas>();
			canvas.overrideSorting = true;
			canvas.sortingOrder = (parentCanvas == null ? 0 : parentCanvas.sortingOrder) + 1;
			go.AddComponent<GraphicRaycaster>();
			var placer = go.GetComponent<FuelRefillPlacer>();
			placer.Cell = cell;
			placer.Kinds = kinds;
			placer.Dropdown = cell.DropdownButton == null ? null : cell.DropdownButton.transform as RectTransform;

			// A dark, slightly see-through bar with a thin light frame (like the game's own frames); lighter on hover, darker pressed
			var image = go.GetComponent<Image>();
			image.color = new Color(0.07f, 0.08f, 0.08f, 0.82f);
			var frame = go.AddComponent<UnityEngine.UI.Outline>();
			frame.effectColor = new Color(0.55f, 0.55f, 0.52f, 0.9f);
			frame.effectDistance = new Vector2(1f, -1f);
			var click = go.GetComponent<Button>();
			click.targetGraphic = image;
			var colors = click.colors;
			colors.normalColor = Color.white;
			colors.highlightedColor = new Color(2.2f, 2.2f, 2.2f, 1f);
			colors.pressedColor = new Color(0.6f, 0.6f, 0.6f, 1f);
			click.colors = colors;
			click.onClick.AddListener(() => Start(cell, kinds));

			var hover = go.GetComponent<QuoteHover>();
			hover.CacheKey = () => "fuel:" + string.Join("+", kinds) + StateKey(cell != null && cell.FuelItem != null, kinds);
			hover.Quote = () => Quote(cell != null && cell.FuelItem != null, kinds);

			var caption = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
			caption.transform.SetParent(go.transform, false);
			var label = caption.GetComponent<TextMeshProUGUI>();
			if (cell._details != null)
			{
				label.font = cell._details.font;
			}

			label.text = "Refill";
			label.color = Color.white;
			label.alignment = TextAlignmentOptions.Center;
			label.enableAutoSizing = true;
			label.fontSizeMin = 8f;
			label.fontSizeMax = 13f;
			label.enableWordWrapping = false;
			label.raycastTarget = false;
			var rect = (RectTransform)caption.transform;
			rect.anchorMin = Vector2.zero;
			rect.anchorMax = Vector2.one;
			rect.offsetMin = new Vector2(2f, 0f);
			rect.offsetMax = new Vector2(-2f, 0f);
			placer.Label = label;
			go.SetActive(false);
		}
	}

	// Keeps the button across the top of the slot's box, worked out in world space (so it doesn't depend on how the game anchors the slot).
	// The box is the slot's item icon when that is about as wide as the dropdown arrow is tall (the box is square, the arrow as tall as it);
	// otherwise it is estimated from the arrow.
	internal class FuelRefillPlacer : MonoBehaviour
	{
		private const float Height = 22f;
		private const float Inset = 3f;

		public FuelSelectionCell Cell;
		public RectTransform Dropdown;
		public TextMeshProUGUI Label;
		public string[] Kinds;
		private readonly Vector3[] _corners = new Vector3[4];
		private bool _logged;

		private void LateUpdate()
		{
			if (Cell == null || Dropdown == null)
			{
				return;
			}

			var rect = (RectTransform)transform;
			var scale = rect.lossyScale.x;
			if (scale <= 0f)
			{
				return;
			}

			Dropdown.GetWorldCorners(_corners);
			var arrowLeft = _corners[0].x;
			var arrowHeight = _corners[1].y - _corners[0].y;
			var left = 0f;
			var top = 0f;
			var width = 0f;
			var source = "arrow";

			var icon = Cell._selectedItemIcon == null ? null : Cell._selectedItemIcon.transform as RectTransform;
			if (icon != null)
			{
				icon.GetWorldCorners(_corners);
				var iconWidth = _corners[2].x - _corners[0].x;
				if (iconWidth > arrowHeight * 0.7f && iconWidth < arrowHeight * 1.4f)
				{
					left = _corners[0].x;
					top = _corners[1].y;
					width = iconWidth;
					source = "icon";
				}
			}

			if (width <= 0f)
			{
				// The box is a little taller than the arrow and sits just to the left of it
				width = arrowHeight * 1.09f;
				left = arrowLeft - arrowHeight * 0.07f - width;
				Dropdown.GetWorldCorners(_corners);
				top = _corners[1].y + arrowHeight * 0.04f;
			}

			if (!_logged)
			{
				_logged = true;
				Plugin.Verbose($"Refill button placed from the {source}: left {left:0.0}, width {width:0.0}, top {top:0.0}, arrow height {arrowHeight:0.0}, scale {scale:0.00}");
			}

			var localWidth = width / scale - 2f * Inset;
			if (localWidth < 10f)
			{
				return;
			}

			rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
			rect.pivot = new Vector2(0f, 1f);
			rect.sizeDelta = new Vector2(localWidth, Height);
			var target = new Vector3(left + Inset * scale, top - Inset * scale, rect.position.z);
			if ((rect.position - target).sqrMagnitude > 1e-6f)
			{
				rect.position = target;
			}
		}
	}

	// A fuel slot is shown: it gets its button
	internal class FuelRefillShowPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(FuelSelectionCell).GetMethod(nameof(FuelSelectionCell.Show), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(FuelSelectionCell __instance, IConsumer consumer)
		{
			// Never let a problem with the button break the screen
			try
			{
				FuelRefill.Attach(__instance, consumer);
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Fuel refill button: {e}");
			}
		}
	}

	// The slot's fuel changed (put in, taken out, used up): the button follows
	internal class FuelRefillUpdatePatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(FuelSelectionCell).GetMethod(nameof(FuelSelectionCell.UpdateDetails), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(FuelSelectionCell __instance)
		{
			try
			{
				FuelRefill.Refresh(__instance);
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Fuel refill button: {e}");
			}
		}
	}
}
