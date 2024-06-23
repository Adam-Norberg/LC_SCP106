using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.AI;

namespace SCP106
{
    class SCPCollisionDetect : EnemyAICollisionDetect
    {
        private SCPAI scpAI;
        private GameObject currentDoorPassing;

        [Conditional("DEBUG")]
        void LogIfDebugBuild(string text) {
            Plugin.Logger.LogInfo(text);
        }

        public void Start() {
            scpAI = GetComponentInParent<SCPAI>();
        }

        /*

            InteractTrigger (Layer 9) (Includes: Doors)
                - Interactable Statics, like doors but not objects like the whoopiecushion.
            Triggers (Layer 13) (Includes: OutsideShip, ShipDoor, KillTrigger, WaterTrigger)
                - Audio/Ambience triggers and KillTriggers (e.g. falling off map)
            MapHazards (Layer 21) (Includes: Steam, BloodDecal, Underwater)
                - Steam Particle System, Blood Decals, Underwater AudioFX
            Doors =
                tag: InteractTrigger
                layer: 9 (InteractableObject)
                Notable Components:
                    DoorLock
                    InteractTrigger
                    AnimatedObjectTrigger
                    NavMeshObstacle
            MapHazards = Layer 21
            Water =
                name: WaterTrigger
                tag: Untagged
                layer: 13
                Notable Components:
                    QuicksandTrigger
                    MeshFilter
                    MeshRenderer
                    BoxCollider
                    Rigidbody
        */
        public void OnTriggerEnter(Collider other){
            // Pass through locked door
            if(other.gameObject.layer == 9){
                DoorLock doorLock = other.gameObject.GetComponent<DoorLock>();
                if(doorLock != null && scpAI.currentBehaviourStateIndex == (int)SCPAI.State.HUNTING){
                    currentDoorPassing = other.gameObject;
                    doorLock.OpenDoorAsEnemyServerRpc();
                    scpAI.PassDoorStart();
                }
            }
            // Debugging below
            /*LogIfDebugBuild($"Layer: {other.gameObject.layer}, Type: {other.gameObject.tag}");
            Component[] comp = other.gameObject.GetComponents<Component>();
            foreach(var c in comp){
                LogIfDebugBuild($"{c}");
            }*/
            /*if(other.gameObject.layer == 9){
                LogIfDebugBuild("LOCKING DOOR");
                DoorLock doorLock = other.gameObject.GetComponent<DoorLock>();
                NavMeshObstacle obstacle = doorLock.GetComponent<NavMeshObstacle>();
                doorLock.isLocked = true;
                obstacle.carving = true;
            }*/
        }
        public void OnTriggerExit(Collider other){
            if(other.gameObject == currentDoorPassing){
                scpAI.PassDoorEnd();
            }
        }
    }
}