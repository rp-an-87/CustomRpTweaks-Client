using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using CustomRpTweaks.Client.Patches;

namespace CustomRpTweaks.Client
{
	[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
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
		internal static ConfigEntry<bool> KitButton;
		internal static ConfigEntry<bool> KeepSpeedWhileZooming;
		internal static ConfigEntry<int> KitHealthResource;
		internal static ConfigEntry<int> KitBandageResource;
		internal static ConfigEntry<int> KitHeavyBleedResource;
		internal static ConfigEntry<int> KitPropital;
		// Grenade types the kit can bring, one amount each. A type with several grenades takes whichever of them the stash has, at random.
		internal static readonly (string[] Templates, string Name, int Default)[] KitGrenadeTypes =
		{
			(new[] { "58d3db5386f77426186285a0" }, "M67", 2),
			(new[] { "5448be9a4bdc2dfd2f8b456a" }, "RGD-5", 0),
			(new[] { "5710c24ad2720bc3458b45a3" }, "F-1", 0),
			(new[] { "5e32f56fcb6d5863cc5e5ee4", "5e340dcdcb6d5863cc5e5efb" }, "VOG-17 / VOG-25 (random)", 0),
			(new[] { "617fd91e5539a84ec44ce155", "618a431df1eb8e24b8741deb" }, "Impact RGN / RGO (random)", 0),
		};
		internal static ConfigEntry<int>[] KitGrenadeCounts;
		internal static ConfigEntry<bool> KitAllowOvershoot;
		internal static ConfigEntry<bool> KitSwapWorse;

		internal static ConfigEntry<KeyboardShortcut> TopUpMagazine;

		private void Update()
		{
			MagazineTopUp.Update();
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

			KitButton = Config.Bind("6. Kit button", "1. Show kit button in the stash", false,
				"Adds a button above the pin button of your stash (hideout/menu only). Pressing it moves a preset list of consumables from the stash into your pockets, then vest, skipping what you already carry and what the stash doesn't have. Prefers non-FiR items, then the fullest ones.");
			KitHealthResource = Config.Bind("6. Kit button", "2. Health resource (0 disables)", 300,
				new ConfigDescription("Health resource of meds to carry. What you carry counts towards it (partly used kits count for what they have left). Missing resource is filled in priority order: AFAK, Salewa, IFAK, Car, AI-2. AFAK and Salewa are 400, IFAK 300, Car 220, AI-2 100.", new AcceptableValueRange<int>(0, 2000)));
			KitBandageResource = Config.Bind("6. Kit button", "3. Bandage points (0 disables)", 2,
				new ConfigDescription("Bandage points to carry: an Army bandage counts its remaining uses (2 when new), an Aseptic bandage is 1. Army bandages are preferred.", new AcceptableValueRange<int>(0, 20)));
			KitHeavyBleedResource = Config.Bind("6. Kit button", "4. Heavy bleed points (0 disables)", 2,
				new ConfigDescription("Heavy bleed points to carry: a CALOK-B counts its remaining uses (3 when new), a CAT or an Esmarch tourniquet is 1. Preference: CALOK-B, CAT, Esmarch.", new AcceptableValueRange<int>(0, 20)));
			KitPropital = Config.Bind("6. Kit button", "5. Propital (0 disables)", 1,
				new ConfigDescription("How many Propital injectors to carry.", new AcceptableValueRange<int>(0, 10)));
			KitGrenadeCounts = KitGrenadeTypes
				.Select((grenade, index) => Config.Bind("6b. Kit grenades", $"{index + 1}. {grenade.Name} (0 disables)", grenade.Default,
					new ConfigDescription($"How many {grenade.Name} grenades to carry. Any combination of the types can be set; a type with two grenades takes whichever the stash has, at random.", new AcceptableValueRange<int>(0, 10))))
				.ToArray();
			KitAllowOvershoot = Config.Bind("6. Kit button", "7. Allow overshoot", true,
				"When the missing amount is smaller than the next item in priority (e.g. 1 point missing and an Army bandage is next), take it anyway. When off, it skips to a smaller item that fits, or takes nothing.");

			KitSwapWorse = Config.Bind("6. Kit button", "8. Swap worse items", true,
				"Before filling up, items you carry that are worse than what the stash has (lower priority, partly used, or FiR when a non-FiR one is available) are moved to the stash and replaced by the better ones.");

			new LoadoutKitButtonPatch().Enable();

			KeepSpeedWhileZooming = Config.Bind("8. Movement", "1. Keep walk speed while zooming a scope", false,
				"The mouse wheel changes both your walk speed and the zoom of a smooth-zoom scope. With this on, the wheel does not change your walk speed while you aim through a smooth-zoom (variable) scope and hold your scope zoom key combination (for example Alt + wheel). The wheel alone, other sights and not aiming are not affected.");
			new ScopeZoomSpeedPatch().Enable();

			TopUpMagazine = Config.Bind("7. Magazine top-up", "1. Top-up hotkey", new KeyboardShortcut(KeyCode.T),
				"Hover a magazine in the stash or in a raid inventory and press this to fill it up. It uses the ammo the magazine already holds, or else the highest-penetration ammo it accepts, taken from the stash and your gear. In a raid it is the normal timed load. Unassign the key to turn the feature off.");

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
		}
	}
}
