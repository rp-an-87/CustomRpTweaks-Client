using System;
using System.Reflection;
using Comfort.Common;
using EFT;
using SPT.Reflection.Patching;
using SPT.Reflection.Utils;

namespace CustomRpTweaks.Client.Patches
{
	// In raid, skills can't be leveled past (PMC level + overshoot). The experience gained past that point is discarded.
	// Only experience gained through Skill.OnTrigger is throttled, so loading/syncing profiles and bots are untouched,
	// and skills that are already above the limit are never lowered. Weapon mastery is not a Skill and is not throttled.
	internal static class SkillThrottlePatches
	{
		[ThreadStatic]
		private static bool _inTrigger;

		// Resolves the PMC profile that owns the skill. In raid it's the main player (the throttle only applies there);
		// with includeMenu it also falls back to the session profile, so the UI can show what will be throttled in the next raid.
		private static bool TryGetPmc(Skill skill, bool includeMenu, out Profile pmc)
		{
			pmc = null;
			if (!Plugin.ThrottleSkillsToPmcLevel.Value || skill.Id == ESkillId.Crafting || skill.Id == ESkillId.HideoutManagement)
			{
				return false;
			}

			if (Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance.MainPlayer != null)
			{
				pmc = Singleton<GameWorld>.Instance.MainPlayer.Profile;
			}
			else if (includeMenu)
			{
				pmc = ClientAppUtils.GetMainApp()?.GetClientBackEndSession()?.Profile;
			}

			return pmc != null && pmc.Side != EPlayerSide.Savage && skill.SkillManager == pmc.Skills;
		}

		private static float MaxExperience(Profile pmc) => (pmc.Info.Level + Plugin.SkillLevelOvershoot.Value + 1) * BaseSkill.EXP_PER_LEVEL - 0.01f;

		// True when the skill is sitting at the limit (in raid, or in the menu for the UI)
		internal static bool IsThrottled(Skill skill, out int pmcLevel)
		{
			pmcLevel = 0;
			if (!TryGetPmc(skill, true, out var pmc) || skill.Current < MaxExperience(pmc) - 0.02f)
			{
				return false;
			}

			pmcLevel = pmc.Info.Level;
			return true;
		}

		internal class SkillOnTriggerPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(Skill).GetMethod(nameof(Skill.OnTrigger), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPrefix]
			private static void Prefix() => _inTrigger = true;

			[PatchFinalizer]
			private static void Finalizer() => _inTrigger = false;
		}

		internal class SkillSetCurrentPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(BaseSkill).GetMethod(nameof(BaseSkill.SetCurrent), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPrefix]
			private static void Prefix(BaseSkill __instance, ref float value)
			{
				if (!_inTrigger || !(__instance is Skill skill) || value <= skill.Current || !TryGetPmc(skill, false, out var pmc))
				{
					return;
				}

				value = Math.Min(value, Math.Max(skill.Current, MaxExperience(pmc)));
			}
		}
	}
}
