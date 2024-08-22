using System.Collections;
using System.Diagnostics;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace SCP106{
    class PocketDimExit : NetworkBehaviour{
        #pragma warning disable 0649
        public Transform pocketDimCentre; // The center of the Pocket Dimension (pdspawn)
        public PocketDimController pdController;
        public Transform crushCollider; // For Teleport Death 1 (DeathStyle Slam)
        private System.Random random = new();

        [Conditional("DEBUG")]
        void LogIfDebugBuild(string text) {
            Plugin.Logger.LogInfo(text);
        }

        public void Awake(){
            LogIfDebugBuild($"PocketDimExit Awake!");
        }

        public void Start(){

        }

        private void OnTriggerEnter(Collider other){
            if (other.tag == "Player"){
                PlayerControllerB player = other.GetComponent<PlayerControllerB>();
                if(player.playerClientId == NetworkManager.LocalClientId){
                    RollForExit(player);
                }
            }
        }

        //[ServerRpc(RequireOwnership = false)]
        public void RollForExit(PlayerControllerB player){
            //PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            int escapeChance = random.Next(1,9); // 1 to 8
            // 3 in 8 chance to escape
            if (escapeChance<=3){
                pdController.PlayerExitServerRpc((int)player.playerClientId,(int)PocketDimController.ExitStyle.ESCAPED);
            }
            // 3 in 8 chance for another attempt
            else if (escapeChance<=6){
                player.SpawnPlayerAnimation();
                player.TeleportPlayer(pocketDimCentre.position);
            }
            // 2 in 8 chance to be killed
            else {
                pdController.DeathStyleSlamClientRpc((int)player.playerClientId,crushCollider.position);
            }
        }

        private void OnTriggerStay(Collider other){

        }
    }
}