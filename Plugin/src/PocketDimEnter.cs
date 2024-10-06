using UnityEngine;
using System.Diagnostics;
using GameNetcodeStuff;

namespace SCP106{

    // A trigger when a player enters the Pocket Dimension. Keeps track of when they should die from "bleeding".
    class PocketDimEnter : EnemyAICollisionDetect{

        private PlayerControllerB[] playersInsidePocketDimension;

        [Conditional("DEBUG")]
        void LogIfDebugBuild(string text) {
            Plugin.Logger.LogInfo(text);
        }

        public void Start(){
            playersInsidePocketDimension = new PlayerControllerB[RoundManager.Instance.playersManager.allPlayerObjects.Length];
        }

        public void Update(){

        }

        private void OnTriggerEnter(Collider other){
            if (other.tag == "Player"){
                PlayerControllerB playerController = other.GetComponent<PlayerControllerB>();

                // Check if they've entered before
                if (playersInsidePocketDimension[(int)playerController.playerClientId] == null){
                    LogIfDebugBuild("New Player entered Pocket dimension!");
                    playersInsidePocketDimension[(int)playerController.playerClientId] = playerController;
                }
            }
        }

    }
}