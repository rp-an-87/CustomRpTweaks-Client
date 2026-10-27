using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using EFT.InventoryLogic;

namespace CustomRpTweaks.Client
{
	// The magazine top-up and repair hotkeys live in GetReady (RPAN87.GetReady). When that plugin is loaded it handles the keys and
	// gives the hints for them: this finds its QuickActionHints.For by name and calls it, so neither mod needs the other to load.
	// Without GetReady, the copies in this plugin keep working as before.
	internal static class GetReadyHints
	{
		internal const string TargetGuid = "RPAN87.GetReady";

		private static Func<Item, List<KeyValuePair<KeyboardShortcut, string>>> _for;
		private static bool _bound;

		// GetReady is loaded and offers the hints: it handles top-up and repair, so this plugin leaves those keys alone. Bound on the
		// first call, after every plugin has been loaded.
		internal static bool Active
		{
			get
			{
				if (!_bound)
				{
					_bound = true;
					try
					{
						if (Chainloader.PluginInfos.TryGetValue(TargetGuid, out var info) && info.Instance != null)
						{
							var method = info.Instance.GetType().Assembly.GetType("GetReady.QuickActionHints")?.GetMethod("For");
							if (method != null)
							{
								_for = (Func<Item, List<KeyValuePair<KeyboardShortcut, string>>>)Delegate.CreateDelegate(typeof(Func<Item, List<KeyValuePair<KeyboardShortcut, string>>>), method);
							}
						}

						Plugin.Log.LogInfo(_for != null ? "GetReady found: it handles magazine top-up and repair, with their hints." : "GetReady not found: top-up and repair run from this plugin.");
					}
					catch (Exception e)
					{
						_for = null;
						Plugin.Log.LogWarning($"Couldn't use GetReady's hotkey hints, top-up and repair run from this plugin: {e.Message}");
					}
				}

				return _for != null;
			}
		}

		internal static List<string> For(Item item, Func<KeyboardShortcut, string> keyLabel)
		{
			try
			{
				return _for(item).Select(pair => new { key = keyLabel(pair.Key), action = pair.Value })
					.Where(hint => hint.key != null)
					.Select(hint => $"<color=#B8B8B8>[{hint.key}]</color> {hint.action}")
					.ToList();
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning($"GetReady's hotkey hints failed: {e.Message}");
				return new List<string>();
			}
		}
	}
}
