using System;
using System.Collections;
using System.Diagnostics;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace SCP106{

    class PocketDimController : NetworkBehaviour{
        #pragma warning disable 0649
        // Clips to be played per-player (breathing, death sounds, ...)
        public AudioClip[] personalAudioBreathing; // [breath0gas.ogg, ]
        public AudioClip[] personalAudioDeath;  // [Damage1.ogg, Damage2.ogg, Damage3.ogg, Damage5.ogg]
        public AudioClip[] personalAudioEnter; // [Enter.ogg, ]
        public AudioClip[] personalAudioExit; // [Exit.ogg, ]

        public GameObject personalAudioPrefab; // Audio Source per-player
        private Vector3 pocketDimensionPosition;
        private bool[] playerIsInPD;
        private Coroutine localPlayerCoroutine;
        private Vector3 localPlayerExitLocation;
        private AudioSource[] personalAudios;

        public enum ExitStyle{
            RESET, // a.k.a Unknown Escape, just reset player values and stop Coroutines.
            ESCAPED, // Escaped alive. Reset player values and stop Coroutine.
        }

        public enum DeathStyle{
            SLAM,
            BLEED,
        }

        private enum AudioGroup{
            BREATHING,
            DEATH,
            ENTER,
            EXIT,
        }

        [Conditional("DEBUG")]
        void LogIfDebugBuild(string text) {
            Plugin.Logger.LogInfo(text);
        }

        public void Awake(){
        }
        
        public void Start(){
        }

        // On SCP spawn, let SCP-106 and PocketDimension be known to each other.
        [ServerRpc]
        public void RegisterSCPServerRpc(Vector3 pocketPosition){
            int numberOfPlayers = StartOfRound.Instance.allPlayerScripts.Length;
            ulong[] networkObjects = new ulong[numberOfPlayers];
            // Creates a PersonalAudio for each player.
            for (int i=0; i < numberOfPlayers; i++){
                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[i];
                // NOTE: Players fail to be added as parents here. Parenting is done in ClientRpc call.
                GameObject playerAudio = Instantiate(personalAudioPrefab,player.cameraContainerTransform.position,Quaternion.identity,player.gameObject.transform);
                NetworkObject networkObject = playerAudio.GetComponent<NetworkObject>();
                networkObject.Spawn();
                networkObject.DestroyWithScene = true;
                networkObjects[i] = networkObject.NetworkObjectId;
            }
            RegisterSCPClientRpc(numberOfPlayers,pocketPosition,networkObjects);
        }

        [ClientRpc]
        public void RegisterSCPClientRpc(int numberOfPlayers, Vector3 pocketPosition, ulong[] networkObjects){
            
            this.pocketDimensionPosition = pocketPosition;
            this.playerIsInPD = new bool[numberOfPlayers];
            this.personalAudios = new AudioSource[numberOfPlayers];
            for(int i = 0; i < personalAudios.Length; i++){
                NetworkObject networkObject = NetworkManager.Singleton.SpawnManager.SpawnedObjects[networkObjects[i]];
                // Sets the correct player as parent for the new Audio Sources
                networkObject.TrySetParent(StartOfRound.Instance.allPlayerScripts[i].transform,true);
                this.personalAudios[i] = networkObject.GetComponent<AudioSource>();
            }
        }

        [ServerRpc(RequireOwnership = false)]
        private void PlayPersonalSoundServerRpc(int playerClientId, int clipIndex, int group){
            PlayPersonalSoundClientRpc(playerClientId, clipIndex, group);
        }

        [ClientRpc]
        private void PlayPersonalSoundClientRpc(int playerClientId, int clipIndex, int group){
            switch(group){
                case (int)AudioGroup.BREATHING: {
                    personalAudios[playerClientId].PlayOneShot(personalAudioBreathing[clipIndex]);
                    break;
                }
                case (int)AudioGroup.DEATH: {
                    personalAudios[playerClientId].PlayOneShot(personalAudioDeath[clipIndex]);
                    break;
                }
                case (int)AudioGroup.ENTER: {
                    personalAudios[playerClientId].PlayOneShot(personalAudioEnter[clipIndex]);
                    break;
                }
                case (int)AudioGroup.EXIT: {
                    personalAudios[playerClientId].PlayOneShot(personalAudioExit[clipIndex]);
                    break;
                }
                default: break;
            }
        }

        // Called when a player enters the Pocket Dimension.
        [ServerRpc]
        public void PlayerEnterPocketDimensionServerRpc(int playerClientId, Vector3 playerLocation){
            if (playerIsInPD[playerClientId]){
                return;
            }
            PlayerEnterPocketDimensionClientRpc(playerClientId,playerLocation);
        }
        
        // Player Entering: [Starts Coroutine, set self to True in playerIsInPD]
        // All Others: [Set Player Entering to True in playerIsInPD]
        [ClientRpc]
        public void PlayerEnterPocketDimensionClientRpc(int playerClientId, Vector3 playerLocation){
            this.playerIsInPD[playerClientId] = true;
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            LogIfDebugBuild($"Player movementspeed Original: {player.movementSpeed}");
            player.playerBodyAnimator.speed/=3;
            // Only affected player needs to run the Coroutine
            if(playerClientId == (int)NetworkManager.Singleton.LocalClientId){
                this.localPlayerExitLocation = playerLocation;
                this.localPlayerCoroutine = StartCoroutine(PocketDimensionEffect(playerClientId));
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void PlayerExitServerRpc(int playerClientId, int exitStyle){
            PlayerExitClientRpc(playerClientId,exitStyle);
        }

        // 
        [ClientRpc]
        public void PlayerExitClientRpc(int playerClientId, int exitStyle){
            // For all Players
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            this.playerIsInPD[playerClientId] = false;
            player.playerBodyAnimator.speed = 1;
            if(playerClientId != (int)NetworkManager.Singleton.LocalClientId){
                return;
            }

            // Only Affected Player runs code below
            StopCoroutine(this.localPlayerCoroutine);
            switch (exitStyle)
            {
                case (int)ExitStyle.RESET: break;
                case (int)ExitStyle.ESCAPED: {
                    player.drunkness = 5f;
                    player.playerBodyAnimator.speed = 1;
                    player.movementSpeed = 4.6f;
                    player.SpawnPlayerAnimation();
                    //personalAudios[playerClientId].PlayOneShot(personalAudioExit[0]);
                    PlayPersonalSoundServerRpc(playerClientId,0,(int)AudioGroup.EXIT);
                    //Vector3 doorPos = RoundManager.FindMainEntrancePosition(true,false);
                    player.TeleportPlayer(this.localPlayerExitLocation);
                    break;
                }
                default: break;
            }
        }

        // Limited time to escape Pocket Dimension, then the player dies.
        // Coroutine to move camera similar to SCP-CB Pocket Dimension
        public IEnumerator PocketDimensionEffect(int playerClientId){
            float bleedoutTimer = 25f;
            float timeSinceEntered = Time.realtimeSinceStartup; // StartOfRound.Instance.timeSinceRoundStarted
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            
            //personalAudios[playerClientId].PlayOneShot(personalAudioEnter[0]);
            PlayPersonalSoundServerRpc(playerClientId,0,(int)AudioGroup.ENTER);
            // Make the player drunk and slow
            
            player.drunkness = 20f;
            player.drunknessInertia = 15f;
            player.movementSpeed/=3;
            player.playerBodyAnimator.speed/=3;
            player.TeleportPlayer(pocketDimensionPosition);
            player.SpawnPlayerAnimation();
            player.DropBlood(default,true,true);
            

            // Stay drunk for a while
            while (!player.isPlayerDead && (Time.realtimeSinceStartup - timeSinceEntered < bleedoutTimer - 4)){
                player.drunkness += 1.5f*Time.deltaTime;
                player.drunknessInertia += 1.5f*Time.deltaTime;
                yield return null;
            }
            // Start to die
            PlayPersonalSoundServerRpc(playerClientId,0,(int)AudioGroup.BREATHING);
            player.DropBlood(default,true,true);
            player.MakeCriticallyInjured(true);
            while(!player.isPlayerDead && (Time.realtimeSinceStartup - timeSinceEntered < bleedoutTimer)){
                player.movementSpeed -= 0.1f*Time.deltaTime;
                player.drunkness += 2f*Time.deltaTime;
                player.drunknessInertia += 2f*Time.deltaTime;
                yield return null;
            }
            // Has player already died before blood loss? 
            // (This Coroutine should already be killed if player dies by ways related to this mod/Pocket Dimension)
            if (player.isPlayerDead){
                yield break;
            } else {
                // Expiration Death 1 - Blood Loss
                // Ensure that the player affected is the one to call the actual death.
                // (To avoid race conditions due to latency issues)
                if(playerClientId == (int)NetworkManager.LocalClientId){
                    //personalAudios[playerClientId].PlayOneShot(personalAudioDeath[0]);
                    PlayPersonalSoundServerRpc(playerClientId,0,(int)AudioGroup.DEATH);
                    PlayerDeathServerRpc(playerClientId,(int)DeathStyle.BLEED);
                }
            }
        }

        [ServerRpc(RequireOwnership = false)]
        private void KillPlayerServerRpc(int playerClientId){

        }   
        [ClientRpc]
        private void KillPlayerClientRpc(int playerClientId){

        }

        [ServerRpc(RequireOwnership = false)]
        public void PlayerDeathServerRpc(int playerClientId, int deathStyle, Vector3 optionalExtraInfo = default){
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];

            // Check if Player can be in Animation (Avoid duplicate function calls)
            if(player.inSpecialInteractAnimation){
                return;
            }
            player.UpdateSpecialAnimationValue(true,default,4,default);
            switch(deathStyle){
                case (int)DeathStyle.SLAM: {
                    DeathStyleSlamClientRpc(playerClientId,optionalExtraInfo);
                    break;
                }
                case (int)DeathStyle.BLEED: {
                    DeathStyleBleedClientRpc(playerClientId);
                    break;
                }
                default: break;
            }
        }

        [ServerRpc(RequireOwnership = false)]
        private void PlayerValueResetServerRpc(int playerClientId){
            PlayerValueResetClientRpc(playerClientId);
        }

        // Reset to standard values
        [ClientRpc]
        private void PlayerValueResetClientRpc(int playerClientId){
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            player.drunkness = 0f;
            player.drunknessInertia = 0f;
            player.playerBodyAnimator.speed = 1;
        }

        // Teleport Death 1.2 - Player slammed into wall
        [ClientRpc]
        public void DeathStyleSlamClientRpc(int playerClientId, Vector3 crushColliderPosition){
            // "Damage5.ogg", CauseOfDeath.Crushing
            // Everything
            if(playerClientId != (int)NetworkManager.LocalClientId){
                return;
            }
            StartCoroutine(SlamToWall(playerClientId,crushColliderPosition));
        }

        // Teleport Death 1.2 - Animation to Kill Player
        private IEnumerator SlamToWall(int playerClientId, Vector3 crushColliderPosition){
            StopCoroutine(this.localPlayerCoroutine);
            float timeSinceStart = Time.realtimeSinceStartup;
            float timeToDie = 1f;

            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            Vector3 turnDirection = crushColliderPosition - player.gameObject.transform.position;

            // Smooth animation to collide with wall
            while (Time.realtimeSinceStartup - timeSinceStart < timeToDie){
                player.gameObject.transform.rotation = Quaternion.Lerp(player.gameObject.transform.rotation, Quaternion.LookRotation(turnDirection), Time.deltaTime *2f);
                player.gameObject.transform.position = Vector3.Lerp(player.gameObject.transform.position, crushColliderPosition, Time.deltaTime*2f);
                yield return null;
            }

            PlayPersonalSoundServerRpc(playerClientId,3,(int)AudioGroup.DEATH);
            player.KillPlayer(new Vector3(5,0,5),true,CauseOfDeath.Crushing,1,default);

            PlayerValueResetServerRpc(playerClientId);
        }

        // Expiration Death 1 - Player dies from blood loss
        [ClientRpc]
        public void DeathStyleBleedClientRpc(int playerClientId){
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            player.KillPlayer(Vector3.zero,true,CauseOfDeath.Stabbing,0,default);
            PlayerValueResetClientRpc(playerClientId);
        }
    }
}