using System;
using System.Reflection;
using System.Text.RegularExpressions;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using SPT.Reflection.Patching;
using UnityEngine;

namespace CustomRpTweaks.Client
{
	// Hotkey: sets the zero of the sight you are aiming through to the distance your rangefinder last showed, rounded to the nearest step
	// the sight offers (half way between two steps goes to the longer one). The reading is taken from the rangefinder's own display
	// (the one on a weapon, such as the RAPTAR, or the handheld one) every time it measures; a reading older than the setting, or a
	// display that shows no distance, is not used. It steps the zero the way the game's own zeroing keys do, so it only works while aiming.
	internal static class RangefinderZero
	{
		private static float _reading = -1f;
		private static float _readingAt;
		private static TacticalRangeFinderController _source;

		// The display shows "0437" or "437.5", with markup around the digits (and "----" when nothing is hit)
		private static readonly Regex Number = new Regex(@"\d+(\.\d+)?", RegexOptions.Compiled);
		private static readonly Regex Markup = new Regex(@"<[^>]*>", RegexOptions.Compiled);

		internal static void Record(TacticalRangeFinderController controller)
		{
			var text = controller == null || controller._textOnDisplay == null ? null : controller._textOnDisplay.text;
			var match = string.IsNullOrEmpty(text) ? null : Number.Match(Markup.Replace(text, string.Empty));
			if (match != null && match.Success && float.TryParse(match.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var metres))
			{
				_reading = metres;
				_readingAt = Time.realtimeSinceStartup;
				_source = controller;
			}
			else
			{
				_reading = -1f;
			}
		}

		// The display that gave the reading is still on: its object is active, and a rangefinder built into a tactical device (the RAPTAR) has
		// that device switched on
		private static bool SourceIsOn()
		{
			if (_source == null || !_source.isActiveAndEnabled)
			{
				return false;
			}

			var device = _source.GetComponentInParent<TacticalComboVisualController>();
			return device == null || device.LightMod == null || device.LightMod.IsActive;
		}

		private static void Say(string message) => NotificationManager.DisplayMessageNotification("Zero from rangefinder: " + message);

		internal static void Update()
		{
			if (!Plugin.RangefinderZeroHotkey.Value.IsDown())
			{
				return;
			}

			var player = GamePlayerOwner.MyPlayer;
			var animation = player == null ? null : player.ProceduralWeaponAnimation;
			if (animation == null)
			{
				return;
			}

			if (_reading >= 0f && !SourceIsOn())
			{
				Say("the rangefinder is off.");
				return;
			}

			if (_reading < 0f || Time.realtimeSinceStartup - _readingAt > Plugin.RangefinderZeroMaxAge.Value)
			{
				Say("no recent reading (aim the rangefinder at the target first).");
				return;
			}

			var sight = animation.CurrentAimingMod;
			if (!animation.IsAiming || sight == null)
			{
				Say("aim down your sights first.");
				return;
			}

			var scope = sight.SelectedScopeIndex;
			var distances = sight.GetScopeCalibrationDistances(scope);
			if (distances == null || distances.Length == 0 || !sight.HasOpticCalibrationPoints(scope))
			{
				Say("this sight has no zero steps.");
				return;
			}

			// The nearest step; half way between two goes to the longer one
			var target = 0;
			for (var i = 1; i < distances.Length; i++)
			{
				var better = Math.Abs(distances[i] - _reading);
				var best = Math.Abs(distances[target] - _reading);
				if (better < best || (better == best && distances[i] > distances[target]))
				{
					target = i;
				}
			}

			var current = sight.GetCurrentOpticCalibrationPointIndex(scope);
			if (current == target)
			{
				Say($"already zeroed at {distances[target]} m (reading {_reading:0} m).");
				return;
			}

			// The game's own zeroing steps, one at a time, until the sight is on the wanted one
			for (var guard = 0; guard < 64 && sight.GetCurrentOpticCalibrationPointIndex(scope) != target; guard++)
			{
				if (sight.GetCurrentOpticCalibrationPointIndex(scope) < target)
				{
					animation.OpticCalibrationSwitchUp();
				}
				else
				{
					animation.OpticCalibrationSwitchDown();
				}
			}

			var now = sight.GetCurrentOpticCalibrationPointIndex(scope);
			Say(now == target ? $"zero set to {distances[target]} m (reading {_reading:0} m)." : $"could only reach {distances[now]} m (reading {_reading:0} m).");
		}
	}

	// Every measurement of a rangefinder display is noted
	internal class RangefinderReadingPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(TacticalRangeFinderController).GetMethod(nameof(TacticalRangeFinderController.MeasureDistance), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(TacticalRangeFinderController __instance)
		{
			try
			{
				RangefinderZero.Record(__instance);
			}
			catch (Exception e)
			{
				Plugin.Verbose($"Rangefinder reading: {e.Message}");
			}
		}
	}
}
