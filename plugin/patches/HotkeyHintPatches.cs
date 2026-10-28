using System;
using System.Reflection;
using EFT.UI;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CustomRpTweaks.Client.Patches
{
	// Tells the tooltip which item is hovered, and appends the hotkey hints (see HotkeyHints) to its text.
	internal static class HotkeyHintPatches
	{
		// A prefix: the item view shows its tooltip from inside OnPointerEnter, so the hovered item has to be known before that
		internal class HoverEnterPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(GridItemView).GetMethod(nameof(GridItemView.OnPointerEnter), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPrefix]
			private static void Prefix(GridItemView __instance) => HotkeyHints.Hovered = __instance.Item;
		}

		internal class HoverExitPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(GridItemView).GetMethod(nameof(GridItemView.OnPointerExit), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPrefix]
			private static void Prefix() => HotkeyHints.Hovered = null;
		}

		// The item view also shows tooltips for the small icons on it (insured, found in raid...) while the pointer is still on the item,
		// and those must not get the hints. Only the tooltip the view shows for the item itself, from ShowTooltip, does.
		internal class ItemTooltipPatch : ModulePatch
		{
			internal static bool Showing;

			protected override MethodBase GetTargetMethod() => typeof(GridItemView).GetMethod(nameof(GridItemView.ShowTooltip), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPrefix]
			private static void Prefix() => Showing = true;

			[PatchPostfix]
			private static void Postfix() => Showing = false;
		}

		internal class TooltipShowPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(SimpleTooltip).GetMethod(nameof(SimpleTooltip.Show), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			// Late, so what other mods put in the text is already there; either order works, as the hint is only ever added once
			[PatchPrefix]
			[HarmonyPriority(Priority.Low)]
			private static void Prefix(ref string text)
			{
				// Never let a problem with the hints stop a tooltip from showing
				try
				{
					// LootValuePlus redraws the item tooltip on Alt, outside ShowTooltip
					bool redraw = Input.GetKeyDown(KeyCode.LeftAlt) || Input.GetKeyUp(KeyCode.LeftAlt);
					if (text == null || HotkeyHints.Hovered == null || text.Contains(HotkeyHints.Open) || !(ItemTooltipPatch.Showing || redraw))
					{
						return;
					}

					var block = HotkeyHints.Block(HotkeyHints.Hovered);
					if (block != null)
					{
						text += block;
					}
				}
				catch (Exception e)
				{
					if (!_reported)
					{
						_reported = true;
						Plugin.Log.LogError($"Hotkey hints failed, the tooltip is shown without them: {e}");
					}
				}
			}

			private static bool _reported;
		}
	}
}
