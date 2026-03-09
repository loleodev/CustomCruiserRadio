using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

public class CarPatch
{
	[HarmonyPatch(typeof(VehicleController), "Awake")]
	[HarmonyPostfix]
	public static void AwakePatch(VehicleController __instance)
	{
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
				float max = Math.Max(0.01f, __instance.radioAudio.clip.length - 0.1f);
				playbackTime = UnityEngine.Random.Range(0.01f, max);
			}
		}

		if (CruiserTunesMod.SyncPlaybackTimeMessage != null)
		{
			FieldInfo clipField = __instance.GetType().GetField("currentRadioClip", BindingFlags.Instance | BindingFlags.NonPublic);
			int station = clipField != null ? (int)clipField.GetValue(__instance) : 0;
			var syncData = new CruiserTunesMod.RadioSyncData { Station = station, PlaybackTime = playbackTime };
			CruiserTunesMod.PendingSyncData = syncData;
			CruiserTunesMod.SyncPlaybackTimeMessage.SendClients(syncData);
		}
	}

	[HarmonyPatch(typeof(VehicleController), "SetRadioOnLocalClient")]
	[HarmonyPrefix]
	public static void SetOnClientPatch(VehicleController __instance)
	{
		Type type = ((object)__instance).GetType();
		FieldInfo field = type.GetField("currentRadioClip", BindingFlags.Instance | BindingFlags.NonPublic);
		field.SetValue(__instance, (int)field.GetValue(__instance) % __instance.radioClips.Length);
	}

	[HarmonyPatch(typeof(VehicleController), "SetRadioValues")]
	[HarmonyPrefix]
	public static void SetRadioValuesPatch(VehicleController __instance)
	{
		if (CruiserTunesMod.GoodQuality.Value)
		{
			__instance.radioAudio.volume = 1f;
			__instance.radioInterference.volume = 0f;
		}
	}

	[HarmonyPatch(typeof(VehicleController), "SwitchRadio")]
	[HarmonyPrefix]
	public static void SwitchRadioPatch(VehicleController __instance)
	{
	}

	public static IEnumerator AudioSourceListener(AudioSource radio, VehicleController instance)
	{
		while (radio != null && instance != null)
		{
			AudioClip clip = radio.clip;
			instance.radioAudio.loop = CruiserTunesMod.DoLoop.Value;
			yield return null;
			if (clip != radio.clip)
			{
				if (CruiserTunesMod.GoodQuality.Value)
				{
					radio.volume = 1f;
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
						HUDManager.Instance.DisplayTip("Now Playing:", clip.name.Replace("Radio_", "") + " - " + FormatLength(clip.length), false, false, "LC_Tip1");
					}
				}
			}
		}
	}

	public static string FormatLength(float lengthInSeconds)
	{
		int num = Mathf.FloorToInt(lengthInSeconds / 3600f);
		int num2 = Mathf.FloorToInt(lengthInSeconds % 3600f / 60f);
		int num3 = Mathf.FloorToInt(lengthInSeconds % 60f);
		return $"{num:00}:{num2:00}:{num3:00}";
	}

	public static IEnumerator EnsureClipReadyAndPlay(AudioSource audio)
	{
		float timeout = 5f;
		float start = Time.time;
		while (audio == null || audio.clip == null || audio.clip.length <= 0f || !audio.clip.isReadyToPlay)
		{
			if (Time.time - start > timeout)
			{
				break;
			}
			yield return null;
		}

		if (audio == null || audio.clip == null || audio.clip.length <= 0f) yield break;

		if (!audio.isPlaying) audio.Play();

		if (CruiserTunesMod.DoRandomTime != null && CruiserTunesMod.DoRandomTime.Value)
		{
			float max = Mathf.Max(0.01f, audio.clip.length - 0.1f);
			audio.time = UnityEngine.Random.Range(0.01f, max);
		}
		else
		{
			audio.time = 0f;
		}
	}

	public static void ChangeRadioStationWithoutServer(VehicleController instance)
	{
		Type type = ((object)instance).GetType();
		FieldInfo field = type.GetField("radioSignalDecreaseThreshold", BindingFlags.Instance | BindingFlags.NonPublic);
		FieldInfo field2 = type.GetField("radioSignalQuality", BindingFlags.Instance | BindingFlags.NonPublic);
		FieldInfo field3 = type.GetField("currentRadioClip", BindingFlags.Instance | BindingFlags.NonPublic);
		int num = ((int)field3.GetValue(instance) + 1) % instance.radioClips.Length;
		field3.SetValue(instance, num);
		instance.radioAudio.clip = instance.radioClips[num];
		if (CruiserTunesMod.instance != null)
		{
			CruiserTunesMod.instance.StartCoroutine(EnsureClipReadyAndPlay(instance.radioAudio));
		}
		else
		{
			if (CruiserTunesMod.DoRandomTime.Value && instance.radioAudio.clip != null && instance.radioAudio.clip.length > 0f)
			{
				float max = Math.Max(0.01f, instance.radioAudio.clip.length - 0.1f);
				instance.radioAudio.time = UnityEngine.Random.Range(0.01f, max);
			}
			else
			{
				instance.radioAudio.time = 0f;
			}
			instance.radioAudio.Play();
		}
		float num2 = 10f;
		float num3 = (float)field2.GetValue(instance);
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
		field2.SetValue(instance, num3);
		field.SetValue(instance, num2);
		ChangeRadioStationPatch(instance);
	}
}
