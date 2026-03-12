using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LethalNetworkAPI;
using UnityEngine;
using Unity.Netcode;
using UnityEngine.Networking;

[BepInDependency("LethalNetworkAPI")]
[BepInPlugin(modGUID, modName, modVersion)]
public class CruiserTunesMod : BaseUnityPlugin
{
	private const string modGUID = "Mellowdy.CruiserTunes";

	private const string modName = "CruiserTunes";

	private const string modVersion = "1.4.0";

	private readonly Harmony harmony = new Harmony("Mellowdy.CruiserTunes");

	public static ManualLogSource mls;

	public static CruiserTunesMod instance;

	public static ConfigEntry<bool> IncludeOriginal;

	public static ConfigEntry<bool> DoMessage;

	public static ConfigEntry<bool> DoLoop;

	public static ConfigEntry<bool> DoRandomTime;

	public static ConfigEntry<bool> GoodQuality;

	public static ConfigEntry<float> Volume;

	public static bool HasSongs;

	public static AudioClip[] CustomSongs;

	[System.Serializable]
	public struct RadioSyncData
	{
		public int Station;
		public float PlaybackTime;
	}

	public static LNetworkMessage<RadioSyncData> SyncPlaybackTimeMessage;
	public static RadioSyncData? PendingSyncData;

	// Cached VehicleController to avoid FindObjectOfType on hot path
	public static VehicleController CachedVehicleController;

	// Coroutine generation counter — prevents stale coroutines from fighting
	private static int _playbackGeneration;
	private static Coroutine _activePlaybackCoroutine;

	private const string supportedAudioFileTypes = ".mp3 or .wav or .ogg";

	private string folderName = "CustomSongs";

	private string altFolderName = "Custom Songs";

	private List<AudioClip> SongList = new List<AudioClip>();

	private bool[] loadingList;

	private void Awake()
	{
		if (instance == null)
		{
			instance = this;
		}
		mls = Logger;
		mls.LogInfo("Loading...");
		DontDestroyOnLoad(this.gameObject);
		this.gameObject.hideFlags = (HideFlags)61;
		IncludeOriginal = ((BaseUnityPlugin)this).Config.Bind<bool>("Settings", "Include Original Songs", true, "The radio is able to play the orignal songs");
		DoMessage = ((BaseUnityPlugin)this).Config.Bind<bool>("Settings", "Notify When New Song", true, "The radio notifies nearby players what song is now playing");
		DoLoop = ((BaseUnityPlugin)this).Config.Bind<bool>("Settings", "Loop", false, "The radio loops songs upon completion");
		DoLoop.SettingChanged += CarPatch.UpdateLooping;
		DoRandomTime = ((BaseUnityPlugin)this).Config.Bind<bool>("Settings", "Random Start Time", false, "The randomly picks a point in the song to start playing when switching channels");
		GoodQuality = ((BaseUnityPlugin)this).Config.Bind<bool>("Settings", "Always have good quality", true, "When switching between channels the quality will remain good");
		Volume = ((BaseUnityPlugin)this).Config.Bind<float>("Settings", "Volume", 0.75f, "Volume of the radio (does not require restart)");
		Volume.SettingChanged += CarPatch.ChangeVolume;
		SyncPlaybackTimeMessage = LNetworkMessage<RadioSyncData>.Connect("CruiserTunes_SyncTime",
			onServerReceived: OnServerReceivedSync,
			onClientReceived: OnSyncDataReceived);
		string directoryName = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
		string text = Path.Combine(directoryName, folderName);
		List<string> list = new List<string>();
		List<string> list2 = new List<string>();
		string text2 = Path.Combine(Directory.GetParent(directoryName).FullName, folderName);
		string text3 = Path.Combine(Directory.GetParent(directoryName).FullName, altFolderName);
		if (Directory.Exists(text2))
		{
			list.Add(text2);
		}
		else if (Directory.Exists(text3))
		{
			list.Add(text3);
		}
		string text4 = Path.Combine(Directory.GetParent(directoryName).Parent.FullName, altFolderName);
		if (Directory.Exists(text4))
		{
			list.Add(text4);
		}
		string[] directories = Directory.GetDirectories(Directory.GetParent(directoryName).FullName);
		foreach (string path in directories)
		{
			string text5 = Path.Combine(path, folderName);
			if (Directory.Exists(text5))
			{
				list.Add(text5);
			}
		}
		if (list.Count == 0)
		{
			Directory.CreateDirectory(text);
			mls.LogWarning((object)("Folder not found. One has been created at: '" + text + "'"));
			return;
		}
		foreach (string item in list)
		{
			list2.AddRange(Directory.GetFiles(item));
		}
		list2 = RemoveDuplicateFiles(list2);
		bool flag = false;
		loadingList = new bool[list2.Count];
		for (int j = 0; j < list2.Count; j++)
		{
			string text6 = list2[j];
			loadingList[j] = false;
			string fileName = Path.GetFileName(text6);
			string text7 = Path.GetExtension(text6).ToLower();
			if (text7 == ".mp3" || text7 == ".wav" || text7 == ".ogg")
			{
				((MonoBehaviour)this).StartCoroutine(LoadAudioClip(text6, j, text7));
				continue;
			}
			loadingList[j] = true;
			if (text7 != ".old")
			{
				mls.LogWarning((object)(fileName + " is of invalid extention. Must be a .mp3 or .wav or .ogg file or .old file"));
				if (!flag)
				{
					mls.LogWarning((object)".old files ignore this message being printed.");
				}
				flag = true;
			}
		}
		if (loadingList.Length == 0)
		{
			mls.LogError((object)"No songs found");
		}
		harmony.PatchAll(typeof(CruiserTunesMod));
		harmony.PatchAll(typeof(CarPatch));
		((MonoBehaviour)this).StartCoroutine(EndOfPatch());
	}

	private static IEnumerator EndOfPatch()
	{
		mls.LogInfo((object)("Loading " + instance.loadingList.Length + " songs"));
		bool finishedLoading = false;
		while (!finishedLoading)
		{
			finishedLoading = true;
			bool[] array = instance.loadingList;
			for (int i = 0; i < array.Length; i++)
			{
				if (!array[i])
				{
					finishedLoading = false;
					break;
				}
			}
			if (!finishedLoading)
			{
				yield return null;
			}
		}
		instance.loadingList = null;
		mls.LogInfo((object)("Finished loading songs, final song count at:" + instance.SongList.Count));
		if (instance.SongList.Count == 0)
		{
			string currentDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
			string path = Path.Combine(currentDirectory, instance.folderName).Replace("\\", "/");
			mls.LogError((object)"No songs found in folder");
			mls.LogError((object)("Make sure to add any .mp3 or .wav or .ogg files you want as songs to: '" + path + "'"));
		}
		CustomSongs = instance.SongList.ToArray();
		instance.SongList.Clear();
		HasSongs = CustomSongs.Length != 0 || IncludeOriginal.Value;
		mls.LogInfo((object)"CruiserTunes has been loaded with loleo's fix");
	}

	private static IEnumerator LoadAudioClip(string filePath, int index, string fileType)
	{
		float loadingTime = Time.realtimeSinceStartup;
		float maxLoading = 5f;
		string fileName = Path.GetFileName(filePath);
		mls.LogInfo((object)("Loading " + fileName));
		AudioType audioType = GetAudioType(fileType);
		UnityWebRequest loader = UnityWebRequestMultimedia.GetAudioClip(filePath, audioType);
		try
		{
			loader.SendWebRequest();
			while (!loader.isDone)
			{
				if (loadingTime + maxLoading < Time.realtimeSinceStartup)
				{
					mls.LogError((object)("Error loading clip from path: " + fileName));
					instance.loadingList[index] = true;
					yield break;
				}
				yield return null;
			}
			instance.loadingList[index] = true;
			if (loader.error != null)
			{
				mls.LogError((object)("Error loading clip from path: " + fileName + "\n" + loader.error));
				yield break;
			}
			AudioClip content = DownloadHandlerAudioClip.GetContent(loader);
			content.name = fileName;
			instance.SongList.Add(content);
		}
		finally
		{
			((IDisposable)loader)?.Dispose();
		}
	}

	public static AudioType GetAudioType(string fileType)
	{
		return (AudioType)(fileType switch
		{
			".mp3" => 13,
			".wav" => 20,
			".ogg" => 14,
			_ => 13,
		});
	}

	public static List<string> RemoveDuplicateFiles(List<string> filePaths)
	{
		HashSet<string> hashSet = new HashSet<string>();
		List<string> list = new List<string>();
		foreach (string filePath in filePaths)
		{
			string fileName = Path.GetFileName(filePath);
			if (!hashSet.Contains(fileName))
			{
				hashSet.Add(fileName);
				list.Add(filePath);
			}
		}
		return list;
	}

	// Server relay: when a client sends sync data, broadcast to all clients except sender
	private static void OnServerReceivedSync(RadioSyncData data, ulong clientId)
	{
		SyncPlaybackTimeMessage?.SendClients(data);
	}

	// True clip duration using samples/frequency — clip.length can report double for some MP3s
	public static float GetTrueClipLength(AudioClip clip)
	{
		if (clip == null || clip.frequency <= 0) return 0f;
		return clip.samples / (float)clip.frequency;
	}

	private static void OnSyncDataReceived(RadioSyncData data)
	{
		// Ignore sync messages we originated ourselves
		if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
		{
			// On the server/host, we already set PendingSyncData locally in ChangeRadioStationPatch
			// Only apply if this data differs from what we already have (i.e., from another client)
			if (PendingSyncData.HasValue && PendingSyncData.Value.Station == data.Station
				&& Mathf.Approximately(PendingSyncData.Value.PlaybackTime, data.PlaybackTime))
				return;
		}

		PendingSyncData = data;
		var vc = CachedVehicleController;
		if (vc != null && vc.radioAudio != null && vc.radioAudio.clip != null
			&& data.Station >= 0 && data.Station < vc.radioClips.Length
			&& vc.radioAudio.clip == vc.radioClips[data.Station] && data.PlaybackTime > 0f)
		{
			float maxTime = Mathf.Max(0.01f, GetTrueClipLength(vc.radioAudio.clip) - 0.1f);
			vc.radioAudio.time = Mathf.Clamp(data.PlaybackTime, 0.01f, maxTime);
			PendingSyncData = null;
		}
	}

	[HarmonyPatch(typeof(VehicleController), "SetRadioStationClientRpc")]
	[HarmonyPostfix]
	public static void SetRadioStationPatch(ref int radioStation, VehicleController __instance)
	{
		if (radioStation < 0 || radioStation >= __instance.radioClips.Length) return;

		// Only assign clip if different to avoid resetting AudioSource state
		if (__instance.radioAudio.clip != __instance.radioClips[radioStation])
			__instance.radioAudio.clip = __instance.radioClips[radioStation];

		// Cancel any previous playback coroutine to prevent fighting
		int generation = ++_playbackGeneration;
		if (_activePlaybackCoroutine != null && instance != null)
		{
			instance.StopCoroutine(_activePlaybackCoroutine);
			_activePlaybackCoroutine = null;
		}

		if (PendingSyncData.HasValue && PendingSyncData.Value.Station == radioStation)
		{
			float timeToApply = PendingSyncData.Value.PlaybackTime;
			PendingSyncData = null;
			if (instance != null)
				_activePlaybackCoroutine = instance.StartCoroutine(EnsureClipReadyAndPlayAt(__instance.radioAudio, timeToApply, generation));
			else
			{
				if (!__instance.radioAudio.isPlaying) __instance.radioAudio.Play();
				float maxTime = Mathf.Max(0.01f, GetTrueClipLength(__instance.radioAudio.clip) - 0.1f);
				__instance.radioAudio.time = Mathf.Clamp(timeToApply, 0.01f, maxTime);
			}
		}
		else
		{
			if (instance != null)
				_activePlaybackCoroutine = instance.StartCoroutine(WaitForSyncThenPlay(__instance.radioAudio, radioStation, generation));
			else
				__instance.radioAudio.Play();
		}
	}

	private static IEnumerator WaitForSyncThenPlay(AudioSource audio, int expectedStation, int generation)
	{
		float timeout = 0.5f;
		float start = Time.realtimeSinceStartup;
		while (!PendingSyncData.HasValue || PendingSyncData.Value.Station != expectedStation)
		{
			if (generation != _playbackGeneration) yield break;
			if (Time.realtimeSinceStartup - start > timeout) break;
			yield return null;
		}

		if (generation != _playbackGeneration) yield break;

		if (PendingSyncData.HasValue && PendingSyncData.Value.Station == expectedStation)
		{
			float timeToApply = PendingSyncData.Value.PlaybackTime;
			PendingSyncData = null;
			yield return EnsureClipReadyAndPlayAt(audio, timeToApply, generation);
		}
		else
		{
			// Don't clear PendingSyncData on timeout — late arrival will be handled next change
			yield return EnsureClipReadyAndPlay(audio);
		}
	}

	// Helper coroutine to wait for readiness, set a specific time, then play
	public static IEnumerator EnsureClipReadyAndPlayAt(AudioSource audio, float timeToSet, int generation)
	{
		float timeout = 5f;
		float start = Time.realtimeSinceStartup;
		while (audio == null || audio.clip == null || audio.clip.length <= 0f || audio.clip.loadState != AudioDataLoadState.Loaded)
		{
			if (generation != _playbackGeneration) yield break;
			if (Time.realtimeSinceStartup - start > timeout) break;
			yield return null;
		}

		if (generation != _playbackGeneration) yield break;
		if (audio == null || audio.clip == null || audio.clip.length <= 0f) yield break;

		float maxTime = Mathf.Max(0.01f, GetTrueClipLength(audio.clip) - 0.1f);
		float clampedTime = Mathf.Clamp(timeToSet, 0.01f, maxTime);

		// Play first if not already playing, then seek — Unity's Play() resets time to 0
		if (!audio.isPlaying) audio.Play();
		audio.time = clampedTime;

		// Reapply for ~0.5s to override transient resets from game code
		// Account for natural playback advance when checking for drift
		float guardStart = Time.realtimeSinceStartup;
		float guardDuration = 0.5f;
		while (Time.realtimeSinceStartup - guardStart < guardDuration)
		{
			if (generation != _playbackGeneration) yield break;
			if (audio == null || audio.clip == null) yield break;
			float elapsed = Time.realtimeSinceStartup - guardStart;
			float expectedTime = clampedTime + elapsed;
			if (Mathf.Abs(audio.time - expectedTime) > 1.0f)
				audio.time = clampedTime + elapsed;
			yield return null;
		}
	}

	// Helper coroutine to wait for readiness and play (no explicit time set)
	public static IEnumerator EnsureClipReadyAndPlay(AudioSource audio)
	{
		float timeout = 5f;
		float start = Time.realtimeSinceStartup;
		while (audio == null || audio.clip == null || audio.clip.length <= 0f || audio.clip.loadState != AudioDataLoadState.Loaded)
		{
			if (Time.realtimeSinceStartup - start > timeout) break;
			yield return null;
		}

		if (audio == null || audio.clip == null || audio.clip.length <= 0f) yield break;

		if (!audio.isPlaying) audio.Play();
		float maxTime = Mathf.Max(0.01f, GetTrueClipLength(audio.clip) - 0.1f);
		audio.time = Mathf.Clamp(audio.time, 0.01f, maxTime);
	}

}
