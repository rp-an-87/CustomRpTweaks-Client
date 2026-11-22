using System;
using System.Linq;
using CustomRpTweaks.Client.Patches;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.Builds;
using EFT.UI.DragAndDrop;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CustomRpTweaks.Client
{
	// The window that sets the stash sort rows, opened by a right-click on the stash's sort button: a copy of one of the game's own windows
	// (the one for naming a build) with its caption, red cross and border kept and the rest hidden, filled with two sliders and a checkbox.
	// The shading over the stash follows the sliders while they move; the settings are saved when the window closes.
	internal static class StashSortWindow
	{
		private static readonly Color Text = new Color(0.86f, 0.87f, 0.84f);
		private static readonly Color Dim = new Color(0.6f, 0.61f, 0.58f);
		private static readonly Color Track = new Color(0.07f, 0.075f, 0.075f);
		private static readonly Color Red = new Color(0.9f, 0.25f, 0.25f);
		private static readonly Color Blue = new Color(0.25f, 0.55f, 1f);

		private static EditBuildNameWindow _window;
		private static bool _saveOnSet;

		private static TMP_FontAsset _font;
		private static float _unit = 20f;

		internal static void Open(CompoundItem stash)
		{
			if (_window != null)
			{
				_window.WindowTransform.SetAsLastSibling();
				return;
			}

			try
			{
				var context = ItemUiContext.Instance;
				if (context == null || context._editBuildNameWindow == null || context._infoWindowsContainer == null)
				{
					Plugin.Log.LogWarning("Stash sort window: the game's window template is not available here");
					return;
				}

				var window = UnityEngine.Object.Instantiate(context._editBuildNameWindow, context._infoWindowsContainer, false);
				window.name = "StashSort Window";
				var windowContext = ((Window<EditBuildNameWindowContext>)window).Show();
				_font = window.Caption.font;
				_unit = window.Caption.fontSize > 4f ? window.Caption.fontSize : 20f;
				window.Caption.text = "Stash sort rows";
				float top = Hollow(window);
				window.WindowTransform.SetAsLastSibling();
				context.RegisterWindow<EditBuildNameWindowContext>(window, windowContext);

				// The file is written once, when the window closes, not at every step of a slider
				_saveOnSet = Plugin.Instance.Config.SaveOnConfigSet;
				Plugin.Instance.Config.SaveOnConfigSet = false;
				windowContext.OnClose += () =>
				{
					if (window != null)
					{
						UnityEngine.Object.Destroy(window.gameObject);
					}

					_window = null;
					StashSortOverlayPatch.Pinned = false;
					StashSortOverlayPatch.Hide();
					Plugin.Instance.Config.SaveOnConfigSet = _saveOnSet;
					Plugin.Instance.Config.Save();
				};

				_window = window;
				StashSortOverlayPatch.Pinned = true;
				StashSortOverlayPatch.Show(stash);
				Build(window.WindowTransform, top, stash.Grids.Length > 0 ? stash.Grids[0].GridHeight : 40);
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Stash sort window failed: {e}");
			}
		}

		// Everything of the copy but its caption and border hidden, its own layout off, a canvas of its own. Returns the caption's height.
		private static float Hollow(EditBuildNameWindow window)
		{
			var frame = window.WindowTransform;
			window._inputField.gameObject.SetActive(false);
			window._saveButtonSpawner.gameObject.SetActive(false);
			window._placeholderText.gameObject.SetActive(false);
			var caption = window._captionPanel != null ? window._captionPanel.transform : null;
			for (int i = 0; i < frame.childCount; i++)
			{
				var child = frame.GetChild(i);
				if (child.name == "Border" || (caption != null && (child == caption || caption.IsChildOf(child))))
				{
					continue;
				}

				child.gameObject.SetActive(false);
			}

			var fitter = frame.GetComponent<ContentSizeFitter>();
			if (fitter != null)
			{
				fitter.enabled = false;
			}

			foreach (var group in frame.GetComponents<LayoutGroup>())
			{
				group.enabled = false;
			}

			if (frame.GetComponent<Canvas>() == null)
			{
				frame.gameObject.AddComponent<Canvas>();
			}

			if (frame.GetComponent<GraphicRaycaster>() == null)
			{
				frame.gameObject.AddComponent<GraphicRaycaster>();
			}

			return window._captionPanel != null ? ((RectTransform)window._captionPanel.transform).rect.height : 0f;
		}

		private static void Build(RectTransform frame, float captionHeight, int maxRows)
		{
			float top = Mathf.Max(captionHeight, 30f);
			var root = Node("Stash Sort Root", frame);
			root.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
			// Whatever a copied game control draws outside its place is cut off at the window
			root.gameObject.AddComponent<RectMask2D>();
			Stretch(root, 0f, 0f, 0f, top);
			var column = root.gameObject.AddComponent<VerticalLayoutGroup>();
			column.spacing = 6f;
			column.padding = new RectOffset(16, 16, 12, 14);
			column.childControlWidth = true;
			column.childControlHeight = true;
			column.childForceExpandWidth = true;
			column.childForceExpandHeight = false;

			Section(root, "Ignore the top rows", "Items in these rows stay where they are, and sorting never puts anything there.", maxRows, Blue,
				() => Plugin.IgnoreTopRows.Value,
				value => Plugin.IgnoreTopRows.Value = value);

			Gap(root, 8f);

			Section(root, "Keep the top rows free", "Sorting places items below these rows, and only uses them when everything else is full.", maxRows, Red,
				() => StashSortReservePatches.ReservedRows,
				value =>
				{
					Plugin.KeepTopRowsFree.Value = value > 0;
					if (value > 0)
					{
						Plugin.TopRowsToKeepFree.Value = value;
					}
				});

			Gap(root, 8f);

			Check(root, "Sort items that reach below the ignored rows", "Off: an item that starts in an ignored row and reaches below it stays put too.",
				Plugin.SortItemsReachingOut.Value, on => Plugin.SortItemsReachingOut.Value = on);

			Gap(root, 6f);
			Label(root, "Rows are counted from 0 at the top. 0 turns a rule off. Esc or the cross closes this window.", 0.75f, Dim, wrap: true);

			LayoutRebuilder.ForceRebuildLayoutImmediate(root);
			float width = Mathf.Min(_unit * 30f, ((RectTransform)frame.parent).rect.width * 0.9f);
			frame.sizeDelta = new Vector2(width, top + LayoutUtility.GetPreferredHeight(root) + 4f);
			Place(frame);
		}

		// Left of the middle: the stash is on the right, and the shading on it should stay in view
		private static void Place(RectTransform frame)
		{
			var area = ((RectTransform)frame.parent).rect;
			frame.anchorMin = frame.anchorMax = new Vector2(0.5f, 0.5f);
			frame.pivot = new Vector2(0.5f, 0.5f);
			frame.anchoredPosition = new Vector2(-area.width * 0.2f, area.height * 0.05f);
		}

		// A heading, what the rule does, and a slider with the value beside it
		private static void Section(Transform parent, string title, string what, int maxRows, Color color, Func<int> get, Action<int> set)
		{
			Label(parent, title, 1f, Text, bold: true);
			Label(parent, what, 0.8f, Dim, wrap: true);
			var value = Label(parent, Describe(Mathf.Clamp(get(), 0, maxRows)), 0.85f, color, align: TextAlignmentOptions.Left);
			Action<int> apply = number =>
			{
				set(number);
				value.text = Describe(number);
				StashSortOverlayPatch.Refresh();
			};

			int start = Mathf.Clamp(get(), 0, maxRows);
			if (!GameSlider(parent, maxRows, start, apply))
			{
				var line = Node("Slider Row", parent);
				Size(line, height: _unit * 1.8f, flexWidth: 1f);
				var own = MakeSlider(line, maxRows, start, color, number => apply(number));
				Stretch((RectTransform)own.transform, 0f, 0f, 0f, 0f);
			}
		}

		// What a copied game control is made of, for the log (verbose logging), so its layout can be fixed from facts
		private static void Dump(Transform part, string indent = "")
		{
			if (Plugin.VerboseLogging == null || !Plugin.VerboseLogging.Value)
			{
				return;
			}

			var rect = part as RectTransform;
			var kinds = string.Join(",", part.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name).ToArray());
			Plugin.Verbose($"Stash sort window copy: {indent}{part.name} [{kinds}] size {(rect != null ? rect.rect.size.ToString() : "-")} active {part.gameObject.activeSelf}");
			for (int i = 0; i < part.childCount; i++)
			{
				Dump(part.GetChild(i), indent + "  ");
			}
		}

		// Esc closes the window and nothing else (the stash screens ask first, see StashSortEscape)
		internal static bool ConsumeEscape()
		{
			if (_window == null)
			{
				return false;
			}

			_window.CloseAsync();
			return true;
		}

		private static float _checkedAt;

		// Leaving the stash screen closes the window too
		internal static void Update()
		{
			if (_window == null || Time.unscaledTime - _checkedAt < 0.5f)
			{
				return;
			}

			_checkedAt = Time.unscaledTime;
			if (UnityEngine.Object.FindObjectOfType<GridSortPanel>() == null)
			{
				_window.CloseAsync();
			}
		}

		private static NumberSlider _sliderTemplate;
		private static UpdatableToggle _toggleTemplate;

		// The templates are the controls of the game's settings screen (a prefab asset when there is one, so no state of a screen that used it)
		private static T Template<T>() where T : Component
		{
			var found = Resources.FindObjectsOfTypeAll<T>();
			return found.FirstOrDefault(part => part != null && !part.gameObject.scene.IsValid()) ?? found.FirstOrDefault(part => part != null);
		}

		// A copy of the settings screen's number slider (the slider with the value box), whole numbers from 0. False when the game has none here.
		private static bool GameSlider(Transform parent, int max, int start, Action<int> changed)
		{
			try
			{
				_sliderTemplate = _sliderTemplate != null ? _sliderTemplate : Template<NumberSlider>();
				if (_sliderTemplate == null)
				{
					return false;
				}

				if (_sliderTemplate._slider == null || _sliderTemplate._valueInput == null)
				{
					return false;
				}

				// The game lays its slider out for a full settings row, so only the slider and its value box are taken from the copy and set
				// in a row of our own; the rest of the copy (labels, backgrounds) goes
				var row = Node("Slider Row", parent);
				var group = row.gameObject.AddComponent<HorizontalLayoutGroup>();
				group.spacing = 10f;
				group.childAlignment = TextAnchor.MiddleLeft;
				group.childControlWidth = true;
				group.childControlHeight = true;
				group.childForceExpandWidth = false;
				group.childForceExpandHeight = false;
				Size(row, height: _unit * 2f, flexWidth: 1f);
				var copy = UnityEngine.Object.Instantiate(_sliderTemplate.gameObject, row, false);
				copy.name = "Number Slider";
				copy.SetActive(true);
				var slider = copy.GetComponent<NumberSlider>();
				Dump(copy.transform);
				// The slider is the copy's root (track, notches and handle are its children); the value box is a child of it, taken out
				// to sit beside it
				var box = (RectTransform)slider._valueInput.transform;
				box.SetParent(row, false);
				var track = copy.GetComponent<LayoutElement>() ?? copy.AddComponent<LayoutElement>();
				track.ignoreLayout = false;
				track.minWidth = _unit * 6f;
				track.preferredWidth = _unit * 6f;
				track.flexibleWidth = 1f;
				track.minHeight = track.preferredHeight = _unit * 1.6f;
				Size(box, width: _unit * 4f, height: _unit * 1.8f);

				slider._slider.wholeNumbers = true;
				slider.Show(0f, Mathf.Max(1, max), "0");
				slider.UpdateValue(start, true, 0f, Mathf.Max(1, max));
				slider.Bind(value => changed((int)Mathf.Round(value)));
				return true;
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning($"Stash sort window: the game's slider could not be copied, drawing one: {e.Message}");
				return false;
			}
		}

		private static string Describe(int rows) => rows <= 0 ? "Off" : rows == 1 ? "1 row (0)" : $"{rows} rows (0-{rows - 1})";

		private static Slider MakeSlider(Transform parent, int max, int value, Color fillColor, Action<int> changed)
		{
			var rect = Node("Slider", parent);
			Size(rect, flexWidth: 1f, height: _unit * 1.6f);
			var hit = rect.gameObject.AddComponent<Image>();
			hit.color = new Color(0f, 0f, 0f, 0f);

			var background = Node("Background", rect);
			background.anchorMin = new Vector2(0f, 0.5f);
			background.anchorMax = new Vector2(1f, 0.5f);
			background.sizeDelta = new Vector2(0f, 8f);
			var track = background.gameObject.AddComponent<Image>();
			track.color = Track;

			var fillArea = Node("Fill Area", rect);
			fillArea.anchorMin = new Vector2(0f, 0.5f);
			fillArea.anchorMax = new Vector2(1f, 0.5f);
			fillArea.sizeDelta = new Vector2(0f, 8f);
			var fill = Node("Fill", fillArea);
			fill.sizeDelta = Vector2.zero;
			var fillImage = fill.gameObject.AddComponent<Image>();
			fillImage.color = fillColor;

			var handleArea = Node("Handle Slide Area", rect);
			Stretch(handleArea, 8f, 0f, 8f, 0f);
			var handle = Node("Handle", handleArea);
			handle.sizeDelta = new Vector2(16f, _unit * 1.4f);
			var handleImage = handle.gameObject.AddComponent<Image>();
			handleImage.color = new Color(0.78f, 0.79f, 0.76f);

			var slider = rect.gameObject.AddComponent<Slider>();
			slider.fillRect = fill;
			slider.handleRect = handle;
			slider.targetGraphic = handleImage;
			slider.direction = Slider.Direction.LeftToRight;
			slider.minValue = 0;
			slider.maxValue = Mathf.Max(1, max);
			slider.wholeNumbers = true;
			slider.SetValueWithoutNotify(value);
			slider.onValueChanged.AddListener(number => changed((int)number));
			return slider;
		}

		// A copy of the settings screen's tick box. False when the game has none here.
		private static bool GameToggle(RectTransform box, bool on, Action<bool> changed)
		{
			try
			{
				_toggleTemplate = _toggleTemplate != null ? _toggleTemplate : Template<UpdatableToggle>();
				if (_toggleTemplate == null)
				{
					return false;
				}

				var copy = UnityEngine.Object.Instantiate(_toggleTemplate.gameObject, box, false);
				copy.name = "Tick Box";
				copy.SetActive(true);
				Dump(copy.transform);
				// The game's tick box carries its own caption: ours is beside it
				foreach (var text in copy.GetComponentsInChildren<TMP_Text>(true))
				{
					UnityEngine.Object.Destroy(text.gameObject);
				}

				box.gameObject.AddComponent<RectMask2D>();
				// The game's row is 800 wide with the frame at its far right: the root and the frame both fill our small box
				var layout = copy.GetComponent<LayoutElement>();
				if (layout != null)
				{
					UnityEngine.Object.Destroy(layout);
				}

				Stretch((RectTransform)copy.transform, 0f, 0f, 0f, 0f);
				var frame = copy.transform.Find("Background") as RectTransform;
				if (frame != null)
				{
					Stretch(frame, 0f, 0f, 0f, 0f);
				}

				var toggle = copy.GetComponent<UpdatableToggle>();
				toggle.UpdateValue(on, false);
				toggle.Bind(changed);
				return true;
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning($"Stash sort window: the game's tick box could not be copied, drawing one: {e.Message}");
				return false;
			}
		}

		private static void Check(Transform parent, string title, string what, bool on, Action<bool> changed)
		{
			var line = Node("Check Row", parent);
			var group = line.gameObject.AddComponent<HorizontalLayoutGroup>();
			group.spacing = 10f;
			group.childAlignment = TextAnchor.UpperLeft;
			group.childControlWidth = true;
			group.childControlHeight = true;
			group.childForceExpandWidth = false;
			group.childForceExpandHeight = false;

			var box = Node("Box", line);
			Size(box, _unit * 1.6f, _unit * 1.6f).minWidth = _unit * 1.6f;
			if (!GameToggle(box, on, changed))
			{
				var boxImage = box.gameObject.AddComponent<Image>();
				boxImage.color = new Color(0.4f, 0.41f, 0.39f);
				var inside = Node("Inside", box);
				Stretch(inside, 1f, 1f, 1f, 1f);
				var insideImage = inside.gameObject.AddComponent<Image>();
				insideImage.color = Track;
				var mark = Node("Mark", inside);
				Stretch(mark, 4f, 4f, 4f, 4f);
				var markImage = mark.gameObject.AddComponent<Image>();
				markImage.color = new Color(0.55f, 0.8f, 0.4f);
				var toggle = box.gameObject.AddComponent<Toggle>();
				toggle.targetGraphic = boxImage;
				toggle.graphic = markImage;
				toggle.SetIsOnWithoutNotify(on);
				markImage.enabled = on;
				toggle.onValueChanged.AddListener(state => changed(state));
			}

			var texts = Node("Texts", line);
			var stack = texts.gameObject.AddComponent<VerticalLayoutGroup>();
			stack.spacing = 2f;
			stack.childControlWidth = true;
			stack.childControlHeight = true;
			stack.childForceExpandWidth = true;
			stack.childForceExpandHeight = false;
			Size(texts, flexWidth: 1f);
			Label(texts, title, 0.95f, Text);
			Label(texts, what, 0.8f, Dim, wrap: true);
		}

		private static RectTransform Node(string name, Transform parent)
		{
			var go = new GameObject(name, typeof(RectTransform));
			go.layer = parent.gameObject.layer;
			go.transform.SetParent(parent, false);
			return (RectTransform)go.transform;
		}

		private static void Stretch(RectTransform rect, float left, float bottom, float right, float top)
		{
			rect.anchorMin = Vector2.zero;
			rect.anchorMax = Vector2.one;
			rect.offsetMin = new Vector2(left, bottom);
			rect.offsetMax = new Vector2(-right, -top);
		}

		private static LayoutElement Size(Component part, float width = -1f, float height = -1f, float flexWidth = -1f)
		{
			var element = part.GetComponent<LayoutElement>() ?? part.gameObject.AddComponent<LayoutElement>();
			if (width >= 0f)
			{
				element.preferredWidth = width;
			}

			if (height >= 0f)
			{
				element.preferredHeight = height;
				element.minHeight = height;
			}

			if (flexWidth >= 0f)
			{
				element.flexibleWidth = flexWidth;
			}

			return element;
		}

		private static void Gap(Transform parent, float height) => Size(Node("Gap", parent), height: height);

		private static TextMeshProUGUI Label(Transform parent, string text, float size, Color color, bool bold = false, TextAlignmentOptions align = TextAlignmentOptions.Left, bool wrap = false)
		{
			var rect = Node("Label", parent);
			var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
			if (_font != null)
			{
				label.font = _font;
			}

			label.fontSize = _unit * size;
			label.color = color;
			label.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
			label.alignment = align;
			label.enableWordWrapping = wrap;
			label.overflowMode = TextOverflowModes.Overflow;
			label.raycastTarget = false;
			label.text = text ?? "";
			return label;
		}

		private static void PushButton(Transform parent, string text, Action click, float width, float height)
		{
			var rect = Node("Button", parent);
			var image = rect.gameObject.AddComponent<Image>();
			image.color = new Color(0.2f, 0.21f, 0.21f);
			var edge = rect.gameObject.AddComponent<Outline>();
			edge.effectColor = new Color(0.33f, 0.34f, 0.32f);
			edge.effectDistance = new Vector2(1f, 1f);
			var button = rect.gameObject.AddComponent<Button>();
			button.targetGraphic = image;
			var colors = button.colors;
			colors.normalColor = Color.white;
			colors.highlightedColor = new Color(1.35f, 1.35f, 1.35f, 1f);
			colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
			colors.fadeDuration = 0.05f;
			button.colors = colors;
			button.onClick.AddListener(() => click());
			var group = rect.gameObject.AddComponent<HorizontalLayoutGroup>();
			group.padding = new RectOffset(16, 16, 6, 6);
			group.childAlignment = TextAnchor.MiddleCenter;
			group.childControlWidth = true;
			group.childControlHeight = true;
			group.childForceExpandWidth = false;
			group.childForceExpandHeight = false;
			Label(rect, text, 0.9f, Text, align: TextAlignmentOptions.Center);
			Size(rect, width, height);
		}
	}
}
