using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.Ragfair;
using SPT.Reflection.Patching;

namespace CustomRpTweaks.Client
{
	// Ammo can't be bought on the flea market, its boxes can. "Filter by item" (the menu entry, and the hotkey of UI Fixes, which runs the same
	// game action) on an ammo item with boxes on sale searches those boxes instead. Anything else, and ammo without a box on sale, keeps the
	// game's own search. The game's plain filter takes one template; the search the builds use takes a list, so that one carries the boxes.
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

		// True when the search was started here (the game's own must not run)
		internal static bool TrySearch(BaseItemContextInteractions interactions)
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

			var value = new BuildItemSearchValue
			{
				BuildName = item.ShortName.Localized() + " boxes",
				BuildCount = 1,
				BuildItems = boxes.ToDictionary(id => id, id => 1)
			};
			Plugin.Verbose($"Flea: {item.ShortName.Localized()} searched as {boxes.Count} box(es)");
			interactions.Search(new RagfairSearch(EFilterType.BuildItems, value, true));
			return true;
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
				return !FleaAmmoBoxSearch.TrySearch(__instance);
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
