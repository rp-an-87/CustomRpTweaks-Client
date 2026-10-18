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

			var grid = panel.GetComponent<GridLayoutGroup>() ?? ReplaceLayoutWithGrid(panel);
			grid.constraintCount = perRow;
			LayoutRebuilder.MarkLayoutForRebuild(panel);
		}

		private static GridLayoutGroup ReplaceLayoutWithGrid(RectTransform panel)
		{
			var oldLayout = panel.GetComponent<HorizontalOrVerticalLayoutGroup>();
			var spacing = oldLayout != null ? oldLayout.spacing : 0f;
			var padding = oldLayout != null ? oldLayout.padding : new RectOffset();
			if (oldLayout != null)
			{
				Object.DestroyImmediate(oldLayout);
			}

			var grid = panel.gameObject.AddComponent<GridLayoutGroup>();
			grid.padding = padding;
			grid.spacing = new Vector2(spacing, spacing);
			grid.cellSize = new Vector2(CellSize, CellSize);
			grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
			return grid;
		}
	}
}
