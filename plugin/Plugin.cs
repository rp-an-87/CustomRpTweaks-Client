using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using CustomRpTweaks.Client.Patches;

namespace CustomRpTweaks.Client
{
	[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
	[BepInDependency(LoadAmmoSkillSpeed.TargetGuid, BepInDependency.DependencyFlags.SoftDependency)]
	[BepInDependency(UiFixesHints.TargetGuid, BepInDependency.DependencyFlags.SoftDependency)]
	public class Plugin : BaseUnityPlugin
	{
		public const string PluginGuid = "RPAN87.CustomRpTweaks";
		public const string PluginName = "CustomRpTweaks.Client";
		public const string PluginVersion = "1.0.0";

		internal static ManualLogSource Log;
		internal static Plugin Instance;

		internal static ConfigEntry<bool> HideOtherPlayersSkillNotifications;
		internal static ConfigEntry<bool> LogSuppressedNotifications;

		internal static ConfigEntry<bool> ThrottleSkillsToPmcLevel;
		internal static ConfigEntry<int> SkillLevelOvershoot;
		internal static ConfigEntry<bool> LogThrottleDecisions;

		internal static ConfigEntry<int> SpecialSlotsPerRow;
		internal static ConfigEntry<bool> CraftFirHandling;
		internal static ConfigEntry<bool> KeepTopRowsFree;
		internal static ConfigEntry<int> TopRowsToKeepFree;
		internal static ConfigEntry<bool> KeepSpeedWhileZooming;
		internal static ConfigEntry<bool> SteadyAimHoldBreathFix;

		internal static ConfigEntry<bool> ScaleLoadAmmoSpeed;
		internal static ConfigEntry<float> LoadAmmoSpeedAtLevel0;
		internal static ConfigEntry<float> LoadAmmoSpeedAtLevel50;
		internal static ConfigEntry<float> LoadAmmoSpeedAtElite;

		internal static ConfigEntry<KeyboardShortcut> TopUpMagazine;
		internal static ConfigEntry<KeyboardShortcut> RepairWithKitHotkey;

		internal static ConfigEntry<KeyboardShortcut> DismantleHotkey;
		internal static ConfigEntry<KeyboardShortcut> DismantleNonVitalHotkey;
		internal static ConfigEntry<bool> DismantleSelf;
		internal static ConfigEntry<bool> DismantleInRaid;

		internal static ConfigEntry<KeyboardShortcut> EquipPlateHotkey;

		internal static ConfigEntry<KeyboardShortcut> SplitHotkey;

		internal static ConfigEntry<bool> HotkeyHints;

		internal static ConfigEntry<bool> PriceIndicator;
		internal static ConfigEntry<string> PriceIndicatorNewColor;
		internal static ConfigEntry<int> PriceIndicatorTraderSize;
		internal static ConfigEntry<bool> PriceIndicatorTooltip;
		internal static ConfigEntry<string> PriceIndicatorOldColor;

		private void Update()
		{
			MagazineTopUp.Update();
			RepairWithKit.Update();
			LoadAmmoSkillSpeed.Update();
			Dismantle.Update();
			EquipPlate.Update();
			SplitStack.Update();
		}

		// A renamed setting takes over the value it had under its old name, if the config file still has one
		private void Migrate(ConfigEntryBase entry, string oldSection, string oldKey)
		{
			// BepInEx keeps what it read from the file but found no setting for here; the list isn't public
			var orphaned = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(Config, null)
				as System.Collections.Generic.Dictionary<ConfigDefinition, string>;
			var oldDefinition = new ConfigDefinition(oldSection, oldKey);
			if (orphaned != null && orphaned.TryGetValue(oldDefinition, out var value))
			{
				entry.SetSerializedValue(value);
				orphaned.Remove(oldDefinition);
			}
		}

		private void Awake()
		{
			Log = Logger;
			Instance = this;

			HideOtherPlayersSkillNotifications = Config.Bind("1. Notifications", "1. Hide skill level-up notifications of other characters", true,
				"Bots/NPCs also level up their skills (a lot, with high skill multipliers). Only your own character should be able to show the skill and weapon mastery level-up notifications.");
			LogSuppressedNotifications = Config.Bind("1. Notifications", "2. Log suppressed notifications", false,
				"Writes a line to the BepInEx log every time a notification coming from another character is suppressed. Useful to confirm who was causing them.");

			ThrottleSkillsToPmcLevel = Config.Bind("2. Skill throttle", "1. Throttle skills to PMC level", true,
				"Your skills can't level past your PMC level (plus the overshoot below). Experience gained past that point is discarded. Skills already above the limit are not lowered. Weapon mastery is not affected.");
			SkillLevelOvershoot = Config.Bind("2. Skill throttle", "2. Allowed levels above PMC level", 0,
				new ConfigDescription("How many levels above your PMC level a skill is still allowed to reach. 0 = skills are capped at your PMC level.",
					new AcceptableValueRange<int>(0, 20)));

			LogThrottleDecisions = Config.Bind("2. Skill throttle", "3. Log throttle decisions", false,
				"Writes to the BepInEx log (at most once every 5 seconds per skill) what the throttle did to skill experience gains, and why it didn't apply when it didn't. For troubleshooting.");

			SpecialSlotsPerRow = Config.Bind("3. Pockets", "1. Special slots per row", 3,
				new ConfigDescription("When you have more special slots than this, the extra ones wrap onto the next row instead of overflowing.",
					new AcceptableValueRange<int>(1, 6)));

			new SpecialSlotsLayoutPatch().Enable();

			KeepTopRowsFree = Config.Bind("4. Stash sort", "1. Keep top rows free when sorting", false,
				"Sorting the stash places items below the rows set here. The top rows are only used when everything else is full.");
			TopRowsToKeepFree = Config.Bind("4. Stash sort", "2. Rows to keep free", 5,
				new ConfigDescription("How many rows, counting from the top of the stash, to keep free when sorting.", new AcceptableValueRange<int>(1, 50)));

			CraftFirHandling = Config.Bind("5. Hideout", "1. Show FiR items in crafts and buy replacements", false,
				"Craft requirements read 'total (FiR) / required' so you can see how many FiR items a craft would use, and the buy button purchases replacements for those (plus anything missing). Crafts can still use FiR items. Hideout upgrades and tools are not affected.");

			new HideoutCraftFirPatches.RequirementViewPatch().Enable();
			new HideoutCraftFirPatches.BuyMaterialsPatch().Enable();
			new HideoutCraftFirPatches.StartButtonPatch().Enable();
			new HideoutCraftFirPatches.BuyTooltipPatch().Enable();

			KeepSpeedWhileZooming = Config.Bind("8. Movement", "1. Keep walk speed while zooming a scope", false,
				"The mouse wheel changes both your walk speed and the zoom of a smooth-zoom scope. With this on, the wheel does not change your walk speed while you aim through a smooth-zoom (variable) scope and hold your scope zoom key combination (for example Alt + wheel). The wheel alone, other sights and not aiming are not affected.");
			new ScopeZoomSpeedPatch().Enable();

			SteadyAimHoldBreathFix = Config.Bind("16. Aiming", "1. Fix holding breath during the steady aim perk", true,
				"The elite weapon draw perk (Aim Drills and weapon skills) steadies your aim for 3 seconds after you start aiming, but the game applies it even while you hold your breath, and the aim then drifts as the 3 seconds run out. With this on, holding your breath during those seconds steadies the aim as it normally does, and the perk applies again if you release the breath within them.");
			new SteadyAimBreathPatch().Enable();

			// Only offered when Continuous Load Ammo is installed (it is a soft dependency, so it has loaded before us)
			if (Chainloader.PluginInfos.ContainsKey(LoadAmmoSkillSpeed.TargetGuid))
			{
				ScaleLoadAmmoSpeed = Config.Bind("9. Loading ammo", "1. Scale loading walk speed with Mag Drills", false,
					"Needs the Continuous Load Ammo mod. While you load ammo outside the inventory, your walk speed is limited to a share of your current top walking speed (already reduced by your weight). With this on, that share depends on your Mag Drills level, using the three values below, instead of Continuous Load Ammo's own fixed 'Speed Limit'. Its setting is changed in memory only, its config file isn't touched. Applies from the next load you start.");
				LoadAmmoSpeedAtLevel0 = Config.Bind("9. Loading ammo", "2. Loading speed at Mag Drills level 0", 0.2f,
					new ConfigDescription("Share of your current top walking speed while loading ammo at Mag Drills level 0 (0.2 = 20%). Rises evenly up to the level 50 value.", new AcceptableValueRange<float>(0f, 1f)));
				LoadAmmoSpeedAtLevel50 = Config.Bind("9. Loading ammo", "3. Loading speed at Mag Drills level 50", 0.7f,
					new ConfigDescription("Share of your current top walking speed while loading ammo at Mag Drills level 50 (0.7 = 70%).", new AcceptableValueRange<float>(0f, 1f)));
				LoadAmmoSpeedAtElite = Config.Bind("9. Loading ammo", "4. Loading speed at Mag Drills level 51 (elite)", 0.9f,
					new ConfigDescription("Share of your unencumbered walking speed (not reduced by weight, so weight can still cap it) while loading ammo at the elite Mag Drills level 51 (0.9 = 90%). Unlike the lower levels this one is absolute.", new AcceptableValueRange<float>(0f, 1f)));
				new LoadAmmoSkillUiPatches.SkillTooltipShowPatch().Enable();
				new LoadAmmoSkillUiPatches.BuffDescriptionPatch().Enable();
			}

			TopUpMagazine = Config.Bind("7. Magazine top-up", "1. Top-up hotkey", new KeyboardShortcut(KeyCode.T),
				"Hover a magazine in the stash or in a raid inventory and press this to fill it up. It uses the ammo the magazine already holds, or else the highest-penetration ammo it accepts, taken from the stash and your gear. In a raid it is the normal timed load. Unassign the key to turn the feature off.");

			RepairWithKitHotkey = Config.Bind("7b. Repair with kit", "1. Repair hotkey", new KeyboardShortcut(KeyCode.R),
				"Hover an item in the stash and press this to repair it fully with one of your repair kits, without opening the repair window (the same repair as the window's button, as far as the kits can pay for). It uses the kit with the least resource left that can repair the item. Does nothing if you have no suitable kit, or outside the hideout/menu stash. Unassign the key to turn the feature off.");

			DismantleHotkey = Config.Bind("10. Dismantle", "1. Dismantle hotkey", new KeyboardShortcut(KeyCode.D, KeyCode.LeftAlt),
				"Hover an item and press this to take off the parts attached to it (a weapon's mods, an armor's plates and other removable parts) and put them next to it, in the container it lies in. The item itself stays where it is, only what is attached to it comes off. Magazines keep their ammo and containers keep their contents. Parts the game won't let you remove stay on. Unassign the key to turn the feature off.");
			DismantleNonVitalHotkey = Config.Bind("10. Dismantle", "4. Dismantle non-vital parts hotkey", new KeyboardShortcut(KeyCode.D, KeyCode.LeftControl, KeyCode.LeftAlt),
				"Like the dismantle hotkey, but the vital parts (the ones in required slots, a receiver for instance) stay attached, in the stash as in a raid, and the parts attached to them still come off. Pressed with the keys of the other hotkey held too, this one wins. Unassign the key to turn the feature off.");
			DismantleSelf = Config.Bind("10. Dismantle", "5. Take off the hovered part itself too", false,
				"Off: only what is attached to the hovered item comes off, the item itself stays where it is (UI Fixes' uninstall shortcut takes off a single part). On: when the hovered item is itself a part attached to something (a handguard on a gun, a plate in armor), it comes off first and its own parts come off after it.");
			DismantleInRaid = Config.Bind("10. Dismantle", "3. Allow in a raid", true,
				"Also works in a raid inventory, with the game's own raid rules: parts that can't be modded in a raid, vital parts and locked armor slots stay on, but the parts attached to them can still come off (a scope off a receiver that has to stay). Parts of an item you carry or of a dead body go to your or its pockets, vest and backpack, if there is room.");

			// The settings used to be called Disassemble: keep what was chosen
			Migrate(DismantleHotkey, "10. Disassemble", "1. Disassemble hotkey");
			Migrate(DismantleNonVitalHotkey, "10. Disassemble", "4. Disassemble non-vital parts hotkey");
			Migrate(DismantleInRaid, "10. Disassemble", "3. Allow in a raid");

			EquipPlateHotkey = Config.Bind("11. Equip plate", "1. Equip plate hotkey", new KeyboardShortcut(KeyCode.I),
				"Hover an armor plate in the stash and press this to put it into the first free plate slot of the armor, rig or helmet you wear that accepts it. The game's own rules decide what fits. Not in a raid. Unassign the key to turn the feature off.");

			SplitHotkey = Config.Bind("15. Split stack", "1. Split hotkey", new KeyboardShortcut(KeyCode.S, KeyCode.LeftControl),
				"Hover a stack (ammo, money, grenades, anything that stacks) and press this to split it in half without the split dialog. The new stack, the lower half, goes to the first free place in the container it lies in (or, for something you carry, your pockets, vest or backpack). Unassign the key to turn the feature off.");

			new StashSortOverlayPatch().Enable();
			new StashSortReservePatches.SortPatch().Enable();
			new StashSortReservePatches.FindFreeSpacePatch().Enable();
			new StashSortReservePatches.GetFreeLocationPatch().Enable();

			new SkillThrottlePatches.SkillSetCurrentPatch().Enable();
			new SkillThrottlePatches.SkillCurrentSetterPatch().Enable();

			new SkillThrottleUiPatches.SkillPanelShowPatch().Enable();
			new SkillThrottleUiPatches.SkillPanelLevelChangedPatch().Enable();
			new SkillThrottleUiPatches.SkillTooltipShowPatch().Enable();

			new SkillNotificationPatches.SkillLevelChangedPatch().Enable();
			new SkillNotificationPatches.WeaponMasteredPatch().Enable();
			new SkillNotificationPatches.ClientPlayerSkillLevelChangedPatch().Enable();
			new SkillNotificationPatches.ClientPlayerWeaponMasteredPatch().Enable();

			HotkeyHints = Config.Bind("13. Hotkey hints", "1. Show hotkey hints in item tooltips", true,
				"Adds a small line below a separator at the end of the tooltip of an item in your inventory with the hotkeys of this mod that apply to it right now: top up (a magazine that isn't full), repair (a damaged item that one of your kits can repair), equip plate, and dismantle (an item with attached parts; the keep-vital one only when some of them are in required slots). The keys shown are the ones configured. Not shown for items of traders, the flea market or the mail. Turn it off if another mod's tooltip lines clash with it.");
			// Only offered when UI Fixes is installed (a soft dependency, so it has loaded before us)
			UiFixesHints.Setup(Config);
			new HotkeyHintPatches.HoverEnterPatch().Enable();
			new HotkeyHintPatches.HoverExitPatch().Enable();
			new HotkeyHintPatches.TooltipShowPatch().Enable();

			PriceIndicator = Config.Bind("12. Price indicator", "1. Colour prices the server has raised", true,
				"Needs the CustomRpTweaks server mod with its trader overstock feature on. Prices the server has raised for you are drawn in the colour below, on the trader screen and in the flea market. The client works out which prices are raised from the prices it has seen, and asks the server once per game session (right after the first trader offers or flea results load) for offers that were already raised when the game started. If the server mod is missing or has the feature or its 'clientIndicator' option off, the client stops asking and shows nothing extra until the game restarts. Applies from the next load of the offers.");
			PriceIndicatorNewColor = Config.Bind("12. Price indicator", "2. Raised price colour", "#FF9A3C", "Colour of the raised price, as #RRGGBB.");
			PriceIndicatorTraderSize = Config.Bind("12. Price indicator", "3. Trader screen: raised price size (%)", 125,
				new ConfigDescription("Size of a raised price under an item in the trader's grid, as a percentage of a normal price (100 = same size), so the increase is easier to spot.", new AcceptableValueRange<int>(80, 150)));
			PriceIndicatorTooltip = Config.Bind("12. Price indicator", "4. Trader screen: base price in the tooltip", true,
				"Hover an item in the trader's grid: when its price has been raised, the tooltip shows the base price, struck through, before the raised price.");
			PriceIndicatorOldColor = Config.Bind("12. Price indicator", "5. Tooltip base price colour", "#FFFF00", "Colour of the struck-through base price in the tooltip, as #RRGGBB.");

			new OverstockPricePatches.TraderOffersHook().Enable();
			new OverstockPricePatches.FleaOffersHook().Enable();
			new OverstockPricePatches.FleaPurchaseRefreshPatch().Enable();
			new OverstockPricePatches.TraderGridPricePatch().Enable();
			new OverstockPricePatches.TraderTooltipPatch().Enable();
			new OverstockPricePatches.FleaRowPricePatch().Enable();
		}
	}
}
