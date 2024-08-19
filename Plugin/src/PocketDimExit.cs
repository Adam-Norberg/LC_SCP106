using System.Diagnostics;
using UnityEngine;

namespace SCP106{
    class PocketDimExit : EnemyAICollisionDetect{

        public Vector3 pocketDimCentre; // The center of the Pocket Dimension
        private Vector3 exitLocation; // Location where player "wakes up" when escaping the Pocket Dimension
        private System.Random random = new();

        [Conditional("DEBUG")]
        void LogIfDebugBuild(string text) {
            Plugin.Logger.LogInfo(text);
        }

        public void Start(){
            // Gets the location a Pocket Dimension survivor appears at
            exitLocation = RoundManager.FindMainEntrancePosition(true,false);
            pocketDimCentre = base.gameObject.GetComponentInParent<Transform>().position;
        }

        private void OnTriggerEnter(Collider other){
            if (other.tag == "Player"){
                LogIfDebugBuild("Collided with Player!");
                int escapeChance = random.Next(1,9);
                // 3 in 8 chance to escape
                if (escapeChance<=3){
                    LogIfDebugBuild("Rolled less than 4, player escaped!");
                    other.transform.position = exitLocation;
                }
                // 3 in 8 chance for another attempt
                else if (escapeChance<=6){
                    LogIfDebugBuild("Rolled more than 3 less than 7, player gets new attempt!");
                    other.transform.position = pocketDimCentre;
                }
                // 2 in 8 chance to be killed
                else {
                    LogIfDebugBuild("Rolled more than 6, player died!");
                }
            }
        }
    }
}