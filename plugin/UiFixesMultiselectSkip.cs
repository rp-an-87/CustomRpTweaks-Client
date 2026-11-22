using System;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace CustomRpTweaks.Client
{
	// UI Fixes' multiselect decides which buttons its menu offers by building the item menu of EVERY selected item, once for each button it asks
	// about (MultiSelect.InteractionCount / InteractionAvailable), and only reads whether a button of the game's own is available. Each of those
	// builds ran our menu additions (hand in, dismantle, empty, quick buy), so right-clicking a big selection cost (items x buttons) of them for
	// nothing. While UI Fixes asks, our additions are skipped (see UiFixesHints.SkipOurMenuEntries). Reached by name, like SelectAllOfKind: when
	// a UI Fixes update renames them this logs a warning and changes nothing.
	internal static class UiFixesMultiselectSkip
	{
		private static readonly string[] Names = { "InteractionAvailable", "InteractionCount" };

		internal static void Setup()
		{
			try
			{
				if (!Chainloader.PluginInfos.TryGetValue(SelectAllOfKind.TargetGuid, out var info) || info.Instance == null)
				{
					return;
				}

				var type = info.Instance.GetType().Assembly.GetType("UIFixes.MultiSelect");
				var methods = type == null ? new MethodInfo[0] : type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
					.Where(method => Names.Contains(method.Name)).ToArray();
				if (methods.Length == 0)
				{
					Plugin.Log.LogWarning("UI Fixes multiselect menu queries were not found in the expected form: right-clicking a big selection stays slower.");
					return;
				}

				var harmony = new Harmony(Plugin.PluginGuid + ".multiselect-skip");
				var enter = new HarmonyMethod(typeof(UiFixesMultiselectSkip), nameof(Enter));
				var leave = new HarmonyMethod(typeof(UiFixesMultiselectSkip), nameof(Leave));
				foreach (var method in methods)
				{
					harmony.Patch(method, prefix: enter, finalizer: leave);
				}

				Plugin.Verbose($"UI Fixes multiselect: our menu entries are skipped in its {methods.Length} button queries.");
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning("UI Fixes multiselect speed-up is off: " + e.Message);
			}
		}

		private static void Enter() => UiFixesHints.BeginSkippingOurMenuEntries();

		private static void Leave() => UiFixesHints.EndSkippingOurMenuEntries();
	}
}
