using System.Reflection;
using EFT;
using EFT.CameraControl;
using EFT.Vehicle;
using SPT.Reflection.Patching;
using UnityEngine;

namespace CustomRpTweaks.Client.Patches
{
	// While the BTR drives you somewhere (you are inside and it is Running) the main camera is put at the turret, looking where the cannon points.
	// Only the camera moves: your character, hands and weapon stay where they are. FirstPersonCameraState copies the position and rotation of the
	// player's camera from the character every frame, so doing nothing here (setting off, BTR stopped, you got out) gives the normal view back on the next frame.
	internal class BtrCannonCamPatch : ModulePatch
	{
		private static bool _loggedMissingAnchor;

		protected override MethodBase GetTargetMethod() => typeof(PlayerCameraController).GetMethod(nameof(PlayerCameraController.LateUpdate), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(PlayerCameraController __instance)
		{
			if (!Plugin.BtrCannonCam.Value)
			{
				return;
			}

			var player = __instance.Player;
			if (player == null || !player.IsYourPlayer || player.BtrState != EPlayerBtrState.Inside)
			{
				return;
			}

			var btrView = BtrController.Instance?.BtrView;
			if (btrView == null || btrView.BtrState != EBtrState.Running)
			{
				return;
			}

			var anchor = GetAnchor(btrView.turret);
			if (anchor == null)
			{
				if (!_loggedMissingAnchor)
				{
					_loggedMissingAnchor = true;
					Plugin.Log.LogWarning("BTR cannon camera: the BTR turret has no usable camera anchor, the normal view stays.");
				}
				return;
			}

			var camera = __instance.Camera;
			if (camera != null)
			{
				camera.transform.SetPositionAndRotation(anchor.position, anchor.rotation);
			}
		}

		private static Transform GetAnchor(BTRTurretView turret)
		{
			if (turret == null)
			{
				return null;
			}
			if (turret.machineGunLaunchPoint != null)
			{
				return turret.machineGunLaunchPoint;
			}
			return turret.GunRoot;
		}
	}
}
