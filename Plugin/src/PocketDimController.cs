using System;
using System.Collections;
using System.Diagnostics;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace SCP106{

    class PocketDimController : NetworkBehaviour{

        // Clips to be played per-player (breathing, death sounds, ...)
        public AudioClip[] personalAudioBreathing; // [breath0gas.ogg, ]
        public AudioClip[] personalAudioDeath;  // [Damage1.ogg, Damage2.ogg, Damage3.ogg, Damage5.ogg]
        public AudioClip[] personalAudioEnter; // []
        public AudioClip[] personalAudioExit; // []

        public GameObject personalAudioPrefab; // Audio Source per-player
        private SCPAI scp106;
        private Vector3 pocketDimensionPosition;
        private Coroutine[] playersInPD;
        //private GameObject[] personalAudios;
        private AudioSource[] personalAudios;

        public enum ExitStyle{
            RESET, // a.k.a Unknown Escape, just reset player values and stop Coroutines.
            ESCAPED, // Escaped alive. Reset player values and stop Coroutine.
        }

        public enum DeathStyle{
            SLAM,
            BLEED,
        }

        [Conditional("DEBUG")]
        void LogIfDebugBuild(string text) {
            Plugin.Logger.LogInfo(text);
        }

        public void Awake(){
            LogIfDebugBuild($"PocketDimController Awake!");
        }
        
        public void Start(){
        }

        // On SCP spawn, let SCP-106 and PocketDimension be known to each other.
        [ServerRpc]
        public void RegisterSCPServerRpc(Vector3 pocketPosition){
            int numberOfPlayers = StartOfRound.Instance.allPlayerScripts.Length;

            // Creates a PersonalAudio for each player.
            for (int i=0; i < numberOfPlayers; i++){
                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[i];
                GameObject playerAudio = Instantiate(personalAudioPrefab,player.cameraContainerTransform.position,Quaternion.identity,player.gameObject.transform);
                playerAudio.GetComponent<NetworkObject>().Spawn();
            }

            RegisterSCPClientRpc(numberOfPlayers,pocketPosition);
        }

        [ClientRpc]
        public void RegisterSCPClientRpc(int numberOfPlayers,Vector3 pocketPosition){
            this.pocketDimensionPosition = pocketPosition;
            this.playersInPD = new Coroutine[numberOfPlayers];
            //this.personalAudios = new GameObject[numberOfPlayers];
            this.personalAudios = new AudioSource[numberOfPlayers];
            for(int i = 0; i < numberOfPlayers; i++){
                AudioSource s = StartOfRound.Instance.allPlayerScripts[i].gameObject.GetComponentInChildren<AudioSource>();
                LogIfDebugBuild($"Player {i} Audio Name: {s.name}");
            }
        }

        [ServerRpc]
        public void PlayPersonalSoundServerRpc(int playerClientId, int clipIndex, int group){

        }

        // Called when a player enters the Pocket Dimension. Stops existing PD Coroutine and starts a new one.
        [ServerRpc]
        public void PlayerEnterPocketDimensionServerRpc(int playerClientId){
            /*if (this.playersInPD[playerClientId] != null){
                StopCoroutine(this.playersInPD[playerClientId]);
            }*/
            
            /*if (NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsServer){
                LogIfDebugBuild("PocketDimEnter I AM HOST!");
                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
                GameObject playerAudio = Instantiate(personalAudioPrefab,player.cameraContainerTransform.position,Quaternion.identity,player.gameObject.transform);
                playerAudio.GetComponent<NetworkObject>().Spawn();
                this.personalAudios[playerClientId] = playerAudio;
            }*/
            if (this.playersInPD[playerClientId] != null){
                return;
            }
            PlayerEnterPocketDimensionClientRpc(playerClientId);
        }

        [ClientRpc]
        public void PlayerEnterPocketDimensionClientRpc(int playerClientId){
            LogIfDebugBuild("PocketDimEnterEffectCalled");
            Coroutine playerRoutine = StartCoroutine(PocketDimensionEffect(playerClientId));
            // Add to Host's list of players running the PocketDimension Coroutine
            if(IsHost){
                this.playersInPD[playerClientId] = playerRoutine;
                LogIfDebugBuild("PocketDimEnterEffectCalled and Host added Coroutine to List");
            }
        }

        // Stops a player's Pocket Dimension Coroutine, for whatever reason (died, escaped, ...)
        public void PlayerExit(int playerClientId, ExitStyle exitStyle){
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            switch (exitStyle)
            {
                case ExitStyle.RESET: break;
                case ExitStyle.ESCAPED: {
                    player.drunkness = 5f;
                    player.playerBodyAnimator.speed*=2;
                    player.movementSpeed*=2;
                    player.SpawnPlayerAnimation();
                    personalAudios[playerClientId].GetComponent<AudioSource>().PlayOneShot(personalAudioExit[0]);
                    player.gameObject.transform.position = RoundManager.FindMainEntrancePosition(true,false);
                    break;
                }
                default: break;
            }
            StopCoroutine(this.playersInPD[playerClientId]);
            //Destroy(this.personalAudios[playerClientId]);
        }

        // Limited time to escape Pocket Dimension, then the player dies.
        // Coroutine to move camera similar to SCP-CB Pocket Dimension
        public IEnumerator PocketDimensionEffect(int playerClientId){
            LogIfDebugBuild("PocketDimensionEffect running");
            float bleedoutTimer = 45f;
            float timeSinceEntered = Time.realtimeSinceStartup;
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
        
            //AudioSource personalAudio = personalAudios[playerClientId].GetComponent<AudioSource>();
            
            //personalAudio.PlayOneShot(personalAudioEnter[0]);

            // Make the player drunk and slow
            player.drunkness = 20f;
            player.drunknessInertia = 15f;
            player.movementSpeed/=2;
            player.playerBodyAnimator.speed/=2;
            LogIfDebugBuild($"Player {NetworkManager.LocalClientId}: {playerClientId} has movementspeed value {player.movementSpeed}");
            player.TeleportPlayer(pocketDimensionPosition);
            player.SpawnPlayerAnimation();
            player.DropBlood(new(0,-1,0),true,true);

            // Stay drunk for a while
            while (!player.isPlayerDead && (Time.realtimeSinceStartup - timeSinceEntered < bleedoutTimer)){
                player.drunkness += 1.5f*Time.deltaTime;
                player.drunknessInertia += 1.5f*Time.deltaTime;
                //bleedoutTimer -= Time.deltaTime;
                yield return null;
            }
            // Start to die
            //personalAudio.GetComponent<AudioSource>().PlayOneShot(personalAudioBreathing[0]);
            player.MakeCriticallyInjured(true);
            while(!player.isPlayerDead && (Time.realtimeSinceStartup - timeSinceEntered < bleedoutTimer)){
                player.movementSpeed -= 0.1f*Time.deltaTime;
                player.drunkness += 2f*Time.deltaTime;
                player.drunknessInertia += 2f*Time.deltaTime;

                //bleedoutTimer -= Time.deltaTime;
                yield return null;
            }
            // Has player already died before blood loss? 
            // (This Coroutine should already be killed if player dies by ways related to this mod/Pocket Dimension)
            if ((Time.realtimeSinceStartup - timeSinceEntered > bleedoutTimer) && player.isPlayerDead){
                //personalAudio.GetComponent<AudioSource>().Stop(true);
            } else {
                // Expiration Death 1 - Blood Loss
                
            }
        }
        
        // 
        [ServerRpc]
        public void PlayerDeathServerRpc(int playerClientId, int deathStyle, Vector3 optionalExtraInfo = default){
            LogIfDebugBuild("PDC: Called PlayerDeathServerRpc");
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

        // Reset to standard values
        private void PlayerDeathValueReset(int playerClientId){
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            player.drunkness = 0f;
            player.drunknessInertia = 0f;
        }

        // Teleport Death 1.2 - Player slammed into wall
        [ClientRpc]
        public void DeathStyleSlamClientRpc(int playerClientId, Vector3 crushColliderPosition){
            // "Damage5.ogg", CauseOfDeath.Crushing
            LogIfDebugBuild("PDC: Player died of Crushing");
            StartCoroutine(SlamToWall(playerClientId,crushColliderPosition));
        }

        // Teleport Death 1.2 - Animation to Kill Player
        public IEnumerator SlamToWall(int playerClientId, Vector3 crushColliderPosition){
            float timeSinceStart = Time.realtimeSinceStartup;
            float timeToDie = 1f;

            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            
            //AudioSource personalAudio = personalAudios[playerClientId].GetComponent<AudioSource>();
            Vector3 turnDirection = crushColliderPosition - player.gameObject.transform.position;

            while (Time.realtimeSinceStartup - timeSinceStart < timeToDie){
                player.gameObject.transform.rotation = Quaternion.Lerp(player.gameObject.transform.rotation, Quaternion.LookRotation(turnDirection), Time.deltaTime *2f);
                player.gameObject.transform.position = Vector3.Lerp(player.gameObject.transform.position, crushColliderPosition, Time.deltaTime*2f);
                yield return null;
            }
            //personalAudio.PlayOneShot(personalAudioDeath[3]);

            player.KillPlayer(player.velocityLastFrame*5f,true,CauseOfDeath.Crushing,1,default);

            PlayerDeathValueReset(playerClientId);
        }

        // Expiration Death 1 - Player dies from blood loss
        [ClientRpc]
        private void DeathStyleBleedClientRpc(int playerClientId){
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            //AudioSource personalAudio = personalAudios[playerClientId].GetComponent<AudioSource>();
            // "Damage1.ogg", CauseOfDeath.Stabbing
            LogIfDebugBuild("PDC: Player died of Blood Loss");
            //personalAudio.PlayOneShot(personalAudioDeath[0]);
            player.KillPlayer(Vector3.zero,true,CauseOfDeath.Stabbing,0,default);
            PlayerDeathValueReset(playerClientId);
        }
    }
}