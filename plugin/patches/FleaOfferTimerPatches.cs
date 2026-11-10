using System.Reflection;
using EFT.UI.Ragfair;
using SPT.Reflection.Patching;

namespace CustomRpTweaks.Client.Patches
{
	// Listing an offer makes the game set the expiry timers of only the new offer (RagFair.UpdateMyOffers ->
	// SetTimersForMyOffers), which first cancels the timers of every older offer. Those offers are then only removed by the
	// countdown of the open flea market screen; with the screen closed they never leave your offer list, the counter stays
	// at the maximum and you cannot list. Re-arming the timers for all offers after the update fixes it (the old timers are
	// cancelled first, so there are no duplicates).
	internal class FleaOfferTimerPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(RagFair).GetMethod(nameof(RagFair.UpdateMyOffers), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(RagFair __instance, Offer[] newOffers)
		{
			if (!Plugin.FleaOfferTimerFix.Value || newOffers == null || newOffers.Length == 0)
			{
				return;
			}

			__instance.method_14();
			Plugin.Verbose("Flea: re-armed the expiry timers of all " + __instance.MyOffers.Count + " own offers");
		}
	}
}
