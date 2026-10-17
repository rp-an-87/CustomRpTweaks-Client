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

		private void Awake()
		{
			Log = Logger;

			HideOtherPlayersSkillNotifications = Config.Bind("1. Notifications", "1. Hide skill level-up notifications of other characters", true,
				"Bots/NPCs also level up their skills (a lot, with high skill multipliers). Only your own character should be able to show the skill and weapon mastery level-up notifications.");
			LogSuppressedNotifications = Config.Bind("1. Notifications", "2. Log suppressed notifications", false,
				"Writes a line to the BepInEx log every time a notification coming from another character is suppressed. Useful to confirm who was causing them.");

			new SkillNotificationPatches.SkillLevelChangedPatch().Enable();
			new SkillNotificationPatches.WeaponMasteredPatch().Enable();
			new SkillNotificationPatches.ClientPlayerSkillLevelChangedPatch().Enable();
			new SkillNotificationPatches.ClientPlayerWeaponMasteredPatch().Enable();
		}
	}
}
