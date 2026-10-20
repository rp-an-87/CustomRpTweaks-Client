using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using SPT.Reflection.Patching;
using SPT.Reflection.Utils;
using UnityEngine;

namespace CustomRpTweaks.Client.Patches
{
	// In raid, skills can't be leveled past (PMC level + overshoot). The experience gained past that point is discarded.
	// Every skill experience write ends in BaseSkill.SetCurrent (gameplay actions, server-pushed experience) or the BaseSkill.Current
	// setter (direct writes that skip SetCurrent), so both are patched. Only the main player's PMC skills are affected, only in raid,
	// and skills that are already above the limit are never lowered. Weapon mastery is not a Skill and is not throttled.
	internal static class SkillThrottlePatches
	{
		// Elite level: nothing to throttle past it
		private const int MaxSkillLevel = 51;

		// True while BaseSkill.SetCurrent runs, so the Current setter it calls isn't handled (and logged) twice
		[ThreadStatic]
		private static bool _inSetCurrent;

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

			return pmc != null && pmc.Side != EPlayerSide.Savage && (skill.SkillManager == pmc.Skills || (pmc.Skills.TryGetSkill(skill.Id, out var own) && own == skill));
		}

		private static float MaxExperience(Profile pmc) => (pmc.Info.Level + Plugin.SkillLevelOvershoot.Value + 1) * BaseSkill.EXP_PER_LEVEL - 0.01f;

		// True when the skill has reached the highest level it can get (in raid, or in the menu for the UI): from there on it can only fill its bar.
		// untilPmcLevel is the PMC level at which the skill can level up again.
		internal static bool IsThrottled(Skill skill, out int untilPmcLevel)
		{
			untilPmcLevel = 0;
			if (!TryGetPmc(skill, true, out var pmc) || skill.Level >= MaxSkillLevel || skill.Current < (pmc.Info.Level + Plugin.SkillLevelOvershoot.Value) * BaseSkill.EXP_PER_LEVEL)
			{
				return false;
			}

			untilPmcLevel = Math.Max(pmc.Info.Level + 1, skill.Level + 1 - Plugin.SkillLevelOvershoot.Value);
			return true;
		}

		private static readonly Dictionary<string, float> LastLogged = new Dictionary<string, float>();

		// Clamps an experience increase to the limit. Returns the value to write.
		private static float Throttle(BaseSkill baseSkill, float value, string source)
		{
			if (!(baseSkill is Skill skill) || value <= skill.Current)
			{
				return value;
			}

			bool applies = TryGetPmc(skill, false, out var pmc);
			float result = value;
			if (applies)
			{
				result = Math.Min(value, Math.Max(skill.Current, MaxExperience(pmc)));

				// The game already counted the full gain in the skill's "points earned" (the +XP shown in raid): take back what was discarded
				float discarded = value - result;
				if (discarded > 0f)
				{
					skill.AddPointsEarnedForWorkout(-Math.Min(discarded, skill.PointsEarned));
				}
			}

			if (Plugin.LogThrottleDecisions.Value)
			{
				LogDecision(skill, value, result, applies, pmc, source);
			}

			return result;
		}

		// At most one line per skill and source every 5 seconds, since skills like Endurance gain experience every tick
		private static void LogDecision(Skill skill, float value, float result, bool applies, Profile pmc, string source)
		{
			var mainPlayer = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;
			if (!applies && mainPlayer != null && Plugin.ThrottleSkillsToPmcLevel.Value)
			{
				// Another character's skill (bots level up too): not interesting
				return;
			}

			string key = skill.Id + source;
			if (LastLogged.TryGetValue(key, out float last) && Time.realtimeSinceStartup - last < 5f)
			{
				return;
			}

			LastLogged[key] = Time.realtimeSinceStartup;
			string outcome = !applies
				? (!Plugin.ThrottleSkillsToPmcLevel.Value ? "not throttled: toggle off"
					: skill.Id == ESkillId.Crafting || skill.Id == ESkillId.HideoutManagement ? "not throttled: excluded skill"
					: "not throttled: not in raid")
				: result < value ? $"throttled to {result:0.##}" : "within the limit";
			string limit = pmc != null ? $", limit {MaxExperience(pmc):0.##} (PMC level {pmc.Info.Level} + {Plugin.SkillLevelOvershoot.Value})" : string.Empty;
			string caller = source == "SetCurrent" ? string.Empty
				: " from " + string.Join(" < ", new StackTrace(3, false).GetFrames()?.Take(6).Select(f => f.GetMethod()).Where(m => m != null).Select(m => $"{m.DeclaringType?.Name}.{m.Name}") ?? Enumerable.Empty<string>());
			Plugin.Log.LogInfo($"Skill {skill.Id} [{source}]: {skill.Current:0.##} -> {value:0.##}{limit}: {outcome}{caller}");
		}

		internal class SkillSetCurrentPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(BaseSkill).GetMethod(nameof(BaseSkill.SetCurrent), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPrefix]
			private static void Prefix(BaseSkill __instance, ref float value)
			{
				_inSetCurrent = true;
				value = Throttle(__instance, value, "SetCurrent");
			}

			[PatchFinalizer]
			private static void Finalizer() => _inSetCurrent = false;
		}

		// Catches writes that skip SetCurrent (UpdateFromAnother, UpdateFromInfo, other mods)
		internal class SkillCurrentSetterPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(BaseSkill).GetProperty(nameof(BaseSkill.Current), BindingFlags.Instance | BindingFlags.Public).GetSetMethod();

			[PatchPrefix]
			private static void Prefix(BaseSkill __instance, ref float value)
			{
				if (!_inSetCurrent)
				{
					value = Throttle(__instance, value, "direct write");
				}
			}
		}
	}
}
