using System;
using System.Reflection;
using EFT.InventoryLogic;
using EFT.UI;
using SPT.Reflection.Patching;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CustomRpTweaks.Client.Patches
{
	// Adds a "kit" button above the pin button of the player's stash (hideout/menu only). It is a clone of the stash's
	// Meds filter tab, so it has the tab look, icon and hover; the clone isn't part of the tab group, so it only clicks.
	internal class LoadoutKitButtonPatch : ModulePatch
	{
		private const string ButtonName = "CustomRpTweaks Kit Button";
		private const int MedsTabIndex = 4; // order of FilterPanel's filter rules: All, Weapon, Magazine, Ammo, Meds

		protected override MethodBase GetTargetMethod() => typeof(SimpleStashPanel).GetMethod(nameof(SimpleStashPanel.Show), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(SimpleStashPanel __instance, CompoundItem item, InventoryController inventoryController, bool inRaid)
		{
			try
			{
				var pin = __instance._pinModeButton;
				if (pin == null || pin.transform.parent == null)
				{
					return;
				}

				var existing = pin.transform.parent.Find(ButtonName);
				bool show = !inRaid && Plugin.KitButton.Value && LoadoutKit.AnyGroupEnabled
					&& item is Stash && inventoryController?.Inventory?.Stash == item;

				if (existing != null)
				{
					// The panel is reused, but its controller changes after a raid or a profile reload: point the button at the current one
					existing.GetComponent<ClickHandler>().OnClick = () => LoadoutKit.Run(inventoryController);
					existing.gameObject.SetActive(show);
					return;
				}

				var tabs = __instance._filterPanel?._filterTabs;
				if (!show || tabs == null || tabs.Length <= MedsTabIndex || tabs[MedsTabIndex] == null)
				{
					return;
				}

				var clone = UnityEngine.Object.Instantiate(tabs[MedsTabIndex].gameObject, pin.transform.parent, false);
				clone.name = ButtonName;
				clone.GetComponent<Tab>().SetInteractable(true);
				var icon = clone.GetComponent<FilterTab>()._filterIcon;
				icon.color = new Color(0.45f, 0.85f, 0.45f, icon.color.a);
				Place(clone.transform, pin.transform);

				var click = clone.AddComponent<ClickHandler>();
				click.OnClick = () => LoadoutKit.Run(inventoryController);

				var hover = clone.GetComponent<HoverTrigger>() ?? clone.AddComponent<HoverTrigger>();
				hover.OnHoverStart += _ => ItemUiContext.Instance.Tooltip.Show("Bring your preset consumables and raid cash from the stash into your gear", null, 0.25f, null);
				hover.OnHoverEnd += _ => ItemUiContext.Instance.Tooltip.Close();
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Kit button failed: {e}");
			}
		}

		// Put the clone right before the pin button in a layout group, or one button height above it otherwise
		private static void Place(Transform clone, Transform pin)
		{
			var parent = pin.parent;
			if (parent.GetComponent<LayoutGroup>() != null)
			{
				clone.SetSiblingIndex(pin.GetSiblingIndex());
				return;
			}

			var cloneRect = (RectTransform)clone;
			var pinRect = (RectTransform)pin;
			cloneRect.anchoredPosition = pinRect.anchoredPosition + new Vector2(0f, pinRect.rect.height + 4f);
		}

		internal class ClickHandler : MonoBehaviour, IPointerClickHandler
		{
			internal Action OnClick;

			public void OnPointerClick(PointerEventData eventData) => OnClick?.Invoke();
		}
	}
}
