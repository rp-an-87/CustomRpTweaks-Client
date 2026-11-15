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
	// "Refill" on the generator's fuel slots: a button over a slot that is empty or whose fuel ran out. It buys one full fuel tank on the
	// flea market (traders' offers included, the cheapest full one) and puts it in the slot the way the slot's own dropdown would: the empty
	// tank goes back to the stash, the new one takes its place. A Metal fuel tank is preferred; when none is for sale, the Expeditionary one
	// is bought instead and the tooltip and the message say so.
	internal static class FuelRefill
	{
		private const string ButtonName = "RefillFuel";

		// In order of preference
		private static readonly string[] Tanks = { "5d1b36a186f7742523398433", "5d1b371186f774253763a656" };
		private static readonly string[] TankNames = { "Metal fuel tank", "Expeditionary fuel tank" };

		private class Choice
		{
			public Offer Offer;
			public int Tank;
			public long Price;
			public ECurrencyType Currency;
			public float Capacity;
			public string Problem;
			public string Name => TankNames[Tank];
			public bool Fallback => Tank > 0;
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

		// The cheapest full tank you can pay for, the Metal one first. The Expeditionary one is only taken when no full Metal tank is for
		// sale at all; a Metal tank you can't afford is reported, not swapped for the other.
		private static async Task<Choice> Choose(RagFair ragfair, Dictionary<ECurrencyType, long> wallet)
		{
			var profile = ragfair._tradingSession.Profile;
			FleaBarterBuy.UpdateRates(ragfair);
			for (var tank = 0; tank < Tanks.Length; tank++)
			{
				var offers = (await FleaBarterBuy.FindOffers(ragfair, profile, Tanks[tank]))
					.Where(offer => IsFull(offer) && (!offer.SellInOnePiece || offer.CurrentItemCount == 1))
					.OrderBy(FleaBarterBuy.Value).ThenBy(offer => offer.IntId).ToList();
				if (offers.Count == 0)
				{
					continue;
				}

				var pick = offers.FirstOrDefault(offer => wallet[offer.MoneyType] >= PriceOf(offer));
				if (pick != null)
				{
					return new Choice { Offer = pick, Tank = tank, Price = PriceOf(pick), Currency = pick.MoneyType, Capacity = CapacityOf(pick) };
				}

				var cheapest = offers[0];
				var missing = PriceOf(cheapest) - wallet[cheapest.MoneyType];
				return new Choice
				{
					Tank = tank,
					Problem = $"The cheapest full {TankNames[tank]} costs {FleaBarterBuy.Price(new Dictionary<ECurrencyType, long> { { cheapest.MoneyType, PriceOf(cheapest) } }, false)} and you are short by {FleaBarterBuy.Price(new Dictionary<ECurrencyType, long> { { cheapest.MoneyType, missing } }, false)}"
				};
			}

			return new Choice { Problem = "There is no full Metal fuel tank or Expeditionary fuel tank for sale on the flea market" };
		}

		private static List<Item> StashOf(RagFair ragfair) => ragfair._tradingSession.Profile.Inventory.Stash.GetAllItemsFromCollection().ToList();

		// The tooltip of the button
		internal static async Task<string> Quote(bool replacing)
		{
			var ragfair = RagfairNow();
			if (!Available(ragfair))
			{
				return $"<b><color={FleaBarterBuy.Red}>Can't refill:</color></b> the flea market is not available";
			}

			var choice = await Choose(ragfair, FleaBarterBuy.Wallet(StashOf(ragfair)));
			var lines = new List<string>();
			if (choice.Problem != null)
			{
				lines.Add($"<b><color={FleaBarterBuy.Red}>Can't refill:</color></b> " + choice.Problem);
				return string.Join("\n", lines);
			}

			if (choice.Fallback)
			{
				lines.Add($"<b><color={FleaBarterBuy.Yellow}>No full {TankNames[0]} for sale.</color></b>");
				lines.Add($"<b>Will buy:</b> a full <b>{choice.Name}</b> instead (holds {choice.Capacity:0} fuel)");
			}
			else
			{
				lines.Add($"<b>Will buy:</b> a full <b>{choice.Name}</b>");
			}

			lines.Add("<b>Buy for:</b> " + FleaBarterBuy.Price(new Dictionary<ECurrencyType, long> { { choice.Currency, choice.Price } }, true));
			if (replacing)
			{
				lines.Add($"<size=85%><color={FleaBarterBuy.Grey}>The empty tank in the slot goes to your stash.</color></size>");
			}

			return string.Join("\n", lines);
		}

		// What a quote depends on besides the offers: what you hold and whether there is a tank to replace
		internal static string StateKey(bool replacing)
		{
			var ragfair = RagfairNow();
			if (ragfair == null)
			{
				return "none";
			}

			return replacing + ":" + string.Join(",", FleaBarterBuy.Wallet(StashOf(ragfair)).Select(pair => pair.Key + "=" + pair.Value));
		}

		internal static void Start(FuelSelectionCell cell)
		{
			// What the tooltip says is about to be out of date
			QuoteHover.ClearCache();
			var tooltip = ItemUiContext.Instance == null ? null : ItemUiContext.Instance.Tooltip;
			if (tooltip != null)
			{
				tooltip.Close();
			}

			FleaBarterBuy.RunGuarded(() => Run(cell));
		}

		private static async Task Run(FuelSelectionCell cell)
		{
			var ragfair = RagfairNow();
			if (!Available(ragfair))
			{
				FleaBarterBuy.Notify("Refill: the flea market is not available.");
				return;
			}

			var stash = StashOf(ragfair);
			var choice = await Choose(ragfair, FleaBarterBuy.Wallet(stash));
			if (choice.Problem != null)
			{
				FleaBarterBuy.Notify("Refill: nothing bought. " + choice.Problem + ".");
				return;
			}

			var before = new HashSet<string>(stash.Select(item => item.Id.ToString()));
			var failure = await FleaBarterBuy.Purchase(ragfair, stash, new Dictionary<IExchangeable, int> { { choice.Offer, 1 } });
			if (failure != null)
			{
				FleaBarterBuy.Notify(failure.Replace("Quick buy:", "Refill:"));
				return;
			}

			// The tank should be in the stash as the purchase returns; give the game a moment if not
			Item bought = null;
			for (var attempt = 0; attempt < 15 && bought == null; attempt++)
			{
				bought = StashOf(ragfair).FirstOrDefault(item => item.StringTemplateId == Tanks[choice.Tank] && !before.Contains(item.Id.ToString()));
				if (bought == null)
				{
					await Task.Delay(200);
				}
			}

			var price = FleaBarterBuy.Price(new Dictionary<ECurrencyType, long> { { choice.Currency, choice.Price } }, false);
			var what = choice.Fallback ? $"{choice.Name} (no full {TankNames[0]} for sale)" : choice.Name;
			if (bought == null)
			{
				FleaBarterBuy.Notify($"Refill: bought a {what} for {price}, but it is not in your stash yet. Put it in the slot yourself.");
				return;
			}

			RefreshHideout();
			if (cell == null || !cell.isActiveAndEnabled)
			{
				FleaBarterBuy.Notify($"Refill: bought a {what} for {price}. The screen was closed, so it is in your stash.");
				return;
			}

			// The same call the slot's dropdown makes: the empty tank is taken out to the stash, the new one is put in
			cell.ItemSelected(bought);
			var filled = cell.FuelItem != null && cell.FuelItem.Value > Mathf.Epsilon;
			FleaBarterBuy.Notify(filled
				? $"Refill: bought a {what} for {price} and put it in the slot."
				: $"Refill: bought a {what} for {price}, but the slot could not take it (is the stash full?). It is in your stash.");
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

		// Called when a slot is shown: the button is made once per slot (generator only; the air filter unit uses the same screen)
		internal static void Attach(FuelSelectionCell cell, IConsumer consumer)
		{
			if (!(consumer is GeneratorBehaviour))
			{
				return;
			}

			if (cell.transform.Find(ButtonName) == null)
			{
				Build(cell);
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
		}

		private static void Build(FuelSelectionCell cell)
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
			placer.Dropdown = cell.DropdownButton == null ? null : cell.DropdownButton.transform as RectTransform;

			// Like the game's own flat dark buttons: dark panel, light text, lighter on hover
			var image = go.GetComponent<Image>();
			image.color = new Color(0.16f, 0.18f, 0.18f, 0.95f);
			var click = go.GetComponent<Button>();
			click.targetGraphic = image;
			var colors = click.colors;
			colors.normalColor = Color.white;
			colors.highlightedColor = new Color(1.6f, 1.6f, 1.6f, 1f);
			colors.pressedColor = new Color(0.7f, 0.7f, 0.7f, 1f);
			click.colors = colors;
			click.onClick.AddListener(() => Start(cell));

			var hover = go.GetComponent<QuoteHover>();
			hover.CacheKey = () => "fuel:" + StateKey(cell != null && cell.FuelItem != null);
			hover.Quote = () => Quote(cell != null && cell.FuelItem != null);

			var caption = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
			caption.transform.SetParent(go.transform, false);
			var label = caption.GetComponent<TextMeshProUGUI>();
			if (cell._details != null)
			{
				label.font = cell._details.font;
			}

			label.text = "REFILL";
			label.color = new Color(0.82f, 0.81f, 0.74f, 1f);
			label.alignment = TextAlignmentOptions.Center;
			label.enableAutoSizing = true;
			label.fontSizeMin = 8f;
			label.fontSizeMax = 14f;
			label.enableWordWrapping = false;
			label.raycastTarget = false;
			var rect = (RectTransform)caption.transform;
			rect.anchorMin = Vector2.zero;
			rect.anchorMax = Vector2.one;
			rect.offsetMin = new Vector2(2f, 0f);
			rect.offsetMax = new Vector2(-2f, 0f);
			go.SetActive(false);
		}
	}

	// Keeps the button across the top of the slot's box, worked out in world space (so it doesn't depend on how the game anchors the
	// slot): from the left edge of the slot to where its dropdown arrow starts.
	internal class FuelRefillPlacer : MonoBehaviour
	{
		private const float Height = 30f;
		private const float Inset = 2f;

		public FuelSelectionCell Cell;
		public RectTransform Dropdown;
		private readonly Vector3[] _corners = new Vector3[4];
		private bool _logged;

		private void LateUpdate()
		{
			if (Cell == null)
			{
				return;
			}

			var rect = (RectTransform)transform;
			var scale = rect.lossyScale.x;
			if (scale <= 0f)
			{
				return;
			}

			((RectTransform)Cell.transform).GetWorldCorners(_corners);
			var left = _corners[1].x;
			var top = _corners[1].y;
			var hasDropdown = false;
			var right = _corners[2].x;
			if (Dropdown != null)
			{
				Dropdown.GetWorldCorners(_corners);
				// The arrow is as tall as the box and starts at its top: the cell's own top is not (the button ended up under the box)
				top = _corners[1].y;
				hasDropdown = true;
				if (_corners[0].x > left + 1f * scale)
				{
					right = Mathf.Min(right, _corners[0].x);
				}
			}

			if (!_logged)
			{
				_logged = true;
				Plugin.Verbose($"Refill button placed (dropdown {hasDropdown}): left {left:0.0}, right {right:0.0}, top {top:0.0}, scale {scale:0.00}");
			}

			var width = (right - left) / scale - 2f * Inset;
			if (width < 10f)
			{
				return;
			}

			rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
			rect.pivot = new Vector2(0f, 1f);
			rect.sizeDelta = new Vector2(width, Height);
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
