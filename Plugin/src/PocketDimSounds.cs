using System.Diagnostics;
using GameNetcodeStuff;
using UnityEngine;

namespace SCP106{

    // Plays Sounds within the Pocket Dimension.
    // Before playing a sound it moves to a random location.
    class PocketDimSounds : EnemyAICollisionDetect{

        public AudioSource soundSource; // AudioSource
        public AudioClip[] sounds;
        public BoxCollider bounds; // The bounds which this Audio Source can move around in
        private PlayerControllerB playerToFollow;
        private System.Random random;
        private float timeSinceSound = 0f;
        private float soundTimeLimit = 15f;
        private Vector3 boundsPosition;
        private int soundLength = 0;

        [Conditional("DEBUG")]
        void LogIfDebugBuild(string text) {
            Plugin.Logger.LogInfo(text);
        }

        public void Start(){
            LogIfDebugBuild("Player entered PocketDimension!");
            boundsPosition = bounds.transform.position + bounds.center;
            soundLength = sounds.Length;
            random = new();
        }

        public void Update(){
            timeSinceSound += Time.deltaTime;
            // Play sound
            if (timeSinceSound>=soundTimeLimit){
                Vector3 randomVector = new Vector3(random.Next(-15,15),random.Next(-5,5),random.Next(-15,15));
                this.transform.position = bounds.bounds.ClosestPoint(randomVector + boundsPosition);
                
                soundSource.PlayOneShot(sounds[random.Next(0,soundLength)]);
                timeSinceSound = 0;
            }
        }
    }
}