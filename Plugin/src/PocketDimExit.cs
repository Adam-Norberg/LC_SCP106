using System.Collections;
using System.Diagnostics;
using GameNetcodeStuff;
using UnityEngine;

namespace SCP106{
    class PocketDimExit : MonoBehaviour{

        public Transform pocketDimCentre; // The center of the Pocket Dimension (pdspawn)
        public PocketDimController pdController;
        public Transform crushCollider; // For Teleport Death 1 (DeathStyle Slam)
        private System.Random random = new();

        [Conditional("DEBUG")]
        void LogIfDebugBuild(string text) {
            Plugin.Logger.LogInfo(text);
        }

        public void Start(){

        }

        private void OnTriggerEnter(Collider other){
            if (other.tag == "Player"){
                PlayerControllerB player = other.GetComponent<PlayerControllerB>();
                int escapeChance = random.Next(1,9); // 1 to 8
                // 3 in 8 chance to escape
                if (escapeChance<=0){
                    LogIfDebugBuild("Rolled less than 4, player escaped!");
                    // Play "Escaped Sound"
                    pdController.PlayerExit((int)player.playerClientId,PocketDimController.ExitStyle.ESCAPED);
                }
                // 3 in 8 chance for another attempt
                else if (escapeChance<=0){
                    player.SpawnPlayerAnimation();
                    
                    LogIfDebugBuild("Rolled more than 3 less than 7, player gets new attempt!");
                    other.transform.position = pocketDimCentre.position;
                }
                // 2 in 8 chance to be killed
                else {
                    pdController.DeathStyleSlam((int)player.playerClientId,crushCollider);
                }
            }
        }

        private void OnTriggerStay(Collider other){

        }
    }
}