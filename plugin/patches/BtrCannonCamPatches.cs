using System.Reflection;
using EFT;
using EFT.CameraControl;
using EFT.Vehicle;
using SPT.Reflection.Patching;
using UnityEngine;

namespace CustomRpTweaks.Client.Patches
{
	// While the BTR drives you somewhere (you are inside and it is Running) the main camera is put behind and above the cannon, looking where it points.
	// Only the camera moves: your character, hands and weapon stay where they are. FirstPersonCameraState copies the position and rotation of the
	// player's camera from the character every frame, so doing nothing here (setting off, BTR stopped, you got out) gives the normal view back on the next frame.
	// The turret itself only turns in coarse steps (BTRTurretView.LerpRotate follows synced targets) and rides the suspension, so the pose is levelled
	// (no roll) and eased towards the target; without that the view twitches.
	internal class BtrCannonCamPatch : ModulePatch
	{
		private static bool _loggedMissingAnchor;
		private static bool _active;
		private static Vector3 _position;
		private static Quaternion _rotation;

		protected override MethodBase GetTargetMethod() => typeof(PlayerCameraController).GetMethod(nameof(PlayerCameraController.LateUpdate), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(PlayerCameraController __instance)
		{
			if (!ShouldApply(__instance, out var anchor))
			{
				_active = false;
				return;
			}

			var camera = __instance.Camera;
			if (camera == null)
			{
				return;
			}

			var forward = anchor.forward;
			var targetPosition = anchor.position - forward * Plugin.BtrCannonCamDistance.Value + Vector3.up * Plugin.BtrCannonCamHeight.Value;
			// LookRotation with world up drops the roll the vehicle picks up on bumps
			var targetRotation = Quaternion.LookRotation(forward, Vector3.up);

			var tau = Plugin.BtrCannonCamSmoothing.Value;
			if (!_active || tau <= 0.001f)
			{
				_position = targetPosition;
				_rotation = targetRotation;
			}
			else
			{
				// Position follows closely (the vehicle really moves), rotation more slowly (that is where the twitching is)
				var dt = Time.deltaTime;
				_position = Vector3.Lerp(_position, targetPosition, 1f - Mathf.Exp(-dt / (tau * 0.25f)));
				_rotation = Quaternion.Slerp(_rotation, targetRotation, 1f - Mathf.Exp(-dt / tau));
			}
			_active = true;

			camera.transform.SetPositionAndRotation(_position, _rotation);
		}

		private static bool ShouldApply(PlayerCameraController controller, out Transform anchor)
		{
			anchor = null;
			if (!Plugin.BtrCannonCam.Value)
			{
				return false;
			}

			var player = controller.Player;
			if (player == null || !player.IsYourPlayer || player.BtrState != EPlayerBtrState.Inside)
			{
				return false;
			}

			var btrView = BtrController.Instance?.BtrView;
			if (btrView == null || btrView.BtrState != EBtrState.Running)
			{
				return false;
			}

			var turret = btrView.turret;
			if (turret != null)
			{
				anchor = turret.machineGunLaunchPoint != null ? turret.machineGunLaunchPoint : turret.GunRoot;
			}
			if (anchor == null && !_loggedMissingAnchor)
			{
				_loggedMissingAnchor = true;
				Plugin.Log.LogWarning("BTR cannon camera: the BTR turret has no usable camera anchor, the normal view stays.");
			}
			return anchor != null;
		}
	}
}
