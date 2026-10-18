using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
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

		internal static ConfigEntry<bool> HideOtherPlayersSkillNotifications;
		internal static ConfigEntry<bool> LogSuppressedNotifications;

		internal static ConfigEntry<bool> ThrottleSkillsToPmcLevel;
		internal static ConfigEntry<int> SkillLevelOvershoot;

		internal static ConfigEntry<int> SpecialSlotsPerRow;
		internal static ConfigEntry<bool> CraftFirHandling;
		internal static ConfigEntry<bool> KeepTopRowsFree;
		internal static ConfigEntry<int> TopRowsToKeepFree;

		private void Awake()
		{
			Log = Logger;

			HideOtherPlayersSkillNotifications = Config.Bind("1. Notifications", "1. Hide skill level-up notifications of other characters", true,
				"Bots/NPCs also level up their skills (a lot, with high skill multipliers). Only your own character should be able to show the skill and weapon mastery level-up notifications.");
			LogSuppressedNotifications = Config.Bind("1. Notifications", "2. Log suppressed notifications", false,
				"Writes a line to the BepInEx log every time a notification coming from another character is suppressed. Useful to confirm who was causing them.");

			ThrottleSkillsToPmcLevel = Config.Bind("2. Skill throttle", "1. Throttle skills to PMC level", true,
				"Your skills can't level past your PMC level (plus the overshoot below). Experience gained past that point is discarded. Skills already above the limit are not lowered. Weapon mastery is not affected.");
			SkillLevelOvershoot = Config.Bind("2. Skill throttle", "2. Allowed levels above PMC level", 0,
				new ConfigDescription("How many levels above your PMC level a skill is still allowed to reach. 0 = skills are capped at your PMC level.",
					new AcceptableValueRange<int>(0, 20)));

			SpecialSlotsPerRow = Config.Bind("3. Pockets", "1. Special slots per row", 3,
				new ConfigDescription("When you have more special slots than this, the extra ones wrap onto the next row instead of overflowing.",
					new AcceptableValueRange<int>(1, 6)));

			new SpecialSlotsLayoutPatch().Enable();

			KeepTopRowsFree = Config.Bind("4. Stash sort", "1. Keep top rows free when sorting", false,
				"Sorting the stash places items below the rows set here. The top rows are only used when everything else is full.");
			TopRowsToKeepFree = Config.Bind("4. Stash sort", "2. Rows to keep free", 5,
				new ConfigDescription("How many rows, counting from the top of the stash, to keep free when sorting.", new AcceptableValueRange<int>(1, 30)));

			CraftFirHandling = Config.Bind("5. Hideout", "1. Show FiR items in crafts and buy replacements", false,
				"Craft requirements read 'total (FiR) / required' so you can see how many FiR items a craft would use, and the buy button purchases replacements for those (plus anything missing). Crafts can still use FiR items. Hideout upgrades and tools are not affected.");

			new HideoutCraftFirPatches.RequirementViewPatch().Enable();
			new HideoutCraftFirPatches.BuyMaterialsPatch().Enable();
			new HideoutCraftFirPatches.StartButtonPatch().Enable();
			new HideoutCraftFirPatches.BuyTooltipPatch().Enable();

			new StashSortOverlayPatch().Enable();
			new StashSortReservePatches.SortPatch().Enable();
			new StashSortReservePatches.FindFreeSpacePatch().Enable();
			new StashSortReservePatches.GetFreeLocationPatch().Enable();

			new SkillThrottlePatches.SkillOnTriggerPatch().Enable();
			new SkillThrottlePatches.SkillSetCurrentPatch().Enable();

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
