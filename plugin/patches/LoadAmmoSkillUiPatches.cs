using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace CustomRpTweaks.Client.Patches
{
	// Shows the walking speed while loading ammo (see LoadAmmoSkillSpeed) as a perk row in the Mag Drills skill tooltip.
	// The row is an extra BuffPanel built from the tooltip's own template, so it looks like the vanilla perks: the
	// description and icon of a buff come from its id, so those two lookups answer for our buff instead.
	internal static class LoadAmmoSkillUiPatches
	{
		private static readonly FieldInfo UiField = AccessTools.Field(typeof(UIElement), "UI");

		// Switching: a normal row, and the gold ELITE tag and background once the skill is elite
		private class LoadAmmoWalkBuff : SkillManager.Buff
		{
			internal readonly Skill Skill;

			internal LoadAmmoWalkBuff(Skill skill)
			{
				Skill = skill;
				Id = EBuffId.MagDrillsLoadSpeed;
				BuffType = SkillManager.EBuffType.Switching;
			}

			internal string Description()
			{
				var share = Mathf.RoundToInt(LoadAmmoSkillSpeed.ShareAtLevel(Skill.Level) * 100f);
				return Skill.IsEliteLevel
					? $"Walking speed while loading ammo unlocked: <color=#54c1ff>{share}%</color> of full speed"
					: $"Walking speed while loading ammo: <color=#54c1ff>{share}%</color> of your top speed";
			}
		}

		internal class SkillTooltipShowPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(SkillTooltip).GetMethod(nameof(SkillTooltip.Show), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPostfix]
			private static void Postfix(SkillTooltip __instance, Skill skill)
			{
				if (skill == null || skill.Id != ESkillId.MagDrills || !LoadAmmoSkillSpeed.Active)
				{
					return;
				}

				var ui = (UIParent)UiField.GetValue(__instance);
				ui.AddViewList(new List<SkillManager.Buff> { new LoadAmmoWalkBuff(skill) }, __instance._buffPanelTemplate, __instance._buffContainer,
					(buff, panel) =>
					{
						panel.Show(skill, buff);
						panel.UpdateBuff();
						panel.UpdateVisibility();
					});
			}
		}

		internal class BuffDescriptionPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(BuffIcon).GetMethod(nameof(BuffIcon.GetBuffDescription), BindingFlags.Static | BindingFlags.Public);

			[PatchPrefix]
			private static bool Prefix(SkillManager.Buff buff, ref string __result)
			{
				if (buff is not LoadAmmoWalkBuff walk)
				{
					return true;
				}

				__result = walk.Description();
				return false;
			}
		}
	}
}
