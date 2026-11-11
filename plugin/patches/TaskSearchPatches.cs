using System;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.Quests;
using EFT.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using SPT.Reflection.Patching;

namespace CustomRpTweaks.Client.Patches
{
	// A free-text search on the tasks screen, to the right of the Active / Operational tasks toggles. It narrows the list to the tasks whose
	// name, trader or location contain every word typed (any word in any of the three). The box is a copy of the game's own notes search
	// field; the red cross in it is a copy of the game's window close button and clears the text.
	internal static class TaskSearch
	{
		private static string[] _words = new string[0];
		private static TMP_InputField _field;
		private static GameObject _cross;
		private static readonly FieldInfo FilterField = typeof(TasksScreen).GetField("_questsAdditionalFilter", BindingFlags.Instance | BindingFlags.NonPublic);

		internal static bool Active => _words.Length > 0;

		internal static void Reset()
		{
			_words = new string[0];
		}

		internal static bool Matches(Quest quest)
		{
			var template = quest.Template;
			if (template == null)
			{
				return false;
			}

			var haystack = ((template.Name ?? "") + " " + (template.TraderId + " Nickname").Localized() + " " + (template.TraderId + " FullName").Localized() + " " + (template.LocationId + " Name").Localized()).ToLowerInvariant();
			return _words.All(word => haystack.Contains(word));
		}

		private static void TextChanged(TasksScreen screen, string text)
		{
			_words = (text ?? "").ToLowerInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
			if (_cross != null)
			{
				_cross.SetActive(!string.IsNullOrEmpty(text));
			}

			// Show the list again with the toggle's own filter; the ShowQuests patch adds the search to it
			var filter = FilterField?.GetValue(screen) as Func<Quest, bool>;
			if (filter != null && screen._tasksPanel != null)
			{
				screen._tasksPanel.ShowQuests(filter);
			}
		}

		// Makes the box once per screen and empties it every time the screen opens
		internal static void Prepare(TasksScreen screen)
		{
			try
			{
				if (_field != null && _field.transform.IsChildOf(screen.transform))
				{
					_field.SetTextWithoutNotify("");
					if (_cross != null)
					{
						_cross.SetActive(false);
					}

					return;
				}

				_field = null;
				_cross = null;
				Build(screen);
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning("Task search: could not add the box: " + e);
			}
		}

		private static void Build(TasksScreen screen)
		{
			if (screen._searchField == null || screen._dailyQuestsToggleSpawner == null)
			{
				Plugin.Log.LogWarning("Task search: the screen has no search field or toggle to copy from");
				return;
			}

			var anchor = (RectTransform)screen._dailyQuestsToggleSpawner.transform;
			var parent = anchor.parent;
			var copy = UnityEngine.Object.Instantiate(screen._searchField.gameObject, parent, false);
			copy.name = "TaskSearchField";
			copy.SetActive(true);
			var field = copy.GetComponent<TMP_InputField>();
			field.onValueChanged.RemoveAllListeners();
			field.SetTextWithoutNotify("");
			if (field.placeholder is TMP_Text placeholder)
			{
				placeholder.text = "Search task, trader or location";
			}

			var rect = (RectTransform)copy.transform;
			var height = Mathf.Max(anchor.rect.height * 0.8f, 30f);
			const float width = 460f;
			if (parent.GetComponent<LayoutGroup>() != null)
			{
				var layout = copy.GetComponent<LayoutElement>() ?? copy.AddComponent<LayoutElement>();
				layout.preferredWidth = width;
				layout.preferredHeight = height;
				layout.flexibleWidth = 0f;
				rect.SetSiblingIndex(anchor.GetSiblingIndex() + 1);
			}
			else
			{
				// No layout in the header: stand just right of the toggle, at its height
				var corners = new Vector3[4];
				anchor.GetWorldCorners(corners);
				rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
				rect.pivot = new Vector2(0f, 0.5f);
				rect.sizeDelta = new Vector2(width, height);
				rect.position = (corners[2] + corners[3]) / 2f + new Vector3(20f, 0f, 0f);
			}

			Plugin.Verbose("Task search: box added under " + parent.name + (parent.GetComponent<LayoutGroup>() != null ? " (layout)" : " (placed by hand)"));
			_field = field;
			AddCross(screen, field, height);
			field.onValueChanged.AddListener(text => TextChanged(screen, text));
		}

		private static void AddCross(TasksScreen screen, TMP_InputField field, float height)
		{
			var size = height * 0.7f;
			var context = ItemUiContext.Instance;
			var close = context != null && context._editBuildNameWindow != null ? context._editBuildNameWindow.CloseButton : null;
			GameObject cross;
			Button button;
			if (close != null)
			{
				cross = UnityEngine.Object.Instantiate(close.gameObject, field.transform, false);
				button = cross.GetComponent<Button>();
			}
			else
			{
				// The game has no close button to copy here: a plain red x
				cross = new GameObject("Cross", typeof(RectTransform), typeof(Image), typeof(Button));
				cross.transform.SetParent(field.transform, false);
				var label = new GameObject("x", typeof(RectTransform), typeof(TextMeshProUGUI));
				label.transform.SetParent(cross.transform, false);
				var text = label.GetComponent<TextMeshProUGUI>();
				text.text = "×";
				text.color = new Color(0.86f, 0.3f, 0.27f);
				text.alignment = TextAlignmentOptions.Center;
				text.fontSize = size;
				var labelRect = (RectTransform)label.transform;
				labelRect.anchorMin = Vector2.zero;
				labelRect.anchorMax = Vector2.one;
				labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
				cross.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.01f);
				button = cross.GetComponent<Button>();
			}

			cross.name = "Cross";
			var layoutIgnore = cross.GetComponent<LayoutElement>() ?? cross.AddComponent<LayoutElement>();
			layoutIgnore.ignoreLayout = true;
			var rect = (RectTransform)cross.transform;
			rect.anchorMin = rect.anchorMax = new Vector2(1f, 0.5f);
			rect.pivot = new Vector2(0f, 0.5f);
			rect.sizeDelta = new Vector2(size, size);
			rect.anchoredPosition = new Vector2(6f, 0f);
			button.onClick.RemoveAllListeners();
			button.onClick.AddListener(() =>
			{
				field.text = "";
				field.DeactivateInputField();
			});

			cross.SetActive(false);
			_cross = cross;
		}
	}

	internal static class TaskSearchPatches
	{
		// The search box is made (and emptied) each time the tasks screen opens
		internal class ScreenShowPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() =>
				typeof(TasksScreen).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly).First(m => m.Name == "Show" && m.GetParameters().Length == 5);

			[PatchPrefix]
			private static void Prefix()
			{
				TaskSearch.Reset();
			}

			[PatchPostfix]
			private static void Postfix(TasksScreen __instance)
			{
				if (Plugin.TaskSearchBox.Value)
				{
					TaskSearch.Prepare(__instance);
				}
			}
		}

		// Whatever list the game builds (a toggle, the screen opening, our box) is narrowed by the search
		internal class ShowQuestsPatch : ModulePatch
		{
			protected override MethodBase GetTargetMethod() => typeof(TasksPanel).GetMethod(nameof(TasksPanel.ShowQuests), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

			[PatchPrefix]
			private static void Prefix(ref Func<Quest, bool> questsAdditionalFilter)
			{
				if (!TaskSearch.Active || questsAdditionalFilter == null)
				{
					return;
				}

				var inner = questsAdditionalFilter;
				questsAdditionalFilter = quest => inner(quest) && TaskSearch.Matches(quest);
			}
		}
	}
}
