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
	// Ammo can't be bought on the flea market, its boxes can. "Filter by item" (the menu entry, and the hotkey of UI Fixes, which runs the same
	// game action) on an ammo item that has boxes on sale runs as the game does it, untouched (chip, request, the result on screen at once).
	// Once that has settled, the chip is taken off the way the X on it does (the list stays: it is the ammo's category), the ammo and its boxes
	// are put into the left tree the way the game's own name search fills it, the ammo's name goes into the search box (clear it and the normal
	// tree is back) and the ammo's row is highlighted. Every row is then the game's own search. Anything else keeps the game's own search.
	internal static class FleaAmmoBoxSearch
	{
		// Ammo template id -> the boxes that hold it and can be listed on the flea market; built once, the templates don't change in a session
		private static Dictionary<string, List<string>> _boxesOf;

		// The latest Show task of each panel; the game builds the tree inside it (see FleaTreeShowPatch)
		internal static readonly Dictionary<RagfairCategoriesPanel, Task> ShowTasks = new Dictionary<RagfairCategoriesPanel, Task>();

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

		// Called after the game's own Filter by item action has run
		internal static void AfterFilter(BaseItemContextInteractions interactions)
		{
			var item = interactions.Item;
			if (!Plugin.FleaAmmoBoxSearchEnabled.Value || !(item is Ammo))
			{
				return;
			}

			var boxes = BoxesFor(item.TemplateId.ToString());
			if (boxes == null || boxes.Count == 0)
			{
				return;
			}

			// The ammo last: the boxes are what can be bought
			var ids = boxes.Concat(new[] { item.TemplateId.ToString() }).ToList();
			Plugin.Verbose($"Flea: {item.ShortName.Localized()} filtered by the game, adding {boxes.Count} box(es) to the tree after");
			Plugin.Instance.StartCoroutine(AddBoxes(interactions.UIContext, ids, item.TemplateId.ToString(), (item.TemplateId.ToString() + " Name").Localized()));
		}

		private static IEnumerator AddBoxes(ItemUiContext uiContext, List<string> ids, string ammoId, string name)
		{
			// 1. The game's own search is done: the flea market is open and built, the chip of this ammo is on, and the request has been answered
			RagfairCategoriesPanel panel = null;
			RagFair ragfair = null;
			var waited = 0f;
			for (; waited < 20f; waited += Time.unscaledDeltaTime)
			{
				yield return null;
				ragfair = uiContext?.Session?.RagFair;
				var found = UnityEngine.Object.FindObjectOfType<RagfairCategoriesPanel>();
				if (ragfair != null && found != null && found.isActiveAndEnabled && found.Nodes != null && found.FilteredNodes != null && ids.All(id => found.Nodes[id] != null)
					&& ShowTasks.TryGetValue(found, out var task) && task.IsCompleted
					&& ragfair.CancellableFilters.IsActive(EFilterType.FilterSearch) && ragfair.FilterRule.FilterSearchId == ammoId)
				{
					panel = found;
					break;
				}
			}

			if (panel == null)
			{
				Plugin.Log.LogWarning("Ammo box search: the game's own search didn't settle in time, the boxes were not added");
				yield break;
			}

			yield return WaitIdle(ragfair);
			Plugin.Verbose($"Flea: the game's search is on screen after {waited:0.00} s, chips [{Chips(ragfair)}]");

			// 2. The chip narrows the tree to the one item and the game ignores clicks on its rows: off it goes, as the X on it does. The rule keeps the
			// ammo as its category, so the list stays as it is.
			var chip = ragfair.CancellableFilters[EFilterType.FilterSearch];
			if (chip != null)
			{
				ragfair.RemoveCancellableFilter(chip, true);
			}

			yield return null;
			yield return null;
			yield return WaitIdle(ragfair);
			for (var guard = 0f; guard < 10f; guard += Time.unscaledDeltaTime)
			{
				if (ShowTasks.TryGetValue(panel, out var rebuilt) && rebuilt.IsCompleted)
				{
					break;
				}

				yield return null;
			}

			// Highlight the ammo's row the way the screen does it itself
			var offerList = UnityEngine.Object.FindObjectsOfType<OfferViewList>().FirstOrDefault(view => view.isActiveAndEnabled && view._browseCategoriesPanel == panel);
			if (offerList == null)
			{
				Plugin.Log.LogWarning("Ammo box search: the flea market's offer list wasn't found, the tree is filled but the row was not highlighted");
				yield break;
			}

			// While the chip was on, the game counted the tree's items from the offers it had (the ammo's alone), so the boxes count as 0 and their rows
			// stay hidden. Ask the way the refresh button does: the answer carries the real counts of every item.
			Plugin.Verbose($"Flea: counts before the refresh: {Counts(panel, ids)}");
			ragfair.ClearCachedInfo();
			offerList.GetOffers(RagFair.ESetFilterSource.FilterWindow, true, true);
			yield return null;
			yield return null;
			yield return WaitIdle(ragfair);
			// The answer sets the counts; a row with none stays hidden. Wait until a box has one (the limit is for a box nobody sells at the moment)
			var boxIds = ids.Where(id => id != ammoId).ToList();
			for (var guard = 0f; guard < 6f && !boxIds.Any(id => panel.Nodes[id].Count > 0); guard += Time.unscaledDeltaTime)
			{
				yield return null;
			}

			Plugin.Verbose($"Flea: counts after the refresh: {Counts(panel, ids)}, getting offers: {ragfair.GettingOffers}");

			// The ammo and its boxes into the tree, the name into the box. Built now, after the counts are in: a row copies its item's count when it is made,
			// so rows made earlier would stay at 0 and hidden. If the screen rebuilds its tree after that (a second Show), do it again.
			for (var round = 0; round < 3 && panel != null; round++)
			{
				var shown = ShowTasks[panel];
				panel.FilterHandler();
				foreach (var id in ids)
				{
					panel.FilteredNodes.Add(id, panel.Nodes[id].CreateDummy(false));
				}

				ShowAsSearch(panel, name);

				// The rows' views are made over the next frames, and only a row with a view can be highlighted
				for (var frames = 0; frames < 180 && panel.GetNode(ammoId) == null; frames++)
				{
					yield return null;
				}

				yield return null;
				if (ShowTasks.TryGetValue(panel, out var latest) && latest == shown)
				{
					break;
				}

				Plugin.Verbose("Flea: the screen rebuilt its tree after the fill, filling again");
			}

			offerList.HandleNodeSelection(new OffersData(RagFair.EUpdatePagesStatus.DoNotUpdate, ammoId));
			Plugin.Verbose($"Flea: tree rows {ViewCount(panel)}, filtered nodes {panel.FilteredNodes.Count}, rule item search '{ragfair.FilterRule.FilterSearchId}', handbook '{ragfair.FilterRule.HandbookId}'");
		}

		// The tree now stands for a search by the ammo's name: the name is in the search box, and the panel knows a filter is on, so clearing the box
		// (or typing something else) brings back the full tree the way it does after any search
		private static void ShowAsSearch(RagfairCategoriesPanel panel, string name)
		{
			panel.SearchInputField.SetTextWithoutNotify(name);
			var type = typeof(BrowseCategoriesPanel);
			type.GetField("_previousFilter", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(panel, name);
			type.GetField("_clearedAlready", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(panel, false);
		}

		// Waits until the game has no offers request in flight (starting a frame later, as a request begins a frame after what asked for it)
		private static IEnumerator WaitIdle(RagFair ragfair)
		{
			yield return null;
			for (var guard = 0f; guard < 10f && ragfair.GettingOffers; guard += Time.unscaledDeltaTime)
			{
				yield return null;
			}
		}

		private static string Counts(RagfairCategoriesPanel panel, List<string> ids) =>
			string.Join(", ", ids.Select(id => $"{id.Substring(0, 6)}: node {panel.Nodes[id].Count}, row {(panel.FilteredNodes.ContainsKey(id) ? panel.FilteredNodes[id].Count.ToString() : "-")}").ToArray());

		private static string Chips(RagFair ragfair) =>
			string.Join(", ", ragfair.CancellableFilters.AllFilters.Select(chip => chip.Type.ToString()).ToArray());

		// How many rows the tree shows (the game keeps them in a protected dictionary)
		private static int ViewCount(BrowseCategoriesPanel panel)
		{
			var member = typeof(BrowseCategoriesPanel).GetProperty("ViewNodes", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(panel)
				?? typeof(BrowseCategoriesPanel).GetField("ViewNodes", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(panel);
			return member is ICollection collection ? collection.Count : 0;
		}
	}

	// The game's Filter by item action: it runs as it is, and the boxes are added after it
	internal class FleaAmmoBoxTreePatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(BaseItemContextInteractions).GetMethod("method_41", BindingFlags.Instance | BindingFlags.Public);

		[PatchPostfix]
		private static void Postfix(BaseItemContextInteractions __instance)
		{
			try
			{
				FleaAmmoBoxSearch.AfterFilter(__instance);
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Ammo box search failed: {e}");
			}
		}
	}

	// RagfairCategoriesPanel.Show is an async method: its Task completes once the tree's rows are made. The task is kept per panel, so the
	// code above waits for the game's own "tree built" instead of a timer.
	internal class FleaTreeShowPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() =>
			typeof(RagfairCategoriesPanel).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).First(method => method.Name == "Show");

		[PatchPostfix]
		private static void Postfix(RagfairCategoriesPanel __instance, Task __result)
		{
			if (__instance != null && __result != null)
			{
				FleaAmmoBoxSearch.ShowTasks[__instance] = __result;
			}
		}
	}
}
