using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using EFT;
using SPT.Reflection.Patching;
using UnityEngine;

namespace CustomRpTweaks.Client.Patches
{
	// Troubleshooting aid: writes a line to the BepInEx log every time the player's sprint ends, with who called it and what the stamina and
	// conditions were. Sprint is switched off in Physical.Sprint(false), which MovementContext.EnableSprint, leg hits, state exits and mods all go through.
	// Running out of stamina removes the sprint consumption inside Physical's own update without calling it, so that is caught by Update() watching the flag.
	internal class SprintStopLogPatch : ModulePatch
	{
		private static float _lastLogTime = -10f;
		private static int _suppressed;
		private static int _lastCallFrame = -10;
		private static bool _wasSprinting;

		protected override MethodBase GetTargetMethod() => typeof(Physical).GetMethod(nameof(Physical.Sprint), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPrefix]
		private static void Prefix(Physical __instance, bool target)
		{
			try
			{
				if (!Plugin.LogSprintStops.Value || target || !__instance.Sprinting || !IsMine(__instance))
				{
					return;
				}
				_lastCallFrame = Time.frameCount;
				Write("Physical.Sprint(false) called", __instance, new StackTrace(2, false));
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning("Sprint stop log failed: " + e.Message);
			}
		}

		// Called every frame from Plugin.Update
		internal static void Update()
		{
			if (!Plugin.LogSprintStops.Value)
			{
				_wasSprinting = false;
				return;
			}
			var physical = GamePlayerOwner.MyPlayer?.Physical;
			if (physical == null)
			{
				_wasSprinting = false;
				return;
			}
			bool sprinting = physical.Sprinting;
			// The call that ends a sprint logs itself in the prefix during the same frame; only an end without one is new here
			if (_wasSprinting && !sprinting && _lastCallFrame < Time.frameCount - 1)
			{
				Write("sprint ended with no Sprint(false) call (stamina ran out or the consumption was removed)", physical, null);
			}
			_wasSprinting = sprinting;
		}

		private static bool IsMine(PhysicalBase physical)
		{
			var me = GamePlayerOwner.MyPlayer;
			return me != null && ReferenceEquals(me.Physical, physical);
		}

		private static void Write(string why, PhysicalBase physical, StackTrace trace)
		{
			// Mashing the key can end the sprint many times a second; keep the log readable
			if (Time.realtimeSinceStartup - _lastLogTime < 0.25f)
			{
				_suppressed++;
				return;
			}
			_lastLogTime = Time.realtimeSinceStartup;

			var me = GamePlayerOwner.MyPlayer;
			var sb = new StringBuilder();
			sb.Append("[SprintStop] ").Append(why);
			if (_suppressed > 0)
			{
				sb.Append(" (+").Append(_suppressed).Append(" more within 0.25 s since the last line)");
				_suppressed = 0;
			}
			sb.Append(" | stamina ").Append(physical.Stamina.Current.ToString("0.0"));
			sb.Append(" exhausted=").Append(physical.Stamina.Exhausted);
			sb.Append(" overweight=").Append(physical.Overweight.ToString("0.00"));
			sb.Append(" canSprint=").Append(physical.CanSprint);
			if (me != null)
			{
				sb.Append(" state=").Append(me.CurrentManagedState != null ? me.CurrentManagedState.Name.ToString() : "?");
				sb.Append(" conditions=").Append(me.MovementContext.PhysicalCondition);
				sb.Append(" prone=").Append(me.IsInPronePose);
			}
			if (trace != null)
			{
				sb.Append(" | via ");
				int shown = 0;
				foreach (var frame in trace.GetFrames() ?? new StackFrame[0])
				{
					var method = frame.GetMethod();
					if (method == null)
					{
						continue;
					}
					if (shown > 0)
					{
						sb.Append(" < ");
					}
					sb.Append(method.DeclaringType != null ? method.DeclaringType.Name + "." : "").Append(method.Name);
					if (++shown >= 12)
					{
						break;
					}
				}
			}
			Plugin.Log.LogInfo(sb.ToString());
		}
	}
}
