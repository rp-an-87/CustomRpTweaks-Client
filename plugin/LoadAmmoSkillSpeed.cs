using System;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using UnityEngine;

namespace CustomRpTweaks.Client
{
	// Continuous Load Ammo (com.ozen.continuousloadammo) slows you to a flat share of your walk speed while you load ammo
	// outside the inventory, and reads that share from its 'Speed Limit' setting every time a load starts. This keeps that
	// setting in step with the Mag Drills level instead, by writing to the other plugin's config entry in memory only
	// (nothing of ours is patched, and its .cfg file isn't rewritten by us). That plugin multiplies the setting by the
	// base walk speed; the setting is written as share * the walk-speed limit from weight, so the share we ask for is
	// of the top speed you can actually walk at right now.
	internal static class LoadAmmoSkillSpeed
	{
		internal const string TargetGuid = "com.ozen.continuousloadammo";
		private const float EliteLevel = 51f;
		private const float CheckInterval = 1f;

		private static ConfigEntry<float> _target;
		private static ConfigFile _targetConfig;
		private static bool _searched;
		private static float? _original;
		private static float _nextCheck;

		// Hooked to the other plugin and switched on: the skill tooltip row is only shown then
		internal static bool Active => _target != null && Plugin.ScaleLoadAmmoSpeed != null && Plugin.ScaleLoadAmmoSpeed.Value;

		internal static void Update()
		{
			if (Plugin.ScaleLoadAmmoSpeed == null || Time.unscaledTime < _nextCheck)
			{
				return;
			}
			_nextCheck = Time.unscaledTime + CheckInterval;

			if (!_searched)
			{
				_searched = true;
				Find();
			}
			if (_target == null)
			{
				return;
			}

			var player = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;
			var level = player?.Profile?.Skills?.MagDrills?.Level;
			if (!Plugin.ScaleLoadAmmoSpeed.Value || level == null || player.Physical == null)
			{
				// Switched off, or no raid running: put back what the other plugin had
				Set(_original);
				return;
			}

			if (_original == null)
			{
				_original = _target.Value;
			}
			// Elite is absolute (a share of the unencumbered walk speed, as Continuous Load Ammo does it); the levels below are relative to the weight-limited speed
			Set(level.Value >= EliteLevel ? ShareAtLevel(level.Value) : ShareAtLevel(level.Value) * Mathf.Clamp01(player.Physical.WalkSpeedLimit));
		}

		private static void Find()
		{
			try
			{
				if (!Chainloader.PluginInfos.TryGetValue(TargetGuid, out var info) || info.Instance == null)
				{
					Plugin.Log.LogInfo($"{TargetGuid} not found, Mag Drills loading speed does nothing.");
					return;
				}
				var property = info.Instance.GetType().GetProperty("SpeedLimit", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				_target = property?.GetValue(null, null) as ConfigEntry<float>;
				_targetConfig = (info.Instance as BaseUnityPlugin)?.Config;
				if (_target == null)
				{
					Plugin.Log.LogWarning($"{TargetGuid} has no 'SpeedLimit' setting of the expected type (changed in an update?), Mag Drills loading speed does nothing.");
					return;
				}
				Plugin.Log.LogInfo($"Mag Drills loading speed hooked to {TargetGuid} (its Speed Limit is {_target.Value}).");
			}
			catch (Exception e)
			{
				Plugin.Log.LogError($"Could not hook {TargetGuid}: {e}");
				_target = null;
			}
		}

		// Even rise from level 0 to level 50, then the elite level has its own value
		internal static float ShareAtLevel(float level)
		{
			if (level >= EliteLevel)
			{
				return Plugin.LoadAmmoSpeedAtElite.Value;
			}
			return Mathf.Lerp(Plugin.LoadAmmoSpeedAtLevel0.Value, Plugin.LoadAmmoSpeedAtLevel50.Value, Mathf.Clamp01(level / (EliteLevel - 1f)));
		}

		private static void Set(float? value)
		{
			if (value == null || Mathf.Abs(_target.Value - value.Value) < 0.0005f)
			{
				return;
			}
			var save = _targetConfig != null && _targetConfig.SaveOnConfigSet;
			if (save)
			{
				_targetConfig.SaveOnConfigSet = false;
			}
			try
			{
				_target.Value = Mathf.Clamp01(value.Value);
			}
			finally
			{
				if (save)
				{
					_targetConfig.SaveOnConfigSet = true;
				}
			}
		}
	}
}
