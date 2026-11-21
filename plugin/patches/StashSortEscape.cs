using System.Reflection;
using EFT;
using EFT.Hideout;
using EFT.InputSystem;
using EFT.UI;
using SPT.Reflection.Patching;

namespace CustomRpTweaks.Client.Patches
{
	// Esc closes the stash sort window and nothing else. The game's stash screens answer Esc by closing the whole screen, so they are asked
	// first: while the window is up they let it have the key and report it as handled.
	internal static class StashSortEscape
	{
		internal static bool Handle(ECommand command, ref InputNode.ETranslateResult result)
		{
			if (!command.IsCommand(ECommand.Escape) || !StashSortWindow.ConsumeEscape())
			{
				return false;
			}

			result = InputNode.ETranslateResult.BlockAll;
			return true;
		}

		internal class InventoryScreenPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(InventoryScreen).GetMethod(nameof(InventoryScreen.TranslateCommand), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPrefix]
			private static bool Prefix(ECommand command, ref InputNode.ETranslateResult __result) => !Handle(command, ref __result);
		}

		internal class HideoutOverlayPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(HideoutScreenOverlay).GetMethod(nameof(HideoutScreenOverlay.TranslateCommand), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPrefix]
			private static bool Prefix(ECommand command, ref InputNode.ETranslateResult __result) => !Handle(command, ref __result);
		}
	}
}
