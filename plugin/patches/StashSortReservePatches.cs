using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using SPT.Reflection.Patching;

namespace CustomRpTweaks.Client.Patches
{
	// Sorting the stash normally packs items from the top-left. Here, while sorting the main stash, items are first
	// placed below the reserved top rows, and only spill into them when nothing else fits.
	internal static class StashSortReservePatches
	{
		[ThreadStatic]
		private static bool _sortingStash;

		// First pass of a placement search: only rows from _minRow downward are considered
		[ThreadStatic]
		private static bool _firstPass;

		[ThreadStatic]
		private static int _minRow;

		internal static int ReservedRows => Plugin.KeepTopRowsFree.Value ? Plugin.TopRowsToKeepFree.Value : 0;

		internal class SortPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(ItemManipulator).GetMethod(nameof(ItemManipulator.Sort), BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly,
				null, new[] { typeof(CompoundItem), typeof(InventoryController), typeof(bool) }, null);

			[PatchPrefix]
			private static void Prefix(CompoundItem sortedItem) => _sortingStash = sortedItem is Stash && ReservedRows > 0;

			[PatchFinalizer]
			private static void Finalizer() => _sortingStash = false;
		}

		internal class FindFreeSpacePatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(Grid).GetMethod(nameof(Grid.FindFreeSpace), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPrefix]
			private static bool Prefix(Grid __instance, Item item, ref LocationInGrid __result)
			{
				if (!_sortingStash || _firstPass || !(__instance.ParentItem is Stash))
				{
					return true;
				}

				_firstPass = true;
				_minRow = ReservedRows;
				try
				{
					// Re-enters this patch, which then lets the original run, restricted to the rows below the reserved ones
					var location = __instance.FindFreeSpace(item);

					// A location at the bottom edge is the stretch fallback, not a real free spot
					if (location != null && location.y < __instance.GridHeight)
					{
						__result = location;
						return false;
					}
				}
				finally
				{
					_firstPass = false;
					_minRow = 0;
				}

				// Nothing fits below the reserved rows: unrestricted search
				return true;
			}
		}

		// Same scan as Grid.GetFreeLocation, but starting at row _minRow
		internal class GetFreeLocationPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(Grid).GetMethod(nameof(Grid.GetFreeLocation), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPrefix]
			private static bool Prefix(int itemMainSize, int itemSecondSize, ItemRotation rotation, int firstDimensionSize, int secondDimensionSize,
				List<int> firstDimensionSpaces, List<int> secondDimensionSpaces, bool invertDimensions, ref LocationInGrid __result)
			{
				if (!_firstPass || _minRow <= 0 || invertDimensions)
				{
					return true;
				}

				for (int row = _minRow; row < firstDimensionSize; row++)
				{
					for (int col = 0; col + itemSecondSize <= secondDimensionSize; col++)
					{
						int free = secondDimensionSpaces[row * secondDimensionSize + col];
						if (free < itemSecondSize && free != -1)
						{
							continue;
						}

						bool fits = true;
						for (int c = col; fits && c < col + itemSecondSize; c++)
						{
							int space = firstDimensionSpaces[row * secondDimensionSize + c];
							fits = space >= itemMainSize || space == -1;
						}

						if (fits)
						{
							__result = new LocationInGrid(col, row, rotation);
							return false;
						}
					}
				}

				__result = null;
				return false;
			}
		}
	}
}
