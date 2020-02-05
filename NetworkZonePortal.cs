using UnityEngine;
using Mirror;

public class NetworkZonePortal : MonoBehaviour
{
    [Header("Destination"), SerializeField]
    private string scenePath = "Assets/uMMORPG/Scenes/World.unity";

	[SerializeField, Tooltip("Destination in the other zone to teleport player")]
	private Vector3 position = Vector3.zero;

	// TODO: Add portal requirements

    private void OnPortal(Player player)
    {
		// Only allow server collisions to be considered
		if (player == null || player.isClient)
		{
			return;
		}
        // special case: in host mode, we can't disconnect or forward the
        // local player as this would shut down the server
        else if (player.isLocalPlayer)
        {
            Debug.LogWarning("can't move the host to another server as this would shut down this server!");
        }
        // on server: save new scene index
        else
        {
			// TODO: Add validation if the zone we are trying to transfer to is valid/online

            // immediately save new scene index and position
            print("[Zones]: saving player(" + player.name + ") scene index(" + scenePath + ")");
            player.transform.position = position;
            Database.singleton.CharacterSave(player, false);
            Database.singleton.SaveCharacterScenePath(player.name, scenePath);

			// ask client to switch server
			player.connectionToClient.Send(new SwitchServerMsg(scenePath, player.name));

            // immediately destroy so nothing messes with the new
            // position and so it's not saved again etc.
            NetworkServer.Destroy(player.gameObject);
        }
    }

    // for 3D
    private void OnTriggerEnter(Collider co)
    {
        OnPortal(co.GetComponentInParent<Player>());
    }

    // for 2D
    private void OnTriggerEnter2D(Collider2D co)
    {
        OnPortal(co.GetComponentInParent<Player>());
    }
}