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
	// game action) on an ammo item with boxes on sale turns into a search by the ammo's name in the flea market's own search box, so the list
	// shows the ammo and its boxes together. Anything else, and ammo without a box on sale, keeps the game's own search.
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

		// Ammo with a box on sale: no item search at all. The game opens the flea market for any search; the harmless one used here switches off the
	// one-hour-expiration filter (which is off unless the owner turned it on), so nothing narrows the list. Then the ammo's name goes into the
	// flea market's own search box, and its tree lists the ammo and every box of it.
	internal static bool ShouldReplace(BaseItemContextInteractions interactions)
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

		var name = (item.TemplateId.ToString() + " Name").Localized();
		Plugin.Verbose($"Flea: {name} searched by name, {boxes.Count} box(es) on sale");
		interactions.Search(new RagfairSearch(EFilterType.OneHourExpiration, 0, false));
		Plugin.Instance.StartCoroutine(TypeName(name));
		return true;
	}

	private static IEnumerator TypeName(string name)
	{
		// The screen opens over a few frames and may clear its box while it does: wait for it, then type, and type again if it was cleared
		RagfairCategoriesPanel panel = null;
		for (var waited = 0f; waited < 6f; waited += Time.unscaledDeltaTime)
		{
			yield return null;
			panel = UnityEngine.Object.FindObjectOfType<RagfairCategoriesPanel>();
			if (panel != null && panel.isActiveAndEnabled && panel.SearchInputField != null)
			{
				break;
			}

			panel = null;
		}

		if (panel == null)
		{
			Plugin.Log.LogWarning("Ammo box search: the flea market didn't open in time, nothing typed");
			yield break;
		}

		// A Filter by item chip from an earlier search would keep narrowing the list to that one item: off it goes, as the X on it does
		var ragfair = ItemUiContext.Instance?.Session?.RagFair;
		var chip = ragfair?.CancellableFilters[EFilterType.FilterSearch];
		if (chip != null)
		{
			ragfair.RemoveCancellableFilter(chip, true);
		}

		for (var i = 0; i < 4 && panel != null; i++)
		{
			yield return new WaitForSecondsRealtime(0.4f);
			if (panel != null && panel.SearchInputField != null && panel.SearchInputField.text != name)
			{
				panel.SearchInputField.text = name;
			}
		}
	}
}

	internal class FleaAmmoBoxSearchPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(BaseItemContextInteractions).GetMethod("method_41", BindingFlags.Instance | BindingFlags.Public);

		[PatchPrefix]
		private static bool Prefix(BaseItemContextInteractions __instance)
		{
			try
			{
				return !FleaAmmoBoxSearch.ShouldReplace(__instance);
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
