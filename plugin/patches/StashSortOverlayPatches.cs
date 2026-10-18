using System.Linq;
using System.Reflection;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CustomRpTweaks.Client.Patches
{
	// Hovering the stash sort button shades the top rows that sorting will keep free.
	internal class StashSortOverlayPatch : ModulePatch
	{
		private const int CellSize = 63;
		private static readonly FieldInfo ItemField = typeof(GridSortPanel).GetField("_item", BindingFlags.Instance | BindingFlags.NonPublic);

		private static GameObject _overlay;
		private static Sprite _stripes;

		protected override MethodBase GetTargetMethod() => typeof(GridSortPanel).GetMethod(nameof(GridSortPanel.Awake), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(GridSortPanel __instance)
		{
			var trigger = __instance._button.gameObject.AddComponent<EventTrigger>();
			AddEntry(trigger, EventTriggerType.PointerEnter, () => Show((CompoundItem)ItemField.GetValue(__instance)));
			AddEntry(trigger, EventTriggerType.PointerExit, Hide);
			AddEntry(trigger, EventTriggerType.PointerClick, Hide);
		}

		private static void AddEntry(EventTrigger trigger, EventTriggerType type, UnityEngine.Events.UnityAction action)
		{
			var entry = new EventTrigger.Entry { eventID = type };
			entry.callback.AddListener(_ => action());
			trigger.triggers.Add(entry);
		}

		private static void Show(CompoundItem item)
		{
			Hide();
			int rows = StashSortReservePatches.ReservedRows;
			if (!(item is Stash) || rows <= 0)
			{
				return;
			}

			var gridView = Object.FindObjectsOfType<GridView>().FirstOrDefault(view => view.Grid != null && view.Grid.ParentItem == item);
			if (gridView == null)
			{
				return;
			}

			rows = Mathf.Min(rows, gridView.Grid.GridHeight);
			_overlay = new GameObject("Reserved rows overlay", typeof(RectTransform), typeof(Image));
			var rect = (RectTransform)_overlay.transform;
			rect.SetParent(gridView.transform, false);
			rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
			rect.anchoredPosition = Vector2.zero;
			rect.sizeDelta = new Vector2(gridView.Grid.GridWidth * CellSize, rows * CellSize);

			var image = _overlay.GetComponent<Image>();
			image.sprite = Stripes();
			image.type = Image.Type.Tiled;
			image.raycastTarget = false;
		}

		private static void Hide()
		{
			if (_overlay != null)
			{
				Object.Destroy(_overlay);
			}

			_overlay = null;
		}

		// Translucent red diagonal stripes, built once
		private static Sprite Stripes()
		{
			if (_stripes != null)
			{
				return _stripes;
			}

			const int size = 16;
			var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Point };
			for (int y = 0; y < size; y++)
			{
				for (int x = 0; x < size; x++)
				{
					texture.SetPixel(x, y, (x + y) % 8 < 3 ? new Color(0.9f, 0.15f, 0.15f, 0.275f) : new Color(0.9f, 0.15f, 0.15f, 0.075f));
				}
			}

			texture.Apply();
			_stripes = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
			return _stripes;
		}
	}
}
