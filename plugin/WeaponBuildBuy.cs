using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.Builds;
using EFT.UI.DragAndDrop;
using UnityEngine;

namespace CustomRpTweaks.Client
{
	// One entry per saved build in the submenu (the game's context menus are keyed by an enum, so the number of builds listed is capped)
	internal enum EBuildPreset
	{
		P01, P02, P03, P04, P05, P06, P07, P08, P09, P10, P11, P12,
		P13, P14, P15, P16, P17, P18, P19, P20, P21, P22, P23, P24
	}

	// "Build" in the context menu of a weapon, under "Edit build": pick one of the builds saved for that weapon and it is made on the spot.
	// What the build needs and the stash doesn't hold is bought with the quick buy engine (cheapest offers, traders' and players', any
	// currency), then the game's own workbench assembly (WeaponAssembler, the one behind the Edit build screen's Assemble button) puts the
	// parts on the weapon: parts the build doesn't use are left in the stash.
	internal static class WeaponBuildBuy
	{
		internal const string Key = "Build";
		internal const int MaxPresets = 24;

		// What the build asks for: the builds saved for this weapon (the game's default ones are hidden by a setting)
		internal static List<WeaponBuild> PresetsFor(Weapon weapon)
		{
			var storage = ItemUiContext.Instance?.Session?.WeaponBuildsStorage;
			if (storage == null)
			{
				return new List<WeaponBuild>();
			}

			return storage.WeaponBuilds.Values
				.Where(build => build.Item is Weapon built && built.TemplateId == weapon.TemplateId && (!Plugin.BuildHideDefaults.Value || !build.FromPreset))
				.OrderBy(build => build.HandbookName, StringComparer.OrdinalIgnoreCase)
				.Take(MaxPresets)
				.ToList();
		}

		// The reason the entry is greyed out (the same conditions as the game's Edit build), or null when it can be used
		internal static string Unavailable(InventoryController controller, Weapon weapon, int presets)
		{
			if (InGameStatus.InRaid)
			{
				return "Not available in a raid.";
			}

			if (Busy.Contains(weapon))
			{
				return "This weapon is being built right now.";
			}

			if (!Singleton<BonusController>.Instance.HasBonus(EBonusType.UnlockWeaponModification))
			{
				return "Needs the workbench: weapon modification is not unlocked yet.";
			}

			if (controller != null && controller.Inventory.Equipment.IsItemEquipped(weapon))
			{
				return "You can't edit an equipped weapon.";
			}

			return presets == 0 ? "No saved build for this weapon." : null;
		}

		// ---- what the build has, needs and lacks, counted by template

		// Every part attached below this item, all the way down
		private static IEnumerable<Mod> ModsOf(CompoundItem root)
		{
			var stack = new Stack<CompoundItem>();
			stack.Push(root);
			while (stack.Count > 0)
			{
				foreach (var slot in stack.Pop().Slots)
				{
					if (slot.ContainedItem is Mod mod)
					{
						yield return mod;
						stack.Push(mod);
					}
				}
			}
		}

		private static Dictionary<string, int> Count(IEnumerable<Mod> mods)
		{
			var counts = new Dictionary<string, int>();
			foreach (var mod in mods)
			{
				counts.TryGetValue(mod.TemplateId, out var have);
				counts[mod.TemplateId] = have + 1;
			}

			return counts;
		}

		// Lying loose in a container (not on a weapon): what the game's assembly takes from the stash
		private static bool IsLoose(Mod mod) => mod.PinLockState != EItemPinLockState.Locked && mod.Parent?.Container?.ParentItem != null && mod.Parent.Container.ParentItem.IsContainer;

		// What the weapon wears and the stash holds loose. Parts attached to a loose part (a scope in a mount) are not counted here: the game's
		// assembly doesn't take them off, so they are demounted first (see Donors)
		private static Dictionary<string, int> Available(Weapon body, IEnumerable<Item> stash)
		{
			var mods = new List<Mod>(ModsOf(body));
			mods.AddRange(stash.OfType<Mod>().Where(IsLoose));
			return Count(mods);
		}

		// A part that can be taken off its parent without harm: not locked, not in a vital slot, nothing attached to it, not a magazine
		private static bool CanDemount(Mod mod)
		{
			if (mod is Magazine || mod.PinLockState == EItemPinLockState.Locked || !(mod.Parent is SlotItemAddress address) || address.Slot.Required)
			{
				return false;
			}

			return !mod.Slots.Any(slot => slot.ContainedItem != null && !(slot.ContainedItem is Ammo));
		}

		// Parts attached to loose parts in the stash (a scope in a mount) that can be demounted
		private static List<Mod> LooseDonors(IEnumerable<Item> stash) =>
			stash.OfType<Mod>().Where(IsLoose).SelectMany(ModsOf).Where(CanDemount).ToList();

		private static Dictionary<string, int> Lacking(Dictionary<string, int> needed, Dictionary<string, int> available)
		{
			var lacking = new Dictionary<string, int>();
			foreach (var pair in needed)
			{
				available.TryGetValue(pair.Key, out var have);
				if (pair.Value > have)
				{
					lacking[pair.Key] = pair.Value - have;
				}
			}

			return lacking;
		}

		// Parts of other weapons in the stash that can be taken off without harm: not in a vital slot, nothing attached to them, not a magazine
		private static List<Mod> Donors(Weapon body, IEnumerable<Item> stash)
		{
			var donors = new List<Mod>();
			foreach (var weapon in stash.OfType<Weapon>().Where(other => other != body && other.PinLockState != EItemPinLockState.Locked))
			{
				foreach (var mod in ModsOf(weapon))
				{
					if (CanDemount(mod))
					{
						donors.Add(mod);
					}
				}
			}

			return donors;
		}

		private class PartRequirement : IExchangeRequirement
		{
			public PartRequirement(Mod sample, int count)
			{
				Item = sample;
				IntCount = count;
				PreciseCount = count;
			}

			public Item Item { get; }
			public string TemplateId => Item.StringTemplateId;
			public string ItemName => Item.Name;
			public int IntCount { get; }
			public double PreciseCount { get; }
			public bool OnlyFunctional => false;
			public bool IsEncoded => false;
			public bool IsSpawnedInSession => false;
		}

		private static string NameOf(WeaponBuild build) => string.IsNullOrEmpty(build.HandbookName) ? build.Item.ShortName.Localized(null) : build.HandbookName;

		internal static void Start(InventoryController controller, Weapon weapon, WeaponBuild build) =>
			FleaBarterBuy.RunGuarded(() => RunBusy(controller, weapon, build));

		// The weapons being built right now: they blink in the stash like an item the server is working on, and can't be built again
		private static readonly HashSet<Weapon> Busy = new HashSet<Weapon>();
		private static float _nextBlink;

		private static async Task RunBusy(InventoryController controller, Weapon weapon, WeaponBuild build)
		{
			try
			{
				await Run(controller, weapon, build);
			}
			finally
			{
				Busy.Remove(weapon);
				SetBlinking(weapon, false);
			}
		}

		// The weapon's views in the stash blink (the game's own "being worked on" state); views the stash redraws are picked up again
		private static void SetBlinking(Weapon weapon, bool on)
		{
			foreach (var view in UnityEngine.Object.FindObjectsOfType<ItemView>())
			{
				if (view.Item == weapon && view.IsBeingLoadedAmmo.Value != on)
				{
					view.IsBeingLoadedAmmo.Value = on;
				}
			}
		}

		internal static void Update()
		{
			if (Busy.Count == 0 || Time.unscaledTime < _nextBlink)
			{
				return;
			}

			_nextBlink = Time.unscaledTime + 0.25f;
			foreach (var weapon in Busy)
			{
				SetBlinking(weapon, true);
			}
		}

		// What a build needs, what is there and what is still missing (and where it could come from)
		internal class Analysis
		{
			public WeaponBuild Build;
			public Weapon Body;
			public Weapon BuildWeapon;
			public Dictionary<string, int> Needed;
			public Dictionary<string, int> Lacking;
			public Dictionary<string, Mod> Samples;
			public readonly List<Mod> Borrowed = new List<Mod>();
			public bool Built;
			public int ToBuy => Lacking.Values.Sum();

			public FleaBarterBuy.Source Source() => new FleaBarterBuy.Source
			{
				Requirements = Lacking.Select(pair => (IExchangeRequirement)new PartRequirement(Samples[pair.Key], pair.Value)).ToArray(),
				Required = requirement => requirement.IntCount,
				Suitable = (item, requirement) => false,
				Stash = () => new List<Item>(),
				UnbuyableBlocks = true,
				Setting = QuickBuyMode.BuyAll,
				ShowHave = false
			};
		}

		internal static Analysis Analyze(InventoryController controller, Weapon weapon, WeaponBuild build)
		{
			var buildWeapon = build.Item as Weapon;
			if (buildWeapon == null)
			{
				return null;
			}

			var analysis = new Analysis
			{
				Build = build,
				Body = weapon,
				BuildWeapon = buildWeapon,
				Needed = Count(ModsOf(buildWeapon)),
				Samples = ModsOf(buildWeapon).GroupBy(mod => mod.StringTemplateId).ToDictionary(group => group.Key, group => group.First()),
				Built = WeaponAssembler.CheckIfAlreadyBuilt(WeaponAssembler.GetInstalledMods(weapon), buildWeapon)
			};

			var stash = controller.Inventory.Stash.GetAllItemsFromCollection().ToList();
			analysis.Lacking = Lacking(analysis.Needed, Available(weapon, stash));

			// Parts that must be demounted first: those on loose parts always, those on other weapons only with the setting. Taken off
			// after the purchase is worked out
			if (analysis.Lacking.Count > 0)
			{
				var donors = LooseDonors(stash);
				if (Plugin.BuildBorrow.Value)
				{
					donors.AddRange(Donors(weapon, stash));
				}

				foreach (var template in analysis.Lacking.Keys.ToList())
				{
					foreach (var donor in donors.Where(mod => mod.TemplateId == template).Take(analysis.Lacking[template]))
					{
						analysis.Borrowed.Add(donor);
						analysis.Lacking[template]--;
					}

					if (analysis.Lacking[template] <= 0)
					{
						analysis.Lacking.Remove(template);
					}
				}
			}

			return analysis;
		}

		// The mark after a build's name in the submenu: parts to buy, or already assembled
		internal static string Indicator(Analysis analysis)
		{
			if (analysis == null)
			{
				return string.Empty;
			}

			if (analysis.Built)
			{
				return "  <color=#9A9A9A>(built)</color>";
			}

			if (analysis.ToBuy > 0)
			{
				return $"  <color=#F2C94C>(buys {analysis.ToBuy})</color>";
			}

			return analysis.Borrowed.Count > 0 ? $"  <color=#F2C94C>(borrows {analysis.Borrowed.Count})</color>" : "  <color=#6FCF6F>(ready)</color>";
		}

		// Nothing to buy or borrow: the entry says (ready) and has no tooltip
		internal static bool IsReady(Analysis analysis) => analysis != null && !analysis.Built && analysis.ToBuy == 0 && analysis.Borrowed.Count == 0;

		// The tooltip of a build in the submenu: whether it buys anything, what and for how much
		internal static async Task<string> Quote(InventoryController controller, Weapon weapon, WeaponBuild build)
		{
			var analysis = Analyze(controller, weapon, build);
			if (analysis == null)
			{
				return "That build has no weapon in it.";
			}

			var lines = new List<string> { $"<b>{NameOf(build)}</b>" };
			if (analysis.Built)
			{
				lines.Add("The weapon already is this build.");
				return string.Join("\n", lines);
			}

			if (analysis.Borrowed.Count > 0)
			{
				lines.Add("<b>Takes off mounts and other weapons:</b> " + string.Join(", ", analysis.Borrowed.GroupBy(mod => mod.TemplateId).Select(group => $"{group.Count()} {group.First().ShortName.Localized(null)}")));
			}

			if (analysis.Lacking.Count == 0)
			{
				lines.Add("<b>Nothing to buy:</b> every part is in your stash or on the weapon.");
				return string.Join("\n", lines);
			}

			lines.Add(await FleaBarterBuy.Quote(ItemUiContext.Instance.Session.RagFair, analysis.Source()));
			return string.Join("\n", lines);
		}

		private static async Task Run(InventoryController controller, Weapon weapon, WeaponBuild build)
		{
			var reason = Unavailable(controller, weapon, 1);
			var analysis = reason == null ? Analyze(controller, weapon, build) : null;
			if (analysis == null)
			{
				Notify("Build: " + (reason ?? "that build has no weapon in it."));
				return;
			}

			var inventory = controller.Inventory;
			var ragfair = ItemUiContext.Instance.Session.RagFair;
			var name = NameOf(build);
			var buildWeapon = analysis.BuildWeapon;
			var needed = analysis.Needed;
			var lacking = analysis.Lacking;
			var borrowed = analysis.Borrowed;
			var stash = inventory.Stash.GetAllItemsFromCollection().ToList();

			if (analysis.Built)
			{
				Notify($"Build: the weapon already is \"{name}\".");
				return;
			}

			Busy.Add(weapon);
			SetBlinking(weapon, true);
			Notify($"Build: building \"{name}\"...");

			// What is still missing is bought: nothing is bought unless all of it can be had and paid for
			if (lacking.Count > 0)
			{
				if (!await FleaBarterBuy.Run(ragfair, analysis.Source()))
				{
					return;
				}

				// The bought parts should be in the stash as the purchase returns; give the game a moment if not
				for (var attempt = 0; attempt < 15; attempt++)
				{
					stash = inventory.Stash.GetAllItemsFromCollection().ToList();
					if (Lacking(lacking, Available(weapon, stash).Where(pair => lacking.ContainsKey(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value)).Count == 0
						|| lacking.All(pair => Count(stash.OfType<Mod>().Where(IsLoose)).TryGetValue(pair.Key, out var have) && have >= pair.Value))
					{
						break;
					}

					await Task.Delay(200);
				}
			}

			// The borrowed parts come off their weapons into the stash, where the assembly finds them
			foreach (var mod in borrowed)
			{
				if (!await TakeOff(controller, mod))
				{
					Notify($"Build: couldn't take {mod.ShortName.Localized(null)} off another weapon; nothing was assembled.");
					return;
				}
			}

			stash = inventory.Stash.GetAllItemsFromCollection().ToList();
			var assembled = await WeaponAssembler.Assemble(weapon, buildWeapon, controller, stash);
			if (!assembled)
			{
				Notify("Build: the game couldn't assemble it. The parts are in your stash.");
				return;
			}

			var stillLacking = Lacking(needed, Count(ModsOf(weapon)));
			Notify(stillLacking.Count == 0
				? $"Build: {weapon.ShortName.Localized(null)} is now \"{name}\"."
				: $"Build: {weapon.ShortName.Localized(null)} assembled as \"{name}\", but {stillLacking.Values.Sum()} part{(stillLacking.Values.Sum() == 1 ? "" : "s")} didn't fit or are missing.");
		}

		// Moves a part into the first free place of the stash
		private static async Task<bool> TakeOff(InventoryController controller, Mod mod)
		{
			foreach (var grid in controller.MainStorage)
			{
				var address = Dismantle.FindFreePlace(grid, mod);
				if (address == null)
				{
					continue;
				}

				var operation = ItemManipulator.Move(mod, address, controller, true);
				if (operation.Failed)
				{
					Plugin.Verbose($"Build: {mod.ShortName.Localized(null)} can't be moved ({operation.Error})");
					return false;
				}

				var outcome = await controller.TryRunNetworkTransaction(operation);
				if (outcome.Failed)
				{
					Plugin.Log.LogWarning($"Build: moving {mod.ShortName.Localized(null)} failed ({outcome.Error})");
					return false;
				}

				return true;
			}

			return false;
		}

		private static void Notify(string message) => NotificationManager.DisplayMessageNotification(message);
	}

	// The submenu under "Build": the builds saved for the weapon
	internal class BuildActions : ContextInteractions<EBuildPreset>
	{
		private readonly Weapon _weapon;
		private readonly List<WeaponBuild> _builds;
		private readonly List<string> _marks = new List<string>();
		private readonly List<bool> _built = new List<bool>();
		private readonly List<bool> _ready = new List<bool>();

		// The submenu being shown (the tooltips are attached to its buttons after the game has made them)
		internal static BuildActions Current;

		internal int Count => _builds.Count;

		// A build that is ready has nothing to say, a built one is disabled
		internal bool HasTooltip(int index) => index < _ready.Count && !_ready[index] && !_built[index];

		internal BuildActions(ItemUiContext uiContext, Weapon weapon) : base(uiContext)
		{
			_weapon = weapon;
			_builds = WeaponBuildBuy.PresetsFor(weapon);
			var controller = Dismantle.ControllerOf(uiContext);
			foreach (var build in _builds)
			{
				try
				{
					var analysis = controller == null ? null : WeaponBuildBuy.Analyze(controller, weapon, build);
					_marks.Add(WeaponBuildBuy.Indicator(analysis));
					_built.Add(analysis != null && analysis.Built);
					_ready.Add(WeaponBuildBuy.IsReady(analysis));
				}
				catch (Exception e)
				{
					_marks.Add(string.Empty);
					_built.Add(false);
					_ready.Add(false);
					Plugin.Log.LogWarning($"Build menu: couldn't look at a build: {e.Message}");
				}
			}

			Current = this;
		}

		// Used by the hover tooltip of the entry
		internal string CacheKey(int index)
		{
			var controller = Dismantle.ControllerOf(UIContext);
			return $"build|{_weapon.Id}|{_builds[index].Id}|{(controller == null ? 0 : controller.Inventory.Stash.GetAllItemsFromCollection().Count())}";
		}

		internal Task<string> Quote(int index)
		{
			var controller = Dismantle.ControllerOf(UIContext);
			return controller == null ? Task.FromResult("") : WeaponBuildBuy.Quote(controller, _weapon, _builds[index]);
		}

		public override Dictionary<EBuildPreset, string> CustomNames
		{
			get
			{
				var names = new Dictionary<EBuildPreset, string>();
				for (var i = 0; i < _builds.Count; i++)
				{
					names[(EBuildPreset)i] = (string.IsNullOrEmpty(_builds[i].HandbookName) ? "Build " + (i + 1) : _builds[i].HandbookName) + (i < _marks.Count ? _marks[i] : "");
				}

				return names;
			}
		}

		public override bool TryGetCustomIcon(EBuildPreset key, out Sprite icon)
		{
			icon = null;
			return false;
		}

		public override bool IsActive(EBuildPreset button) => (int)button < _builds.Count;

		// The weapon already is that build: greyed out, the game shows the reason as the tooltip
		public override IResult IsInteractive(EBuildPreset button) =>
			(int)button < _built.Count && _built[(int)button] ? (IResult)new FailedResult("The weapon already is this build.", 0) : SuccessfulResult.New;

		public override void ExecuteInteractionInternal(EBuildPreset interaction)
		{
			try
			{
				var controller = Dismantle.ControllerOf(UIContext);
				if (controller != null && (int)interaction < _builds.Count)
				{
					WeaponBuildBuy.Start(controller, _weapon, _builds[(int)interaction]);
				}
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Build menu failed: {e}");
			}
		}
	}
}
