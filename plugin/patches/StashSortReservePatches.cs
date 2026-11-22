using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using SPT.Reflection.Patching;

namespace CustomRpTweaks.Client.Patches
{
	// Sorting the stash normally packs items from the top-left. Two rules for the top rows of the main stash (set in the window that opens
	// on a right-click of the sort button):
	//  - ignored rows: items there stay where they are and nothing sorted is ever placed there
	//  - reserved rows: sorted items are placed below them, and only spill into them when nothing else fits
	internal static class StashSortReservePatches
	{
		[ThreadStatic]
		private static bool _sortingStash;

		// First pass of a placement search: only rows from _minRow downward are considered
		[ThreadStatic]
		private static bool _firstPass;

		[ThreadStatic]
		private static int _minRow;

		// 0 = off for both
		internal static int ReservedRows => Plugin.StashSortRows.Value && Plugin.KeepTopRowsFree.Value ? Plugin.TopRowsToKeepFree.Value : 0;

		internal static int IgnoredRows => Plugin.StashSortRows.Value ? Plugin.IgnoreTopRows.Value : 0;

		// Whether an item whose top row is y and that is height rows tall stays out of the sort. One that starts in an ignored row and
		// reaches below them is sorted (and so moved) unless the owner chose to keep it.
		internal static bool StaysPut(int y, int height, int ignoredRows, bool sortStraddling)
		{
			if (ignoredRows <= 0 || y >= ignoredRows)
			{
				return false;
			}

			return !(sortStraddling && y + height > ignoredRows);
		}

		internal class SortPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(ItemManipulator).GetMethod(nameof(ItemManipulator.Sort), BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly,
				null, new[] { typeof(CompoundItem), typeof(InventoryController), typeof(bool) }, null);

			[PatchPrefix]
			private static void Prefix(CompoundItem sortedItem) => _sortingStash = sortedItem is Stash && (ReservedRows > 0 || IgnoredRows > 0);

			[PatchFinalizer]
			private static void Finalizer() => _sortingStash = false;
		}

		// Sort collects the items to move with a filter that keeps the ones that are not pinned (pinned items stay where they are). The
		// items in the ignored rows are dropped from it the same way.
		internal class SortFilterPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod()
			{
				foreach (var type in Nested(typeof(ItemManipulator)))
				{
					var filter = type.GetMethod("method_10", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, new[] { typeof(Item) }, null);
					var grids = type.GetMethod("method_9", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, new[] { typeof(Grid) }, null);
					if (filter != null && grids != null && filter.ReturnType == typeof(bool))
					{
						return filter;
					}
				}

				throw new InvalidOperationException("Sort's pin filter was not found");
			}

			private static IEnumerable<Type> Nested(Type type)
			{
				foreach (var inner in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
				{
					yield return inner;
					foreach (var deeper in Nested(inner))
					{
						yield return deeper;
					}
				}
			}

			[PatchPostfix]
			private static void Postfix(Item gridItem, ref bool __result)
			{
				if (!__result || !_sortingStash || IgnoredRows <= 0)
				{
					return;
				}

				var address = gridItem.CurrentAddress as GridItemAddress;
				var location = address?.LocationInGrid;
				if ((object)location == null)
				{
					return;
				}

				int height = gridItem.CalculateRotatedSize(location.r).Y;
				if (StaysPut(location.y, height, IgnoredRows, Plugin.SortItemsReachingOut.Value))
				{
					__result = false;
				}
			}
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

				int ignored = IgnoredRows;
				int reserved = Math.Max(ReservedRows, ignored);
				if (reserved <= 0)
				{
					return true;
				}

				var location = Search(__instance, item, reserved);

				// A location at the bottom edge is the stretch fallback, not a real free spot
				if (location != null && location.y < __instance.GridHeight)
				{
					__result = location;
					return false;
				}

				if (ignored > 0)
				{
					// Nothing fits below the reserved rows: the reserved rows may be used, the ignored ones never
					if (reserved > ignored)
					{
						location = Search(__instance, item, ignored);
					}

					__result = location;
					return false;
				}

				// Nothing fits below the reserved rows: unrestricted search
				return true;
			}

			// Re-enters this patch, which then lets the original run, restricted to the rows from minRow downward
			private static LocationInGrid Search(Grid grid, Item item, int minRow)
			{
				_firstPass = true;
				_minRow = minRow;
				try
				{
					return grid.FindFreeSpace(item);
				}
				finally
				{
					_firstPass = false;
					_minRow = 0;
				}
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
