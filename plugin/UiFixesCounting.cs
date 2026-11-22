using System;
using System.Reflection;
using BepInEx.Bootstrap;

namespace CustomRpTweaks.Client
{
	// UI Fixes' multiselect decides which buttons its menu offers by building the item menu of EVERY selected item, once for each button it counts,
	// and only reads whether a button of the game's own is available. UI Fixes raises a public static flag (UIFixes.MultiSelect.CountingInteractions)
	// while it does that, so our menu additions can skip their work then (see UiFixesHints.SkipOurMenuEntries). The flag is reached by reflection
	// and the lookup is made once: without UI Fixes, or when an update renames it, there is no flag and everything simply runs as before.
	internal static class UiFixesCounting
	{
		private static Func<bool> _counting;

		internal static bool Counting => _counting != null && _counting();

		internal static void Setup()
		{
			try
			{
				if (!Chainloader.PluginInfos.TryGetValue(SelectAllOfKind.TargetGuid, out var info) || info.Instance == null)
				{
					return;
				}

				var type = info.Instance.GetType().Assembly.GetType("UIFixes.MultiSelect");
				const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
				var field = type?.GetField("CountingInteractions", Flags);
				var getter = type?.GetProperty("CountingInteractions", Flags)?.GetGetMethod(true);
				if (field != null && field.FieldType == typeof(bool))
				{
					_counting = () => (bool)field.GetValue(null);
				}
				else if (getter != null && getter.ReturnType == typeof(bool))
				{
					_counting = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), getter);
				}
				else
				{
					Plugin.Log.LogWarning("UI Fixes' CountingInteractions flag was not found: right-clicking a big selection stays slower.");
					return;
				}

				Plugin.Verbose("UI Fixes multiselect: our menu entries are skipped while it counts interactions.");
			}
			catch (Exception e)
			{
				_counting = null;
				Plugin.Log.LogWarning("UI Fixes' CountingInteractions flag could not be read: " + e.Message);
			}
		}
	}
}
