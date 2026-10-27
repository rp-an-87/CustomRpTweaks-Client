using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using EFT.InventoryLogic;
using EFT.UI;
using UnityEngine;

namespace CustomRpTweaks.Client
{
	// A small block under the tooltip of an item in your inventory that lists the hotkeys of this mod that apply to it right now
	// (top up, repair, equip plate, dismantle), with the keys as configured. It is appended after everything else, below a thin
	// separator, so it stays apart from the lines other mods (LootValuePlus) add. Hints share a line only while it stays short, so the
	// block stays narrow.
	internal static class HotkeyHints
	{
		// The item the pointer is on, set by the hover patches
		internal static Item Hovered;

		// Opens the block and is how an existing one is recognised, so a tooltip shown again with it isn't given a second one
		internal const string Open = "<size=10><color=#8A8A8A>";
		internal const string Close = "</color></size>";
		private const string Gap = "   ";

		// The most visible characters on a line of hints: short hints pair up, long ones get a line to themselves
		private const int LineBudget = 40;

		private static readonly Regex Tags = new Regex("<[^>]*>", RegexOptions.Compiled);

		private static int VisibleLength(string text) => Tags.Replace(text, "").Length;

		// About 170 pixels: as wide as the narrowest tooltips already are, so it doesn't widen any
		internal const int SeparatorLength = 48;

		private static readonly FieldInfo ControllerField = typeof(ItemUiContext).GetField("_inventoryController", BindingFlags.Instance | BindingFlags.NonPublic);

		// Tooltips are shown again and again while one item is hovered (LootValuePlus redraws its own): look the hotkeys up once per moment
		private static Item _cachedItem;
		private static float _cachedAt;
		private static List<string> _cachedHints;

		// The text to append to the tooltip text of the hovered item, or null
		internal static string Block(Item item)
		{
			var hints = For(item);
			if (hints == null || hints.Count == 0)
			{
				return null;
			}

			var lines = new List<string>();
			var line = "";
			foreach (var hint in hints)
			{
				if (line.Length == 0)
				{
					line = hint;
				}
				else if (VisibleLength(line) + Gap.Length + VisibleLength(hint) <= LineBudget)
				{
					line += Gap + hint;
				}
				else
				{
					lines.Add(line);
					line = hint;
				}
			}

			lines.Add(line);

			return "\n" + TooltipSeparators.Line(SeparatorLength) + "\n" + Open + string.Join("\n", lines) + Close;
		}

		private static List<string> For(Item item)
		{
			if (item == null || Plugin.HotkeyHints == null || !Plugin.HotkeyHints.Value)
			{
				return null;
			}

			if (item == _cachedItem && Time.unscaledTime - _cachedAt < 0.5f)
			{
				return _cachedHints;
			}

			_cachedItem = item;
			_cachedAt = Time.unscaledTime;
			_cachedHints = Build(item);
			return _cachedHints;
		}

		private static List<string> Build(Item item)
		{
			// Items of traders, the flea market and the mail are not yours to top up or take apart
			if (item.Owner == null || item.Owner.OwnerType != EOwnerType.Profile)
			{
				return null;
			}

			var uiContext = ItemUiContext.Instance;
			var controller = uiContext == null ? null : ControllerField.GetValue(uiContext) as InventoryController;
			var hints = new List<string>();

			void Add(ConfigEntry<KeyboardShortcut> key, string action)
			{
				var label = KeyLabel(key.Value);
				if (label != null)
				{
					hints.Add($"<color=#B8B8B8>[{label}]</color> {action}");
				}
			}

			// Top up and repair: GetReady's when it is loaded, else the copies in this plugin
			if (GetReadyHints.Active)
			{
				hints.AddRange(GetReadyHints.For(item, KeyLabel));
			}
			else
			{
				if (MagazineTopUp.IsApplicable(item))
				{
					Add(Plugin.TopUpMagazine, "top up");
				}

				if (uiContext != null && RepairWithKit.IsApplicable(uiContext, controller, item))
				{
					Add(Plugin.RepairWithKitHotkey, "repair");
				}
			}

			if (EquipPlate.IsApplicable(controller, item))
			{
				Add(Plugin.EquipPlateHotkey, "equip");
			}

			if (SplitStack.IsApplicable(item))
			{
				Add(Plugin.SplitHotkey, "split");
			}

			// The item shortcuts of UI Fixes, when that mod is loaded
			if (uiContext != null)
			{
				hints.AddRange(UiFixesHints.For(uiContext, item, KeyLabel));
			}

			// Last: it is the heaviest action
			if (Dismantle.IsApplicable(item, out var hasVitalParts))
			{
				// With vital parts the two hotkeys differ, so say how; without them they do the same
				Add(Plugin.DismantleHotkey, hasVitalParts ? "dismantle all" : "dismantle");
				if (hasVitalParts)
				{
					Add(Plugin.DismantleNonVitalHotkey, "dismantle non-vital");
				}
			}

			return hints;
		}

		// "Ctrl+Alt+D", or null when the hotkey is unassigned
		internal static string KeyLabel(KeyboardShortcut shortcut)
		{
			if (shortcut.MainKey == KeyCode.None)
			{
				return null;
			}

			var names = shortcut.Modifiers.Select(ModifierName).Distinct().ToList();
			names.Add(shortcut.MainKey.ToString().Replace("Alpha", ""));
			return string.Join("+", names);
		}

		private static string ModifierName(KeyCode key)
		{
			switch (key)
			{
				case KeyCode.LeftControl:
				case KeyCode.RightControl:
					return "Ctrl";
				case KeyCode.LeftAlt:
				case KeyCode.RightAlt:
					return "Alt";
				case KeyCode.LeftShift:
				case KeyCode.RightShift:
					return "Shift";
				default:
					return key.ToString();
			}
		}
	}
}
