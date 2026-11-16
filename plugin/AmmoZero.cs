using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace CustomRpTweaks.Client
{
	// The game builds every sight's zero from the weapon's default ammo, so a zero set at 200 m is only right for that round. With this on,
	// the zero of the weapon in your hands is built from the round that is about to fire (the chambered one, else the first in the magazine),
	// and rebuilt while you aim whenever that round changes. The zero steps themselves also get finer at long range (see FineSteps).
	internal static class AmmoZero
	{
		private static readonly Stopwatch Timer = new Stopwatch();
		private static Weapon _weapon;
		private static AmmoTemplate _ammo;
		private static bool _have;

		// The weapon in your hands, or null
		internal static Weapon MyWeapon()
		{
			var player = GamePlayerOwner.MyPlayer;
			return player == null ? null : (player.HandsController as Player.FirearmController)?.Item;
		}

		// The round the zero is built for: the round about to fire in your weapon, otherwise the game's own choice (the weapon's default)
		internal static AmmoTemplate AmmoFor(WeaponTemplate template)
		{
			if (Plugin.AmmoZeroEnabled.Value)
			{
				var weapon = MyWeapon();
				if (weapon != null && weapon.Template == template)
				{
					var ammo = weapon.CurrentAmmoTemplate;
					if (ammo != null)
					{
						return ammo;
					}
				}
			}

			return template.DefAmmoTemplate;
		}

		// While aiming: when the round about to fire changes (or aiming starts), the zero is rebuilt and the sight re-aimed
		internal static void Update()
		{
			var player = GamePlayerOwner.MyPlayer;
			var animation = player == null ? null : player.ProceduralWeaponAnimation;
			var weapon = animation == null || !animation.IsAiming || !Plugin.AmmoZeroEnabled.Value ? null : MyWeapon();
			if (weapon == null)
			{
				_have = false;
				return;
			}

			var ammo = weapon.CurrentAmmoTemplate;
			if (_have && _weapon == weapon && _ammo == ammo)
			{
				return;
			}

			_have = true;
			_weapon = weapon;
			_ammo = ammo;
			Timer.Restart();
			weapon.RecalculateOpticCalibrationPoints();
			animation.method_2(); // the sight is re-aimed on the next frame
			animation.method_14(); // iron sights
			Timer.Stop();
			Plugin.Verbose($"Ammo zero: rebuilt for {(ammo == null ? "default ammo" : ammo.NameLocalizationKey)} in {Timer.Elapsed.TotalMilliseconds:0.00} ms");
		}

		// Finer zero steps: the steps in the setting that fall inside the sight's own range are added; the sight's own steps stay
		private static ConditionalWeakTable<int[], int[]> _cache = new ConditionalWeakTable<int[], int[]>();
		private static int[] _extra = new int[0];

		internal static void ReloadSteps()
		{
			_extra = (Plugin.FineZeroStepList.Value ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(text => int.TryParse(text, out var value) ? value : 0).Where(value => value > 0).Distinct().ToArray();
			_cache = new ConditionalWeakTable<int[], int[]>();
		}

		internal static int[] FineSteps(int[] original)
		{
			if (original == null || original.Length < 2 || !Plugin.FineZeroSteps.Value)
			{
				return original;
			}

			if (_cache.TryGetValue(original, out var cached))
			{
				return cached;
			}

			var result = original;
			var ascending = true;
			for (var i = 1; i < original.Length; i++)
			{
				ascending &= original[i] > original[i - 1];
			}

			if (ascending)
			{
				var set = new SortedSet<int>(original);
				foreach (var step in _extra)
				{
					if (step >= original[0] && step <= original[original.Length - 1])
					{
						set.Add(step);
					}
				}

				result = set.ToArray();
			}

			_cache.Add(original, result);
			return result;
		}
	}

	// The scope and red dot zero is built from the weapon's default ammo; it gets the round about to fire instead (your own weapon only)
	internal class AmmoZeroCalibrationPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(Weapon).GetMethod(nameof(Weapon.CreateOpticCalibrationData), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPrefix]
		private static void Prefix(Weapon __instance, ref AmmoTemplate ammoTemplate)
		{
			if (!Plugin.AmmoZeroEnabled.Value || __instance != AmmoZero.MyWeapon())
			{
				return;
			}

			var current = __instance.CurrentAmmoTemplate;
			if (current != null)
			{
				ammoTemplate = current;
			}
		}
	}

	internal class AmmoZeroIronPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(Weapon).GetMethod(nameof(Weapon.CalculateShotDirectionForIron), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchTranspiler]
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) => AmmoZeroIl.UseAmmoFor(instructions);
	}

	internal static class AmmoZeroIl
	{
		// WeaponTemplate.DefAmmoTemplate (read on a template) becomes AmmoZero.AmmoFor(template)
		internal static IEnumerable<CodeInstruction> UseAmmoFor(IEnumerable<CodeInstruction> instructions)
		{
			var getter = AccessTools.PropertyGetter(typeof(WeaponTemplate), nameof(WeaponTemplate.DefAmmoTemplate));
			var replacement = AccessTools.Method(typeof(AmmoZero), nameof(AmmoZero.AmmoFor));
			var count = 0;
			foreach (var instruction in instructions)
			{
				if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) && instruction.operand as MethodInfo == getter)
				{
					count++;
					yield return new CodeInstruction(OpCodes.Call, replacement);
				}
				else
				{
					yield return instruction;
				}
			}

			if (count == 0)
			{
				Plugin.Verbose("Ammo zero: found no DefAmmoTemplate read to replace; the zero stays on the default ammo");
			}
		}
	}

	// The sight's list of zero steps is the finer one
	internal class FineZeroStepsPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(SightComponent).GetMethod(nameof(SightComponent.GetScopeCalibrationDistances), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(ref int[] __result) => __result = AmmoZero.FineSteps(__result);
	}

	// The game reads the current step's distance from the sight's own list; it has to read the finer one
	internal class FineZeroDistancePatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(SightComponent).GetMethod(nameof(SightComponent.GetCurrentOpticCalibrationDistance), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPrefix]
		private static bool Prefix(SightComponent __instance, ref int __result)
		{
			if (!Plugin.FineZeroSteps.Value)
			{
				return true;
			}

			var steps = __instance.GetScopeCalibrationDistances(__instance.SelectedScope);
			__result = steps == null || steps.Length == 0 ? 0 : steps[Math.Min(__instance.GetCurrentOpticCalibrationPointIndex(__instance.SelectedScope), steps.Length - 1)];
			return false;
		}
	}
}
