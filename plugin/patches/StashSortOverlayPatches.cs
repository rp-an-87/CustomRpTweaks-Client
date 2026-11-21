using System.Linq;
using System.Reflection;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CustomRpTweaks.Client.Patches
{
	// Hovering the stash sort button shades the top rows that sorting treats specially: red stripes (/) for the rows that are kept free,
	// blue stripes (\) for the rows that are ignored. Both start at the top, so where they overlap the stripes cross. Right-clicking
	// the button opens the window that sets them, and the shading then stays up and follows the sliders.
	internal class StashSortOverlayPatch : ModulePatch
	{
		private const int CellSize = 63;
		private static readonly FieldInfo ItemField = typeof(GridSortPanel).GetField("_item", BindingFlags.Instance | BindingFlags.NonPublic);

		private static GameObject _reservedBand, _ignoredBand;
		private static Sprite _reservedStripes, _ignoredStripes;
		private static CompoundItem _shownFor;

		// While the settings window is open the shading stays, whatever the pointer does
		internal static bool Pinned;

		protected override MethodBase GetTargetMethod() => typeof(GridSortPanel).GetMethod(nameof(GridSortPanel.Awake), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(GridSortPanel __instance)
		{
			var trigger = __instance._button.gameObject.AddComponent<EventTrigger>();
			AddEntry(trigger, EventTriggerType.PointerEnter, _ =>
			{
				var item = (CompoundItem)ItemField.GetValue(__instance);
				Show(item);
				ShowTip(item);
			});
			AddEntry(trigger, EventTriggerType.PointerExit, _ =>
			{
				if (!Pinned)
				{
					Hide();
				}

				CloseTip();
			});
			AddEntry(trigger, EventTriggerType.PointerClick, data =>
			{
				CloseTip();
				var item = (CompoundItem)ItemField.GetValue(__instance);
				if (item is Stash && data is PointerEventData pointer && pointer.button == PointerEventData.InputButton.Right)
				{
					StashSortWindow.Open(item);
					return;
				}

				if (!Pinned)
				{
					Hide();
				}
			});
		}

		private static void AddEntry(EventTrigger trigger, EventTriggerType type, UnityEngine.Events.UnityAction<BaseEventData> action)
		{
			var entry = new EventTrigger.Entry { eventID = type };
			entry.callback.AddListener(action);
			trigger.triggers.Add(entry);
		}

		// The sort button has no tooltip of its own: the one here is only the hint for the window (with the hint setting, like the items' hotkeys)
		private static void ShowTip(CompoundItem item)
		{
			if (!(item is Stash) || Plugin.HotkeyHints == null || !Plugin.HotkeyHints.Value)
			{
				return;
			}

			ItemUiContext.Instance?.Tooltip?.Show("Sort the stash" + "\n" + TooltipSeparators.Line(HotkeyHints.SeparatorLength) + "\n" + HotkeyHints.Open + "Right-click: rows to ignore and keep free" + HotkeyHints.Close, null, 0.25f, null);
		}

		private static void CloseTip() => ItemUiContext.Instance?.Tooltip?.Close();

		internal static void Show(CompoundItem item)
		{
			Hide();
			if (!(item is Stash))
			{
				return;
			}

			_shownFor = item;
			Refresh();
		}

		// Draws the bands again for the current settings (called while a slider moves)
		internal static void Refresh()
		{
			DestroyBands();
			var item = _shownFor;
			if (item == null)
			{
				return;
			}

			int reserved = StashSortReservePatches.ReservedRows;
			int ignored = StashSortReservePatches.IgnoredRows;
			if (reserved <= 0 && ignored <= 0)
			{
				return;
			}

			var gridView = Object.FindObjectsOfType<GridView>().FirstOrDefault(view => view.Grid != null && view.Grid.ParentItem == item);
			if (gridView == null)
			{
				return;
			}

			_reservedBand = Band(gridView, reserved, _reservedStripes ?? (_reservedStripes = Stripes(false, new Color(0.9f, 0.15f, 0.15f))));
			// Drawn last, so on top
			_ignoredBand = Band(gridView, ignored, _ignoredStripes ?? (_ignoredStripes = Stripes(true, new Color(0.2f, 0.5f, 1f))));
		}

		private static GameObject Band(GridView gridView, int rows, Sprite stripes)
		{
			rows = Mathf.Min(rows, gridView.Grid.GridHeight);
			if (rows <= 0)
			{
				return null;
			}

			var band = new GameObject("Sort rows overlay", typeof(RectTransform), typeof(Image));
			var rect = (RectTransform)band.transform;
			rect.SetParent(gridView.transform, false);
			rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
			rect.anchoredPosition = Vector2.zero;
			rect.sizeDelta = new Vector2(gridView.Grid.GridWidth * CellSize, rows * CellSize);

			var image = band.GetComponent<Image>();
			image.sprite = stripes;
			image.type = Image.Type.Tiled;
			image.raycastTarget = false;
			return band;
		}

		internal static void Hide()
		{
			DestroyBands();
			_shownFor = null;
		}

		private static void DestroyBands()
		{
			if (_reservedBand != null)
			{
				Object.Destroy(_reservedBand);
			}

			if (_ignoredBand != null)
			{
				Object.Destroy(_ignoredBand);
			}

			_reservedBand = _ignoredBand = null;
		}

		// Translucent diagonal stripes, built once per direction
		private static Sprite Stripes(bool mirrored, Color color)
		{
			const int size = 16;
			var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Point };
			for (int y = 0; y < size; y++)
			{
				for (int x = 0; x < size; x++)
				{
					int diagonal = mirrored ? (x - y + size) : (x + y);
					var stripe = new Color(color.r, color.g, color.b, 0.275f);
					var between = new Color(color.r, color.g, color.b, 0.075f);
					texture.SetPixel(x, y, diagonal % 8 < 3 ? stripe : between);
				}
			}

			texture.Apply();
			return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
		}
	}
}
