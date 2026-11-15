using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using SPT.Reflection.Patching;
using UnityEngine;

namespace CustomRpTweaks.Client
{
	internal enum RangefinderRounding
	{
		[System.ComponentModel.Description("Down")]
		Down,

		[System.ComponentModel.Description("Up")]
		Up
	}

	// Hotkey: sets the zero of the sight you are aiming through to the distance the rangefinder attached to your weapon (a RAPTAR) shows,
	// rounded down (or up, a setting) to a step the sight offers. It only listens to a rangefinder built into a device on the weapon in your
	// hands, that is switched on and whose display is in use; the handheld rangefinder is not used, and a display that shows no distance
	// gives no reading. It steps the zero the way the game's own zeroing keys do, so it only works while aiming.
	internal static class RangefinderZero
	{
		private static float _reading = -1f;
		private static TacticalRangeFinderController _source;
		private static string _lastUnparsed;

		// The display shows "0437" or "437.5", with markup around the digits (and "----" when nothing is hit)
		private static readonly Regex Number = new Regex(@"\d+(\.\d+)?", RegexOptions.Compiled);
		private static readonly Regex Markup = new Regex(@"<[^>]*>", RegexOptions.Compiled);

		internal static void Record(TacticalRangeFinderController controller)
		{
			// Only a rangefinder on the weapon in your hands counts (not the handheld one, nor another character's weapon)
			if (!IsOnYourWeapon(controller))
			{
				return;
			}

			var text = controller == null || controller._textOnDisplay == null ? null : controller._textOnDisplay.text;
			var match = string.IsNullOrEmpty(text) ? null : Number.Match(Markup.Replace(text, string.Empty));
			if (match != null && match.Success && float.TryParse(match.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var metres))
			{
				_reading = metres;
				_source = controller;
			}
			else
			{
				_reading = -1f;
				// What the display says when it has no distance ("----" is the usual); noted once per different text
				if (text != _lastUnparsed)
				{
					_lastUnparsed = text;
					Plugin.Verbose($"Rangefinder display shows no distance: \"{text}\"");
				}
			}
		}

		// The rangefinder is built into a tactical device (the RAPTAR) that is part of the weapon in your hands
		private static bool IsOnYourWeapon(TacticalRangeFinderController controller)
		{
			if (controller == null)
			{
				return false;
			}

			var device = controller.GetComponentInParent<TacticalComboVisualController>();
			var player = GamePlayerOwner.MyPlayer;
			var weapon = player == null ? null : (player.HandsController as Player.FirearmController)?.Item;
			return device != null && device.LightMod != null && weapon != null && weapon.GetAllItemsFromCollection().Any(item => item == device.LightMod.Item);
		}

		// The rangefinder that gave the reading is switched on, still on your weapon, and its display is in use (it draws only while the
		// weapon is up near the camera)
		private static bool SourceIsOn()
		{
			if (_source == null || !_source.isActiveAndEnabled || !IsOnYourWeapon(_source))
			{
				return false;
			}

			if (_source._canvas != null && !_source._canvas.enabled)
			{
				return false;
			}

			return _source.GetComponentInParent<TacticalComboVisualController>().LightMod.IsActive;
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

			if (_reading < 0f)
			{
				Say("no reading (switch on the rangefinder on your weapon and aim it at the target).");
				return;
			}

			if (!SourceIsOn())
			{
				Say("the rangefinder on your weapon is off or its display is not in use.");
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

			// The step at or below the reading (or at or above it, by the setting); past the ends of the list, the end
			var up = Plugin.RangefinderZeroRounding.Value == RangefinderRounding.Up;
			var target = -1;
			for (var i = 0; i < distances.Length; i++)
			{
				var fits = up ? distances[i] >= _reading : distances[i] <= _reading;
				if (fits && (target < 0 || (up ? distances[i] < distances[target] : distances[i] > distances[target])))
				{
					target = i;
				}
			}

			if (target < 0)
			{
				target = 0;
				for (var i = 1; i < distances.Length; i++)
				{
					if (up ? distances[i] > distances[target] : distances[i] < distances[target])
					{
						target = i;
					}
				}
			}

			var current = sight.GetCurrentOpticCalibrationPointIndex(scope);
			Plugin.Verbose($"Zero from rangefinder: reading {_reading:0.0} m, rounding {Plugin.RangefinderZeroRounding.Value}, steps [{string.Join(", ", distances)}], now at index {current}, wanted index {target}");
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
			Plugin.Verbose($"Zero from rangefinder: ended at index {now}");
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
