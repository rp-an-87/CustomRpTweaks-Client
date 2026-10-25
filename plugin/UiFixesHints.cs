using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;

namespace CustomRpTweaks.Client
{
	// Hotkey hints for the item shortcuts of UI Fixes (com.tyfon.uifixes), only when that plugin is loaded. The keys are read from
	// its own settings every time, so rebinding one there changes the hint. Whether a shortcut applies to the hovered item is decided
	// per shortcut below, from the game's own item menu rules plus a check of the item type where the menu alone isn't enough.
	// The hints are added to the block of HotkeyHints, after our own.
	internal static class UiFixesHints
	{
		internal const string TargetGuid = "com.tyfon.uifixes";

		private class Rule
		{
			public readonly string Setting;       // the name of the shortcut setting in UI Fixes
			public readonly string Name;          // how the setting is named in our menu
			public readonly bool OnByDefault;
			// The text after the key, or null when the shortcut doesn't apply to the item; can: whether the game's item menu offers an action
			public readonly Func<Item, Func<EItemInfoButton, bool>, string> Applies;
			public ConfigEntry<bool> Show;
			public ConfigEntry<KeyboardShortcut> Key;

			public Rule(string setting, string name, bool onByDefault, Func<Item, Func<EItemInfoButton, bool>, string> applies)
			{
				Setting = setting;
				Name = name;
				OnByDefault = onByDefault;
				Applies = applies;
			}
		}

		private static string If(bool condition, string text) => condition ? text : null;

		// In the order they are listed in a tooltip. One rule per item type logic: what the shortcut works on.
		private static readonly Rule[] Rules =
		{
			// weapons, armor, rigs with plates and the like: what has slots to look into
			new Rule("Inspect Shortcut", "Inspect items with slots", true, (item, can) =>
				If(item is CompoundItem compound && compound.Slots.Length > 0 && can(EItemInfoButton.Inspect), "inspect")),
			new Rule("Inspect Shortcut", "Inspect", false, (item, can) => If(can(EItemInfoButton.Inspect), "inspect")),
			new Rule("Open Shortcut", "Open", true, (item, can) => If(item is CompoundItem && can(EItemInfoButton.Open), "open")),
			new Rule("Examine/Interact Shortcut", "Examine / interact", false, (item, can) =>
				If(new[] { EItemInfoButton.Examine, EItemInfoButton.Fold, EItemInfoButton.Unfold, EItemInfoButton.TurnOn, EItemInfoButton.TurnOff, EItemInfoButton.CheckMagazine }.Any(can), "examine")),
			// an ammo stack that isn't full
			new Rule("Top Up Ammo Shortcut", "Top up ammo", true, (item, can) =>
				If(item is Ammo ammo && ammo.StackObjectsCount < ammo.StackMaxSize && can(EItemInfoButton.TopUp), "top up ammo")),
			// consumables: meds, food, drinks
			new Rule("Use Item Shortcut", "Use", true, (item, can) => If(can(EItemInfoButton.Use), "use")),
			new Rule("Use Item (All) Shortcut", "Use all", true, (item, can) => If(can(EItemInfoButton.UseAll), "use all")),
			// a weapon, in the menus only (the in-raid reload has its own key)
			new Rule("Reload Weapon Shortcut", "Reload weapon", true, (item, can) =>
				If(item is Weapon && !InGameStatus.InRaid && can(EItemInfoButton.Reload), "reload")),
			// the ammo of a magazine, or the magazine of a gun
			new Rule("Unload Mag/Ammo Shortcut", "Unload", true, (item, can) =>
				If((item is Magazine || item is Weapon) && (can(EItemInfoButton.Unload) || can(EItemInfoButton.UnloadAmmo)), "unload")),
			// weapon parts: install on the weapon you hold, take off one that is attached
			new Rule("Install Mod Shortcut", "Install mod", true, (item, can) => If(item is Mod && can(EItemInfoButton.Install), "install")),
			new Rule("Uninstall Mod Shortcut", "Uninstall mod", true, (item, can) => If(item is Mod && can(EItemInfoButton.Uninstall), "uninstall")),
			// sealed cases and the like
			new Rule("Unpack Shortcut", "Unpack", true, (item, can) => If(can(EItemInfoButton.Unpack), "unpack")),
			// flea market
			new Rule("Filter by Item Shortcut", "Search the flea for it", false, (item, can) => If(can(EItemInfoButton.FilterSearch), "find on flea")),
			new Rule("Linked Search Shortcut", "Search the flea for linked items", false, (item, can) => If(can(EItemInfoButton.LinkedSearch), "linked on flea")),
			new Rule("Required Search Shortcut", "Search the flea for barters", false, (item, can) => If(can(EItemInfoButton.NeededSearch), "barters on flea")),
			new Rule("Add Offer Shortcut", "List on the flea", false, (item, can) => If(can(EItemInfoButton.AddOffer), "list on flea")),
			// any item you own, the text follows its state; off by default as every item in the stash has them
			new Rule("Pin Item Shortcut", "Pin", false, (item, can) => can(EItemInfoButton.SetUnPin) ? "unpin" : If(can(EItemInfoButton.SetPin), "pin")),
			new Rule("Lock Item Shortcut", "Lock", false, (item, can) => can(EItemInfoButton.SetUnLock) ? "unlock" : If(can(EItemInfoButton.SetLock), "lock")),
		};

		private static ConfigEntry<bool> _enabled;
		private static ConfigEntry<int> _maxHints;
		private static bool _found;

		// Switched on, and UI Fixes was found with at least one of its shortcut settings
		private static bool Active => _found && _enabled != null && _enabled.Value;

		// Called once from the plugin's Awake, only when UI Fixes is loaded: finds its settings and adds ours to the menu
		internal static void Setup(ConfigFile config)
		{
			try
			{
				if (!Chainloader.PluginInfos.TryGetValue(TargetGuid, out var info) || info.Instance == null)
				{
					return;
				}

				// Its settings by name, wherever they are sectioned (the section letters change with its versions)
				var theirs = info.Instance.Config;
				var shortcuts = theirs.Keys
					.Select(definition => theirs[definition])
					.Where(entry => entry.SettingType == typeof(KeyboardShortcut))
					.GroupBy(entry => entry.Definition.Key)
					.ToDictionary(group => group.Key, group => (ConfigEntry<KeyboardShortcut>)group.First());

				var number = 3;
				foreach (var rule in Rules)
				{
					if (!shortcuts.TryGetValue(rule.Setting, out rule.Key))
					{
						continue;
					}

					var scope = rule.Name == "Inspect items with slots"
						? "only on items that have slots to look into: weapons, armor, rigs with plates, parts with parts of their own"
						: "on every item it works on";
					rule.Show = config.Bind("14. UI Fixes hints", $"{number++}. {rule.Name}", rule.OnByDefault,
						$"Show the hint for UI Fixes' \"{rule.Setting}\" {scope}.");
				}

				_found = Rules.Any(rule => rule.Key != null);
				_enabled = config.Bind("14. UI Fixes hints", "1. Show hints for UI Fixes shortcuts", true,
					"Needs the UI Fixes mod. Adds the item shortcuts of UI Fixes that work on the hovered item to the hotkey hints at the end of the tooltip, with the keys set in UI Fixes. Each shortcut can be switched off below; a shortcut you cleared in UI Fixes isn't shown.");
				_maxHints = config.Bind("14. UI Fixes hints", "2. Most UI Fixes hints per tooltip", 6,
					new ConfigDescription("Keeps the tooltip short when many shortcuts apply to an item. Shortcuts that share a key count as one.", new AcceptableValueRange<int>(1, 20)));
				Plugin.Log.LogInfo(_found ? $"UI Fixes found: hints for {Rules.Count(rule => rule.Key != null)} of its shortcuts." : "UI Fixes found, but none of the shortcut settings the hints need.");
			}
			catch (Exception e)
			{
				_found = false;
				Plugin.Log.LogWarning($"Couldn't read the UI Fixes settings, no hints for its shortcuts: {e.Message}");
			}
		}

		// The hints for the hovered item, in the form of the others: "[E] inspect / use"
		internal static List<string> For(ItemUiContext uiContext, Item item, Func<KeyboardShortcut, string> keyLabel)
		{
			var hints = new List<string>();
			var itemContext = uiContext.CurrentItemContext;
			if (!Active || itemContext == null || itemContext.Item != item)
			{
				return hints;
			}

			var interactions = uiContext.GetItemContextInteractions(itemContext, null);
			bool Can(EItemInfoButton button) => interactions.IsInteractionAvailable(button).Succeed;

			// Several shortcuts on one key (E inspects and uses) become one hint
			var byKey = new List<KeyValuePair<string, List<string>>>();
			foreach (var rule in Rules.Where(rule => rule.Key != null && rule.Show != null && rule.Show.Value))
			{
				var key = keyLabel(rule.Key.Value);
				var action = key == null ? null : rule.Applies(item, Can);
				if (action == null)
				{
					continue;
				}

				var existing = byKey.FirstOrDefault(pair => pair.Key == key);
				if (existing.Value != null)
				{
					// two rules can give the same text (inspect for any item, inspect for items with slots)
					if (!existing.Value.Contains(action))
					{
						existing.Value.Add(action);
					}
				}
				else
				{
					byKey.Add(new KeyValuePair<string, List<string>>(key, new List<string> { action }));
				}
			}

			return byKey.Take(_maxHints.Value).Select(pair => $"<color=#B8B8B8>[{pair.Key}]</color> {string.Join(" / ", pair.Value)}").ToList();
		}
	}
}
