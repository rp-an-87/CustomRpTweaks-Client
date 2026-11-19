using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using CustomRpTweaks.Client.Patches;

namespace CustomRpTweaks.Client
{
	[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
	[BepInDependency(LoadAmmoSkillSpeed.TargetGuid, BepInDependency.DependencyFlags.SoftDependency)]
	[BepInDependency(UiFixesHints.TargetGuid, BepInDependency.DependencyFlags.SoftDependency)]
	public class Plugin : BaseUnityPlugin
	{
		public const string PluginGuid = "RPAN87.CustomRpTweaks";
		public const string PluginName = "CustomRpTweaks.Client";
		public const string PluginVersion = "1.0.0";

		internal static ManualLogSource Log;
		internal static Plugin Instance;

		internal static ConfigEntry<bool> VerboseLogging;
		internal static ConfigEntry<bool> LogSprintStops;

		// Routine progress messages (what a hotkey did, which optional mods were found); warnings and errors always go to the log
		internal static void Verbose(string message)
		{
			if (VerboseLogging != null && VerboseLogging.Value)
			{
				Log.LogInfo(message);
			}
		}

		internal static ConfigEntry<bool> HideOtherPlayersSkillNotifications;
		internal static ConfigEntry<bool> LogSuppressedNotifications;

		internal static ConfigEntry<bool> ThrottleSkillsToPmcLevel;
		internal static ConfigEntry<int> SkillLevelOvershoot;
		internal static ConfigEntry<bool> LogThrottleDecisions;

		internal static ConfigEntry<int> SpecialSlotsPerRow;
		internal static ConfigEntry<bool> HideoutQuickBuy;
		internal static ConfigEntry<QuickBuyMode> HideoutBuyMode;
		internal static ConfigEntry<bool> HideoutBuyTools;
		internal static ConfigEntry<bool> HideoutUpgradeQuickBuy;
		internal static ConfigEntry<bool> HideoutFuelRefill;
		internal static ConfigEntry<KeyboardShortcut> RangefinderZeroHotkey;
		internal static ConfigEntry<RangefinderRounding> RangefinderZeroRounding;
		internal static ConfigEntry<KeyboardShortcut> ZeroResetHotkey;
		internal static ConfigEntry<bool> AmmoZeroEnabled;
		internal static ConfigEntry<bool> FineZeroSteps;
		internal static ConfigEntry<bool> LaserZeroPitch;
		internal static ConfigEntry<bool> ZeroKeepsAim;
		internal static ConfigEntry<bool> RangefinderZeroPitch;
		internal static ConfigEntry<string> FineZeroStepList;
		internal static ConfigEntry<bool> BipodVisualFix;
		internal static ConfigEntry<bool> BipodRelax;
		internal static ConfigEntry<bool> BipodAnySurface;
		internal static ConfigEntry<float> BipodClearance;
		internal static ConfigEntry<float> BipodExtraReach;
		internal static ConfigEntry<float> BipodLedgeReach;
		internal static ConfigEntry<float> BipodMinFlatness;
		internal static ConfigEntry<bool> KeepTopRowsFree;
		internal static ConfigEntry<int> TopRowsToKeepFree;
		internal static ConfigEntry<bool> KeepSpeedWhileZooming;
		internal static ConfigEntry<bool> SteadyAimHoldBreathFix;
		internal static ConfigEntry<bool> FleaOfferTimerFix;
		internal static ConfigEntry<bool> FleaAmmoBoxSearchEnabled;
		internal static ConfigEntry<FleaAmmoBoxMode> FleaAmmoBoxMode;
		internal static ConfigEntry<bool> FleaBarterBuyMenu;
		internal static ConfigEntry<QuickBuyMode> FleaBarterBuyMode;
		internal static ConfigEntry<bool> FleaBarterBuyComplete;
		internal static ConfigEntry<bool> FleaBarterBuyButtons;
		internal static ConfigEntry<bool> QuickBuyTraders;
		internal static ConfigEntry<bool> TaskSearchBox;
		internal static ConfigEntry<bool> BtrCannonCam;
		internal static ConfigEntry<bool> TagillaFeelsAI;
		internal static ConfigEntry<bool> BtrNotEnemyOfPmcs;
		internal static ConfigEntry<float> BtrCannonCamDistance;
		internal static ConfigEntry<float> BtrCannonCamHeight;
		internal static ConfigEntry<float> BtrCannonCamSmoothing;
		internal static ConfigEntry<float> BtrCannonCamTransition;
		internal static ConfigEntry<bool> BtrCannonCamFirstPersonInScreens;

		internal static ConfigEntry<bool> ScaleLoadAmmoSpeed;
		internal static ConfigEntry<float> LoadAmmoSpeedAtLevel0;
		internal static ConfigEntry<float> LoadAmmoSpeedAtLevel50;
		internal static ConfigEntry<float> LoadAmmoSpeedAtElite;

		internal static ConfigEntry<KeyboardShortcut> UnchamberHotkey;

		internal static ConfigEntry<KeyboardShortcut> DismantleHotkey;
		internal static ConfigEntry<KeyboardShortcut> DismantleNonVitalHotkey;
		internal static ConfigEntry<bool> DismantleSelf;
		internal static ConfigEntry<bool> DismantleInRaid;

		internal static ConfigEntry<KeyboardShortcut> EmptyHotkey;
		internal static ConfigEntry<bool> EmptyInRaid;
		internal static ConfigEntry<bool> ContextMenuEntries;
		internal static ConfigEntry<bool> ReplaceDisassemble;
		internal static ConfigEntry<bool> BuildMenu;
		internal static ConfigEntry<bool> BuildHideDefaults;
		internal static ConfigEntry<bool> BuildBorrow;

		internal static ConfigEntry<KeyboardShortcut> EquipPlateHotkey;

		internal static ConfigEntry<KeyboardShortcut> SplitHotkey;

		internal static ConfigEntry<bool> HandInMenu;

		internal static ConfigEntry<bool> HotkeyHints;

		internal static ConfigEntry<bool> PriceIndicator;
		internal static ConfigEntry<string> PriceIndicatorNewColor;
		internal static ConfigEntry<int> PriceIndicatorTraderSize;
		internal static ConfigEntry<bool> PriceIndicatorTooltip;
		internal static ConfigEntry<string> PriceIndicatorOldColor;

		private void Update()
		{
			LoadAmmoSkillSpeed.Update();
			SprintStopLogPatch.Update();
			Dismantle.Update();
			Empty.Update();
			Unchamber.Update();
			EquipPlate.Update();
			SplitStack.Update();
			ServerTime.Update();
			RangefinderZero.Update();
			AmmoZero.Update();
			WeaponBuildBuy.Update();
			BipodVisual.Update();
		}

		// A renamed setting takes over the value it had under its old name, if the config file still has one
		private void Migrate(ConfigEntryBase entry, string oldSection, string oldKey)
		{
			// BepInEx keeps what it read from the file but found no setting for here; the list isn't public
			var orphaned = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(Config, null)
				as System.Collections.Generic.Dictionary<ConfigDefinition, string>;
			var oldDefinition = new ConfigDefinition(oldSection, oldKey);
			if (orphaned != null && orphaned.TryGetValue(oldDefinition, out var value))
			{
				entry.SetSerializedValue(value);
				orphaned.Remove(oldDefinition);
			}
		}

		// The sections were renumbered with two digits and regrouped (F12 sorts them as text): what was saved under the old names goes to the new
		// ones, before anything is bound. A value already saved under a new name wins.
		private static readonly System.Collections.Generic.Dictionary<string, string> SectionRenames = new System.Collections.Generic.Dictionary<string, string>
		{
			{ "19. Flea market", "01. Flea market" },
			{ "5. Hideout", "02. Hideout" },
			{ "12. Price indicator", "03. Price indicator" },
			{ "20. Tasks screen", "04. Tasks screen" },
			{ "10. Dismantle", "05. Dismantle" },
			{ "10c. Empty", "06. Empty" },
			{ "10b. Unchamber", "07. Unchamber" },
			{ "11. Equip plate", "08. Equip plate" },
			{ "15. Split stack", "09. Split stack" },
			{ "22. Select all of kind", "10. Select all of kind" },
			{ "16. Hand in", "11. Hand in" },
			{ "18. Context menu", "12. Context menu" },
			{ "4. Stash sort", "13. Stash sort" },
			{ "3. Pockets", "14. Pockets" },
			{ "13. Hotkey hints", "15. Hotkey hints" },
			{ "14. UI Fixes hints", "16. UI Fixes hints" },
			{ "2. Skill throttle", "17. Skill throttle" },
			{ "1. Notifications", "18. Notifications" },
			{ "16. Aiming", "19. Aiming" },
			{ "8. Movement", "20. Movement" },
			{ "9. Loading ammo", "21. Loading ammo" },
			{ "21. BTR", "22. BTR" },
			{ "20. Server time", "23. Server time" },
			{ "17. Logging", "24. Logging" },
		};

		private void RenameSections()
		{
			var orphaned = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(Config, null)
				as System.Collections.Generic.Dictionary<ConfigDefinition, string>;
			if (orphaned == null)
			{
				return;
			}

			foreach (var pair in orphaned.ToList())
			{
				if (!SectionRenames.TryGetValue(pair.Key.Section, out var section))
				{
					continue;
				}

				orphaned.Remove(pair.Key);
				var target = new ConfigDefinition(section, pair.Key.Key);
				if (!orphaned.ContainsKey(target))
				{
					orphaned[target] = pair.Value;
				}
			}
		}

		private void Awake()
		{
			Log = Logger;
			Instance = this;
			RenameSections();

			VerboseLogging = Config.Bind("24. Logging", "1. Verbose logging", false,
				"Writes a line to the BepInEx log for every step of Dismantle, Empty, Split, Equip plate, Hand in and Unchamber, and for optional mods found at startup. Warnings and errors are always logged. For troubleshooting.");

			HideOtherPlayersSkillNotifications = Config.Bind("18. Notifications", "1. Hide skill level-up notifications of other characters", true,
				"Bots/NPCs also level up their skills (a lot, with high skill multipliers). Only your own character should be able to show the skill and weapon mastery level-up notifications.");
			LogSuppressedNotifications = Config.Bind("18. Notifications", "2. Log suppressed notifications", false,
				"Writes a line to the BepInEx log every time a notification coming from another character is suppressed. Useful to confirm who was causing them.");

			ThrottleSkillsToPmcLevel = Config.Bind("17. Skill throttle", "1. Throttle skills to PMC level", true,
				"Your skills can't level past your PMC level (plus the overshoot below). Experience gained past that point is discarded. Skills already above the limit are not lowered. Weapon mastery is not affected.");
			SkillLevelOvershoot = Config.Bind("17. Skill throttle", "2. Allowed levels above PMC level", 0,
				new ConfigDescription("How many levels above your PMC level a skill is still allowed to reach. 0 = skills are capped at your PMC level.",
					new AcceptableValueRange<int>(0, 20)));

			LogSprintStops = Config.Bind("24. Logging", "2. Log sprint stops", false,
				"Writes a [SprintStop] line to the BepInEx log every time your sprint ends: what called it (the mod or game code), stamina, weight and leg conditions. Use it to find what keeps cancelling your sprint, then turn it off. For troubleshooting.");
			new SprintStopLogPatch().Enable();

			LogThrottleDecisions = Config.Bind("17. Skill throttle", "3. Log throttle decisions", false,
				"Writes to the BepInEx log (at most once every 5 seconds per skill) what the throttle did to skill experience gains, and why it didn't apply when it didn't. For troubleshooting.");

			SpecialSlotsPerRow = Config.Bind("14. Pockets", "1. Special slots per row", 3,
				new ConfigDescription("When you have more special slots than this, the extra ones wrap onto the next row instead of overflowing.",
					new AcceptableValueRange<int>(1, 6)));

			new SpecialSlotsLayoutPatch().Enable();

			KeepTopRowsFree = Config.Bind("13. Stash sort", "1. Keep top rows free when sorting", false,
				"Sorting the stash places items below the rows set here. The top rows are only used when everything else is full.");
			TopRowsToKeepFree = Config.Bind("13. Stash sort", "2. Rows to keep free", 5,
				new ConfigDescription("How many rows, counting from the top of the stash, to keep free when sorting.", new AcceptableValueRange<int>(1, 50)));

			HideoutQuickBuy = Config.Bind("02. Hideout", "1. Buy craft materials with the buy button", true,
				"The flea market buy button of a craft buys everything the craft needs, the cheapest offers first (flea market offers, other players' and traders': every offer is ranked by its price in roubles whatever the currency, a dollar or euro offer is used when it is the cheaper one and you hold that money, otherwise the next one is; a pack sold in one piece only whole). Hover the button: the tooltip shows what it would buy and cost, and whether it can be done at all. Nothing is bought unless every item can be had and you hold the money. Money, dog tags and found-in-raid requirements are not for sale: they are used from the stash. Tools (the wrench-marked requirements) follow the setting below. There is no confirmation: the click buys, starting the craft is still up to you. Off: the game's own button (a flea market search).");
			HideoutBuyMode = Config.Bind("02. Hideout", "2. Craft buy mode", QuickBuyMode.BuyMissingNoFir,
				"What the buy button buys. Buy all: everything the craft asks for, whatever you hold. Buy missing: only what you don't hold. Buy missing, found-in-raid items don't count: like Buy missing, but your found-in-raid items are not counted as held, so they are kept (the craft takes items that are not found in raid first).");
			HideoutBuyTools = Config.Bind("02. Hideout", "3. Buy missing tools too", true,
				"The buy button also buys the tools of the craft (the wrench-marked requirements, which are reused and not used up) that you don't have. A tool you hold is never bought again, in any mode. Off: tools are left out, the craft still needs them in your stash.");
			HideoutUpgradeQuickBuy = Config.Bind("02. Hideout", "5. Buy hideout upgrade items with a button", true,
				"A flea market button to the left of the item requirements of a hideout area upgrade, the same as the craft buy button: it buys the items the upgrade is missing, by the craft buy mode (setting 2) and the same price rules, and hovering it shows what it would buy and cost. Found-in-raid requirements are never bought: if you are short of one, the rest is still bought and the button is tinted red and the tooltip says what is missing. There is no confirmation: the click buys, pressing Upgrade is still up to you. Takes effect the next time an upgrade screen is opened.");
			HideoutFuelRefill = Config.Bind("02. Hideout", "4. Refill button on the generator and water collector slots", true,
				"A Refill button over a generator fuel slot or the water collector's filter slot that is empty or whose contents ran out (the water collector takes Water filters: the same rules, with only the one item). It buys one full fuel tank on the flea market (traders' offers included, the cheapest full one) and puts it in the slot; the empty tank goes back to your stash. It first uses a tank already in your stash that still has fuel (Metal before Expeditionary, the fullest first). Otherwise it buys, in order: a Metal fuel tank at a normal price, an Expeditionary fuel tank at a normal price, then (nothing at a normal price) a Metal tank at an overstock price, then an Expeditionary one. The button's text is white at a normal price, orange at an overstock price, red when nothing can be bought or paid for. Hover the button for what it would buy and cost (an overstock price is shown in orange with the normal price). Takes effect the next time the fuel screen is opened.");
			RangefinderZeroHotkey = Config.Bind("19. Aiming", "2. Zero from rangefinder hotkey", new KeyboardShortcut(KeyCode.PageUp, KeyCode.LeftAlt),
				"While aiming through a sight, press this to set its zero to the distance the rangefinder attached to your weapon (such as the RAPTAR) shows, rounded down or up (see the next setting) to a zero step the sight offers. The rangefinder must be switched on and its display in use; the handheld rangefinder is not used. It uses the game's own zeroing steps, so it only works while aiming. Unassign the key to turn the feature off.");
			RangefinderZeroRounding = Config.Bind("19. Aiming", "3. Zero from rangefinder: rounding", RangefinderRounding.Down,
				"Which zero step the hotkey picks when the rangefinder's distance is between two. Down: the step at or below the distance (a 437 m reading with 50 m steps gives 400 m). Up: the step at or above it (450 m). Past the first or last step of the sight, that step is used.");
			ZeroResetHotkey = Config.Bind("19. Aiming", "3b. Reset zero to minimum hotkey", new KeyboardShortcut(KeyCode.PageDown, KeyCode.LeftAlt),
				"While aiming through a sight, press this to set its zero back to the sight's shortest zero step. It uses the game's own zeroing steps, so it only works while aiming. Unassign the key to turn it off.");
			AmmoZeroEnabled = Config.Bind("19. Aiming", "3a. Zero follows the loaded ammo", true,
				"The game builds every sight's zero from the weapon's default ammo, so a 200 m zero is only right for that round. With this on, the zero of the weapon in your hands is built from the round about to fire (the chambered one, otherwise the first in the magazine) and is rebuilt while you aim whenever that round changes. The zero numbers stay the same; the sight's angle is the one that round needs. Only your own weapon. Off: the game's own behaviour. Turn on verbose logging to see each rebuild and how long it took (lines starting with Ammo zero:).");
			FineZeroSteps = Config.Bind("19. Aiming", "3a2. Finer zero steps at long range", true,
				"Adds the zero steps of the next setting to every sight, but only those inside the sight's own range (its own steps stay). Helps the rangefinder zero hotkey pick closer. The zero a sight had saved may land on another distance the first time. Off: the sight's own steps.");
			FineZeroStepList = Config.Bind("19. Aiming", "3a3. Finer zero steps: the steps (m)", "50,100,150,200,250,300,333,366,400,425,450,475,500,525,550,575,600,620,640,660,680,700,725,750,775,800",
				"Comma-separated distances in metres (the list of the Extended Zero Ranges mod). Applied to each sight within its own shortest and longest step.");
			AmmoZero.ReloadSteps();
			FineZeroStepList.SettingChanged += (sender, args) => AmmoZero.ReloadSteps();
			FineZeroSteps.SettingChanged += (sender, args) => AmmoZero.ReloadSteps();
			LaserZeroPitch = Config.Bind("19. Aiming", "3a4. Laser: vertical correction for the zero", true,
				"EXPERIMENT. In vanilla the laser stays along the barrel while your sight line dips toward it the longer the zero is, so the dot walks up from the reticle at longer zeros. While you aim through a sight, this turns the laser up or down (never sideways) by the pitch the sight line has changed since the sight's shortest zero. At that zero it is exactly vanilla, so every mount keeps the offset vanilla gives it (beside the barrel, on a scope rail...), and that offset stays the same at every zero. Off the sights, or on iron sights, nothing changes. The device itself does not move. Turn on verbose logging to see the turn (lines starting with Aim tilt:).");
			RangefinderZeroPitch = Config.Bind("19. Aiming", "3a5. Rangefinder: vertical correction for the zero", true,
				"EXPERIMENT. The same vertical correction for the rangefinder built into the device (RAPTAR): while you aim through a sight its measuring ray is corrected for the zero, so the reading is the distance to what your reticle is on, and the zero-from-rangefinder hotkey uses it. Off the sights nothing changes.");
			ZeroKeepsAim = Config.Bind("19. Aiming", "3a6. Changing the zero keeps your aim on target", true,
				"EXPERIMENT. When the zero of the sight you aim through changes by a step (the game's keys, the rangefinder hotkey or the reset hotkey), the game turns the sight relative to the gun, so the reticle jumps up or down. With this on, your aim is moved by the same angle the other way, so the reticle stays on what it was on. Turn on verbose logging to see the angle (lines starting with Zero keeps aim:).");
			BipodVisualFix = Config.Bind("19. Aiming", "3c. Fix the bipod looking deployed when it is not", true,
				"After a reload or a weapon switch the bipod can look deployed while the game's bipod state says it is not. This keeps the weapon's animation flag in line with the real state. Off: the game's own behaviour. Turn on verbose logging to see the state and flag (lines starting with Bipod:).");
			BipodRelax = Config.Bind("19. Aiming", "4. Relax deploying a bipod while prone", true,
				"The game only lets you deploy a bipod prone on ground it counts very strictly: low-poly terrain, nothing within 20 cm of the spot, ground right under the bipod and nearly level. With this on, the search uses the settings below instead (the bipod then rests on what it finds, which may not look perfect). Off: the game's own rules. Takes effect on the next deploy.");
			BipodAnySurface = Config.Bind("19. Aiming", "5. Bipod: other surfaces count as ground", true,
				"Rocks, props and other solid objects count as ground, not only the terrain's low-poly collision (the game's rule).");
			BipodClearance = Config.Bind("19. Aiming", "6. Bipod: clear space around the spot (m)", 0.1f,
				new ConfigDescription("The game refuses when terrain is within this distance of the spot where the bipod would stand (20 cm). 0 turns the check off.", new AcceptableValueRange<float>(0f, 0.3f)));
			BipodExtraReach = Config.Bind("19. Aiming", "7. Bipod: extra reach down (m)", 0.4f,
				new ConfigDescription("How much further below the spot the ground is looked for, on top of the game's own reach (at most 40 cm). Helps when the ground drops away under the bipod.", new AcceptableValueRange<float>(0f, 1.5f)));
			BipodLedgeReach = Config.Bind("19. Aiming", "8. Bipod: ledge reach back (m)", 0.3f,
				new ConfigDescription("When there is no ground right under the bipod (the edge of a ledge or rock), the spots up to this far back toward you are tried too, and the bipod rests on the edge. 0 turns it off.", new AcceptableValueRange<float>(0f, 0.8f)));
			BipodMinFlatness = Config.Bind("19. Aiming", "9. Bipod: how flat the ground must be", 0.7f,
				new ConfigDescription("1 is level ground; the game wants 0.94 (about 20 degrees), the server's setting is 0.82 (about 35). 0.7 is about 45 degrees. The lower of this and the server's setting is used.", new AcceptableValueRange<float>(0.3f, 1f)));
			new RelaxedProneBipodPatch().Enable();
			new RangefinderReadingPatch().Enable();
			new LaserZeroPitchPatch().Enable();
			new ZeroKeepsAimPatch().Enable();
			new RangefinderZeroPitchPatch().Enable();
			new AmmoZeroCalibrationPatch().Enable();
			new AmmoZeroIronPatch().Enable();
			new FineZeroStepsPatch().Enable();
			new FineZeroDistancePatch().Enable();
			new BipodDeployLogPatch().Enable();
			new FuelRefillShowPatch().Enable();
			new FuelRefillUpdatePatch().Enable();
			new HideoutQuickBuy.BuyMaterialsPatch().Enable();
			new HideoutQuickBuy.StartButtonPatch().Enable();
			new HideoutQuickBuy.BuyTooltipPatch().Enable();
			new HideoutUpgradeBuy.RequirementsPatch().Enable();
			KeepSpeedWhileZooming = Config.Bind("20. Movement", "1. Keep walk speed while zooming a scope", false,
				"The mouse wheel changes both your walk speed and the zoom of a smooth-zoom scope. With this on, the wheel does not change your walk speed while you aim through a smooth-zoom (variable) scope and hold your scope zoom key combination (for example Alt + wheel). The wheel alone, other sights and not aiming are not affected.");
			new ScopeZoomSpeedPatch().Enable();

			SteadyAimHoldBreathFix = Config.Bind("19. Aiming", "1. Fix holding breath during the steady aim perk", true,
				"The elite weapon draw perk (Aim Drills and weapon skills) steadies your aim for 3 seconds after you start aiming, but the game applies it even while you hold your breath, and the aim then drifts as the 3 seconds run out. With this on, holding your breath during those seconds steadies the aim as it normally does, and the perk applies again if you release the breath within them.");
			new SteadyAimBreathPatch().Enable();

			FleaOfferTimerFix = Config.Bind("01. Flea market", "1. Fix expired offers staying listed", true,
				"A game bug: listing a new flea market offer cancels the expiry timers of your older offers. They then only disappear from your offer list while the flea market screen is open, otherwise they stay (the offer counter stays full and you cannot list) until you restart the game. With this on, the timers of all your offers are restarted whenever the offer list is updated. Takes effect after a restart.");
			new FleaOfferTimerPatch().Enable();

			FleaBarterBuyMenu = Config.Bind("01. Flea market", "2. Quick buy entries for barter offers", true,
				"In the item menu beside a flea market barter offer (Inspect, Filter by item, ...) an entry Buy & fulfil is added: it buys the items the barter asks for (what exactly is set in 'Quick buy mode'), the cheapest offers first (flea market offers, other players' and traders': every offer is ranked by its price in roubles whatever the currency, a dollar or euro offer is used when it is the cheaper one and you hold that money, otherwise the next one is; a pack sold in one piece only whole), then buys the barter with them. Nothing is bought unless every item can be had and you hold the money (dog tags are not used; no currency exchange). There is no confirmation: the click buys. Takes effect the next time the offer's menu is built.");
			FleaBarterBuyMode = Config.Bind("01. Flea market", "3. Quick buy mode", QuickBuyMode.BuyMissingNoFir,
				"What Buy & fulfil buys. Buy all: every item the barter asks for, whatever you hold. Buy missing: only what you don't hold. Buy missing, found-in-raid items don't count: like Buy missing, but the found-in-raid items you hold are not counted as held, so they are kept (the game hands in items that are not found in raid first, so the barter takes the bought ones).");
			FleaBarterBuyComplete = Config.Bind("01. Flea market", "4. Quick buy also buys the barter", true,
				"On: once the items are bought, the barter offer itself is bought with them. Off: only the items are bought, you complete the barter yourself.");
			FleaBarterBuyButtons = Config.Bind("01. Flea market", "5. Quick buy buttons under the barter price", true,
				"A small Buy & fulfil button under the price of a flea market barter offer, the same as the menu entry. Hover it: the tooltip shows what it would buy and cost from the current offers, and whether you hold the money. Takes effect the next time the offers are drawn.");
			QuickBuyTraders = Config.Bind("01. Flea market", "6. Quick buy also uses trader offers", true,
				"The quick buy (barter button, menu entry and the hideout craft buy button) also buys from the traders' offers on the flea market, not only from other players, whichever is cheapest. Ammo, for one, is mostly sold by traders. A trader's offer is only used if you could press Purchase on it yourself: the trader is unlocked, your loyalty level is high enough and you have not used up your buy limit. Off: other players' offers only.");
			FleaAmmoBoxSearchEnabled = Config.Bind("01. Flea market", "7. Filter by item on ammo searches its boxes", true,
				"Ammo can't be bought on the flea market, its boxes can. With this on, Filter by item (menu entry, and the Filter by Item key of UI Fixes) on an ammo item that has boxes on sale lists the ammo and all its boxes together in the normal flea market list. Ammo without a box on sale, and every other item, search as before.");
			FleaAmmoBoxMode = Config.Bind("01. Flea market", "8. Ammo box search: how the list is shown", global::CustomRpTweaks.Client.FleaAmmoBoxMode.Tree,
				"Tree: the left side of the flea market lists the ammo and each of its boxes as rows, the first box is selected, and clicking a row shows that item's offers (the game's own search, with its paging and sorting). Together: the normal Filter by item list, with the offers of the ammo and all its boxes in one list.");
			// A failing patch here must not stop the rest of the plugin from loading
			try
			{
				new FleaAmmoBoxRequestPatch().Enable();
				new FleaAmmoBoxAcceptPatch().Enable();
				new FleaAmmoBoxTreePatch().Enable();
			}
			catch (System.Exception e)
			{
				Log.LogError($"Ammo box search patches failed, the feature is off: {e}");
			}
			new FleaBarterBuyMenuPatch().Enable();
			new FleaBarterBuyButtonsPatch().Enable();

			BtrCannonCam = Config.Bind("22. BTR", "1. Cannon camera while the BTR drives you", true,
				"While the BTR is driving you somewhere (you are inside it and it is moving), the camera is moved to its turret and looks where the cannon points. Your character stays in the BTR and is visible. When the BTR stops, you get out, or you turn this off, the normal first-person view is back on the next frame.");
			BtrCannonCamDistance = Config.Bind("22. BTR", "2. Camera distance behind the cannon", 3f,
				new ConfigDescription("Metres the camera sits behind the cannon's muzzle, along its line. Too small and the camera is inside the barrel. Changes apply live.", new AcceptableValueRange<float>(0f, 10f)));
			BtrCannonCamHeight = Config.Bind("22. BTR", "3. Camera height above the cannon", 1.2f,
				new ConfigDescription("Metres the camera sits above the cannon's muzzle. Negative values put it below. Changes apply live.", new AcceptableValueRange<float>(-2f, 5f)));
			BtrCannonCamSmoothing = Config.Bind("22. BTR", "4. Camera smoothing", 0.3f,
				new ConfigDescription("Seconds the camera takes to catch up with the cannon's direction (its position catches up four times faster). Higher = steadier but laggier. 0 = no smoothing (twitchy). Changes apply live.", new AcceptableValueRange<float>(0f, 1.5f)));
			BtrCannonCamTransition = Config.Bind("22. BTR", "5. Transition time", 0.9f,
				new ConfigDescription("Seconds the camera takes to glide between your normal view and the cannon view, both ways (when the BTR starts or stops, you get out, or a screen opens or closes). 0 = instant cut. Changes apply live.", new AcceptableValueRange<float>(0f, 3f)));
			BtrCannonCamFirstPersonInScreens = Config.Bind("22. BTR", "6. First person while a screen is open", true,
				"While the BTR drives you, the camera goes back to your normal first-person view when you open your inventory or talk to the driver (or any other full screen is open), and returns to the cannon view when you close it. Changes apply live.");
			new BtrCannonCamPatch().Enable();

			BtrNotEnemyOfPmcs = Config.Bind("25. Bot hostility", "1. BTR is not an enemy of PMC bots", true,
				"Vanilla makes every newly spawned bot an enemy of all groups of another side without checking the friendly lists, so PMC bots attack the BTR even though the server lists it as friendly. On = the BTR is skipped there when the group's friendly list says so (a PMC it really shoots can still turn on it). Off = vanilla. Applies to bots spawned after the change.");
			new BtrNotEnemyOfGroupsPatch().Enable();
			TagillaFeelsAI = Config.Bind("25. Bot hostility", "2. Tagilla notices AI PMCs", true,
				"Vanilla: while Tagilla waits in ambush he picks the nearest non-scav enemy, but only real players count, so an AI PMC shooting him is never chosen and he keeps running at the player. On = AI PMCs count too (scavs still never do). Off = vanilla. Changes apply live.");
			new TagillaFeelIncludesAIPatch().Enable();


			TaskSearchBox = Config.Bind("04. Tasks screen", "1. Search box", true,
				"Adds a search box to the right of the Active / Operational tasks toggles on the tasks screen. Type words to narrow the list to the tasks whose name, trader or location contain all of them (any word in any of the three, upper or lower case). The red cross in the box clears it. The box is emptied each time the screen opens. Takes effect the next time the tasks screen opens.");
			new TaskSearchPatches.ScreenShowPatch().Enable();
			new TaskSearchPatches.ShowQuestsPatch().Enable();

			// Only offered when Continuous Load Ammo is installed (it is a soft dependency, so it has loaded before us)
			if (Chainloader.PluginInfos.ContainsKey(LoadAmmoSkillSpeed.TargetGuid))
			{
				ScaleLoadAmmoSpeed = Config.Bind("21. Loading ammo", "1. Scale loading walk speed with Mag Drills", false,
					"Needs the Continuous Load Ammo mod. While you load ammo outside the inventory, your walk speed is limited to a share of your current top walking speed (already reduced by your weight). With this on, that share depends on your Mag Drills level, using the three values below, instead of Continuous Load Ammo's own fixed 'Speed Limit'. Its setting is changed in memory only, its config file isn't touched. Applies from the next load you start.");
				LoadAmmoSpeedAtLevel0 = Config.Bind("21. Loading ammo", "2. Loading speed at Mag Drills level 0", 0.2f,
					new ConfigDescription("Share of your current top walking speed while loading ammo at Mag Drills level 0 (0.2 = 20%). Rises evenly up to the level 50 value.", new AcceptableValueRange<float>(0f, 1f)));
				LoadAmmoSpeedAtLevel50 = Config.Bind("21. Loading ammo", "3. Loading speed at Mag Drills level 50", 0.7f,
					new ConfigDescription("Share of your current top walking speed while loading ammo at Mag Drills level 50 (0.7 = 70%).", new AcceptableValueRange<float>(0f, 1f)));
				LoadAmmoSpeedAtElite = Config.Bind("21. Loading ammo", "4. Loading speed at Mag Drills level 51 (elite)", 0.9f,
					new ConfigDescription("Share of your unencumbered walking speed (not reduced by weight, so weight can still cap it) while loading ammo at the elite Mag Drills level 51 (0.9 = 90%). Unlike the lower levels this one is absolute.", new AcceptableValueRange<float>(0f, 1f)));
				new LoadAmmoSkillUiPatches.SkillTooltipShowPatch().Enable();
				new LoadAmmoSkillUiPatches.BuffDescriptionPatch().Enable();
			}

			UnchamberHotkey = Config.Bind("07. Unchamber", "1. Unchamber hotkey", new KeyboardShortcut(KeyCode.U),
				"Hover a weapon and press this to take the round (or the spent case) out of its chamber, without unloading the magazine or taking the weapon apart (UI Fixes' unload key leaves the chamber alone, and an unloaded weapon sells on the flea market). It goes to the container the weapon lies in, then any container whoever wears it carries (a corpse's, then yours), then any inventory you have open, then in the menu the stash; in a raid, when nothing takes it, nothing is moved. It only acts on a weapon with no magazine in it. The default U is also UI Fixes' unload key: the first press takes the magazine out (UI Fixes), the second empties the chamber (this), leaving the weapon unloaded. Unassign the key to turn the feature off.");

			DismantleHotkey = Config.Bind("05. Dismantle", "1. Dismantle hotkey", new KeyboardShortcut(KeyCode.D, KeyCode.LeftAlt),
				"Hover an item and press this to take off the parts attached to it (a weapon's mods, an armor's plates and other removable parts) and put them next to it, in the container it lies in. The item itself stays where it is, only what is attached to it comes off. Magazines keep their ammo and containers keep their contents. Parts the game won't let you remove stay on. Unassign the key to turn the feature off.");
			DismantleNonVitalHotkey = Config.Bind("05. Dismantle", "4. Dismantle non-vital parts hotkey", new KeyboardShortcut(KeyCode.D, KeyCode.LeftControl, KeyCode.LeftAlt),
				"Like the dismantle hotkey, but the vital parts (the ones in required slots, a receiver for instance) stay attached, in the stash as in a raid, and the parts attached to them still come off. Only works on weapons (vital parts are a weapon thing); on anything else it does nothing. Pressed with the keys of the other hotkey held too, this one wins. Unassign the key to turn the feature off.");
			DismantleSelf = Config.Bind("05. Dismantle", "5. Take off the hovered part itself too", false,
				"Off: only what is attached to the hovered item comes off, the item itself stays where it is (UI Fixes' uninstall shortcut takes off a single part). On: when the hovered item is itself a part attached to something (a handguard on a gun, a plate in armor), it comes off first and its own parts come off after it.");
			DismantleInRaid = Config.Bind("05. Dismantle", "3. Allow in a raid", true,
				"Also works in a raid inventory, with the game's own raid rules: parts that can't be modded in a raid, vital parts and locked armor slots stay on, but the parts attached to them can still come off (a scope off a receiver that has to stay). Parts of an item you carry or of a dead body go to your or its pockets, vest and backpack, if there is room.");

			// The settings used to be called Disassemble: keep what was chosen
			Migrate(DismantleHotkey, "10. Disassemble", "1. Disassemble hotkey");
			Migrate(DismantleNonVitalHotkey, "10. Disassemble", "4. Disassemble non-vital parts hotkey");
			Migrate(DismantleInRaid, "10. Disassemble", "3. Allow in a raid");

			EmptyHotkey = Config.Bind("06. Empty", "1. Empty hotkey", new KeyboardShortcut(KeyCode.E, KeyCode.LeftControl),
				"Hover a container (a backpack, rig, case, pouch) and press this to move everything lying in it out, to the first place that takes it: the container it lies in, then any container whoever carries it has (a corpse's, then yours), then any inventory you have open, then in the menu the stash (your own gear) or what you carry (someone else's). Items that hold items move whole. Whatever finds no place, or the game refuses, stays and a message says how many. Unassign the key to turn the feature off.");
			EmptyInRaid = Config.Bind("06. Empty", "2. Allow in a raid", true,
				"Also works in a raid inventory (your own gear, a body, a bag you opened).");

			ContextMenuEntries = Config.Bind("12. Context menu", "1. Dismantle and Empty in the context menu", true,
				"Right-click an item: Dismantle goes next to Disassemble on a weapon (with a submenu: dismantle all, dismantle non-vital) and next to Modding on anything else that has parts attached (armor, rigs, helmets: a plain button). Empty goes next to Open on a container, greyed out when there is nothing in it. Takes effect the next time the menu opens.");
			ReplaceDisassemble = Config.Bind("12. Context menu", "2. Replace Disassemble with Dismantle on weapons", true,
				"On a weapon the game's own Disassemble button is hidden and Dismantle takes its place. Only when Dismantle is offered for that weapon (it has parts attached, and the in-raid setting allows it). Off: both are listed.");
			BuildMenu = Config.Bind("12. Context menu", "3. Build entry on weapons", true,
				"Right-click a weapon in the stash: a Build entry under Edit build lists the builds you saved for that weapon. Picking one buys the parts the stash doesn't hold (the quick buy: cheapest offers, traders' and players', any currency) and then assembles the weapon the way the workbench does; parts the build doesn't use stay in the stash. Greyed out, with the reason, when the workbench isn't built (the same condition as Edit build), in a raid or when there is no saved build. Takes effect the next time the menu opens.");
			BuildHideDefaults = Config.Bind("12. Context menu", "4. Build entry hides the default builds", true,
				"The game lists its own preset builds beside yours. On: only the builds you saved are in the Build entry's list.");
			BuildBorrow = Config.Bind("12. Context menu", "5. Build entry borrows parts from other weapons", false,
				"On: parts the build needs that you don't hold loose can be taken off other weapons in your stash before anything is bought. Only parts in non-vital slots with nothing attached to them (never a magazine) are borrowed, so the other weapons lose, for instance, a sight or a grip. Off: those parts are bought.");
			new ItemMenuAddPatch().Enable();
			new ItemMenuButtonPatch().Enable();
			new ItemMenuTrackPatch().Enable();
			new ItemMenuSidePatch().Enable();
			new ItemMenuBuildSidePatch().Enable();

			EquipPlateHotkey = Config.Bind("08. Equip plate", "1. Equip plate hotkey", new KeyboardShortcut(KeyCode.I),
				"Hover an armor plate in the stash and press this to put it into the first free plate slot of the armor, rig or helmet you wear that accepts it. The game's own rules decide what fits. Not in a raid. Unassign the key to turn the feature off.");

			SplitHotkey = Config.Bind("09. Split stack", "1. Split hotkey", new KeyboardShortcut(KeyCode.S, KeyCode.LeftControl),
				"Hover a stack (ammo, money, grenades, anything that stacks) and press this to split it in half without the split dialog. The new stack, the lower half, goes to the first free place in the container it lies in (or, for something you carry, your pockets, vest or backpack). Unassign the key to turn the feature off.");

			HandInMenu = Config.Bind("11. Hand in", "1. Hand in in the context menu", true,
				"Right-click an item in the stash: when a started quest still wants exactly this item (found in raid, durability and the other conditions of the quest line all met, the same test as the game's own hand-over window), a \"Hand in\" button hands it in to that quest. If the quest needs fewer than the stack holds, it says so and nothing is handed in (split the stack first). Not in a raid. Takes effect the next time the menu opens.");
			new HandInPatches.ContextMenuPatch().Enable();
			new HandInPatches.ButtonLookPatch().Enable();

			new StashSortOverlayPatch().Enable();
			new StashSortReservePatches.SortPatch().Enable();
			new StashSortReservePatches.FindFreeSpacePatch().Enable();
			new StashSortReservePatches.GetFreeLocationPatch().Enable();

			new SkillThrottlePatches.SkillSetCurrentPatch().Enable();
			new SkillThrottlePatches.SkillCurrentSetterPatch().Enable();

			new SkillThrottleUiPatches.SkillPanelShowPatch().Enable();
			new SkillThrottleUiPatches.SkillPanelLevelChangedPatch().Enable();
			new SkillThrottleUiPatches.SkillTooltipShowPatch().Enable();

			new SkillNotificationPatches.SkillLevelChangedPatch().Enable();
			new SkillNotificationPatches.WeaponMasteredPatch().Enable();
			new SkillNotificationPatches.ClientPlayerSkillLevelChangedPatch().Enable();
			new SkillNotificationPatches.ClientPlayerWeaponMasteredPatch().Enable();

			HotkeyHints = Config.Bind("15. Hotkey hints", "1. Show hotkey hints in item tooltips", true,
				"Adds a small line below a separator at the end of the tooltip of an item in your inventory with the hotkeys of this mod that apply to it right now: top up (a magazine that isn't full), repair (a damaged item that one of your kits can repair), equip plate, and dismantle (an item with attached parts; the keep-vital one only when some of them are in required slots). The keys shown are the ones configured. Not shown for items of traders, the flea market or the mail. Turn it off if another mod's tooltip lines clash with it.");
			// Only offered when UI Fixes is installed (a soft dependency, so it has loaded before us)
			UiFixesHints.Setup(Config);
			SelectAllOfKind.Setup(Config);
			new HotkeyHintPatches.HoverEnterPatch().Enable();
			new HotkeyHintPatches.HoverExitPatch().Enable();
			new HotkeyHintPatches.ItemTooltipPatch().Enable();
			new HotkeyHintPatches.TooltipShowPatch().Enable();

			PriceIndicator = Config.Bind("03. Price indicator", "1. Colour prices the server has raised", true,
				"Needs the CustomRpTweaks server mod with its trader overstock feature on. Prices the server has raised for you are drawn in the colour below, on the trader screen and in the flea market. The client works out which prices are raised from the prices it has seen, and asks the server once per game session (right after the first trader offers or flea results load) for offers that were already raised when the game started. If the server mod is missing or has the feature or its 'clientIndicator' option off, the client stops asking and shows nothing extra until the game restarts. Applies from the next load of the offers.");
			PriceIndicatorNewColor = Config.Bind("03. Price indicator", "2. Raised price colour", "#FF9A3C", "Colour of the raised price, as #RRGGBB.");
			PriceIndicatorTraderSize = Config.Bind("03. Price indicator", "3. Trader screen: raised price size (%)", 125,
				new ConfigDescription("Size of a raised price under an item in the trader's grid, as a percentage of a normal price (100 = same size), so the increase is easier to spot.", new AcceptableValueRange<int>(80, 150)));
			PriceIndicatorTooltip = Config.Bind("03. Price indicator", "4. Trader screen: base price in the tooltip", true,
				"Hover an item in the trader's grid: when its price has been raised, the tooltip shows the base price, struck through, before the raised price.");
			PriceIndicatorOldColor = Config.Bind("03. Price indicator", "5. Tooltip base price colour", "#FFFF00", "Colour of the struck-through base price in the tooltip, as #RRGGBB.");

			new OverstockPricePatches.TraderOffersHook().Enable();
			new OverstockPricePatches.FleaOffersHook().Enable();
			new OverstockPricePatches.FleaPurchaseRefreshPatch().Enable();
			new OverstockPricePatches.TraderGridPricePatch().Enable();
			new OverstockPricePatches.TraderTooltipPatch().Enable();
			new OverstockPricePatches.FleaRowPricePatch().Enable();
			new OverstockPricePatches.TraderBarterCountPatch().Enable();
			new OverstockPricePatches.TraderReloadPatch().Enable();
			new OverstockPricePatches.FleaBarterCountPatch().Enable();

			ServerTime.Setup(Config);
			SortEntries();
		}

		// F12 lists the sections and settings in the order they were bound, not by name, so the entries are put in order here: sections by
		// name (they are numbered), settings by the number in front of their name (so 10 comes after 9)
		private void SortEntries()
		{
			try
			{
				var entries = typeof(ConfigFile).GetProperty("Entries", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(Config, null)
					as System.Collections.Generic.Dictionary<ConfigDefinition, ConfigEntryBase>;
				if (entries == null)
				{
					Log.LogWarning("Config entries not found, F12 keeps the order they were bound in.");
					return;
				}

				var sorted = entries.OrderBy(pair => pair.Key.Section, System.StringComparer.Ordinal)
					.ThenBy(pair => LeadingNumber(pair.Key.Key))
					.ThenBy(pair => pair.Key.Key, System.StringComparer.Ordinal)
					.ToList();
				entries.Clear();
				foreach (var pair in sorted)
				{
					entries.Add(pair.Key, pair.Value);
				}
			}
			catch (System.Exception e)
			{
				Log.LogWarning("Couldn't sort the config entries: " + e.Message);
			}
		}

		private static int LeadingNumber(string key)
		{
			var end = 0;
			while (end < key.Length && char.IsDigit(key[end]))
			{
				end++;
			}

			return end > 0 && int.TryParse(key.Substring(0, end), out var number) ? number : int.MaxValue;
		}
	}
}
