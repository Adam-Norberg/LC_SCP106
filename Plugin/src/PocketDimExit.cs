using System.Collections;
using System.Diagnostics;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace SCP106{
    class PocketDimExit : NetworkBehaviour{

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
                RollForExitServerRpc((int)other.GetComponent<PlayerControllerB>().playerClientId);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void RollForExitServerRpc(int playerClientId){
            LogIfDebugBuild("PDE: Called RollForExitServerRpc");
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            int escapeChance = random.Next(1,9); // 1 to 8
            // 3 in 8 chance to escape
            if (escapeChance<=0){
                pdController.PlayerExit((int)player.playerClientId,PocketDimController.ExitStyle.ESCAPED);
            }
            // 3 in 8 chance for another attempt
            else if (escapeChance<=0){
                player.SpawnPlayerAnimation();
                player.gameObject.transform.position = pocketDimCentre.position;
            }
            // 2 in 8 chance to be killed
            else {
                pdController.PlayerDeathServerRpc((int)player.playerClientId,(int)PocketDimController.DeathStyle.SLAM,crushCollider.position);
            }
        }

        private void OnTriggerStay(Collider other){

        }
    }
}