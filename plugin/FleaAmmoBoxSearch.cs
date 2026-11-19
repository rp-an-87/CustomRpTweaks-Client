using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.Ragfair;
using SPT.Reflection.Patching;
using UnityEngine;

namespace CustomRpTweaks.Client
{
	internal enum FleaAmmoBoxMode { Tree, Together }

	// Ammo can't be bought on the flea market, its boxes can. "Filter by item" (the menu entry, and the hotkey of UI Fixes, which runs the same
	// game action) on an ammo item that has boxes on sale lists the ammo and its boxes together, in the flea market's normal list: the game
	// asks the server for one item per search, so the request is made once per item (the ammo and each box) and the answers are merged,
	// sorted the way the list is sorted; and the game's check that an offer is the item searched for lets the boxes through (mode Together).
	// Mode Tree: no chip; the flea market opens with a neutral search, the ammo and its boxes are put into the left tree the way the game's own
	// name search fills it (without its one-second wait), and the first box is selected; every row then makes the game's own request.
	internal static class FleaAmmoBoxSearch
	{
		// Ammo template id -> the boxes that hold it and can be listed on the flea market; built once, the templates don't change in a session
		private static Dictionary<string, List<string>> _boxesOf;

		// Set while the merge asks for one item, so that request passes the patch untouched
		private static bool _inner;

		internal static List<string> BoxesFor(string ammoId)
		{
			if (_boxesOf == null)
			{
				_boxesOf = Build(Singleton<ItemFactory>.Instance.ItemTemplates.Values);
			}

			return _boxesOf.TryGetValue(ammoId, out var boxes) ? boxes : null;
		}

		private static Dictionary<string, List<string>> Build(IEnumerable<ItemTemplate> templates)
		{
			var map = new Dictionary<string, List<string>>();
			foreach (var box in templates.OfType<AmmoBoxTemplate>().Where(template => template.CanSellOnRagfair))
			{
				foreach (var slot in box.StackSlots ?? new StackSlot[0])
				{
					foreach (var filter in slot.Filters ?? new ItemFilter[0])
					{
						foreach (var allowed in filter.Filter ?? new MongoID[0])
						{
							var ammoId = allowed.ToString();
							if (!map.TryGetValue(ammoId, out var list))
							{
								map[ammoId] = list = new List<string>();
							}

							var boxId = box._id.ToString();
							if (!list.Contains(boxId))
							{
								list.Add(boxId);
							}
						}
					}
				}
			}

			return map;
		}

		// Mode Tree, called from the Filter by item action; true when the search was taken over (the game's own must not run)
		internal static bool TreeSearch(BaseItemContextInteractions interactions)
		{
			var item = interactions.Item;
			if (!Plugin.FleaAmmoBoxSearchEnabled.Value || Plugin.FleaAmmoBoxMode.Value != FleaAmmoBoxMode.Tree || !(item is Ammo))
			{
				return false;
			}

			var boxes = BoxesFor(item.TemplateId.ToString());
			if (boxes == null || boxes.Count == 0)
			{
				return false;
			}

			// The ammo last: the boxes are what can be bought
			var ids = boxes.Concat(new[] { item.TemplateId.ToString() }).ToList();
			Plugin.Verbose($"Flea: {item.ShortName.Localized()} listed with {boxes.Count} box(es) in the tree");
			// Any search opens the flea market; this one only switches off the one-hour-expiration filter, which is off unless turned on
			interactions.Search(new RagfairSearch(EFilterType.OneHourExpiration, 0, false));
			Plugin.Instance.StartCoroutine(FillTree(interactions.UIContext, ids));
			return true;
		}

		private static IEnumerator FillTree(ItemUiContext uiContext, List<string> ids)
		{
			RagfairCategoriesPanel panel = null;
			for (var waited = 0f; waited < 8f; waited += Time.unscaledDeltaTime)
			{
				yield return null;
				panel = UnityEngine.Object.FindObjectOfType<RagfairCategoriesPanel>();
				if (panel != null && panel.isActiveAndEnabled && panel.Nodes != null && panel.FilteredNodes != null && ids.All(id => panel.Nodes[id] != null))
				{
					break;
				}

				panel = null;
			}

			if (panel == null)
			{
				Plugin.Log.LogWarning("Ammo box search: the flea market's list didn't come up in time, nothing filled");
				yield break;
			}

			// A Filter by item chip from an earlier search would narrow the list to that one item
			var ragfair = uiContext?.Session?.RagFair;
			var chip = ragfair?.CancellableFilters[EFilterType.FilterSearch];
			if (chip != null)
			{
				ragfair.RemoveCancellableFilter(chip, true);
			}

			// The screen may still be filling its own list as it opens: fill, and fill again if it was overwritten
			var selected = false;
			for (var round = 0; round < 8 && panel != null; round++)
			{
				if (!ids.All(id => panel.FilteredNodes.ContainsKey(id)) || panel.FilteredNodes.Count != ids.Count)
				{
					panel.FilterHandler();
					foreach (var id in ids)
					{
						panel.FilteredNodes.Add(id, panel.Nodes[id].CreateDummy(false));
					}

					selected = false;
				}

				yield return new WaitForSecondsRealtime(0.15f);
				if (!selected && panel != null)
				{
					selected = panel.SelectNode(ids[0]) != null;
				}
			}
		}

		// The items whose offers belong in the list of this request: the ammo and its boxes; null when the request is not a Filter by item on such ammo
		internal static List<string> ItemsOf(RagFair ragfair, string handbookId, string linkedSearchId, string neededSearchId, int buildCount)
		{
			if (_inner || !Plugin.FleaAmmoBoxSearchEnabled.Value || Plugin.FleaAmmoBoxMode.Value != FleaAmmoBoxMode.Together || ragfair == null || string.IsNullOrEmpty(handbookId) || buildCount > 0
				|| !string.IsNullOrEmpty(linkedSearchId) || !string.IsNullOrEmpty(neededSearchId))
			{
				return null;
			}

			// Only while the Filter by item chip is the reason for the request (picking the ammo in the tree is left alone)
			if (!ragfair.CancellableFilters.IsActive(EFilterType.FilterSearch) || ragfair.FilterRule.FilterSearchId != handbookId)
			{
				return null;
			}

			var boxes = BoxesFor(handbookId);
			return boxes == null || boxes.Count == 0 ? null : new[] { handbookId }.Concat(boxes).ToList();
		}

		internal static Task<Result<OffersList>> Merged(EftClientBackendSession session, List<string> items, int page, int limit, int sortType, bool direction, int currency, int priceFrom,
			int priceTo, int quantityFrom, int quantityTo, int conditionFrom, int conditionTo, bool oneHourExpiration, bool removeBartering, int offerOwnerType, bool onlyFunctional, bool updateOfferCount)
		{
			// Each answer is the first page of its item, long enough to cover the page asked for
			var want = (page + 1) * limit;
			var tasks = new List<Task<Result<OffersList>>>();
			_inner = true;
			try
			{
				foreach (var id in items)
				{
					tasks.Add(session.GetOffers(0, want, sortType, direction, currency, priceFrom, priceTo, quantityFrom, quantityTo, conditionFrom, conditionTo, oneHourExpiration, removeBartering,
						offerOwnerType, onlyFunctional, id, string.Empty, string.Empty, new Dictionary<string, int>(), 0, updateOfferCount));
				}
			}
			finally
			{
				_inner = false;
			}

			return Merge(tasks, page, limit, sortType, direction);
		}

		private static async Task<Result<OffersList>> Merge(List<Task<Result<OffersList>>> tasks, int page, int limit, int sortType, bool direction)
		{
			var results = await Task.WhenAll(tasks);
			if (results.Any(result => result.Failed))
			{
				return results.First(result => result.Failed);
			}

			var first = results[0];
			var offers = results.SelectMany(result => result.Value.offers).ToList();
			var categories = new Dictionary<string, int>();
			foreach (var pair in results.SelectMany(result => result.Value.categories ?? new Dictionary<string, int>()))
			{
				categories[pair.Key] = (categories.TryGetValue(pair.Key, out var count) ? count : 0) + pair.Value;
			}

			// The server sorted each answer; the merge is sorted the same way (the sort kinds the list offers; anything else keeps the order given)
			IOrderedEnumerable<Offer> sorted;
			switch ((ESortType)sortType)
			{
				case ESortType.Price: sorted = Order(offers, offer => (double)offer.SummaryCost, direction); break;
				case ESortType.ExpirationDate: sorted = Order(offers, offer => (double)offer.EndTime.Ticks, direction); break;
				case ESortType.Rating: sorted = Order(offers, offer => offer.User == null ? 0d : offer.User.Rating, direction); break;
				case ESortType.Id: sorted = Order(offers, offer => (double)offer.IntId, direction); break;
				default: sorted = Order(offers, offer => 0d, false); break;
			}

			var value = first.Value;
			value.offers = sorted.Skip(page * limit).Take(limit).ToArray();
			value.offersCount = results.Sum(result => result.Value.offersCount);
			value.categories = categories;
			return first;
		}

		private static IOrderedEnumerable<Offer> Order(List<Offer> offers, Func<Offer, double> key, bool descending) =>
			descending ? offers.OrderByDescending(key) : offers.OrderBy(key);
	}

	internal class FleaAmmoBoxRequestPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(EftClientBackendSession).GetMethod(nameof(EftClientBackendSession.GetOffers), BindingFlags.Instance | BindingFlags.Public, null,
			new[] { typeof(int), typeof(int), typeof(int), typeof(bool), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(int), typeof(bool),
				typeof(string), typeof(string), typeof(string), typeof(Dictionary<string, int>), typeof(int), typeof(bool) }, null);

		[PatchPrefix]
		private static bool Prefix(EftClientBackendSession __instance, ref Task<Result<OffersList>> __result, int page, int limit, int sortType, bool direction, int currency, int priceFrom, int priceTo,
			int quantityFrom, int quantityTo, int conditionFrom, int conditionTo, bool oneHourExpiration, bool removeBartering, int offerOwnerType, bool onlyFunctional, string handbookId,
			string linkedSearchId, string neededSearchId, int buildCount, bool updateOfferCount)
		{
			try
			{
				var items = FleaAmmoBoxSearch.ItemsOf(__instance.RagFair, handbookId, linkedSearchId, neededSearchId, buildCount);
				if (items == null)
				{
					return true;
				}

				Plugin.Verbose($"Flea: Filter by item on ammo, asking for {items.Count} items");
				__result = FleaAmmoBoxSearch.Merged(__instance, items, page, limit, sortType, direction, currency, priceFrom, priceTo, quantityFrom, quantityTo, conditionFrom, conditionTo,
					oneHourExpiration, removeBartering, offerOwnerType, onlyFunctional, updateOfferCount);
				return false;
			}
			catch (Exception e)
			{
				// Never break the game's own request
				Plugin.Log.LogError($"Ammo box search failed, using the game's request: {e}");
				return true;
			}
		}
	}

	// The game's list check lets an offer through for the one item searched; for ammo the boxes are let through too (the check runs as if the box were the item searched)
	internal class FleaAmmoBoxAcceptPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(RagFair).GetMethod(nameof(RagFair.IsOfferAcceptable), BindingFlags.Instance | BindingFlags.Public);

		[PatchPrefix]
		private static void Prefix(RagFair __instance, Offer offer, out FilterRule? __state)
		{
			__state = null;
			try
			{
				if (!Plugin.FleaAmmoBoxSearchEnabled.Value || Plugin.FleaAmmoBoxMode.Value != FleaAmmoBoxMode.Together || offer?.Item == null || !__instance.CancellableFilters.IsActive(EFilterType.FilterSearch))
				{
					return;
				}

				var rule = __instance.FilterRule;
				var boxes = string.IsNullOrEmpty(rule.FilterSearchId) ? null : FleaAmmoBoxSearch.BoxesFor(rule.FilterSearchId);
				if (boxes != null && boxes.Contains(offer.Item.TemplateId.ToString()))
				{
					__state = rule;
					rule.FilterSearchId = offer.Item.TemplateId.ToString();
					__instance.FilterRule = rule;
				}
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Ammo box list check failed: {e}");
			}
		}

		[PatchFinalizer]
		private static void Finalizer(RagFair __instance, FilterRule? __state)
		{
			if (__state.HasValue)
			{
				__instance.FilterRule = __state.Value;
			}
		}
	}

	// The game's Filter by item action, for mode Tree
	internal class FleaAmmoBoxTreePatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(BaseItemContextInteractions).GetMethod("method_41", BindingFlags.Instance | BindingFlags.Public);

		[PatchPrefix]
		private static bool Prefix(BaseItemContextInteractions __instance)
		{
			try
			{
				return !FleaAmmoBoxSearch.TreeSearch(__instance);
			}
			catch (Exception e)
			{
				// Never break the game's own search
				Plugin.Log.LogError($"Ammo box search failed, using the game's search: {e}");
				return true;
			}
		}
	}
}
