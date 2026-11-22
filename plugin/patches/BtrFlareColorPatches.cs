using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using EFT.PrefabSettings;
using HarmonyLib;
using SPT.Reflection.Patching;
using Systems.Effects;
using UnityEngine;

namespace CustomRpTweaks.Client.Patches
{
	// The BTR signal cartridge is a clone of the white one, so its flare is the plain white light. The colour of a flare is set when it is fired (the
	// light and the sparks of the effect object); right after that the colour of this one is replaced with the one from the settings.
	internal class BtrFlareColorPatch : ModulePatch
	{
		protected override MethodBase GetTargetMethod() => typeof(FlareCartridge).GetMethod(nameof(FlareCartridge.Init), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(FlareCartridge __instance, Ammo flareCartridge)
		{
			if (!Plugin.BtrFlareRecolour.Value || flareCartridge == null || !BtrCall.IsBtrAmmo(flareCartridge.TemplateId))
			{
				return;
			}

			try
			{
				var effect = Traverse.Create(__instance).Field("_flareEffectPrefab").GetValue<GameObject>();
				var selector = effect != null ? effect.GetComponent<FlareShotEffectSelector>() : null;
				if (selector == null)
				{
					Plugin.Verbose("BTR flare colour: the flare has no effect object to colour.");
					return;
				}

				var colour = Plugin.BtrFlareColour.Value;
				if (selector._flareLight != null)
				{
					selector._flareLight.color = colour;
				}
				if (selector._flareParticleSystem != null)
				{
					var main = selector._flareParticleSystem.main;
					main.startColor = Color.Lerp(colour, Color.white, 0.25f);
				}
			}
			catch (System.Exception e)
			{
				Plugin.Log.LogError("BTR flare colour failed: " + e);
			}
		}
	}
}
