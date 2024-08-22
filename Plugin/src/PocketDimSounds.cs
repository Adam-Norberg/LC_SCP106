using System.Diagnostics;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace SCP106{

    // Plays Sounds within the Pocket Dimension.
    // Before playing a sound it moves to a random location.
    class PocketDimSounds : NetworkBehaviour{
        #pragma warning disable 0649
        public AudioSource soundSource; // AudioSource
        public AudioClip[] sounds;
        public Transform[] soundPositions; // Empty gameObjects where the sound can be played from
        private System.Random random;
        private float soundTimeLimit = 15f;
        private int previousSound = 0;
        private int soundLength = 0;

        [Conditional("DEBUG")]
        void LogIfDebugBuild(string text) {
            Plugin.Logger.LogInfo(text);
        }

        public void Awake(){
            soundSource.dopplerLevel=4;
        }

        public void Start(){
            soundLength = sounds.Length;
            if(!IsHost){
                return;
            }
            random = new();
            InvokeRepeating("PlaySoundServerRpc",0,soundTimeLimit);
        }

        public void Update(){

        }

        [ServerRpc]
        public void PlaySoundServerRpc() {
            int clipIndex = random.Next(0,soundLength);
            if (clipIndex == previousSound) clipIndex += 1 % soundLength;
            int positionIndex = random.Next(0,soundPositions.Length);
            
            PlaySoundClientRpc(clipIndex,positionIndex);
        }
        
        [ClientRpc]
        public void PlaySoundClientRpc(int clipIndex, int positionIndex) {
            this.transform.position = soundPositions[positionIndex].position;
            //LogIfDebugBuild($"SoundPos: {transform.position}");
            soundSource.PlayOneShot(sounds[clipIndex]);
        }
    }
}