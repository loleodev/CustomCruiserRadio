using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

public class CarPatch
{
	// Cached reflection fields — resolved once, used everywhere
	private static readonly FieldInfo CurrentRadioClipField =
		typeof(VehicleController).GetField("currentRadioClip", BindingFlags.Instance | BindingFlags.NonPublic);
	private static readonly FieldInfo RadioSignalQualityField =
		typeof(VehicleController).GetField("radioSignalQuality", BindingFlags.Instance | BindingFlags.NonPublic);
	private static readonly FieldInfo RadioSignalDecreaseThresholdField =
		typeof(VehicleController).GetField("radioSignalDecreaseThreshold", BindingFlags.Instance | BindingFlags.NonPublic);

	[HarmonyPatch(typeof(VehicleController), "Awake")]
	[HarmonyPostfix]
	public static void AwakePatch(VehicleController __instance)
	{
		// Cache the VehicleController reference for network callbacks
		CruiserTunesMod.CachedVehicleController = __instance;

		if (CruiserTunesMod.HasSongs)
		{
			AudioClip[] source = ((!CruiserTunesMod.IncludeOriginal.Value) ? CruiserTunesMod.CustomSongs : __instance.radioClips.Concat(CruiserTunesMod.CustomSongs).ToArray());
			source = source.OrderByDescending((AudioClip str) => GetAsciiSum(str.name)).ToArray();
			__instance.radioClips = source;
			__instance.radioAudio.loop = CruiserTunesMod.DoLoop.Value;
			if (CruiserTunesMod.instance != null)
			{
				CruiserTunesMod.instance.StartCoroutine(AudioSourceListener(__instance.radioAudio, __instance));
			}
		}
		static int GetAsciiSum(string str)
		{
			return str.Sum((char c) => c);
		}
	}

	public static void UpdateLooping(object sender, EventArgs e)
	{
		var found = UnityEngine.Object.FindObjectOfType(typeof(VehicleController));
		if (found is VehicleController vc)
		{
			vc.radioAudio.loop = CruiserTunesMod.DoLoop.Value;
		}
	}

	public static void ChangeVolume(object sender, EventArgs e)
	{
		var found = UnityEngine.Object.FindObjectOfType(typeof(VehicleController));
		if (found is VehicleController vc)
		{
			vc.radioAudio.volume = Mathf.Clamp(CruiserTunesMod.Volume.Value, 0f, 1.25f);
		}
	}

	[HarmonyPatch(typeof(VehicleController), "ChangeRadioStation")]
	[HarmonyPostfix]
	public static void ChangeRadioStationPatch(VehicleController __instance)
	{
		float playbackTime = 0f;
		if (CruiserTunesMod.DoRandomTime != null && CruiserTunesMod.DoRandomTime.Value)
		{
			if (__instance.radioAudio != null && __instance.radioAudio.clip != null && __instance.radioAudio.clip.length > 0f)
			{
				float trueLen = CruiserTunesMod.GetTrueClipLength(__instance.radioAudio.clip);
				float max = Mathf.Max(0.01f, trueLen - 0.1f);
				playbackTime = UnityEngine.Random.Range(0.01f, max);
			}
		}

		if (CruiserTunesMod.SyncPlaybackTimeMessage != null)
		{
			int station = 0;
			if (CurrentRadioClipField != null)
				station = (int)CurrentRadioClipField.GetValue(__instance);
			var syncData = new CruiserTunesMod.RadioSyncData { Station = station, PlaybackTime = playbackTime };
			CruiserTunesMod.PendingSyncData = syncData;

			bool isServer = Unity.Netcode.NetworkManager.Singleton != null && Unity.Netcode.NetworkManager.Singleton.IsServer;
			if (isServer)
				CruiserTunesMod.SyncPlaybackTimeMessage.SendClients(syncData);
			else
				CruiserTunesMod.SyncPlaybackTimeMessage.SendServer(syncData);
		}
	}

	[HarmonyPatch(typeof(VehicleController), "SetRadioOnLocalClient")]
	[HarmonyPrefix]
	public static void SetOnClientPatch(VehicleController __instance)
	{
		if (CurrentRadioClipField == null) return;
		if (__instance.radioClips == null || __instance.radioClips.Length == 0) return;
		CurrentRadioClipField.SetValue(__instance, (int)CurrentRadioClipField.GetValue(__instance) % __instance.radioClips.Length);
	}

	[HarmonyPatch(typeof(VehicleController), "SetRadioValues")]
	[HarmonyPrefix]
	public static void SetRadioValuesPatch(VehicleController __instance)
	{
		if (CruiserTunesMod.GoodQuality.Value)
		{
			__instance.radioAudio.volume = Mathf.Clamp(CruiserTunesMod.Volume.Value, 0f, 1.25f);
			__instance.radioInterference.volume = 0f;
		}
	}

	public static IEnumerator AudioSourceListener(AudioSource radio, VehicleController instance)
	{
		while (radio != null && instance != null)
		{
			AudioClip clip = radio.clip;
			yield return null;
			if (clip != radio.clip)
			{
				if (CruiserTunesMod.GoodQuality.Value)
				{
					radio.volume = Mathf.Clamp(CruiserTunesMod.Volume.Value, 0f, 1.25f);
					instance.radioInterference.volume = 0f;
				}
				if (CruiserTunesMod.instance != null)
				{
					CruiserTunesMod.instance.StartCoroutine(PlayNotification(instance));
				}
			}
		}
	}

	public static IEnumerator PlayNotification(VehicleController instance)
	{
		if (CruiserTunesMod.DoMessage.Value)
		{
			AudioClip clip = instance.radioAudio.clip;
			yield return new WaitForSeconds(2f);
			if (clip == instance.radioAudio.clip)
			{
				var localPlayer = StartOfRound.Instance.localPlayerController;
				if (localPlayer != null && instance != null)
				{
					if (Vector3.Distance(localPlayer.transform.position, instance.transform.position) < 10f)
					{
						HUDManager.Instance.DisplayTip("Now Playing:", clip.name.Replace("Radio_", "") + " - " + FormatLength(CruiserTunesMod.GetTrueClipLength(clip)), false, false, "LC_Tip1");
					}
				}
			}
		}
	}

	public static string FormatLength(float lengthInSeconds)
	{
		TimeSpan ts = TimeSpan.FromSeconds(lengthInSeconds);
		return ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
	}

	public static void ChangeRadioStationWithoutServer(VehicleController instance)
	{
		if (CurrentRadioClipField == null || RadioSignalQualityField == null || RadioSignalDecreaseThresholdField == null) return;
		if (instance.radioClips == null || instance.radioClips.Length == 0) return;

		int num = ((int)CurrentRadioClipField.GetValue(instance) + 1) % instance.radioClips.Length;
		CurrentRadioClipField.SetValue(instance, num);
		instance.radioAudio.clip = instance.radioClips[num];
		if (CruiserTunesMod.instance != null)
		{
			CruiserTunesMod.instance.StartCoroutine(CruiserTunesMod.EnsureClipReadyAndPlay(instance.radioAudio));
		}
		else
		{
			if (CruiserTunesMod.DoRandomTime != null && CruiserTunesMod.DoRandomTime.Value
				&& instance.radioAudio.clip != null && instance.radioAudio.clip.length > 0f)
			{
				float trueLen = CruiserTunesMod.GetTrueClipLength(instance.radioAudio.clip);
				float max = Mathf.Max(0.01f, trueLen - 0.1f);
				instance.radioAudio.time = UnityEngine.Random.Range(0.01f, max);
			}
			else
			{
				instance.radioAudio.time = 0f;
			}
			instance.radioAudio.Play();
		}
		float num2 = 10f;
		float num3 = (float)RadioSignalQualityField.GetValue(instance);
		switch ((int)Mathf.Round(num3))
		{
		case 3:
			num3 = 1f;
			num2 = 10f;
			break;
		case 0:
			num3 = 3f;
			num2 = 90f;
			break;
		case 1:
			num3 = 2f;
			num2 = 70f;
			break;
		case 2:
			num3 = 1f;
			num2 = 30f;
			break;
		}
		RadioSignalQualityField.SetValue(instance, num3);
		RadioSignalDecreaseThresholdField.SetValue(instance, num2);
		ChangeRadioStationPatch(instance);
	}
}
