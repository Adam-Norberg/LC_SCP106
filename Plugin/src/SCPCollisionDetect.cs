using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.AI;

namespace SCP106
{
    class SCPCollisionDetect : EnemyAICollisionDetect
    {
        [Conditional("DEBUG")]
        void LogIfDebugBuild(string text) {
            Plugin.Logger.LogInfo(text);
        }

        public void Start() {
            
        }

        /*
            Doors =
                tag: InteractTrigger
                layer: 9 (InteractableObject)
            Notable Components:
                DoorLock
                InteractTrigger
                AnimatedObjectTrigger
                NavMeshObstacle
        */
        public void OnTriggerEnter(Collider other){
            if(other.gameObject.layer == 9){
                LogIfDebugBuild("LOCKING DOOR");
                DoorLock doorLock = other.gameObject.GetComponent<DoorLock>();
                NavMeshObstacle obstacle = doorLock.GetComponent<NavMeshObstacle>();
                doorLock.isLocked = true;
                obstacle.carving = true;
            }
        }
    }
}