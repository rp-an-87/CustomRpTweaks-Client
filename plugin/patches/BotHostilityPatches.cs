using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace CustomRpTweaks.Client.Patches
{
	// Two fixes of vanilla bot hostility (moved here from the SAIN fork, 2026-11-19; they do not depend on SAIN).

	// Tagilla's "feel" branch (TagillaEnemyChooser.FindDangerEnemy, while he waits in his Ambush layer) picks the nearest non-scav enemy but only accepts
	// real players (!Person.IsAI), so he never takes an AI PMC that is shooting him. With the setting on, that test is replaced: AI PMCs count too.
	// Scavs stay excluded by the vanilla side check (Side != Savage). The setting is read at run time, so it switches live.
	internal class TagillaFeelIncludesAIPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(TagillaEnemyChooser), nameof(TagillaEnemyChooser.FindDangerEnemy));

		[PatchTranspiler]
		private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
		{
			var isAi = AccessTools.PropertyGetter(typeof(IPlayer), nameof(IPlayer.IsAI));
			var replacement = AccessTools.Method(typeof(TagillaFeelIncludesAIPatch), nameof(IsAIForFeel));
			var replaced = 0;
			var result = new List<CodeInstruction>();
			foreach (var instruction in instructions)
			{
				if (instruction.Calls(isAi))
				{
					var changed = new CodeInstruction(OpCodes.Call, replacement);
					changed.labels.AddRange(instruction.labels);
					changed.blocks.AddRange(instruction.blocks);
					result.Add(changed);
					replaced++;
					continue;
				}
				result.Add(instruction);
			}
			if (replaced == 0)
			{
				Plugin.Log.LogWarning("[BotHostility] TagillaEnemyChooser.FindDangerEnemy: IsAI test not found, the Tagilla fix has no effect");
			}
			else
			{
				Plugin.Log.LogInfo($"[BotHostility] TagillaEnemyChooser.FindDangerEnemy: {replaced} IsAI test(s) replaced");
			}
			return result;
		}

		// Stands in for IPlayer.IsAI: false for every player when the setting is on, so AI PMCs pass the "feel" filter.
		public static bool IsAIForFeel(IPlayer player)
		{
			if (Plugin.TagillaFeelsAI != null && Plugin.TagillaFeelsAI.Value)
			{
				return false;
			}
			return player.IsAI;
		}
	}

	// BotZoneGroups.AddBot makes every newly spawned bot an enemy of all groups of another side (causes addBotAtGroup, addBotNoGroup, initCauseEnemy)
	// without asking BotsGroup.IsPlayerEnemy. The BTR bot (shooterBTR, Savage side) therefore became an enemy of every PMC group at spawn, whatever
	// pmc.json lists as friendly. With the setting on, those adds are skipped when IsPlayerEnemy says the group should not be hostile to the BTR
	// (friendly list), and the BTR goes on the neutral list. Other causes (a PMC actually shot by the BTR, etc.) are untouched.
	internal class BtrNotEnemyOfGroupsPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotsGroup), nameof(BotsGroup.AddEnemy));

		[PatchPrefix]
		private static bool Prefix(BotsGroup __instance, IPlayer person, EBotEnemyCause cause, ref bool __result)
		{
			if (cause != EBotEnemyCause.addBotAtGroup && cause != EBotEnemyCause.addBotNoGroup && cause != EBotEnemyCause.initCauseEnemy)
			{
				return true;
			}
			if (Plugin.BtrNotEnemyOfPmcs == null || !Plugin.BtrNotEnemyOfPmcs.Value)
			{
				return true;
			}
			if (person == null || !person.IsAI)
			{
				return true;
			}
			var owner = person.AIData?.BotOwner;
			if (owner == null || owner.Profile?.Info?.Settings?.Role != WildSpawnType.shooterBTR)
			{
				return true;
			}
			if (__instance.IsPlayerEnemy(person))
			{
				return true;
			}
			__instance.AddNeutral(person);
			__result = false;
			return false;
		}
	}
}
