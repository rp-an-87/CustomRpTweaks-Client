using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;

namespace CustomRpTweaks.Client
{
	// Tells which trader offers have a price the server mod's trader overstock feature has raised for this profile.
	// The client works it out itself: an offer's price doesn't change between restocks, so the lowest price seen for it
	// is its base, and a higher one later means a raised price. The only request is a single one per game session (made right
	// after the first trader offers or flea results load), which recovers the base of offers that were already raised before the
	// game started and tells whether the server has the feature on. When the server mod isn't there or has the feature or its
	// 'clientIndicator' option off, the client stops asking and shows nothing extra.
	internal static class OverstockPrices
	{
		private const string Route = "/customrptweaks/overstock/prices";
		private const int FailuresBeforeGivingUp = 3;

		// trader id -> offer id -> multiplier, from the one request; an entry is used up the first time the offer is seen
		private static Dictionary<string, Dictionary<string, double>> _seed = new Dictionary<string, Dictionary<string, double>>();
		// lowest price seen per offer and screen ("t" trader grid, "f" flea), as each shows the price in its own way
		private static readonly Dictionary<string, int> _baseline = new Dictionary<string, int>();
		private static bool _seeded;
		private static bool _serverOff;
		private static int _failures;

		internal static bool Active => Plugin.PriceIndicator != null && Plugin.PriceIndicator.Value && !_serverOff;

		// Notes the price an offer is shown at. True when it is above the base price, which comes back in basePrice.
		internal static bool IsRaised(char screen, string traderId, string offerId, int price, out int basePrice)
		{
			basePrice = price;
			if (!Active || price < 1)
			{
				return false;
			}

			string key = screen + "|" + traderId + "|" + offerId;
			if (!_baseline.TryGetValue(key, out int lowest))
			{
				lowest = price;
				if (_seed.TryGetValue(traderId, out var offers) && offers.TryGetValue(offerId, out double multiplier) && multiplier > 1)
				{
					lowest = (int)Math.Round(price / multiplier);
				}
			}

			if (price < lowest)
			{
				lowest = price;
			}

			_baseline[key] = lowest;
			basePrice = lowest;
			return price > lowest;
		}

		// Wraps a task the game awaits for trader offers or flea results, so the one request is made once the response is in
		internal static async Task<T> SeedAfter<T>(Task<T> task)
		{
			T result = await task;
			Seed();
			return result;
		}

		internal static bool NeedsSeed => Active && !_seeded;

		private static void Seed()
		{
			if (!NeedsSeed)
			{
				return;
			}

			try
			{
				string json = RequestHandler.GetJson(Route);
				if (string.IsNullOrEmpty(json))
				{
					throw new InvalidOperationException("empty answer");
				}

				JObject root = JObject.Parse(json);
				if (!(root.Value<bool?>("enabled") ?? false))
				{
					GiveUp("the server has the trader overstock price indicator off");
					return;
				}

				var seed = new Dictionary<string, Dictionary<string, double>>();
				if (root["offers"] is JObject traders)
				{
					foreach (var trader in traders)
					{
						var raised = new Dictionary<string, double>();
						foreach (var offer in (JObject)trader.Value)
						{
							raised[offer.Key] = offer.Value.Value<double>();
						}

						seed[trader.Key] = raised;
					}
				}

				_seed = seed;
				_seeded = true;
			}
			catch (Exception e)
			{
				if (++_failures >= FailuresBeforeGivingUp)
				{
					GiveUp("the server didn't answer (" + e.Message + ")");
				}
			}
		}

		private static void GiveUp(string reason)
		{
			_serverOff = true;
			_seed = new Dictionary<string, Dictionary<string, double>>();
			_baseline.Clear();
			Plugin.Log.LogInfo("Price indicator off until the game restarts: " + reason);
		}
	}
}
