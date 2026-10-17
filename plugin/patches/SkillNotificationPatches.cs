using System.Reflection;
using EFT;
using SPT.Reflection.Patching;

namespace CustomRpTweaks.Client.Patches
{
	// LocalPlayer/ClientPlayer show the skill level-up and weapon mastery notifications for every character that levels up.
	// LocalPlayer only guards with !IsAI, which does not hold for every bot, and ClientPlayer does not guard at all.
	// Here we only let our own character show them.
	internal static class SkillNotificationPatches
	{
		private static bool ShouldSkip(Player player, string what)
		{
			if (!Plugin.HideOtherPlayersSkillNotifications.Value || player == null || player.IsYourPlayer)
			{
				return false;
			}

			if (Plugin.LogSuppressedNotifications.Value)
			{
				var info = player.Profile?.Info;
				Plugin.Log.LogInfo($"Suppressed {what} notification from {player.GetType().Name} '{info?.Nickname}' (role {info?.Settings?.Role}, IsAI: {player.IsAI}).");
			}

			return true;
		}

		private static MethodBase Declared<T>(string name)
		{
			return typeof(T).GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
		}

		internal class SkillLevelChangedPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => Declared<LocalPlayer>(nameof(LocalPlayer.OnSkillLevelChanged));

			[PatchPrefix]
			private static bool Prefix(LocalPlayer __instance) => !ShouldSkip(__instance, "skill level-up");
		}

		internal class WeaponMasteredPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => Declared<LocalPlayer>(nameof(LocalPlayer.OnWeaponMastered));

			[PatchPrefix]
			private static bool Prefix(LocalPlayer __instance) => !ShouldSkip(__instance, "weapon mastery");
		}

		internal class ClientPlayerSkillLevelChangedPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => Declared<ClientPlayer>(nameof(ClientPlayer.OnSkillLevelChanged));

			[PatchPrefix]
			private static bool Prefix(ClientPlayer __instance) => !ShouldSkip(__instance, "skill level-up");
		}

		internal class ClientPlayerWeaponMasteredPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => Declared<ClientPlayer>(nameof(ClientPlayer.OnWeaponMastered));

			[PatchPrefix]
			private static bool Prefix(ClientPlayer __instance) => !ShouldSkip(__instance, "weapon mastery");
		}
	}
}
