using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using EFT.Trading;
using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace CustomRpTweaks.Client.Patches
{
	// The BTR driver is flagged "available in raid" in the trader data, and the trading screen only lists traders that are not, so he never has a card
	// there (his standing cannot be seen anywhere). The list is built from the session's traders: his is added to the end. Nothing in the trader data
	// changes: he stays locked, and the game refuses to open the shop of a locked trader. His card shows the standing like the others.
	internal class BtrTraderCardPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(MerchantsList).GetMethod(nameof(MerchantsList.Show), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPrefix]
		private static void Prefix(ref IEnumerable<Trader> tradersList, IEftSession session)
		{
			if (!Plugin.BtrTraderCard.Value || tradersList == null || session?.Traders == null)
			{
				return;
			}
			var list = tradersList.ToList();
			var all = session.Traders.ToList();
			var btr = all.FirstOrDefault(trader => trader.Id == BtrCall.BtrTraderId);
			Plugin.Log.LogInfo($"BTR trader card: the trading screen lists {list.Count} traders ({string.Join(", ", list.Select(t => t.Settings.Nickname))}); the session has {all.Count}, the BTR driver among them: {btr != null}"
				+ (btr != null ? $" (available in raid {btr.Settings.AvailableInRaid}, unlocked {btr.Info.Unlocked}, listed {list.Contains(btr)})" : ""));
			if (btr != null && !list.Contains(btr))
			{
				list.Add(btr);
				tradersList = list;
			}
		}
	}

	// A locked trader's card hides its details group (the standing and the restock time); for the BTR driver the group is shown so the standing can be read
	internal class BtrTraderDetailsPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(TraderPanel).GetMethod(nameof(TraderPanel.UpdateTraderInfo), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(TraderPanel __instance)
		{
			if (!Plugin.BtrTraderCard.Value || __instance.TraderId != BtrCall.BtrTraderId)
			{
				return;
			}
			var details = Traverse.Create(__instance).Field("_detailsGroup").GetValue<UnityEngine.GameObject>();
			if (details != null)
			{
				details.SetActive(true);
			}
		}
	}
}
