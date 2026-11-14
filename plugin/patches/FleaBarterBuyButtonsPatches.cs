using System;
using System.Collections.Generic;
using System.Reflection;
using EFT.UI;
using EFT.UI.Ragfair;
using SPT.Reflection.Patching;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CustomRpTweaks.Client
{
	// Two small buttons (Buy missing, Buy all) under the price of a flea market barter offer, below "per item": the same quick buy as the menu
	// entries. Hovering one shows what it would cost from the current offers. The row is made once per price panel and reused when the offer
	// changes; it is hidden for offers it doesn't belong on.
	internal static class FleaBarterBuyButtons
	{
		private const string RowName = "QuickBuyButtons";
		private static readonly FieldInfo RagfairField = typeof(ItemUiContext).GetField("_ragfair", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

		internal static void Update(OfferItemPrice price, Offer offer, ItemUiContext uiContext, bool expanded)
		{
			var label = price._packLabel;
			if (label == null || label.transform.parent == null)
			{
				return;
			}

			var parent = label.transform.parent;
			var existing = parent.Find(RowName);
			var ragfair = uiContext == null || RagfairField == null ? null : RagfairField.GetValue(uiContext) as RagFair;
			var wanted = Plugin.FleaBarterBuyButtons.Value && expanded && ragfair != null && !ragfair.Disabled && offer != null && !offer.NotAvailable && !offer.OnlyMoney
				&& offer.Requirements != null && offer.Requirements.Length > 0 && offer.CanBeBought && offer.TotalItemCount > 0
				&& offer.User != null && offer.User.Id.ToString() != ragfair._tradingSession.Profile.Id.ToString();
			if (!wanted)
			{
				if (existing != null)
				{
					existing.gameObject.SetActive(false);
				}

				return;
			}

			var row = existing != null ? existing : Build(label, parent);
			row.gameObject.SetActive(true);
			Bind(row.GetChild(0), ragfair, offer, FleaBarterBuy.Mode.Missing);
			Bind(row.GetChild(1), ragfair, offer, FleaBarterBuy.Mode.All);
		}

		private static void Bind(Transform button, RagFair ragfair, Offer offer, FleaBarterBuy.Mode mode)
		{
			var click = button.GetComponent<Button>();
			click.onClick.RemoveAllListeners();
			click.onClick.AddListener(() => FleaBarterBuy.Start(ragfair, offer, mode));
			var hover = button.GetComponent<QuoteHover>();
			hover.Ragfair = ragfair;
			hover.Offer = offer;
			hover.Mode = mode;
		}

		private static Transform Build(TextMeshProUGUI label, Transform parent)
		{
			var row = new GameObject(RowName, typeof(RectTransform), typeof(LayoutElement), typeof(HorizontalLayoutGroup));
			row.transform.SetParent(parent, false);
			row.GetComponent<LayoutElement>().ignoreLayout = true;
			var layout = row.GetComponent<HorizontalLayoutGroup>();
			layout.spacing = 4f;
			layout.childControlWidth = layout.childControlHeight = true;
			layout.childForceExpandWidth = layout.childForceExpandHeight = true;

			// Just below the "per item" label, as wide as it or at least 180
			var labelRect = (RectTransform)label.transform;
			var rect = (RectTransform)row.transform;
			rect.anchorMin = labelRect.anchorMin;
			rect.anchorMax = labelRect.anchorMax;
			rect.pivot = new Vector2(0.5f, 1f);
			var width = Mathf.Max(labelRect.rect.width, 180f);
			rect.sizeDelta = new Vector2(labelRect.sizeDelta.x + (width - labelRect.rect.width), 22f);
			var centerX = labelRect.anchoredPosition.x + (0.5f - labelRect.pivot.x) * labelRect.rect.width;
			var bottom = labelRect.anchoredPosition.y - labelRect.pivot.y * labelRect.rect.height;
			rect.anchoredPosition = new Vector2(centerX, bottom - 6f);

			MakeButton(row.transform, "Buy missing", label);
			MakeButton(row.transform, "Buy all", label);
			return row.transform;
		}

		private static void MakeButton(Transform row, string text, TextMeshProUGUI like)
		{
			var button = new GameObject(text, typeof(RectTransform), typeof(Image), typeof(Button), typeof(QuoteHover));
			button.transform.SetParent(row, false);
			button.GetComponent<Image>().color = new Color(0.3f, 0.33f, 0.34f, 0.95f);
			var click = button.GetComponent<Button>();
			click.targetGraphic = button.GetComponent<Image>();
			var colors = click.colors;
			colors.normalColor = new Color(0.75f, 0.75f, 0.75f, 1f);
			colors.highlightedColor = Color.white;
			colors.pressedColor = new Color(0.55f, 0.55f, 0.55f, 1f);
			click.colors = colors;

			var caption = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
			caption.transform.SetParent(button.transform, false);
			var label = caption.GetComponent<TextMeshProUGUI>();
			label.font = like.font;
			label.text = text;
			label.color = Color.white;
			label.alignment = TextAlignmentOptions.Center;
			label.enableAutoSizing = true;
			label.fontSizeMin = 8f;
			label.fontSizeMax = 14f;
			label.raycastTarget = false;
			var rect = (RectTransform)caption.transform;
			rect.anchorMin = Vector2.zero;
			rect.anchorMax = Vector2.one;
			rect.offsetMin = new Vector2(2f, 1f);
			rect.offsetMax = new Vector2(-2f, -1f);
		}
	}

	// Hovering a button: the tooltip says what it would cost (worked out from the offers as they are now, kept for 20 seconds)
	internal class QuoteHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
	{
		private static readonly Dictionary<string, KeyValuePair<float, string>> Cache = new Dictionary<string, KeyValuePair<float, string>>();

		public RagFair Ragfair;
		public Offer Offer;
		public FleaBarterBuy.Mode Mode;
		private int _hover;

		public async void OnPointerEnter(PointerEventData eventData)
		{
			var token = ++_hover;
			var tooltip = ItemUiContext.Instance == null ? null : ItemUiContext.Instance.Tooltip;
			if (tooltip == null || Offer == null)
			{
				return;
			}

			var key = Offer.Id + ":" + Mode;
			if (Cache.TryGetValue(key, out var cached) && Time.realtimeSinceStartup - cached.Key < 20f)
			{
				tooltip.Show(cached.Value, null, 0f, 380f);
				return;
			}

			tooltip.Show("Working out the cost...", null, 0f, 380f);
			string text;
			try
			{
				text = await FleaBarterBuy.Quote(Ragfair, Offer, Mode);
				Cache[key] = new KeyValuePair<float, string>(Time.realtimeSinceStartup, text);
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Quick buy cost failed: {e}");
				text = "Couldn't work out the cost.";
			}

			// Still on the same hover?
			if (this != null && token == _hover && tooltip != null)
			{
				tooltip.Show(text, null, 0f, 380f);
			}
		}

		public void OnPointerExit(PointerEventData eventData)
		{
			_hover++;
			var tooltip = ItemUiContext.Instance == null ? null : ItemUiContext.Instance.Tooltip;
			if (tooltip != null)
			{
				tooltip.Close();
			}
		}
	}

	// The price panel of an offer is filled in here: the buttons follow the offer it shows
	internal class FleaBarterBuyButtonsPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(OfferItemPrice).GetMethod(nameof(OfferItemPrice.Show), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(OfferItemPrice __instance, Offer offer, ItemUiContext itemUiContext, bool expanded)
		{
			// Never let a problem with the buttons break the offer list
			try
			{
				FleaBarterBuyButtons.Update(__instance, offer, itemUiContext, expanded);
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Quick buy buttons: {e}");
			}
		}
	}
}
