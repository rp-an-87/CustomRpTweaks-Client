using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.Utilities;
using SPT.Reflection.Patching;
using UnityEngine;

namespace CustomRpTweaks.Client
{
	internal enum EDismantleAction { All, NonVital }

	// "Dismantle" and "Empty" in the right-click menu of an item. The game's menu is built from an enum of buttons; ours are dynamic interactions
	// (so the game lists them, last), moved after the button they belong with once the game has made them: Dismantle after Disassemble on a weapon
	// (which it can replace), after Modding on other things; Empty after Open. On a weapon Dismantle's hover opens a submenu of our own (all,
	// non-vital), built the way the game builds its own submenus (the same way GetReady does for its case menu); on anything else it is a plain
	// button, as only weapons have vital parts worth telling apart.
	internal static class ItemMenu
	{
		internal const string DismantleKey = "Dismantle";
		internal const string EmptyKey = "Empty";

		private static readonly FieldInfo SubMenuButtonField = typeof(InteractionButtonsContainer).GetField("_subMenuButton", BindingFlags.Instance | BindingFlags.NonPublic);
		private static readonly FieldInfo SubMenuField = typeof(InteractionButtonsContainer).GetField("_subMenu", BindingFlags.Instance | BindingFlags.NonPublic);

		// The item behind each Dismantle button the game is about to make
		private static readonly Dictionary<string, CompoundItem> Pending = new Dictionary<string, CompoundItem>();

		// The buttons of the menu being built that others are placed by (what the game made: Open, Disassemble, the last of its own) and ours
		private static SimpleContextMenuButton _open, _modding, _disassemble, _lastBuiltIn, _dismantleButton, _emptyButton;

		internal static Sprite IconOf(string name)
		{
			try
			{
				return ResourcesCache.Pop<Sprite>("Characteristics/Icons/" + name);
			}
			catch
			{
				return null;
			}
		}

		// Called when the game makes the interactions of an item: adds our entries
		internal static void Add(ItemUiContext uiContext, ItemContext itemContext, ContextInteractions<EItemInfoButton> interactions)
		{
			if (!Plugin.ContextMenuEntries.Value || HandIn.AddDynamic == null || interactions == null)
			{
				return;
			}

			var item = itemContext?.Item as CompoundItem;
			if (item == null || item.Owner == null || item.Owner.OwnerType != EOwnerType.Profile)
			{
				return;
			}

			// The game may hand out the same interactions again: the old entry goes first
			interactions.RemoveDynamicInteraction(DismantleKey);
			interactions.RemoveDynamicInteraction(EmptyKey);

			var controller = Dismantle.ControllerOf(uiContext);
			if (controller == null)
			{
				return;
			}

			if (Dismantle.IsApplicable(item, out _))
			{
				var id = DismantleKey + ":" + item.Id;
				Pending[id] = item;
				// A weapon's entry opens a submenu (all, non-vital); anything else just dismantles all
				Action click = item is Weapon ? null : (Action)(() => Dismantle.Run(controller, item, false));
				HandIn.AddDynamic.Invoke(interactions, new object[] { id, DismantleKey, click, IconOf("Disassemble") });
			}

			// Shown for every container, greyed out when there is nothing in it
			if (Empty.IsContainer(item))
			{
				var id = EmptyKey + ":" + item.Id;
				Pending[id] = item;
				HandIn.AddDynamic.Invoke(interactions, new object[] { id, EmptyKey, (Action)(() => Empty.Run(controller, item)), IconOf("Unload") });
			}
		}

		private static bool IsOurs(DynamicContextInteraction interaction) =>
			interaction != null && interaction.Id != null && (interaction.Key == DismantleKey || interaction.Key == EmptyKey) && interaction.Id.StartsWith(interaction.Key + ":");

		// The game made a button of its own: remember the ones we place ours by
		internal static void Track(string key, SimpleContextMenuButton button)
		{
			if (button == null || key == null)
			{
				return;
			}

			if (key == nameof(EItemInfoButton.Open))
			{
				_open = button;
			}
			else if (key == nameof(EItemInfoButton.Modding))
			{
				_modding = button;
			}
			else if (key == nameof(EItemInfoButton.Disassemble))
			{
				_disassemble = button;
			}

			// The game's own buttons are named by the enum; the dynamic ones (ours, Hand in) are not
			if (Enum.IsDefined(typeof(EItemInfoButton), key))
			{
				_lastBuiltIn = button;
			}
		}

		// Our Dismantle button, instead of the game's plain dynamic one: with the arrow, and the hover opening the submenu
		internal static void CreateDismantle(InteractionButtonsContainer container, DynamicContextInteraction interaction)
		{
			if (!Pending.TryGetValue(interaction.Id, out var item))
			{
				return;
			}

			SimpleContextMenuButton button = null;
			Action open = () => Open(container, button, item);
			button = container._buttonsContainer == null ? null : container.CreateContextButton(interaction.Key, interaction.Key, container._buttonTemplate, container._buttonsContainer, interaction.Icon, open, open, true, false);
			if (button == null)
			{
				return;
			}

			button.SetButtonInteraction(SuccessfulResult.New);
			container.BindButton(button);
			_dismantleButton = button;
		}

		private static void Open(InteractionButtonsContainer container, SimpleContextMenuButton button, CompoundItem item)
		{
			SubMenuButtonField?.SetValue(container, button);
			container.CloseSubMenu();
			container.SetSubInteractions(new DismantleActions(ItemUiContext.Instance, item));
		}

		private static Transform Alive(InteractionButtonsContainer container, SimpleContextMenuButton button) =>
			button != null && button.transform.parent == container._buttonsContainer ? button.transform : null;

		// The game has made our button (the last one): put it by the button it belongs with, or after the game's last button when that one
		// isn't there (neither at the top nor at the end of the menu)
		internal static void Place(InteractionButtonsContainer container, DynamicContextInteraction interaction)
		{
			var parent = container._buttonsContainer;
			if (parent == null)
			{
				return;
			}

			Pending.TryGetValue(interaction.Id, out var item);
			var dismantle = interaction.Key == DismantleKey;
			var weapon = dismantle && item is Weapon;

			// A weapon's button is the one we made (with the submenu); the others are the game's plain dynamic button, the last one just made
			var button = weapon ? _dismantleButton : (parent.childCount == 0 ? null : parent.GetChild(parent.childCount - 1).GetComponent<SimpleContextMenuButton>());
			if (dismantle)
			{
				_dismantleButton = button;
			}
			else
			{
				_emptyButton = button;
				// Nothing in it: the button is there but greyed out
				if (button != null && item != null && !Empty.IsApplicable(item))
				{
					button.SetButtonInteraction(new FailedResult("Nothing in it", 0));
				}
			}

			var transform = Alive(container, button);
			if (transform == null)
			{
				return;
			}

			var disassemble = Alive(container, _disassemble);
			var modding = Alive(container, _modding);
			var open = Alive(container, _open);
			var last = Alive(container, _lastBuiltIn);
			int index;
			if (dismantle)
			{
				var empty = Alive(container, _emptyButton);
				if (weapon && disassemble != null)
				{
					index = disassemble.GetSiblingIndex() + 1;
					// Dismantle takes the place of Disassemble: the game's own button goes away
					if (Plugin.ReplaceDisassemble.Value)
					{
						disassemble.gameObject.SetActive(false);
					}
				}
				else if (modding != null)
				{
					index = modding.GetSiblingIndex() + 1;
				}
				else if (open != null)
				{
					index = open.GetSiblingIndex() + 1;
					// Open, Empty, Dismantle
					if (empty != null && empty.GetSiblingIndex() == index)
					{
						index++;
					}
				}
				else if (last != null)
				{
					index = last.GetSiblingIndex() + 1;
				}
				else
				{
					return;
				}
			}
			else if (open != null)
			{
				index = open.GetSiblingIndex() + 1;
			}
			else if (last != null)
			{
				index = last.GetSiblingIndex() + 1;
			}
			else
			{
				return;
			}

			transform.SetSiblingIndex(index);
		}

		// The game puts a submenu to the right of its button, then asks whether placing it moved it at all (it rounds the position to whole
		// pixels, so a right edge that is not a whole pixel counts as "moved"); if so it puts the menu on the left. With our menus, whose width
		// follows their text, the edge is rarely whole, so they flipped left with plenty of room. After the game has placed one, put it back on
		// the right when it fits there.
		internal static void KeepRight(InteractionButtonsContainer container)
		{
			var sub = SubMenuField?.GetValue(container) as SimpleContextMenu;
			var button = SubMenuButtonField?.GetValue(container) as SimpleContextMenuButton;
			if (sub == null || button == null)
			{
				return;
			}

			var buttonRect = (RectTransform)button.transform;
			var size = buttonRect.rect.size;
			float rightEdge = buttonRect.TransformPoint(size - size * buttonRect.pivot).x;
			var rect = (RectTransform)sub.transform;
			float width = rect.rect.width * rect.lossyScale.x;
			float left = rect.position.x - rect.pivot.x * width;
			if (Mathf.Abs(left - rightEdge) < 2f * rect.lossyScale.x)
			{
				return;
			}

			var canvas = sub.GetComponentInParent<Canvas>();
			var root = canvas != null ? canvas.rootCanvas.transform as RectTransform : null;
			if (root == null)
			{
				return;
			}

			var corners = new Vector3[4];
			root.GetWorldCorners(corners);
			if (rightEdge + width > corners[2].x)
			{
				return;
			}

			var position = rect.position;
			rect.position = new Vector3(Mathf.Round(rightEdge + rect.pivot.x * width), position.y, position.z);
		}

		internal static bool IsOurEntry(DynamicContextInteraction interaction) => IsOurs(interaction);

		internal static bool IsWeaponEntry(DynamicContextInteraction interaction) => Pending.TryGetValue(interaction.Id, out var item) && item is Weapon;
	}

	// The submenu of Dismantle: everything comes off, or everything but the vital parts
	internal class DismantleActions : ContextInteractions<EDismantleAction>
	{
		private readonly CompoundItem _item;
		private readonly bool _hasVital;

		internal DismantleActions(ItemUiContext uiContext, CompoundItem item) : base(uiContext)
		{
			_item = item;
			Dismantle.IsApplicable(item, out _hasVital);
		}

		public override Dictionary<EDismantleAction, string> CustomNames => new Dictionary<EDismantleAction, string>
		{
			{ EDismantleAction.All, "Dismantle all" },
			{ EDismantleAction.NonVital, "Dismantle non-vital" }
		};

		public override bool TryGetCustomIcon(EDismantleAction key, out Sprite icon)
		{
			icon = ItemMenu.IconOf(key == EDismantleAction.All ? "Disassemble" : "Uninstall");
			return icon != null;
		}

		// Without vital parts the two do the same, so only one entry is listed
		public override bool IsActive(EDismantleAction button) => button == EDismantleAction.All || _hasVital;

		public override IResult IsInteractive(EDismantleAction button) => SuccessfulResult.New;

		public override void ExecuteInteractionInternal(EDismantleAction interaction)
		{
			try
			{
				var controller = Dismantle.ControllerOf(UIContext);
				if (controller != null)
				{
					Dismantle.Run(controller, _item, interaction == EDismantleAction.NonVital);
				}
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Dismantle menu failed: {e}");
			}
		}
	}

	internal class ItemMenuAddPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(ItemUiContext).GetMethod(nameof(ItemUiContext.GetItemContextInteractions), BindingFlags.Instance | BindingFlags.Public);

		[PatchPostfix]
		private static void Postfix(ItemUiContext __instance, ItemContext itemContext, ContextInteractions<EItemInfoButton> __result)
		{
			// Never let a problem with our entries break the menu
			try
			{
				ItemMenu.Add(__instance, itemContext, __result);
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Item menu entries: {e}");
			}
		}
	}

	// Our Dismantle button is made here instead of the game's plain one; both ours are placed once they exist
	internal class ItemMenuButtonPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(InteractionButtonsContainer).GetMethod(nameof(InteractionButtonsContainer.CreateDynamicContextButton), BindingFlags.Instance | BindingFlags.Public);

		[PatchPrefix]
		private static bool Prefix(InteractionButtonsContainer __instance, DynamicContextInteraction interaction)
		{
			// Only a weapon's Dismantle has a submenu; the rest are the game's plain buttons
			if (!ItemMenu.IsOurEntry(interaction) || interaction.Key != ItemMenu.DismantleKey || !ItemMenu.IsWeaponEntry(interaction))
			{
				return true;
			}

			try
			{
				ItemMenu.CreateDismantle(__instance, interaction);
				return false;
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Dismantle menu button failed: {e}");
				return true;
			}
		}

		[PatchPostfix]
		private static void Postfix(InteractionButtonsContainer __instance, DynamicContextInteraction interaction)
		{
			try
			{
				if (ItemMenu.IsOurEntry(interaction))
				{
					ItemMenu.Place(__instance, interaction);
				}
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Item menu placement failed: {e}");
			}
		}
	}

	// Every button the menus make passes here: the ones our own are placed by are noted
	internal class ItemMenuTrackPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(InteractionButtonsContainer).GetMethod(nameof(InteractionButtonsContainer.CreateContextButton), BindingFlags.Instance | BindingFlags.Public, null,
			new[] { typeof(string), typeof(string), typeof(SimpleContextMenuButton), typeof(RectTransform), typeof(Sprite), typeof(Action), typeof(Action), typeof(bool), typeof(bool) }, null);

		[PatchPostfix]
		private static void Postfix(string key, SimpleContextMenuButton __result)
		{
			try
			{
				ItemMenu.Track(key, __result);
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Item menu tracking failed: {e}");
			}
		}
	}

	// After the game has opened our submenu: keep it on the right when there is room (see ItemMenu.KeepRight)
	internal class ItemMenuSidePatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(InteractionButtonsContainer).GetMethod(nameof(InteractionButtonsContainer.SetSubInteractions), BindingFlags.Instance | BindingFlags.Public).MakeGenericMethod(typeof(EDismantleAction));

		[PatchPostfix]
		private static void Postfix(InteractionButtonsContainer __instance)
		{
			try
			{
				ItemMenu.KeepRight(__instance);
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Item menu side failed: {e}");
			}
		}
	}
}
