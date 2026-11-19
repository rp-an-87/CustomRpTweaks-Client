using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
	// game action) on an ammo item that has boxes on sale opens the flea market with no item chip: the ammo and its boxes are put into the left
	// tree the way the game's own name search fills it (without its one-second wait), and the first box is selected; every row then makes the
	// game's own request, with its own paging and sorting. Anything else, and ammo without a box on sale, keeps the game's own search.
	internal static class FleaAmmoBoxSearch
	{
		// Ammo template id -> the boxes that hold it and can be listed on the flea market; built once, the templates don't change in a session
		private static Dictionary<string, List<string>> _boxesOf;

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

		// Called from the Filter by item action; true when the search was taken over (the game's own must not run)
		internal static bool TreeSearch(BaseItemContextInteractions interactions)
		{
			var item = interactions.Item;
			if (!Plugin.FleaAmmoBoxSearchEnabled.Value || !(item is Ammo))
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
	}

	// The game's Filter by item action
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
