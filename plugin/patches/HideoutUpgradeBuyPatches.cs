using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT.Hideout;
using EFT.UI;
using EFT.UI.Ragfair;
using SPT.Reflection.Patching;
using UnityEngine;

namespace CustomRpTweaks.Client.Patches
{
	// A flea market button to the left of the item requirements of a hideout area upgrade, the same button as the craft screen's: it buys what the
	// upgrade is missing (the engine of FleaBarterBuy, the craft buy mode), and hovering it says what it would buy and cost.
	internal static class HideoutUpgradeBuy
	{
		private const string ButtonName = "CustomRpTweaksUpgradeBuy";
		private static readonly Color ShortTint = new Color(0.88f, 0.33f, 0.29f, 1f);
		private static bool _warnedNoTemplate;

		private static bool Available()
		{
			var session = ItemUiContext.Instance == null ? null : ItemUiContext.Instance.Session;
			var ragfair = session == null ? null : session.RagFair;
			return ragfair != null && !ragfair.Disabled && ragfair.Status != RagFair.ERagFairStatus.Banned;
		}

		// The craft screen's button is the template: any one the game has loaded
		private static ComplementaryButton FindTemplate(Transform container)
		{
			var all = Resources.FindObjectsOfTypeAll<ComplementaryButton>().Where(button => button != null && button._icon != null && button.name != ButtonName && !button.transform.IsChildOf(container)).ToList();
			return all.FirstOrDefault(button => button.GetComponentInParent<ProduceView>(true) != null) ?? all.FirstOrDefault();
		}

		// A found-in-raid requirement the stash doesn't cover can't be bought: the button says so by its colour
		private static bool ShortOfFir(FleaBarterBuy.Source src) =>
			src.Requirements.Any(requirement => src.NeedsFir(requirement) && src.Stash().Where(item => item.StringTemplateId == requirement.TemplateId && src.Suitable(item, requirement)).Sum(item => item.StackObjectsCount) < src.Required(requirement));

		private static void Tint(ComplementaryButton button, FleaBarterBuy.Source src)
		{
			var colour = ShortOfFir(src) ? ShortTint : Color.white;
			if (button._icon != null)
			{
				button._icon.color = colour;
			}

			if (button._background != null)
			{
				button._background.color = colour;
			}
		}

		private static void Attach(RequirementsPanel panel)
		{
			var container = panel._itemContainer;
			if (container == null)
			{
				return;
			}

			var existing = container.Find(ButtonName);
			var requirements = panel.Info == null ? new ItemRequirement[0] : panel.Info.OfType<ItemRequirement>().Where(requirement => !(requirement is ToolRequirement)).ToArray();
			if (!Plugin.HideoutUpgradeQuickBuy.Value || requirements.Length == 0)
			{
				if (existing != null)
				{
					existing.gameObject.SetActive(false);
				}

				return;
			}

			ComplementaryButton button;
			if (existing != null)
			{
				button = existing.GetComponent<ComplementaryButton>();
			}
			else
			{
				var template = FindTemplate(container);
				if (template == null)
				{
					if (!_warnedNoTemplate)
					{
						_warnedNoTemplate = true;
						Plugin.Log.LogWarning("Upgrade quick buy: no craft buy button loaded to copy yet (open a craft screen once); no button on the upgrade screen.");
					}

					return;
				}

				var clone = UnityEngine.Object.Instantiate(template.gameObject, container, false);
				clone.name = ButtonName;
				clone.transform.SetAsFirstSibling();
				button = clone.GetComponent<ComplementaryButton>();
			}

			var area = panel.AreaData;
			var source = FleaBarterBuy.UpgradeSource(requirements, null);
			source.Refresh = () => Refresh(requirements, button, source);

			button.Show(selected =>
			{
				if (!Available())
				{
					return;
				}

				QuoteHover.ClearCache();
				var tooltip = ItemUiContext.Instance.Tooltip;
				if (tooltip != null)
				{
					tooltip.Close();
				}

				FleaBarterBuy.StartUpgrade(FleaBarterBuy.UpgradeSource(requirements, source.Refresh));
			}, null, null, true, 0);
			button.SetSelectedStatus(false);
			button.SetTooltipMessages(() => string.Empty, () => string.Empty);
			button.gameObject.SetActive(true);

			var available = Available();
			button.SetUnlockStatus(available);
			Tint(button, source);

			var hover = button.GetComponent<QuoteHover>() ?? button.gameObject.AddComponent<QuoteHover>();
			hover.enabled = available;
			hover.CacheKey = () => "upgrade:" + area.Template.Type + ":" + area.CurrentLevel + ":" + Plugin.HideoutBuyMode.Value + ":" + FleaBarterBuy.StateKey(FleaBarterBuy.UpgradeSource(requirements, null));
			hover.Quote = () =>
			{
				var current = FleaBarterBuy.UpgradeSource(requirements, null);
				Tint(button, current);
				return FleaBarterBuy.Quote(ItemUiContext.Instance.Session.RagFair, current);
			};
		}

		// After a purchase: the hideout learns of the new stash items, the requirements are counted again and the screen redrawn
		private static void Refresh(ItemRequirement[] requirements, ComplementaryButton button, FleaBarterBuy.Source src)
		{
			try
			{
				var hideout = Singleton<HideoutRepresentation>.Instance;
				if (hideout == null)
				{
					return;
				}

				hideout.method_25();
				var stash = hideout.AllStashItems.Where(item => item != null).ToList();
				foreach (var requirement in requirements)
				{
					requirement.Test(stash);
				}

				if (button != null)
				{
					Tint(button, src);
				}
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning("Upgrade quick buy: couldn't refresh the upgrade screen: " + e.Message);
			}
		}

		internal class RequirementsPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(RequirementsPanel).GetMethod(nameof(RequirementsPanel.ShowContents), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			// ShowContents is async: the button goes in once the requirements are drawn
			[PatchPostfix]
			private static void Postfix(RequirementsPanel __instance, ref Task __result)
			{
				__result = Wrap(__result, __instance);
			}

			private static async Task Wrap(Task original, RequirementsPanel panel)
			{
				await original;
				try
				{
					Attach(panel);
				}
				catch (Exception e)
				{
					Plugin.Log.LogError($"Upgrade quick buy button: {e}");
				}
			}
		}
	}
}
