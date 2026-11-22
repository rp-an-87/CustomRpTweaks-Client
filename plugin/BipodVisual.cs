using System;
using EFT;
using UnityEngine;

namespace CustomRpTweaks.Client
{
	// After a reload or a weapon switch the bipod can look deployed while the game's own state says it is not (so the next press deploys
	// it "again"). The game keeps two things: the bipod state it acts on, and a flag in the weapon's animation that draws it. This checks
	// them a few times a second; when they disagree twice in a row it sets the animation flag to the real state. With verbose logging, every
	// change of the state, the flag or the procedural animation's own copy of the state is noted, to see what the mismatch looks like.
	internal static class BipodVisual
	{
		private static float _next;
		private static int _mismatches;
		private static string _last;

		internal static void Update()
		{
			if (Time.time < _next)
			{
				return;
			}

			_next = Time.time + 0.4f;
			try
			{
				var player = GamePlayerOwner.MyPlayer;
				var controller = player == null ? null : player.HandsController as Player.FirearmController;
				if (controller == null || !controller.HasBipod || controller.FirearmsAnimator == null || controller.FirearmsAnimator.Animator == null)
				{
					_mismatches = 0;
					_last = null;
					return;
				}

				var state = controller.BipodState;
				var flag = controller.FirearmsAnimator.Animator.GetBool(AnimationControllerParametersTable.BOOL_BIPOD);
				// The state text is only built for the log, so only with verbose logging on
				if (Plugin.VerboseLogging != null && Plugin.VerboseLogging.Value)
				{
					var used = player.ProceduralWeaponAnimation != null && player.ProceduralWeaponAnimation.IsBipodUsed;
					var now = $"state {state}, animation flag {flag}, procedural {used}";
					if (now != _last)
					{
						_last = now;
						Plugin.Verbose($"Bipod: {now}");
					}
				}

				if (state == flag)
				{
					_mismatches = 0;
					return;
				}

				if (++_mismatches >= 2 && Plugin.BipodVisualFix.Value)
				{
					controller.FirearmsAnimator.SetBipod(state);
					Plugin.Verbose($"Bipod: animation flag set to {state} to match the bipod state");
					_mismatches = 0;
				}
			}
			catch (Exception e)
			{
				Plugin.Verbose($"Bipod visual check: {e.Message}");
			}
		}
	}
}
