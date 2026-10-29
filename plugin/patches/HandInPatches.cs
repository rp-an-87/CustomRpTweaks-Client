using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using EFT.InventoryLogic;
using EFT.UI;
using SPT.Reflection.Patching;

namespace CustomRpTweaks.Client.Patches
{
	internal static class HandInPatches
	{
		private static readonly FieldInfo ButtonsContainerField = typeof(InteractionButtonsContainer).GetField("_buttonsContainer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

		// The game builds extra menu buttons from the plain template and puts them last. Ours goes to the top, in quest yellow (the way
		// Discard is red): its texts and its icon are tinted.
		internal class ButtonLookPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(InteractionButtonsContainer).GetMethod(nameof(InteractionButtonsContainer.CreateDynamicContextButton), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPostfix]
			private static void Postfix(InteractionButtonsContainer __instance, DynamicContextInteraction interaction)
			{
				try
				{
					if (interaction.Id != HandIn.MenuId || ButtonsContainerField == null)
					{
						return;
					}

					var container = ButtonsContainerField.GetValue(__instance) as RectTransform;
					if (container == null || container.childCount == 0)
					{
						return;
					}

					// The button just made is the last one
					var button = container.GetChild(container.childCount - 1);
					button.SetAsFirstSibling();
					foreach (var text in button.GetComponentsInChildren<TMP_Text>(true))
					{
						text.color = HandIn.Yellow;
					}

					foreach (var image in button.GetComponentsInChildren<Image>(true).Where(image => interaction.Icon != null && image.sprite == interaction.Icon))
					{
						image.color = HandIn.Yellow;
					}
				}
				catch (System.Exception e)
				{
					Plugin.Log.LogError($"Hand in button look: {e}");
				}
			}
		}

		// The game builds the context interactions of an item here, for the menu and for its hotkeys; our button is added to them
		internal class ContextMenuPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(ItemUiContext).GetMethod(nameof(ItemUiContext.GetItemContextInteractions), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPostfix]
			private static void Postfix(ItemUiContext __instance, ItemContext itemContext, ContextInteractions<EItemInfoButton> __result)
			{
				// Never let a problem with the button break the menu
				try
				{
					HandIn.AddButton(__instance, __result, itemContext);
				}
				catch (System.Exception e)
				{
					Plugin.Log.LogError($"Hand in button: {e}");
				}
			}
		}
	}
}
