using System.Reflection;
using EFT.Animations;
using SPT.Reflection.Patching;
using UnityEngine;

namespace CustomRpTweaks.Client.Patches
{
	// The elite Aim Drills / weapon draw perk ("steady aim") sets a 3 second stiff window when you start aiming. In BreathEffector.Process that window
	// is checked before holding breath, and its sway is eased from 30% back up to 100% over the window, so holding breath during it is worse
	// than not holding it: the aim drifts as the window runs out. With this on, the window is skipped while the breath is held (holding breath
	// already steadies more than the perk does), and it picks up again if the breath is released inside the window.
	internal class SteadyAimBreathPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(BreathEffector).GetMethod(nameof(BreathEffector.Process), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPrefix]
		private static void Prefix(BreathEffector __instance, out float __state)
		{
			__state = -1f;
			if (Plugin.SteadyAimHoldBreathFix.Value && Time.time < __instance.StiffUntill && __instance.Physical != null && __instance.Physical.HoldingBreath)
			{
				__state = __instance.StiffUntill;
				__instance.StiffUntill = 0f;
			}
		}

		[PatchPostfix]
		private static void Postfix(BreathEffector __instance, float __state)
		{
			if (__state >= 0f)
			{
				__instance.StiffUntill = __state;
			}
		}
	}
}
