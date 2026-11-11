using System;
using System.Threading.Tasks;
using BepInEx.Configuration;
using EFT;
using EFT.Communications;
using EFT.UI;
using JsonType;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using SPT.Reflection.Utils;
using UnityEngine;

namespace CustomRpTweaks.Client
{
	// F12 buttons that ask the CustomRpTweaks server mod to set the raid clock (menu or raid, it applies to raids started after).
	internal static class ServerTime
	{
		private const string Section = "20. Server time";
		private static readonly int[] Hours = { 0, 3, 6, 12, 18, 21 };
		private static readonly string[] Names = { "midnight", "late night", "morning", "noon", "afternoon", "night" };

		internal static void Setup(ConfigFile config)
		{
			for (int i = 0; i < Hours.Length; i++)
			{
				int hour = Hours[i];
				string label = hour.ToString("00") + ":00 " + Names[i];
				Action<ConfigEntryBase> drawer = entry =>
				{
					if (GUILayout.Button("Set server time to " + label, GUILayout.ExpandWidth(true)))
					{
						Set(hour);
					}
				};

				// the key starts with the hour so the buttons stay in order in F12; the value is never read
				config.Bind(Section, (i + 1) + ". " + label, "",
					new ConfigDescription(
						i == 0 ? "Needs the CustomRpTweaks server mod. Sets the raid clock on the server, in the menu or in a raid. It applies to raids you start after pressing it and runs on at the normal speed from there; a server restart goes back to the normal clock. A message confirms it." : "Sets the server's raid clock to " + label + " (see the first button).",
						null,
						new ConfigurationManagerAttributes { CustomDrawer = drawer }));
			}
		}

		private static volatile LocationWeatherTime _pending;

		// The raid itself starts from TarkovApplication._localGameDateTime, built once in LoadLocations (only while empty) from the
		// first /client/weather. Rebuild it from the fresh answer, the way the game does, on the main thread.
		internal static void Update()
		{
			var weather = _pending;
			if (weather == null)
			{
				return;
			}

			_pending = null;
			try
			{
				var app = ClientAppUtils.GetMainApp() as TarkovApplication;
				if (app == null)
				{
					Plugin.Log.LogWarning("Server time: no main application, the next raid keeps the old clock");
					return;
				}

				string date = weather.Date, time = weather.Time;
				float acceleration = weather.Acceleration;
				app._localGameDateTime = GameDateTime.Create(DateTimeExtensions.UtcNow, ref date, ref time, ref acceleration, false);
				Plugin.Verbose("Server time: raid clock rebuilt (" + date + " " + time + ", x" + acceleration + ")");
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning("Server time: could not rebuild the raid clock: " + e.Message);
			}
		}

		// The game reads the raid clock from /client/weather only when the main menu loads and keeps it (time, acceleration, start
		// moment); the menu's clock is refreshed here, and Update rebuilds the one the raid uses.
		private static void RefreshClientClock()
		{
			try
			{
				var session = ClientAppUtils.GetMainApp()?.GetClientBackEndSession();
				if (session == null)
				{
					Plugin.Log.LogWarning("Server time: no backend session, the game keeps its old clock until you reconnect");
					return;
				}

				session.GetWeatherAndTime().ContinueWith(t =>
				{
					if (t.IsFaulted || t.IsCanceled)
					{
						Plugin.Log.LogWarning("Server time: could not refresh the game's clock: " + (t.Exception?.GetBaseException().Message ?? "cancelled"));
						return;
					}

					// Runs on a background thread; the game's clock object needs Unity, so Update applies it
					_pending = t.Result;
				});
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning("Server time: clock refresh failed: " + e.Message);
			}
		}

		private static void Set(int hour)
		{
			try
			{
				string json = RequestHandler.GetJson("/customrptweaks/settime/" + hour);
				JObject answer = string.IsNullOrEmpty(json) ? null : JObject.Parse(json);
				if (answer != null && (answer.Value<bool?>("ok") ?? false))
				{
					RefreshClientClock();
					NotificationManager.DisplayMessageNotification("Server time set to " + hour.ToString("00") + ":00. Applies to your next raid.");
					return;
				}

				string reason = answer?.Value<string>("error") ?? "no answer from the server mod";
				Plugin.Log.LogWarning("Server time: " + reason);
				NotificationManager.DisplayWarningNotification("Server time not set: " + reason);
			}
			catch (Exception e)
			{
				Plugin.Log.LogWarning("Server time request failed: " + e.Message);
				NotificationManager.DisplayWarningNotification("Server time not set: " + e.Message);
			}
		}
	}
}
