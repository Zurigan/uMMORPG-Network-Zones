# uMMORPG-Network-Zones

This is a uMMORPG plugin that adds the ability to spawn multiple scenes on server startup, move characters between those scenes via in game portals.

As far I know this was originally created by vis2k, credit to Puppet from the uMMORPG Discord for some changes. 

# Usage Guide

* Copy NetworkZones folder into uMMORPG Addons folder
* Add NetworkZone component to NetworkManager
  * assign the public components by dragging networkmanager's components into it
  * add [your-path-here]/World2.unity to scene paths to spawn
* Create a GameObject->3D->Cylinder for the portal, place it somewhere in the scene
  (for uMMORPG 2D just add a portal-like sprite with a 2D collider)
  * enable 'Is Trigger' in the Collider
  * add NetworkZonePortal component to it
  * change ScenePath to path to [your-path-here]/World2.unity
* Add DontDestroyOnLoad component to:
  * Canvas
  * EventSystem
  * MinimapCamera (because Canvas/Minimap references it)
* Save scene, select scene in Project Area, duplicate via ctrl+d
  * rename new scene to 'World2'
  * go to File->Build Settings and add World2
  * open it
  * delete:
    * Canvas
    * EventSystem
    * MinimapCamera
    * NetworkManager
  * select Portal, change scene path to [your-path-here]/World.unity
* Paste the following code at the top of the function in NetworkManagerMMO.OnClientCharactersAvailable to skip past character selection

	```
	// Auto Select Character Flow - Skip Character Selection Screen
	if (NetworkZone.Instance != null && !string.IsNullOrEmpty(NetworkZone.Instance.autoSelectCharacter))
	{
		// cache and clear auto select
		var selectCharacter = NetworkZone.Instance.autoSelectCharacter;
		NetworkZone.Instance.ClearSelectCharacter();

		int index = message.characters.ToList().FindIndex(c => c.name == selectCharacter);
		if (index != -1)
		{
			state = NetworkState.World;

			// clear previous previews in any case
			ClearPreviews();

			// Add the player
			print("[Zones]: autoselect " + selectCharacter + "(" + index + ")");
			byte[] extra = BitConverter.GetBytes(index);
			ClientScene.AddPlayer(NetworkClient.connection, extra);

			// send CharacterSelect message (need to be ready first!)
			NetworkClient.connection.Send(new CharacterSelectMsg(index));

			return;
		}
	}
	// rest of the original code  in NetworkManagerMMO.OnClientCharactersAvailable goes below here
  
# Test it

* press build and run
  * select server-only, notice how it automatically launches another zone process
* press play in the editor
  * Login
  * Create/Select Character
  * Run into the portal to see the other zone


# Notes:

* chat doesn't work across zones yet. using an irc server is a possible solution
* sqlite does allow concurrent access. but if you get errors, consider mysql.
