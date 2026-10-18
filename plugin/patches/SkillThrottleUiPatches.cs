using System.Reflection;
using EFT;
using EFT.UI;
using SPT.Reflection.Patching;

namespace CustomRpTweaks.Client.Patches
{
	// Marks throttled skills in the skills screen: red level text and a line in the skill tooltip.
	internal static class SkillThrottleUiPatches
	{
		private const string Red = "#FF3030";

		private static void ColorLevel(SkillPanel panel, Skill skill)
		{
			if (skill == null || panel._level.text.Contains(Red) || !SkillThrottlePatches.IsThrottled(skill, out _))
			{
				return;
			}

			panel._level.text = $"<color={Red}>{panel._level.text}</color>";
		}

		internal class SkillPanelShowPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(SkillPanel).GetMethod(nameof(SkillPanel.Show), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPostfix]
			private static void Postfix(SkillPanel __instance, Skill ____skill) => ColorLevel(__instance, ____skill);
		}

		internal class SkillPanelLevelChangedPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(SkillPanel).GetMethod(nameof(SkillPanel.OnSkillLevelChanged), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPostfix]
			private static void Postfix(SkillPanel __instance, Skill ____skill) => ColorLevel(__instance, ____skill);
		}

		internal class SkillTooltipShowPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(SkillTooltip).GetMethod(nameof(SkillTooltip.Show), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPostfix]
			private static void Postfix(SkillTooltip __instance, Skill skill)
			{
				if (!SkillThrottlePatches.IsThrottled(skill, out int pmcLevel))
				{
					return;
				}

				int overshoot = Plugin.SkillLevelOvershoot.Value;
				string limit = overshoot > 0 ? $"{pmcLevel} + {overshoot}" : pmcLevel.ToString();
				__instance._description.text += $"\n\n<color={Red}>Throttled to PMC Level ({limit})</color>";
			}
		}
	}
}
