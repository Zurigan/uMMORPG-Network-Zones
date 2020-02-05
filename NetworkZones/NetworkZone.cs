// Spawns one server zone process per scene.
// Also shuts down other zones when the main one died or was terminated.
//
// IMPORTANT: we do not EVER set manager.onlineScene/offlineScene. This part of
// UNET is broken and will cause all kinds of random issues when the server
// forces the client to reload the scene while receiving initialization messages
// already. Instead we always load the scene manually, then connect to the
// server afterwards.
using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Mirror;
using SQLite;

[RequireComponent(typeof(NetworkManager), typeof(ApathyTransport), typeof(TelepathyTransport))]
public class NetworkZone : MonoBehaviour
{
	#region Variables
	[HideInInspector]
	public static NetworkZone Instance { get; private set; }

	// Pretty name for the zone
	[SerializeField]
	private string ZoneID = "World1";

	// Paths to the scenes to spawn
	[SerializeField]
	private string[] scenePathsToSpawn = { "Assets/uMMORPG/Scenes/World.unity" };

	[Header("Components")]
	[SerializeField]
    private NetworkManager manager = null;

	[SerializeField]
	private ApathyTransport apathyTransport = null;

	[SerializeField]
	private TelepathyTransport telepathyTransport = null;

    // write online time to db every 'writeInterval'
    // die if not online for 'writeInterval * timeoutMultiplier'
    [Header("AliveCheck")]

	[SerializeField, Range(1, 10)]
	private float writeInterval = 1;

    [SerializeField, Range(2, 10)]
	private float timeoutMultiplier = 3;

	[SerializeField]
    private ushort originalPort = 7777;

	// switch server packet handler
	[HideInInspector]
	public string autoSelectCharacter { get; private set; }

	[HideInInspector]
	private bool autoConnectClient;
	#endregion

	#region CommandLine Args
	private string ParseScenePathFromArgs()
    {
        // note: args are null on android
        String[] args = Environment.GetCommandLineArgs();
        if (args != null)
        {
            int index = args.ToList().FindIndex(arg => arg == "-scenePath");
            return 0 <= index && index < args.Length - 1 ? args[index+1] : "";
        }
        return "";
    }

    private string ArgsString()
    {
        // note: first arg is always process name or empty
        // note: args are null on android
        String[] args = Environment.GetCommandLineArgs();
        return args != null ? String.Join(" ", args.Skip(1).ToArray()) : "";
    }

    // full path to game executable
    // -> osx: ../game.app/Contents/MacOS/game
    // -> GetCurrentProcess, Application.dataPaht etc. all aren't good for this
    private string processPath
    {
        get
        {
            // note: args are null on android
            String[] args = Environment.GetCommandLineArgs();
            return args != null ? args[0] : "";
        }
    }
	#endregion

    private void Awake()
    {
		// if someone accidentally adds another NetworkZone component to
		// another scene then deadlocks will happen because they will reload
		// over and over again. double check to be sure.
		if (Instance != null)
		{
			print("Multiple NetworkZone components in the Scene will cause Deadlocks! Destroying Duplicate.");
			Destroy(this);
			return;
		}

		Instance = this;

		// setup OnSceneLoaded callback
		SceneManager.sceneLoaded -= OnSceneLoaded;
		SceneManager.sceneLoaded += OnSceneLoaded;

        // was this process spawned with -scene parameter?
        string scenePath = ParseScenePathFromArgs();
        if (!string.IsNullOrEmpty(scenePath))
        {
	        // set network port to port + index, this new port will be used in NetworkServer.Start()  
	        var sceneIndex = SceneUtility.GetBuildIndexByScenePath(scenePath);
	        var newPort = (ushort)(originalPort + sceneIndex);
	        print("[Zones] setting zone port: " + newPort + " for scene: "+Path.GetFileNameWithoutExtension(scenePath));
	        SetPort(newPort);
	        
	        StartCoroutine(WaitOnServerReady(scenePath));
        }
    }
    
    private IEnumerator WaitOnServerReady(string scenePath)
    {
	    const int waitInterval = 1;

	    while (!manager.isNetworkActive)
	    {
		    print("[Zones] waiting on manager.isNetworkActive");
		    yield return waitInterval;
	    }
	    
	    // convert scene path to scene name
	    var sceneName = Path.GetFileNameWithoutExtension(scenePath);

	    print("[Zones] switching zone server scene to: " + sceneName);

	    manager.ServerChangeScene(sceneName);
    }

	private void OnDestroy()
	{
		if (Instance == this)
		{
			Instance = null;
			StopAllCoroutines();
		}
	}

	private void SetPort(ushort portNum)
	{
		apathyTransport.port = portNum;
		telepathyTransport.port = portNum;
	}

	public void ClearSelectCharacter()
	{
		autoSelectCharacter = null;
	}

	public void SpawnProcesses()
    {
		// Only standalone servers can call any of this
		if (Application.isEditor ||
			Application.isMobilePlatform ||
			Application.isConsolePlatform ||
			!NetworkServer.active)
		{
			return;
		}
		// Only spawn additional processes for the main instance (if no -scene parameter were passed)
		else if (!string.IsNullOrEmpty(ParseScenePathFromArgs()))
		{
			return;
		}

        // write zone online time every few seconds
        print("[Zones]: main process starts online writer...");
		StartCoroutine("UpdateMainZoneOnline");

        print("[Zones]: main process spawns siblings...");
        foreach (string scenePath in scenePathsToSpawn)
        {
			if (string.IsNullOrEmpty(scenePath))
			{
				continue;
			}

            // only spawn new processes for scenes that aren't this one
            string sceneName = Path.GetFileNameWithoutExtension(scenePath);
            if (sceneName != SceneManager.GetActiveScene().name)
			{
                // spawn process, pass scene argument
                Process p = new Process();
                p.StartInfo.FileName = processPath;

                // pass this process's args in case of -nographics etc.
                p.StartInfo.Arguments = ArgsString() + " -scenePath " + scenePath;
                print("[Zones]: spawning: " + p.StartInfo.FileName + "; args=" + p.StartInfo.Arguments);
                p.Start();
            }
        }
    }

	private IEnumerator UpdateMainZoneOnline()
	{
		var writeDelay = new WaitForSeconds(writeInterval);
		while (Instance != null)
		{
			Database.singleton.SaveZoneOnlineTime(ZoneID);
			yield return writeDelay;
		}
	}

	private IEnumerator VerifyMainZoneOnline()
	{
		var timeoutInterval = writeInterval * timeoutMultiplier;
		var verifyDelay = new WaitForSeconds(timeoutInterval);
		double mainZoneOnline = 0;

		while (Instance != null)
		{
			yield return verifyDelay;

			mainZoneOnline = Database.singleton.TimeElapsedSinceZoneOnline(ZoneID);

			if (mainZoneOnline > timeoutInterval)
			{
				//TODO: Show a message, send user back to main menu, & cleanup instead
				Application.Quit();
			}
		}
	}

    public void OnClientSwitchServerRequested(NetworkConnection conn, SwitchServerMsg message)
    {
        // We only want to do this on the client, skip if the server is active
		if (NetworkServer.active)
		{
			return;
		}

		var sceneName = Path.GetFileNameWithoutExtension(message.scenePath);
		
		// TODO: add validation for message contents

		print("[Zones]: OnClientSwitchServerRequested: " + sceneName);
        print("[Zones]: disconnecting from current server");
        manager.StopClient();

        // clean up as much as possible.
        // if we don't call NetworkManager.Shutdown then objects aren't
        // spawned on the client anymore after connecting to scene #2 for
        // the second time.
        NetworkClient.Shutdown();
        NetworkManager.Shutdown(); // clears singleton too
        NetworkManager.singleton = manager; // setup singleton again
			
        Transport.activeTransport.enabled = false; // don't receive while switching scenes

        print("[Zones]: loading required scene: " + sceneName);
        autoSelectCharacter = message.characterName;

        // load requested scene and make sure to auto connect when it's done
        // TODO: Change to Async with loading screen
		SceneManager.LoadScene(sceneName);

        autoConnectClient = true;
	}

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
		print("[Zones]: OnSceneLoaded: " + scene.name);

		// server started and loaded the requested scene?
		if (NetworkServer.active)
		{
			string requestedPath = ParseScenePathFromArgs();
			string requestedScene = Path.GetFileNameWithoutExtension(requestedPath);
			if (requestedScene == scene.name)
			{
				// write online time every few seconds and check main zone alive every few seconds
				print("[Zones]: starting online alive check for zone: " + ZoneID);
				StartCoroutine("VerifyMainZoneOnline");
			}
		}

        // client started and needs to automatically connect?
        if (autoConnectClient)
        {
			var newPort = (ushort)(originalPort + scene.buildIndex);
			SetPort(newPort);
			print("[Zones]: automatically connecting client to new server at port: " + newPort);

			// DO NOT DO THIS AGAIN! It will cause client to reload scene again, causing unet bugs
			//manager.onlineScene = scene.name;

			manager.StartClient();
            autoConnectClient = false;
        }
    }
}


public class SwitchServerMsg : MessageBase
{
    public static short MsgId = 2002;
    public string scenePath;
    public string characterName;

	public SwitchServerMsg() { }

	public SwitchServerMsg (string scene, string character)
	{
		scenePath = scene;
		characterName = character;
	}
}


public partial class NetworkManagerMMO
{
    private void OnClientConnect_Zones(NetworkConnection conn)
    {
		// setup switch server message handler
		if (NetworkZone.Instance != null)
		{
			NetworkClient.RegisterHandler<SwitchServerMsg>(NetworkZone.Instance.OnClientSwitchServerRequested);
		}
    }

    private void OnStartServer_Zones()
    {
		// spawn instance processes (if any)
		if (NetworkZone.Instance != null)
		{
			NetworkZone.Instance.SpawnProcesses();
		}
	}

	private void OnServerAddPlayer_Zones(string account, GameObject player, NetworkConnection conn, AddPlayerMessage message)
    {
		if (NetworkZone.Instance == null)
		{
			return;
		}

        // where was the player saved the last time?
        string lastScene = Database.singleton.GetCharacterScenePath(player.name);
		if (string.IsNullOrEmpty(lastScene) || lastScene == SceneManager.GetActiveScene().path)
		{
			return;
		}

        print("[Zones]: " + player.name + " was last saved on another scene, transferring to: " + lastScene);

        // ask client to switch server
        conn.Send(new SwitchServerMsg(lastScene, player.name));

        // immediately destroy so nothing messes with the new
        // position and so it's not saved again etc.
        NetworkServer.Destroy(player);
    }
}


public partial class Database : MonoBehaviour
{
	private class scene_info
	{
		[PrimaryKey] // important for performance: O(log n) instead of O(n)
		public string character { get; set; }
		public string scenePath { get; set; }

		public scene_info() { }

		public scene_info (string name, string path)
		{
			character = name;
			scenePath = path;
		}
	}

	private class zone_info
	{
		[PrimaryKey] // important for performance: O(log n) instead of O(n)
		public string zoneName { get; set; }
		public string online { get; set; }

		public zone_info() { }

		public zone_info(string name, string isOnline)
		{
			zoneName = name;
			online = isOnline;
		}
	}

	private void Connect_Zone()
	{
		// TODO: Convert to using MySQL
		if (connection.IsInTransaction)
		{
			StartCoroutine("DelayCreateZones");
		}
		else
		{
			CreateZoneTables();
		}
	}

	private IEnumerator DelayCreateZones()
	{
		while (connection.IsInTransaction)
		{
			yield return null;
		}

		CreateZoneTables();
	}

	private void CreateZoneTables()
	{
		connection.CreateTable<scene_info>();
		connection.CreateIndex(nameof(scene_info), new[] { "character", "scenePath" });

		connection.CreateTable<zone_info>();
		connection.CreateIndex(nameof(zone_info), new[] { "zoneName", "online" });
	}

	public bool IsCharacterOnlineAnywhere(string characterName)
    {
		// a character is online on any of the servers if the online string is not
		// empty and if the time difference is less than the save interval * 2
		// (* 2 to have some tolerance)

		characters character = connection.FindWithQuery<characters>("SELECT * FROM characters WHERE name=?", characterName);
		if (character == null || !character.online)
		{
			return false;
		}

		var lastsaved = character.lastsaved;
		double elapsedSeconds = (DateTime.UtcNow - lastsaved).TotalSeconds;
		float saveInterval = ((NetworkManagerMMO)NetworkManager.singleton).saveInterval;

		// online if 1 and last saved recently (it's possible that online is
		// still 1 after a server crash, hence last saved detection)
		return elapsedSeconds < saveInterval * 2;
    }

    public bool AnyAccountCharacterOnline(string account)
    {
        List<string> characters = CharactersForAccount(account);
        return characters.Any(IsCharacterOnlineAnywhere);
    }

    public string GetCharacterScenePath(string characterName)
    {
		scene_info sceneInfo = connection.FindWithQuery<scene_info>("SELECT * FROM scene_info WHERE character=?", characterName);
		if (sceneInfo != null && !string.IsNullOrEmpty(sceneInfo.scenePath))
		{
			return sceneInfo.scenePath;
		}

		return "";
    }

    public void SaveCharacterScenePath(string characterName, string scene)
    {
		connection.InsertOrReplace(new scene_info(characterName, scene));
    }

    public double TimeElapsedSinceZoneOnline(string zone)
    {
		// a zone is online if the online string is not empty and if the time
		// difference is less than the write interval * multiplier
		// (* multiplier to have some tolerance)

		zone_info zoneInfo = connection.FindWithQuery<zone_info>("SELECT * FROM zone_info WHERE zoneName=?", zone);
		if (zoneInfo != null && !string.IsNullOrEmpty(zoneInfo.online))
		{
			DateTime time = DateTime.Parse(zoneInfo.online);
			return (DateTime.UtcNow - time).TotalSeconds;
		}
        
        return Mathf.Infinity;
    }

    // should only be called by main zone
    public void SaveZoneOnlineTime(string zone)
    {
		//TODO: Add support for checking instances online -> restarting them

        // online status:
        //   '' if offline (if just logging out etc.)
        //   current time otherwise
        // -> it uses the ISO 8601 standard format
        string onlineString = DateTime.UtcNow.ToString("s");
		singleton.connection.InsertOrReplace(new zone_info(zone, onlineString));
    }
}