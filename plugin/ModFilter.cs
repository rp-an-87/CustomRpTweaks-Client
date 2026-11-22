using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.UI;
using EFT.UI.DragAndDrop;
using EFT.UI.WeaponModding;
using SPT.Reflection.Patching;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CustomRpTweaks.Client
{
	// Typing while a mod list is open (the drop-down of a slot in the weapon builds screen or the modding screen, DropDownMenu) dims every
	// part whose short or full name does not contain what was typed. The filter is empty each time a list opens or closes, and any mouse click
	// clears it (that covers picking a part). Dimming is our own black overlay on each tile, so the game's own tile state is untouched.
	internal static class ModFilter
	{
		private const string OverlayName = "CRT_FilterDim";
		private const string LabelName = "CRT_FilterLabel";

		private static DropDownMenu _menu;
		private static string _filter = "";
		private static TextMeshProUGUI _label;
		private static readonly FieldInfo ItemsContainer = typeof(DropDownMenu).GetField("_itemsContainer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		private static readonly FieldInfo NoneContainer = typeof(DropDownMenu).GetField("_noneItemContainer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

		internal static void Opened(DropDownMenu menu)
		{
			Clear();
			_menu = menu;
		}

		internal static void Closing(DropDownMenu menu)
		{
			Clear();
			if (_menu == menu)
			{
				_menu = null;
			}
		}

		internal static void Update()
		{
			if (!Plugin.ModFilterEnabled.Value || _menu == null || !_menu.Open)
			{
				return;
			}

			// A click anywhere clears the filter (picking a part, closing the list, clicking elsewhere)
			if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2))
			{
				Clear();
				return;
			}

			// Don't take the keys while a text field (build name, search) has focus
			var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
			if (selected != null && (selected.GetComponent<InputField>() != null || selected.GetComponent<TMP_InputField>() != null))
			{
				return;
			}

			var typed = Input.inputString;
			if (string.IsNullOrEmpty(typed))
			{
				return;
			}

			var changed = false;
			foreach (var c in typed)
			{
				if (c == '\b')
				{
					if (_filter.Length > 0)
					{
						_filter = _filter.Substring(0, _filter.Length - 1);
						changed = true;
					}
				}
				else if (c >= ' ' && c != 127)
				{
					_filter += c;
					changed = true;
				}
			}

			if (changed)
			{
				Apply();
			}
		}

		private static void Clear()
		{
			if (_filter.Length == 0 && _label == null)
			{
				return;
			}

			_filter = "";
			Apply();
		}

		private static void Apply()
		{
			var menu = _menu;
			if (menu == null)
			{
				RemoveLabel();
				return;
			}

			var active = _filter.Length > 0;
			foreach (var container in new[] { ItemsContainer, NoneContainer })
			{
				var root = container?.GetValue(menu) as RectTransform;
				if (root == null)
				{
					continue;
				}

				foreach (var view in root.GetComponentsInChildren<ItemView>(true))
				{
					var item = view.Item;
					var match = item != null && (Contains(item.ShortName.Localized(), _filter) || Contains(item.Name.Localized(), _filter));
					SetDim(view.transform, active && !match);
				}

				// The NONE tile is not an item: it never matches a typed filter
				foreach (var empty in root.GetComponentsInChildren<EmptyItemView>(true))
				{
					SetDim(empty.transform, active);
				}
			}

			if (active)
			{
				ShowLabel(menu);
			}
			else
			{
				RemoveLabel();
			}
		}

		private static bool Contains(string text, string filter) =>
			text != null && text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

		private static void SetDim(Transform tile, bool dim)
		{
			var existing = tile.Find(OverlayName);
			if (!dim)
			{
				if (existing != null)
				{
					UnityEngine.Object.Destroy(existing.gameObject);
				}
				return;
			}

			if (existing != null)
			{
				return;
			}

			var go = new GameObject(OverlayName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
			var rect = (RectTransform)go.transform;
			rect.SetParent(tile, false);
			rect.anchorMin = Vector2.zero;
			rect.anchorMax = Vector2.one;
			rect.offsetMin = Vector2.zero;
			rect.offsetMax = Vector2.zero;
			rect.SetAsLastSibling();
			var image = go.GetComponent<Image>();
			image.color = new Color(0f, 0f, 0f, 0.72f);
			// Dimmed tiles stay clickable
			image.raycastTarget = false;
		}

		private static void ShowLabel(DropDownMenu menu)
		{
			if (_label == null)
			{
				var go = new GameObject(LabelName, typeof(RectTransform));
				var rect = (RectTransform)go.transform;
				rect.SetParent(menu.transform, false);
				rect.anchorMin = new Vector2(0f, 1f);
				rect.anchorMax = new Vector2(1f, 1f);
				rect.pivot = new Vector2(0f, 0f);
				rect.anchoredPosition = new Vector2(0f, 2f);
				rect.sizeDelta = new Vector2(0f, 24f);
				_label = go.AddComponent<TextMeshProUGUI>();
				var font = menu.GetComponentInChildren<TMP_Text>(true)?.font;
				if (font != null)
				{
					_label.font = font;
				}
				_label.fontSize = 18f;
				_label.color = new Color(1f, 0.92f, 0.4f, 1f);
				_label.raycastTarget = false;
				_label.enableWordWrapping = false;
			}

			_label.text = "Filter: " + _filter;
		}

		private static void RemoveLabel()
		{
			if (_label != null)
			{
				UnityEngine.Object.Destroy(_label.gameObject);
				_label = null;
			}
		}
	}

	internal class ModFilterShowPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(DropDownMenu).GetMethod("Show", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(DropDownMenu __instance)
		{
			if (Plugin.ModFilterEnabled.Value)
			{
				ModFilter.Opened(__instance);
			}
		}
	}

	// A prefix, so the dimming is removed before the game pools the tiles again
	internal class ModFilterClosePatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(DropDownMenu).GetMethod("Close", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPrefix]
		private static void Prefix(DropDownMenu __instance)
		{
			ModFilter.Closing(__instance);
		}
	}
}
