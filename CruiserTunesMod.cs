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
using UnityEngine.Networking;

[BepInDependency("LethalNetworkAPI")]
[BepInPlugin("Mellowdy.CruiserTunes", "CruiserTunes", "1.3.0")]
public class CruiserTunesMod : BaseUnityPlugin
{
	private const string modGUID = "Mellowdy.CruiserTunes";

	private const string modName = "CruiserTunes";

	private const string modVersion = "1.3.0";

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
		harmony.PatchAll();
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
		float loadingTime = Time.time;
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
				if (loadingTime + maxLoading < Time.time)
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

	private static void OnSyncDataReceived(RadioSyncData data)
	{
		PendingSyncData = data;
		// If the RPC already fired and the clip is set, apply the time now
		var vc = UnityEngine.Object.FindObjectOfType<VehicleController>();
		if (vc != null && vc.radioAudio != null && vc.radioAudio.clip != null
			&& data.Station >= 0 && data.Station < vc.radioClips.Length
			&& vc.radioAudio.clip == vc.radioClips[data.Station] && data.PlaybackTime > 0f)
		{
			vc.radioAudio.time = Mathf.Clamp(data.PlaybackTime, 0.01f, vc.radioAudio.clip.length - 0.1f);
			PendingSyncData = null;
		}
	}

	[HarmonyPatch(typeof(VehicleController), "SetRadioStationClientRpc")]
	[HarmonyPostfix]
	public static void SetRadioStationPatch(ref int radioStation, VehicleController __instance)
	{
	    if (radioStation < 0 || radioStation >= __instance.radioClips.Length) return;
	    __instance.radioAudio.clip = __instance.radioClips[radioStation];

	    if (PendingSyncData.HasValue && PendingSyncData.Value.Station == radioStation)
	    {
	        float timeToApply = PendingSyncData.Value.PlaybackTime;
	        PendingSyncData = null;
	        if (instance != null)
	            instance.StartCoroutine(EnsureClipReadyAndPlayAt(__instance.radioAudio, timeToApply));
	        else
	        {
	            __instance.radioAudio.time = timeToApply;
	            __instance.radioAudio.Play();
	        }
	    }
	    else
	    {
	        // Message hasn't arrived yet — wait briefly for it
	        if (instance != null)
	            instance.StartCoroutine(WaitForSyncThenPlay(__instance.radioAudio, radioStation));
	        else
	            __instance.radioAudio.Play();
	    }
	}

	private static IEnumerator WaitForSyncThenPlay(AudioSource audio, int expectedStation)
	{
	    float timeout = 0.5f;
	    float start = Time.time;
	    while (!PendingSyncData.HasValue || PendingSyncData.Value.Station != expectedStation)
	    {
	        if (Time.time - start > timeout) break;
	        yield return null;
	    }

	    if (PendingSyncData.HasValue && PendingSyncData.Value.Station == expectedStation)
	    {
	        float timeToApply = PendingSyncData.Value.PlaybackTime;
	        PendingSyncData = null;
	        yield return EnsureClipReadyAndPlayAt(audio, timeToApply);
	    }
	    else
	    {
	        PendingSyncData = null;
	        yield return EnsureClipReadyAndPlay(audio);
	    }
	}

	// Helper coroutine to wait for readiness, set a specific time, then play
	public static IEnumerator EnsureClipReadyAndPlayAt(AudioSource audio, float timeToSet)
	{
	    float timeout = 5f;
	    float start = Time.time;
	    while (audio == null || audio.clip == null || audio.clip.length <= 0f || !audio.clip.isReadyToPlay)
	    {
	        if (Time.time - start > timeout) break;
	        yield return null;
	    }

	    if (audio == null || audio.clip == null || audio.clip.length <= 0f) yield break;

	    audio.time = Mathf.Clamp(timeToSet, 0.01f, audio.clip.length - 0.1f);
	    audio.Play();

	    // reapply a few frames to override transient resets
	    int frames = 6;
	    while (frames-- > 0)
	    {
	        if (audio == null || audio.clip == null) yield break;
	        if (Mathf.Abs(audio.time - timeToSet) > 0.05f) audio.time = timeToSet;
	        yield return null;
	    }
	}

    // Helper coroutine to wait for readiness and play (no explicit time set)
    public static IEnumerator EnsureClipReadyAndPlay(AudioSource audio)
    {
        float timeout = 5f;
        float start = Time.time;
        while (audio == null || audio.clip == null || audio.clip.length <= 0f || !audio.clip.isReadyToPlay)
        {
            if (Time.time - start > timeout) break;
            yield return null;
        }

        if (audio == null || audio.clip == null || audio.clip.length <= 0f) yield break;

        audio.time = Mathf.Clamp(audio.time, 0.01f, audio.clip.length - 0.1f);
        audio.Play();

        // reapply a few frames to override transient resets
        int frames = 6;
        while (frames-- > 0)
        {
            if (audio == null || audio.clip == null) yield break;
            if (audio.time < 0.01f) audio.time = 0.01f;
            yield return null;
        }
    }

}
