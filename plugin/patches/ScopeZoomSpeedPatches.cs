using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InputSystem;
using EFT.Settings;
using SPT.Reflection.Patching;
using UnityEngine;

namespace CustomRpTweaks.Client.Patches
{
	// The mouse wheel changes both the walk speed and the zoom of a smooth-zoom scope. While such a scope is being aimed
	// through and the scope zoom binding is held (for example Alt + wheel), the speed change is dropped, so zooming no
	// longer slows you down. The wheel on its own still changes the speed.
	internal class ScopeZoomSpeedPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(MoveInputTranslator).GetMethod(nameof(MoveInputTranslator.TranslateCommand), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPrefix]
		private static bool Prefix(MoveInputTranslator __instance, ECommand command, ref InputNode.ETranslateResult __result)
		{
			if (!Plugin.KeepSpeedWhileZooming.Value || (command != ECommand.ScrollNext && command != ECommand.ScrollPrevious))
			{
				return true;
			}

			var player = __instance._controller;
			if (player == null || !player.IsYourPlayer || player.HandsController == null || !player.HandsController.IsAiming)
			{
				return true;
			}

			var sight = player.ProceduralWeaponAnimation?.CurrentAimingMod;
			if (sight == null || !sight.AdjustableOpticData.IsAdjustableOptic)
			{
				return true;
			}

			if (!ZoomBindingHeld())
			{
				return true;
			}

			__result = InputNode.ETranslateResult.Ignore;
			return false;
		}

		// A wheel variant of the scope zoom in/out binding whose modifier keys are all held (a binding without modifiers is always held)
		private static bool ZoomBindingHeld()
		{
			var bindings = Singleton<SettingsManager>.Instance.Control.Settings.UserKeyBindings.Value;
			foreach (var group in bindings)
			{
				if (group.keyName != EGameKey.ScopeZoomIn && group.keyName != EGameKey.ScopeZoomOut)
				{
					continue;
				}

				foreach (var variant in group.variants)
				{
					if (variant.isAxis && variant.keyCode.TrueForAll(Input.GetKey))
					{
						return true;
					}
				}
			}

			return false;
		}
	}
}
