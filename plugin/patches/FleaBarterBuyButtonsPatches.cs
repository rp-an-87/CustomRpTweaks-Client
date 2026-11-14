using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using EFT.UI;
using EFT.UI.Ragfair;
using SPT.Reflection.Patching;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CustomRpTweaks.Client
{
	// A small button (Buy & fulfil) under the price of a flea market barter offer, below "per item": the same quick buy as the menu
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

			var row = existing != null ? existing : Build(price, label, parent);
			row.gameObject.SetActive(true);
			Bind(row.GetChild(0), ragfair, offer);
		}

		private static void Bind(Transform button, RagFair ragfair, Offer offer)
		{
			var click = button.GetComponent<Button>();
			click.onClick.RemoveAllListeners();
			click.onClick.AddListener(() =>
			{
				// What the tooltip says is about to be out of date
				QuoteHover.ClearCache();
				var tooltip = ItemUiContext.Instance == null ? null : ItemUiContext.Instance.Tooltip;
				if (tooltip != null)
				{
					tooltip.Close();
				}

				FleaBarterBuy.Start(ragfair, offer);
			});
			var hover = button.GetComponent<QuoteHover>();
			hover.CacheKey = () => offer.Id + ":" + Plugin.FleaBarterBuyMode.Value + ":" + FleaBarterBuy.StateKey(FleaBarterBuy.BarterSource(ragfair, offer));
			hover.Quote = () => FleaBarterBuy.Quote(ragfair, FleaBarterBuy.BarterSource(ragfair, offer));
			var caption = button.GetComponentInChildren<TextMeshProUGUI>(true);
			if (caption != null)
			{
				caption.text = FleaBarterBuy.ButtonText.ToUpperInvariant();
			}
		}

		private static Transform Build(OfferItemPrice price, TextMeshProUGUI label, Transform parent)
		{
			var row = new GameObject(RowName, typeof(RectTransform), typeof(LayoutElement), typeof(HorizontalLayoutGroup), typeof(QuickBuyRow));
			row.transform.SetParent(parent, false);
			row.GetComponent<LayoutElement>().ignoreLayout = true;
			var layout = row.GetComponent<HorizontalLayoutGroup>();
			layout.spacing = 3f;
			layout.childControlWidth = layout.childControlHeight = true;
			layout.childForceExpandWidth = layout.childForceExpandHeight = true;

			var rect = (RectTransform)row.transform;
			rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
			rect.pivot = new Vector2(0.5f, 0f);
			rect.sizeDelta = new Vector2(QuickBuyRow.Width, QuickBuyRow.Height);
			var placer = row.GetComponent<QuickBuyRow>();
			placer.Label = (RectTransform)label.transform;
			placer.View = price.GetComponentInParent<OfferView>();

			MakeButton(row.transform, FleaBarterBuy.ButtonText.ToUpperInvariant(), label);
			return row.transform;
		}

		// Like the game's own flat dark buttons: dark panel, light text, lighter on hover
		private static void MakeButton(Transform row, string text, TextMeshProUGUI like)
		{
			var button = new GameObject(text, typeof(RectTransform), typeof(Image), typeof(Button), typeof(QuoteHover));
			button.transform.SetParent(row, false);
			var image = button.GetComponent<Image>();
			image.color = new Color(0.16f, 0.18f, 0.18f, 0.95f);
			var click = button.GetComponent<Button>();
			click.targetGraphic = image;
			var colors = click.colors;
			colors.normalColor = Color.white;
			colors.highlightedColor = new Color(1.6f, 1.6f, 1.6f, 1f);
			colors.pressedColor = new Color(0.7f, 0.7f, 0.7f, 1f);
			click.colors = colors;

			var caption = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI));
			caption.transform.SetParent(button.transform, false);
			var label = caption.GetComponent<TextMeshProUGUI>();
			label.font = like.font;
			label.text = text;
			label.color = new Color(0.82f, 0.81f, 0.74f, 1f);
			label.alignment = TextAlignmentOptions.Center;
			label.enableAutoSizing = true;
			label.fontSizeMin = 5f;
			label.fontSizeMax = 8f;
			label.enableWordWrapping = false;
			label.raycastTarget = false;
			var rect = (RectTransform)caption.transform;
			rect.anchorMin = Vector2.zero;
			rect.anchorMax = Vector2.one;
			rect.offsetMin = new Vector2(2f, 0f);
			rect.offsetMax = new Vector2(-2f, 0f);
		}
	}

	// Keeps the row just below the "per item" label, centred on it, without leaving the bottom of the offer's row. Worked out in world
	// space so it doesn't depend on how the game anchors its panels.
	internal class QuickBuyRow : MonoBehaviour
	{
		internal const float Width = 96f;
		internal const float Height = 12f;

		public RectTransform Label;
		public OfferView View;
		private readonly Vector3[] _corners = new Vector3[4];

		private void LateUpdate()
		{
			if (Label == null)
			{
				return;
			}

			var rect = (RectTransform)transform;
			var scale = rect.lossyScale.y;
			Label.GetWorldCorners(_corners);
			var centerX = (_corners[0].x + _corners[3].x) / 2f;
			var bottom = _corners[0].y - Height * scale;
			var viewRect = View == null ? null : View.transform as RectTransform;
			if (viewRect != null)
			{
				viewRect.GetWorldCorners(_corners);
				bottom = Mathf.Max(bottom, _corners[0].y + 1f * scale);
			}

			var target = new Vector3(centerX, bottom, rect.position.z);
			if ((rect.position - target).sqrMagnitude > 1e-6f)
			{
				rect.position = target;
			}
		}
	}

	// Hovering a button: the tooltip says what it would cost (worked out from the offers as they are now, kept for 10 seconds while nothing it depends on changes)
	internal class QuoteHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
	{
		private static readonly Dictionary<string, KeyValuePair<float, string>> Cache = new Dictionary<string, KeyValuePair<float, string>>();

		internal static void ClearCache() => Cache.Clear();

		// The key a quote is kept under (changes when what it depends on does) and how to work one out
		public Func<string> CacheKey;
		public Func<Task<string>> Quote;
		private int _hover;

		public async void OnPointerEnter(PointerEventData eventData)
		{
			var token = ++_hover;
			var tooltip = ItemUiContext.Instance == null ? null : ItemUiContext.Instance.Tooltip;
			if (tooltip == null || Quote == null || CacheKey == null)
			{
				return;
			}

			var key = CacheKey();
			if (Cache.TryGetValue(key, out var cached) && Time.realtimeSinceStartup - cached.Key < 10f)
			{
				tooltip.Show(cached.Value, null, 0f, 420f);
				return;
			}

			tooltip.Show("Calculating price...", null, 0f, 420f);
			string text;
			try
			{
				text = await Quote();
				Cache[key] = new KeyValuePair<float, string>(Time.realtimeSinceStartup, text);
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Quick buy cost failed: {e}");
				text = "Couldn't work out the price.";
			}

			// Still on the same hover?
			if (this != null && token == _hover && tooltip != null)
			{
				tooltip.Show(text, null, 0f, 420f);
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
