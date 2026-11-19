using System.Reflection;
using EFT.UI.Ragfair;
using SPT.Reflection.Patching;

namespace CustomRpTweaks.Client.Patches
{
	// The flea market's refresh button (and F5) go quiet for 3 seconds after every refresh: a guard against hammering the live servers, which
	// means nothing in single player. After each refresh the "can refresh" flag and the button are put back at once.
	internal class FleaRefreshPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(OfferViewList).GetMethod(nameof(OfferViewList.RefreshButtonPressedHandler), BindingFlags.Instance | BindingFlags.Public);

		[PatchPostfix]
		private static void Postfix(OfferViewList __instance, ref bool ___canRefresh, ref float ___timer)
		{
			if (!Plugin.FleaNoRefreshCooldown.Value)
			{
				return;
			}

			___canRefresh = true;
			___timer = 0f;
			if (__instance._refreshButton != null)
			{
				__instance._refreshButton.interactable = true;
			}
		}
	}
}
