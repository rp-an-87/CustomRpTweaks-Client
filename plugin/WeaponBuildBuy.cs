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

		// What the weapon wears and the stash holds loose (with whatever is attached to it)
		private static Dictionary<string, int> Available(Weapon body, IEnumerable<Item> stash)
		{
			var mods = new List<Mod>(ModsOf(body));
			foreach (var loose in stash.OfType<Mod>().Where(IsLoose))
			{
				mods.Add(loose);
				mods.AddRange(ModsOf(loose));
			}

			return Count(mods);
		}

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
					if (mod is Magazine || mod.PinLockState == EItemPinLockState.Locked || !(mod.Parent is SlotItemAddress address) || address.Slot.Required)
					{
						continue;
					}

					if (mod.Slots.Any(slot => slot.ContainedItem != null && !(slot.ContainedItem is Ammo)))
					{
						continue;
					}

					donors.Add(mod);
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
			FleaBarterBuy.RunGuarded(() => Run(controller, weapon, build));

		private static async Task Run(InventoryController controller, Weapon weapon, WeaponBuild build)
		{
			var reason = Unavailable(controller, weapon, 1);
			var buildWeapon = build.Item as Weapon;
			if (reason != null || buildWeapon == null)
			{
				Notify("Build: " + (reason ?? "that build has no weapon in it."));
				return;
			}

			var inventory = controller.Inventory;
			var ragfair = ItemUiContext.Instance.Session.RagFair;
			var name = NameOf(build);
			var needed = Count(ModsOf(buildWeapon));
			var samples = ModsOf(buildWeapon).GroupBy(mod => mod.TemplateId).ToDictionary(group => group.Key, group => group.First());

			if (WeaponAssembler.CheckIfAlreadyBuilt(WeaponAssembler.GetInstalledMods(weapon), buildWeapon))
			{
				Notify($"Build: the weapon already is \"{name}\".");
				return;
			}

			var stash = inventory.Stash.GetAllItemsFromCollection().ToList();
			var lacking = Lacking(needed, Available(weapon, stash));

			// Parts of other weapons that can go on this one (a setting): taken off after the purchase worked out
			var borrowed = new List<Mod>();
			if (Plugin.BuildBorrow.Value && lacking.Count > 0)
			{
				var donors = Donors(weapon, stash);
				foreach (var template in lacking.Keys.ToList())
				{
					foreach (var donor in donors.Where(mod => mod.TemplateId == template).Take(lacking[template]))
					{
						borrowed.Add(donor);
						lacking[template]--;
					}

					if (lacking[template] <= 0)
					{
						lacking.Remove(template);
					}
				}
			}

			// What is still missing is bought: nothing is bought unless all of it can be had and paid for
			if (lacking.Count > 0)
			{
				var source = new FleaBarterBuy.Source
				{
					Requirements = lacking.Select(pair => (IExchangeRequirement)new PartRequirement(samples[pair.Key], pair.Value)).ToArray(),
					Required = requirement => requirement.IntCount,
					Suitable = (item, requirement) => false,
					Stash = () => new List<Item>(),
					UnbuyableBlocks = true,
					Setting = QuickBuyMode.BuyAll
				};

				if (!await FleaBarterBuy.Run(ragfair, source))
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

		internal BuildActions(ItemUiContext uiContext, Weapon weapon) : base(uiContext)
		{
			_weapon = weapon;
			_builds = WeaponBuildBuy.PresetsFor(weapon);
		}

		public override Dictionary<EBuildPreset, string> CustomNames
		{
			get
			{
				var names = new Dictionary<EBuildPreset, string>();
				for (var i = 0; i < _builds.Count; i++)
				{
					names[(EBuildPreset)i] = string.IsNullOrEmpty(_builds[i].HandbookName) ? "Build " + (i + 1) : _builds[i].HandbookName;
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

		public override IResult IsInteractive(EBuildPreset button) => SuccessfulResult.New;

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
