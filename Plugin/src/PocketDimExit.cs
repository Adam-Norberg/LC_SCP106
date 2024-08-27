using System.Collections;
using System.Diagnostics;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace SCP106{
    class PocketDimExit : NetworkBehaviour{
        #pragma warning disable 0649
        [Header("Pocket Dimension")]
        public Transform pocketDimCentre; // Spawn point for this trigger's location (Main/Corridor/Throne)
        public PocketDimController pdController;
        [Header("This Exit's Wall")]
        public Transform crushCollider; // For Teleport Death 1 (DeathStyle Slam)
        [Header("This Trigger's Room Type")]
        public PocketDimController.RoomType type;

        [Conditional("DEBUG")]
        void LogIfDebugBuild(string text) {
            Plugin.Logger.LogInfo(text);
        }

        public void Awake(){
            LogIfDebugBuild($"PocketDimExit {type} Awake!");
        }

        public void Start(){

        }

        private void OnTriggerEnter(Collider other){
            if (other.tag == "Player"){
                PlayerControllerB player = other.GetComponent<PlayerControllerB>();
                if(player.playerClientId == NetworkManager.Singleton.LocalClientId){
                    //RollForExit(player);
                    if(this.type == PocketDimController.RoomType.MAIN){
                        pdController.RollForExit((int)player.playerClientId,(int)type,crushCollider.position);
                    } else {
                        pdController.RollForExit((int)player.playerClientId,(int)type);
                    }
                }
            }
        }

        private void OnTriggerStay(Collider other){

        }
    }
}