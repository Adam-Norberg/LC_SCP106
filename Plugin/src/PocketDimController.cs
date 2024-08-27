using System;
using System.Collections;
using System.Diagnostics;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace SCP106{

    class PocketDimController : NetworkBehaviour{
        #pragma warning disable 0649
        [Header("Personal Audio Clips / Prefab")]
        // Sounds below belong to SCP-CB
        // Clips to be played per-player (breathing, death sounds, ...)
        public AudioClip[] personalAudioBreathing; // [breath0gas.ogg, ]
        public AudioClip[] personalAudioDeath;  // [Damage1.ogg, Damage2.ogg, Damage3.ogg, Damage5.ogg, NeckSnap3.ogg, Impact.ogg]
        public AudioClip[] personalAudioEnter; // [Enter.ogg, ]
        public AudioClip[] personalAudioExit; // [Exit.ogg, ]
        public AudioClip[] personalAudioMisc; // [Laugh.ogg, Screech.ogg]
        public GameObject personalAudioPrefab; // Audio Source per-player
        [Header("Pocket Dimension Main Room")]
        public Transform mainRoomSpawn;
        [Header("Pocket Dimension Throne Room")]
        public Transform throneRoomSpawn;
        public AudioSource throneRoomAudioSource;
        public AudioClip personalAudioKneel; // "Kneel.ogg", for the Throne Room
        [Header("Pocket Dimension Corridor")]
        public Transform corridorSpawn;

        private SCPAI scpReference;
        private Vector3 pocketDimensionPosition;
        public bool[] playerIsInPD;
        private bool[] playerIsInThroneRoom;
        private bool[] playerIsInCorridor;
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
            KNEEL,
        }

        public enum RoomType{
            MAIN,
            CORRIDOR,
            THRONE,
        }

        private enum AudioGroup{
            BREATHING,
            DEATH,
            ENTER,
            EXIT,
            MISC,
        }

        [Conditional("DEBUG")]
        void LogIfDebugBuild(string text) {
            Plugin.Logger.LogInfo(text);
        }

        public void Awake(){

        }
        
        public void Start(){
        }

        public override void OnDestroy(){
            PlayerControllerB localPlayer = GameNetworkManager.Instance.localPlayerController;
            if(localPlayerCoroutine != null){
                StopCoroutine(localPlayerCoroutine);
                localPlayer.gameplayCamera.transform.position = localPlayer.cameraContainerTransform.position;
            }
            localPlayer.drunkness = 0f;
            localPlayer.drunknessInertia = 0f;
            base.OnDestroy();
        }

        // On SCP spawn, let SCP-106 and PocketDimension be known to each other.
        [ServerRpc]
        public void RegisterSCPServerRpc(Vector3 pocketPosition, ulong SCPNetworkId){
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
            RegisterSCPClientRpc(numberOfPlayers,pocketPosition,networkObjects,SCPNetworkId);
        }

        [ClientRpc]
        public void RegisterSCPClientRpc(int numberOfPlayers, Vector3 pocketPosition, ulong[] networkObjects, ulong SCPNetworkId){
            this.scpReference = NetworkManager.Singleton.SpawnManager.SpawnedObjects[SCPNetworkId].GetComponent<SCPAI>();
            this.pocketDimensionPosition = pocketPosition;
            this.playerIsInPD = new bool[numberOfPlayers];
            this.playerIsInThroneRoom = new bool[numberOfPlayers];
            this.playerIsInCorridor = new bool[numberOfPlayers];
            this.personalAudios = new AudioSource[numberOfPlayers];
            for(int i = 0; i < personalAudios.Length; i++){
                NetworkObject networkObject = NetworkManager.Singleton.SpawnManager.SpawnedObjects[networkObjects[i]];
                // Sets the correct player as parent for the new Audio Sources
                networkObject.TrySetParent(StartOfRound.Instance.allPlayerScripts[i].transform,true);
                this.personalAudios[i] = networkObject.GetComponent<AudioSource>();
            }
        }

        [ServerRpc(RequireOwnership = false)]
        private void StopPersonalSoundServerRpc(int playerClientId){
            StopPersonalSoundClientRpc(playerClientId);
        }

        [ClientRpc]
        private void StopPersonalSoundClientRpc(int playerClientId){
            personalAudios[playerClientId].Stop(true);
        }

        [ServerRpc(RequireOwnership = false)]
        private void PlayPersonalSoundServerRpc(int playerClientId, int roomType, int audioGroup, int index){
            PlayPersonalSoundClientRpc(playerClientId, roomType, audioGroup, index);
        }

        [ClientRpc]
        private void PlayPersonalSoundClientRpc(int playerClientId, int roomType, int audioGroup, int index){
            switch(roomType,audioGroup){
                case ((int)RoomType.MAIN,(int)AudioGroup.BREATHING): {
                    personalAudios[playerClientId].PlayOneShot(personalAudioBreathing[index]);
                    break;
                }
                case ((int)RoomType.MAIN,(int)AudioGroup.DEATH): {
                    personalAudios[playerClientId].PlayOneShot(personalAudioDeath[index]);
                    break;
                }
                case ((int)RoomType.MAIN,(int)AudioGroup.ENTER): {
                    personalAudios[playerClientId].PlayOneShot(personalAudioEnter[index]);
                    break;
                }
                case ((int)RoomType.MAIN,(int)AudioGroup.EXIT): {
                    personalAudios[playerClientId].PlayOneShot(personalAudioExit[index]);
                    break;
                }
                case ((int)RoomType.THRONE,(int)AudioGroup.BREATHING): {
                    personalAudios[playerClientId].PlayOneShot(scpReference.killingSounds[index]);
                    break;
                }
                case ((int)RoomType.THRONE,(int)AudioGroup.DEATH): {
                    personalAudios[playerClientId].PlayOneShot(scpReference.playerKilledSounds[index]);
                    break;
                }
                case ((int)RoomType.THRONE,(int)AudioGroup.ENTER): {
                    throneRoomAudioSource.PlayOneShot(personalAudioKneel);
                    break;
                }
                case ((int)RoomType.THRONE,(int)AudioGroup.MISC): {
                    personalAudios[playerClientId].PlayOneShot(personalAudioMisc[index]);
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
        private void PlayerEnterPocketDimensionClientRpc(int playerClientId, Vector3 playerLocation){
            this.playerIsInPD[playerClientId] = true;
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            player.playerBodyAnimator.speed/=2;
            // Only affected player needs to run the Coroutine
            if(playerClientId == (int)NetworkManager.Singleton.LocalClientId){
                this.localPlayerExitLocation = playerLocation;
                LogIfDebugBuild($"Current Pos: {player.transform.position}, pocket position: {pocketDimensionPosition}");
                this.localPlayerCoroutine = StartCoroutine(PocketDimensionEffect(playerClientId));
            }
        }

        [ServerRpc(RequireOwnership = false)]
        private void PlayerExitServerRpc(int playerClientId, int exitStyle){
            PlayerExitClientRpc(playerClientId,exitStyle);
        }

        // 
        [ClientRpc]
        private void PlayerExitClientRpc(int playerClientId, int exitStyle){
            // For all Players
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            this.playerIsInPD[playerClientId] = false;
            player.playerBodyAnimator.speed = 1;
            if(playerClientId != (int)NetworkManager.Singleton.LocalClientId){
                return;
            }

            // Only Affected Player runs code below
            if(this.localPlayerCoroutine != null){
                StopCoroutine(this.localPlayerCoroutine);
            }
            switch (exitStyle)
            {
                case (int)ExitStyle.RESET: break;
                case (int)ExitStyle.ESCAPED: {
                    // TODO: Chance to reappear where player was before entering, and risk for completely random inside facility.
                    player.drunkness = 0.2f;
                    player.drunknessInertia = 0.2f;
                    player.playerBodyAnimator.speed = 1;
                    player.movementSpeed *= 3;
                    player.SpawnPlayerAnimation();
                    PlayPersonalSoundServerRpc(playerClientId,(int)RoomType.MAIN,(int)AudioGroup.EXIT,0);
                    int locationChance = UnityEngine.Random.Range(0,3);
                    // 1/3 chance to appear somewhere far away, 2/3 to appear where you entered.
                    Vector3 reappearPosition = locationChance == 0 ? scpReference.ReturnFromPDRandomLocation(this.localPlayerExitLocation) : this.localPlayerExitLocation;
                    scpReference.PlaceCorrosionServerRpc(playerClientId+2,reappearPosition);
                    player.TeleportPlayer(reappearPosition);
                    break;
                }
                default: break;
            }
        }

        // Limited time to escape Pocket Dimension, then the player dies.
        // Coroutine to move camera similar to SCP-CB Pocket Dimension
        private IEnumerator PocketDimensionEffect(int playerClientId){
            LogIfDebugBuild("Started PocketDimensionEffect");
            float bleedoutTimer = 45f;
            float timeSinceEntered = Time.realtimeSinceStartup; // StartOfRound.Instance.timeSinceRoundStarted
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            
            //personalAudios[playerClientId].PlayOneShot(personalAudioEnter[0]);
            PlayPersonalSoundServerRpc(playerClientId,(int)RoomType.MAIN,(int)AudioGroup.ENTER,0);
            // Make the player drunk and slow
            
            player.drunkness = 20f;
            //player.drunknessInertia = 15f;
            player.movementSpeed/=3;
            player.playerBodyAnimator.speed/=2;
            LogIfDebugBuild($"Attempting Teleport to position {mainRoomSpawn.position}");
            player.TeleportPlayer(mainRoomSpawn.position);
            player.SpawnPlayerAnimation();
            player.DropBlood(default,true,true);

            // Stay drunk for a while
            while (!player.isPlayerDead && (Time.realtimeSinceStartup - timeSinceEntered < bleedoutTimer - 4)){
                //player.drunkness += 0.5f*Time.deltaTime;
                //player.drunknessInertia += 0.5f*Time.deltaTime;
                yield return null;
            }
            // Start to die
            PlayPersonalSoundServerRpc(playerClientId,(int)RoomType.MAIN,(int)AudioGroup.BREATHING,0);
            player.DropBlood(default,true,true);
            player.MakeCriticallyInjured(true);
            while(!player.isPlayerDead && (Time.realtimeSinceStartup - timeSinceEntered < bleedoutTimer)){
                player.movementSpeed -= 0.1f*Time.deltaTime;
                player.drunknessInertia += 1f*Time.deltaTime;
                yield return null;
            }
            // Has player already died before blood loss? 
            // (This Coroutine should already be killed if player dies by ways related to this mod/Pocket Dimension)
            if (player.isPlayerDead){
                player.movementSpeed = 4.6f;
                player.drunkness = 0f;
                player.drunknessInertia = 0f;
                yield break;
            } else {
                // Expiration Death 1 - Blood Loss
                // Ensure that the player affected is the one to call the actual death.
                // (To avoid race conditions due to latency issues)
                if(playerClientId == (int)NetworkManager.LocalClientId){
                    int clipIndex = UnityEngine.Random.Range(0,2);
                    PlayPersonalSoundServerRpc(playerClientId,(int)RoomType.MAIN,(int)AudioGroup.DEATH,clipIndex);
                    PlayerDeathServerRpc(playerClientId,(int)DeathStyle.BLEED);
                }
            }
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
                    //DeathStyleSlamClientRpc(playerClientId,optionalExtraInfo);
                    break;
                }
                case (int)DeathStyle.BLEED: {
                    DeathStyleBleedClientRpc(playerClientId);
                    break;
                }
                case (int)DeathStyle.KNEEL: {
                    DeathStyleKneelClientRpc(playerClientId);
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

        // Called when a player enters a trigger. Rolls for chance to escape, retry, or die.
        public void RollForExit(int playerClientId, int roomType, Vector3 optionalVector = default){
            LogIfDebugBuild($"RollForExit! {playerClientId} {roomType} {optionalVector}");
            int rollChance = UnityEngine.Random.Range(0,10);
            switch(roomType){
                case (int)RoomType.MAIN:
                    if(rollChance <= 0){ //0 10% chance for Death
                        DeathStyleSlam(playerClientId,optionalVector);
                    } else if(rollChance <= 1){ // 10 % chance for Immediate Escape
                        PlayerExitServerRpc(playerClientId,(int)ExitStyle.ESCAPED);
                    } else if (rollChance <= 3){ // 20% chance for Retry
                        StartOfRound.Instance.allPlayerScripts[playerClientId].TeleportPlayer(mainRoomSpawn.position);
                        personalAudios[playerClientId].PlayOneShot(personalAudioMisc[0]); // Only for local player
                    } else { // 60% chance for Corridor
                        StartOfRound.Instance.allPlayerScripts[playerClientId].TeleportPlayer(corridorSpawn.position);
                    }
                    break;
                case (int)RoomType.CORRIDOR:
                    if(rollChance <= 1){ // 20% chance for Retry
                        StartOfRound.Instance.allPlayerScripts[playerClientId].TeleportPlayer(corridorSpawn.position);
                    } else if (rollChance <= 3) { // 20% chance for Escape
                        PlayerExitServerRpc(playerClientId,(int)ExitStyle.ESCAPED);
                    } else { // 60% chance for Throne
                        StartOfRound.Instance.allPlayerScripts[playerClientId].TeleportPlayer(throneRoomSpawn.position);    
                    }
                    break;
                case (int)RoomType.THRONE:
                    if (localPlayerCoroutine != null){
                        StopCoroutine(localPlayerCoroutine);
                    }
                    localPlayerCoroutine = StartCoroutine(ThroneRoomEffect(playerClientId)); 
                    break;
                default: break;
            }
            LogIfDebugBuild($"RollForExit of room type {roomType}!");
        }

        // Sequence for when a player enters the Throne Room.
        private IEnumerator ThroneRoomEffect(int playerClientId){
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            player.drunkness = 0f;
            player.drunknessInertia = 0f;
            float timeToKneel = 10f; // 10 seconds to kneel before death
            yield return new WaitForSeconds(3f);
            float startTime = Time.realtimeSinceStartup;
            PlayPersonalSoundServerRpc(playerClientId,(int)RoomType.THRONE,(int)AudioGroup.ENTER,0);
            
            LogIfDebugBuild($"ThroneRoomEffect Waiting for player to Crouch!");
            bool warningMusic = false;
            // Wait for player to crouch.
            while(!player.isCrouching && Time.realtimeSinceStartup - startTime < timeToKneel){
                // Play warning music when there's 4 seconds left
                if (!warningMusic && Time.realtimeSinceStartup - startTime > timeToKneel - 4){
                    warningMusic = true;
                    PlayPersonalSoundServerRpc(playerClientId,(int)RoomType.MAIN,(int)AudioGroup.DEATH,0);
                }
                yield return new WaitForSeconds(0.1f); // Don't run every frame
            }
            // Let Player Live
            if(player.isCrouching){
                StopPersonalSoundServerRpc(playerClientId);
                LogIfDebugBuild($"ThroneRoomEffect Player Crouched in time!");
                player.gameplayCamera.transform.position = player.cameraContainerTransform.position; // Restore camera position
                PlayerExitServerRpc(playerClientId,(int)ExitStyle.ESCAPED);
            } else { // Let Player Die
                LogIfDebugBuild($"ThroneRoomEffect Player will be killed!");
                Coroutine cameraShake = StartCoroutine(ShakePlayerCamera(player));
                yield return new WaitForSeconds(4);
                PlayPersonalSoundServerRpc(playerClientId,(int)RoomType.MAIN,(int)AudioGroup.DEATH,4);
                if(cameraShake != null){
                    StopCoroutine(cameraShake);
                }
                player.gameplayCamera.transform.position = player.cameraContainerTransform.position;
                player.KillPlayer(new Vector3(0,0,0),true,CauseOfDeath.Crushing,1);
            }
        }

        // Shake the player's camera, increasing intensity over time until player dies.
        private IEnumerator ShakePlayerCamera(PlayerControllerB player){
            float intensityOverTime = 0f;
            while(!player.isPlayerDead){
                player.gameplayCamera.transform.position = player.cameraContainerTransform.position + UnityEngine.Random.insideUnitSphere * intensityOverTime;
                if(intensityOverTime<0.15f){
                    intensityOverTime += Time.deltaTime*0.04f;
                }
                yield return null;
            }
            player.gameplayCamera.transform.position = player.cameraContainerTransform.position; // Restore camera position
        }

        // Teleport Death 1.2 - Player slammed into wall
        public void DeathStyleSlam(int playerClientId, Vector3 crushColliderPosition){
            StartCoroutine(SlamToWall(playerClientId,crushColliderPosition));
        }

        // Teleport Death 1.2 - Animation to Kill Player
        private IEnumerator SlamToWall(int playerClientId, Vector3 crushColliderPosition){
            if(playerClientId == (int)NetworkManager.Singleton.LocalClientId){
                StopCoroutine(this.localPlayerCoroutine);
            }
            float timeSinceStart = Time.realtimeSinceStartup;
            float timeToDie = 1f;

            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            Vector3 turnDirection = crushColliderPosition - player.gameObject.transform.position;

            // Play Death Sound
            //int clipIndex = UnityEngine.Random.Range(2,5);
            //PlayPersonalSoundServerRpc(playerClientId,(int)RoomType.MAIN,(int)AudioGroup.DEATH,clipIndex);
            PlayPersonalSoundServerRpc(playerClientId,(int)RoomType.MAIN,(int)AudioGroup.DEATH,3);
            //player.disableSyncInAnimation = true;
            // Smooth animation to collide with wall
            while (Time.realtimeSinceStartup - timeSinceStart < timeToDie){
                player.gameObject.transform.rotation = Quaternion.Lerp(player.gameObject.transform.rotation, Quaternion.LookRotation(turnDirection), Time.deltaTime *2f);
                player.gameObject.transform.position = Vector3.Lerp(player.gameObject.transform.position, crushColliderPosition, Time.deltaTime*2f);
                yield return null;
            }
            PlayPersonalSoundServerRpc(playerClientId,(int)RoomType.MAIN,(int)AudioGroup.DEATH,5);
            yield return new WaitForSeconds(0.2f);
            player.KillPlayer(new Vector3(5,0,5),true,CauseOfDeath.Crushing,1,default);
            //player.disableSyncInAnimation = false;
            PlayerValueResetServerRpc(playerClientId);
        }

        // Expiration Death 1 - Player dies from blood loss
        [ClientRpc]
        private void DeathStyleBleedClientRpc(int playerClientId){
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            player.KillPlayer(Vector3.zero,true,CauseOfDeath.Stabbing,0,default);
            PlayerValueResetClientRpc(playerClientId);
        }

        // Expiration Death 2 - Player fails to Kneel (Head Explosion)
        [ClientRpc]
        private void DeathStyleKneelClientRpc(int playerClientId){
            StartOfRound.Instance.allPlayerScripts[playerClientId].KillPlayer(new(0,0,0),true,CauseOfDeath.Crushing,1);
            PlayerValueResetClientRpc(playerClientId);
        }
    }
}