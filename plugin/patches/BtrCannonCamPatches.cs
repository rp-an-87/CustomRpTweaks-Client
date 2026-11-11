using System.Reflection;
using EFT;
using EFT.CameraControl;
using EFT.UI.Screens;
using EFT.Vehicle;
using SPT.Reflection.Patching;
using UnityEngine;

namespace CustomRpTweaks.Client.Patches
{
	// While the BTR drives you somewhere (you are inside and it is Running) the main camera is put behind and above the cannon, looking where it points.
	// Only the camera moves: your character, hands and weapon stay where they are. FirstPersonCameraState copies the position and rotation of the
	// player's camera from the character every frame, so by the time this postfix runs the camera sits at the normal first-person pose; the cannon pose is
	// blended in over it with a weight that eases 0 -> 1 when the view is wanted and back to 0 when it is not (BTR stopped, you got out, a screen such as
	// the inventory or the driver's dialog is open, setting off). At weight 0 nothing is touched.
	// The turret itself only turns in coarse steps (BTRTurretView.LerpRotate follows synced targets) and rides the suspension, so the cannon pose is
	// levelled (no roll) and eased towards the target; without that the view twitches.
	internal class BtrCannonCamPatch : ModulePatch
	{
		private static bool _loggedMissingAnchor;
		private static float _weight;
		private static bool _poseValid;
		private static Vector3 _position;
		private static Quaternion _rotation;
		private static bool _lastWanted;
		private static bool _lastScreenOpen;

		protected override MethodBase GetTargetMethod() => typeof(PlayerCameraController).GetMethod(nameof(PlayerCameraController.LateUpdate), BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);

		[PatchPostfix]
		private static void Postfix(PlayerCameraController __instance)
		{
			var anchor = GetAnchor(__instance, out var wanted);
			var camera = __instance.Camera;

			// The setting switched off, or no BTR / camera: no blend-out, the normal view is back at once
			if (camera == null || anchor == null || !Plugin.BtrCannonCam.Value)
			{
				_weight = 0f;
				_poseValid = false;
				return;
			}

			var duration = Plugin.BtrCannonCamTransition.Value;
			if (duration <= 0.001f)
			{
				_weight = wanted ? 1f : 0f;
			}
			else
			{
				_weight = Mathf.MoveTowards(_weight, wanted ? 1f : 0f, Time.deltaTime / duration);
			}

			if (_weight <= 0f)
			{
				_poseValid = false;
				return;
			}

			var forward = anchor.forward;
			var targetPosition = anchor.position - forward * Plugin.BtrCannonCamDistance.Value + Vector3.up * Plugin.BtrCannonCamHeight.Value;
			// LookRotation with world up drops the roll the vehicle picks up on bumps
			var targetRotation = Quaternion.LookRotation(forward, Vector3.up);

			var tau = Plugin.BtrCannonCamSmoothing.Value;
			if (!_poseValid || tau <= 0.001f)
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
			_poseValid = true;

			// Smootherstep: slow start, fast middle, slow arrival
			var t = _weight * _weight * _weight * (_weight * (_weight * 6f - 15f) + 10f);
			var cameraTransform = camera.transform;
			cameraTransform.SetPositionAndRotation(
				Vector3.Lerp(cameraTransform.position, _position, t),
				Quaternion.Slerp(cameraTransform.rotation, _rotation, t));
		}

		// The turret anchor (null when there is no BTR, no player or no usable anchor) and whether the cannon view is wanted right now
		private static Transform GetAnchor(PlayerCameraController controller, out bool wanted)
		{
			wanted = false;
			var player = controller.Player;
			if (player == null || !player.IsYourPlayer)
			{
				return null;
			}

			var btrView = BtrController.Instance?.BtrView;
			var turret = btrView != null ? btrView.turret : null;
			Transform anchor = null;
			if (turret != null)
			{
				anchor = turret.machineGunLaunchPoint != null ? turret.machineGunLaunchPoint : turret.GunRoot;
			}
			if (anchor == null)
			{
				if (btrView != null && player.BtrState == EPlayerBtrState.Inside && !_loggedMissingAnchor)
				{
					_loggedMissingAnchor = true;
					Plugin.Log.LogWarning("BTR cannon camera: the BTR turret has no usable camera anchor, the normal view stays.");
				}
				return null;
			}

			var driving = player.BtrState == EPlayerBtrState.Inside && btrView.BtrState == EBtrState.Running;
			var screenOpen = Plugin.BtrCannonCamFirstPersonInScreens.Value && AnyScreenOpen();
			if (driving && screenOpen != _lastScreenOpen)
			{
				Plugin.Verbose("BTR cannon camera: " + (screenOpen ? "a screen is open, back to first person" : "screen closed, cannon view again"));
			}
			_lastScreenOpen = screenOpen;
			wanted = driving && !screenOpen;
			return anchor;
		}

		// Inventory, the driver's dialog, the transfer screen and every other full screen sit over the battle UI; while only the battle UI is up none is open
		private static bool AnyScreenOpen()
		{
			var current = EftScreenManager.Instance.CurrentScreenController;
			if (current == null)
			{
				return false;
			}
			var type = current.ScreenType;
			return type != EEftScreenType.BattleUI && type != EEftScreenType.None;
		}
	}
}
