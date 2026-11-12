using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace CustomRpTweaks.Client
{
	// Select all of a kind: with the cursor on ammo, a throwable (grenade, flashbang, smoke) or a dog tag, one hotkey selects every
	// item of that whole category in the container (UI Fixes' own select-all only takes the exact same item type). The selection
	// itself is UI Fixes' multiselect, so everything that works on a selection there works on this one. Only offered when UI Fixes
	// (com.tyfon.uifixes) is loaded; its members are reached by reflection, so a UI Fixes update that renames them turns this off
	// with a warning in the log instead of breaking anything.
	internal static class SelectAllOfKind
	{
		internal const string TargetGuid = "com.tyfon.uifixes";

		internal static ConfigEntry<KeyboardShortcut> Hotkey;

		private static MethodInfo _select;      // UIFixes.MultiSelect.Select(GridItemView, bool)
		private static MethodInfo _clear;       // UIFixes.MultiSelect.Clear()
		private static PropertyInfo _enabled;   // UIFixes.MultiSelect.Enabled
		private static readonly FieldInfo ItemViews = AccessTools.Field(typeof(GridView), "ItemViews");
		private static readonly FieldInfo DragContext = AccessTools.Field(typeof(ItemUiContext), "_dragItemContext");

		// The kinds; an item belongs to at most one
		private static readonly Func<Item, bool>[] Kinds =
		{
			item => item.Template is AmmoTemplate,
			item => item is ThrowWeap,
			item => item.GetItemComponent<DogtagComponent>() != null,
		};

		internal static void Setup(ConfigFile config)
		{
			try
			{
				if (!Chainloader.PluginInfos.TryGetValue(TargetGuid, out var info) || info.Instance == null)
				{
					return;
				}

				var type = info.Instance.GetType().Assembly.GetType("UIFixes.MultiSelect");
				_select = type?.GetMethod("Select", BindingFlags.Public | BindingFlags.Static);
				_clear = type?.GetMethod("Clear", BindingFlags.Public | BindingFlags.Static);
				_enabled = type?.GetProperty("Enabled", BindingFlags.Public | BindingFlags.Static);
				if (_select == null || _clear == null || _enabled == null || ItemViews == null || DragContext == null)
				{
					Plugin.Log.LogWarning("Select all of kind: UI Fixes' multiselect was not found in the expected form, the feature is off.");
					_select = null;
					return;
				}

				Hotkey = config.Bind("22. Select all of kind", "1. Select all of kind hotkey", new KeyboardShortcut(KeyCode.A, KeyCode.LeftControl),
					"Needs UI Fixes with Multiselect on. Hover ammo, a throwable (any grenade, flashbang or smoke) or a dog tag and press this to select every item of that whole category in the container: all ammo whatever the caliber, all throwables whatever the type, all dog tags whatever the side. UI Fixes' own select-all key (same exact item only) is untouched. Unassign the key to turn the feature off. If both keys are the same, this one wins.");
				new Patch().Enable();
				Plugin.Verbose("Select all of kind: hooked into UI Fixes' multiselect.");
			}
			catch (Exception e)
			{
				_select = null;
				Plugin.Log.LogWarning("Select all of kind: setup failed, the feature is off: " + e.Message);
			}
		}

		private static Func<Item, bool> KindOf(Item item)
		{
			foreach (var kind in Kinds)
			{
				if (kind(item))
				{
					return kind;
				}
			}

			return null;
		}

		// After UI Fixes' own patch on the same method, so when both keys are the same it is this selection that stays
		private class Patch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => AccessTools.DeclaredMethod(typeof(ItemView), nameof(ItemView.Update));

			[PatchPostfix]
			[HarmonyPriority(Priority.Last)]
			private static void Postfix(ItemView __instance, ItemUiContext ___ItemUiContext)
			{
				if (_select == null || Hotkey == null || __instance is not GridItemView gridItemView || !gridItemView.IsSearched)
				{
					return;
				}

				if (!Hotkey.Value.IsDown() || !(bool)_enabled.GetValue(null, null))
				{
					return;
				}

				var item = __instance.Item;
				if (item == null || ___ItemUiContext == null
					|| ___ItemUiContext.CurrentItemContext != __instance.ItemContext // another item is under the cursor
					|| DragContext.GetValue(___ItemUiContext) != null)                // something is being dragged
				{
					return;
				}

				var kind = KindOf(item);
				if (kind == null)
				{
					return;
				}

				// Some places have several grids, some have one
				GridView[] grids = null;
				var gridsView = __instance.GetComponentInParent<ContainedGridsView>();
				if (gridsView != null)
				{
					grids = gridsView.GridViews;
				}
				else
				{
					var grid = __instance.GetComponentInParent<GridView>();
					if (grid != null)
					{
						grids = new[] { grid };
					}
				}

				if (grids != null)
				{
					SelectKind(kind, grids);
				}
			}
		}

		private static void SelectKind(Func<Item, bool> kind, IEnumerable<GridView> grids)
		{
			_clear.Invoke(null, null);
			var count = 0;
			foreach (var gridView in grids)
			{
				var views = (Dictionary<string, ItemView>)ItemViews.GetValue(gridView);
				// A copy: creating a view for an off-screen item adds to the dictionary
				foreach (var item in new List<Item>(gridView.Grid.Items))
				{
					if (!kind(item))
					{
						continue;
					}

					GridItemView view;
					if (views.TryGetValue(item.Id, out var existing))
					{
						view = existing as GridItemView;
					}
					else if (item.CurrentAddress is GridItemAddress address)
					{
						// Same as UI Fixes: an item off screen has no view yet; the grid view keeps the one it creates
						view = gridView.CreateItemView(item, address.LocationInGrid, ItemUiContext.Instance, item.Parent.GetOwnerOrNull()) as GridItemView;
					}
					else
					{
						continue;
					}

					if (view != null)
					{
						_select.Invoke(null, new object[] { view, false });
						count++;
					}
				}
			}

			Plugin.Verbose("Select all of kind: selected " + count + " items.");
		}
	}
}
