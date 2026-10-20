using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using EFT.UI.DragAndDrop;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.UI;

namespace CustomRpTweaks.Client.Patches
{
	// With more than 3 special slots the pockets panel overflows to the right.
	// Swaps the panel's horizontal layout for a grid so the slots wrap after N per row.
	internal class SpecialSlotsLayoutPatch : ModulePatch
	{
		// Size of a 1x1 inventory cell; the slot template reports no usable size of its own
		private const float CellSize = 63f;

		// Panels whose layout is being swapped; the swap finishes next frame
		private static readonly HashSet<RectTransform> Pending = new HashSet<RectTransform>();

		protected override MethodBase GetTargetMethod() => typeof(SearchableSlotView).GetMethod(nameof(SearchableSlotView.CreateSlots), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(SearchableSlotView __instance)
		{
			// Only the pockets slot has a special slots panel
			var panel = __instance._specSlotsPanel as RectTransform;
			int perRow = Plugin.SpecialSlotsPerRow.Value;
			if (panel == null || !panel.gameObject.activeSelf || panel.childCount <= perRow)
			{
				return;
			}

			var grid = panel.GetComponent<GridLayoutGroup>();
			if (grid != null)
			{
				grid.constraintCount = perRow;
				LayoutRebuilder.MarkLayoutForRebuild(panel);
				return;
			}

			var oldLayout = panel.GetComponent<HorizontalOrVerticalLayoutGroup>();
			var spacing = oldLayout != null ? oldLayout.spacing : 0f;
			var padding = oldLayout != null ? new RectOffset(oldLayout.padding.left, oldLayout.padding.right, oldLayout.padding.top, oldLayout.padding.bottom) : new RectOffset();
			if (oldLayout != null)
			{
				Object.DestroyImmediate(oldLayout);
			}

			// A panel can only have one layout group. Unity refuses to destroy components immediately in some callbacks
			// (e.g. during screen transitions): in that case remove it normally and add the grid next frame
			if (panel.GetComponent<LayoutGroup>() != null)
			{
				if (Pending.Add(panel))
				{
					Object.Destroy(panel.GetComponent<LayoutGroup>());
					Plugin.Instance.StartCoroutine(AddGridNextFrame(panel, spacing, padding));
				}

				return;
			}

			AddGrid(panel, spacing, padding);
		}

		private static IEnumerator AddGridNextFrame(RectTransform panel, float spacing, RectOffset padding)
		{
			yield return null;

			Pending.Remove(panel);
			if (panel != null && panel.GetComponent<GridLayoutGroup>() == null)
			{
				AddGrid(panel, spacing, padding);
			}
		}

		private static void AddGrid(RectTransform panel, float spacing, RectOffset padding)
		{
			var grid = panel.gameObject.AddComponent<GridLayoutGroup>();
			grid.padding = padding;
			grid.spacing = new Vector2(spacing, spacing);
			grid.cellSize = new Vector2(CellSize, CellSize);
			grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
			grid.constraintCount = Plugin.SpecialSlotsPerRow.Value;
			LayoutRebuilder.MarkLayoutForRebuild(panel);
		}
	}
}
