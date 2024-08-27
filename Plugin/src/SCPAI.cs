using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using UnityEditor;
using UnityEngine.AI;
using UnityEngine.SearchService;
using UnityEngine.UIElements;
using System;
using DunGen;

namespace SCP106 {

    class SCPAI : EnemyAI, IVisibleThreat, INoiseListener
    {
        /*TODO:
        * SCP-106 invisible bug
            - When spawned, players not inside don't render him (on test map)
        * Camera Effects
            - Grabbed Player Corrosion Camera Effect
            - Grabbed Player Lens Distortion
            - Pocket Dimension After Image Effect
        * Better SoundEffects
            - Grabbed SFX
            - LookAtPlayer / TwistNeck SFX
        * Improve networking
            - Remove unneccesary Coroutine calls (avoid data inconsistensies, only let affected player run routine)
            - Look over Server & Client RPC calls
        * Corrosion GFX
            - Place underneath players going to Pocket Dimension
            - Place underneath SCP-106 when sinking, another when emerging 
            - "Growing puddle" animation
        * Personal Audio
            - Similar to L4D2's Sound System, highly personalised experience
            - If one player spots SCP, don't play that sound for everyone
            - Only play audio for those relevant
        * Carry Head after Kill
            - DeadBodyInfo body, body.attachedTo / body.attackedLimb = SCP Hands
        * Kneel image
            - playercontrollerb.playerScreen ?
        * PD Footstep sound
            - StartOfRound.Instance.footstepSurfaces (surfaceTag)
        */
        // We set these in our Asset Bundle, so we can disable warning CS0649:
        // Field 'field' is never assigned to, and will always have its default value 'value'
        #pragma warning disable 0649
        [Header("Pocket Dimension")]
        public GameObject pocketdimension; // Pocket Dimension Prefab
        private GameObject pocketd; // Pocket Dimension Spawned
        private PocketDimController pdController; // Pocket Dimension Controller
        [Header("Corrosion Decal")]
        public GameObject corrosion; // Prefab
        private GameObject[] corrosionSpawned; // Instantiated & Spawned (1 for each player + 2 for SCP)
        [Header("SCP-106 Model")]
        public Transform boneHead; // Head object reference of the model
        public Transform boneNeck; // Neck object reference
        public Transform turnReference; // Invisible object pointing towards player, as reference to smoothly track head & neck
        public GameObject scanNode;
        public MeshRenderer mapNode;
        public SkinnedMeshRenderer scpRenderer;

        [Header("Audio Sources / Clips")]
        // creatureSFX - Special SFX's (e.g. Spotted)
        // creatureVoice - Breathing, Laughing
        public AudioSource creatureStep; // Footstep Source
        public AudioSource chaseSource; // [] Chase Music
        public AudioClip[] footstepSounds; // [StepSCP1, -2, -3, -4]
        public AudioClip[] spottedSounds; // [Horror1, Horror2, Horror10, Horror14, Horror16] When player sees SCP (First 2 Intense)
        public AudioClip[] neckSounds; // [NeckSnap1.ogg, NeckSnap2.ogg] 
        public AudioClip[] killingSounds; // [GrabbedSFX, Damage5, Horror9, Horror14] SCP's SFX
        public AudioClip[] playerKilledSounds; // [KillSFX, Damage2] Player's SFX
        public AudioClip[] corrosionSounds; // [WallDecay1, -2, -3] When seeing a player killed
        public AudioClip sinkSFX; // Decay3
        public AudioClip emergeSFX; // Decay0

        public ParticleSystem creatureVFX; // "Puke" / Corrosion VFX

        private Coroutine killCoroutine;
        private Coroutine faceCoroutine;

        private bool lookAtPlayer = false;
        private bool killingPlayer = false;
        private bool sneaking = false;

        #pragma warning restore 0649

        // TODO: use timestamps instead of counting (for optimization)
        private float timeAtHittingPlayer;
        private float timeAtHitByPlayer;
        private float timeAtLastSpotted;
        private float timeAtLastNoiseHeard;
        private float timeAtHuntStart;
        private float timeAtLastExit;

        readonly float spottedSFXCooldown = 60; // Cooldown in Seconds between doing the surprised "Spotted" sequence
        readonly float chaseMusicLimit = 60; // Play chase music for 60 seconds, then check if we can turn it off (only if no one nearby)
        readonly float emergeCooldown = 120; // After this many seconds of not seeing a player, emerge near the loneliest one.
        readonly float emergeCooldownOutside = 240; // Same as above but for when outside, occurs left often by default to not incessently annoy people in ship

        // Configuration settings
        private int nonDeadlyInteractions = 15; // Chance to push, etc
        private int chanceForPocketDimension = 20;
        private bool stunnable = true;
        private bool canGoOutside = false;
        private bool canGoInsideShip = false;

        public ThreatType type => throw new System.NotImplementedException();

        private enum State { // SCP Creature States
            IDLE,
            SEARCHING,
            SPOTTED,
            HUNTING,
            KILLING,
            EMERGING, // "Teleport" To a Player
            SINKING,
        }
        public enum HeadState { // SCP Head State (Animation) called by Unity Animation, must be public
            StartLookingAtPlayer,
            FinishedLookingAtPlayer,
        }
        private enum SFX {
            Breathing,
            Laughing,
            Spotted,
            Chasing,
            Neck,
            Killing,
            PlayerKilled,
            Sinking,
            Emerging,
        }
        private enum KillState { // SCP Kill States (Kill Animations / Kill Events, e.g. "Death by PukingAtPlayer")
            StaringAtPlayer, // General Collision Kill
            NeckSnap, // If SCP kills from behind
            DragDownIntoCorrosion, // Pull player down into corrosion if collision during Emerging
        }

        [Conditional("DEBUG")]
        void LogIfDebugBuild(string text) {
            Plugin.Logger.LogInfo(text);
        }

        /*
            Start is called when the enemy is spawned in.
        */
        public override void Start() {
            base.Start();
            LogIfDebugBuild("SCP-106 has breached containment.");
            //FindAndIgnoreAllDoors();
            if (IsHost || IsServer){
                InitSCPValuesServerRpc();
            }
        }

        public override void OnDestroy(){
            PlayerControllerB localPlayer = GameNetworkManager.Instance.localPlayerController;
            localPlayer.gameplayCamera.transform.position = localPlayer.cameraContainerTransform.position;
            base.OnDestroy();
        }

        [ServerRpc]
        public void InitSCPValuesServerRpc() {
            int deadly = Math.Clamp(Plugin.BoundConfig.NonDeadlyInteractions.Value,0,100);
            int pdChance = Math.Clamp(Plugin.BoundConfig.ChanceForPocketDimension.Value,0,100);
            bool stun = Plugin.BoundConfig.Stunnable.Value;
            bool outside = Plugin.BoundConfig.CanGoOutside.Value;
            bool ship = Plugin.BoundConfig.CanGoInsideShip.Value;

            Transform bottomMostNode = RoundManager.Instance.GetClosestNode(this.transform.position - new Vector3(0,400,0),false);
            // Pocket dimension
            this.pocketd = Instantiate(pocketdimension,bottomMostNode.position - new Vector3(0,100,0),Quaternion.identity,base.transform);
            pocketd.GetComponent<NetworkObject>().Spawn(); // Spawns Pocket dimension for all players.
            this.pdController = pocketd.GetComponentInChildren<PocketDimController>();
            ulong networkObjectId = this.GetComponent<NetworkObject>().NetworkObjectId;
            this.pdController.RegisterSCPServerRpc(pocketd.transform.position,networkObjectId);

            // Corrosion Decal. 1 for each player, and 2 for SCP. (Index: 0,1 for SCP; 2 + playerClientID for Players)
            // Placed on ground where player enters Pocket Dimension.
            // Placed on ground where SCP sinks, and the second where SCP emerges.
            ulong[] corrosionObjects = new ulong[StartOfRound.Instance.allPlayerScripts.Length + 2];
            for(int i = 0; i < corrosionObjects.Length; i++){
                GameObject corr = Instantiate(corrosion, new Vector3(0,0,0),Quaternion.Euler(90,0,0),base.transform);
                NetworkObject corrosionNetworkObject = corr.GetComponent<NetworkObject>();
                corrosionNetworkObject.Spawn();
                corrosionNetworkObject.DestroyWithScene = true;
                corrosionObjects[i] = corrosionNetworkObject.NetworkObjectId;
            }

            InitSCPValuesClientRpc(deadly,pdChance,stun,outside,ship,corrosionObjects);
        }

        [ClientRpc]
        public void InitSCPValuesClientRpc(int deadly, int pdChance, bool stun, bool outside, bool ship, ulong[] corrosionObjects) {
            // Setup the default values, e.g. config values
            nonDeadlyInteractions = deadly;
            chanceForPocketDimension = pdChance;
            stunnable = stun;
            enemyType.canBeStunned = stun;
            //canGoOutside = outside;
            //canGoInsideShip = ship;
            canGoOutside = false;
            canGoInsideShip = false;

            timeAtHitByPlayer = Time.realtimeSinceStartup;
            timeAtLastExit = Time.realtimeSinceStartup;
            timeAtHuntStart = Time.realtimeSinceStartup;
            timeAtLastSpotted = Time.realtimeSinceStartup - 60;
            timeAtLastNoiseHeard = Time.realtimeSinceStartup - 15;
            timeAtHittingPlayer = Time.realtimeSinceStartup;
            
            // Define where SCP can walk
            agent.areaMask = NavMesh.AllAreas;
            if(!ship){
                //agent.areaMask &= ~(1 << NavMesh.GetAreaFromName("PlayerShip"));
            }
            agent.areaMask &= ~(1 << NavMesh.GetAreaFromName("Not Walkable"));
            agent.areaMask &= ~(1 << NavMesh.GetAreaFromName("SmallSpace"));

            // Corrosion Decals
            this.corrosionSpawned = new GameObject[corrosionObjects.Length];
            for(int i = 0; i < corrosionObjects.Length; i++){
                this.corrosionSpawned[i] = NetworkManager.Singleton.SpawnManager.SpawnedObjects[corrosionObjects[i]].gameObject;
                this.corrosionSpawned[i].SetActive(false);
            }
            
            //AvoidHazards();

            // Start spawn animation
            creatureAnimator.SetTrigger("startStill");
            StartCoroutine(DelayAndStateClient(3f, (int)State.SEARCHING));
        }

        // Waits for specified time and then changes to specified state.
        private IEnumerator DelayAndStateClient(float timeToWait, int newStateInt) {
            yield return new WaitForSeconds(timeToWait);
            SwitchToBehaviourClientRpc(newStateInt);
            DoAnimationClientRpc(newStateInt);
        }

        /*
            Update is called every frame (light calculations only)
        */
        public override void Update() {
            base.Update();
        }

        public void LateUpdate() {
            // Snap the head to look at player during "SPOTTED" phase
            if (lookAtPlayer){
                // Model
                boneHead.LookAt(targetPlayer.transform.position);
                boneHead.Rotate(0, 180, -90);
            }
        }

        // Spotted 'turning head' VFX
        [ServerRpc(RequireOwnership = false)]
        public void LookServerRpc(bool look) {
            LookClientRpc(look);
        }

        [ClientRpc]
        public void LookClientRpc(bool look) {
            lookAtPlayer = look;
            if (!look) {
                return;
            }
            eye.LookAt(targetPlayer.gameObject.transform);
            AudioClip spottedSFX = spottedSounds[UnityEngine.Random.Range(0,spottedSounds.Length)];
            AudioClip neckSFX = neckSounds[UnityEngine.Random.Range(0,neckSounds.Length)];
            creatureSFX.PlayOneShot(spottedSFX);
            creatureSFX.PlayOneShot(neckSFX);
        }
        /*
            DoAIInterval runs every X seconds as defined in Unity (not every frame, allows heavier calculations)
        */
        public override void DoAIInterval() {
            base.DoAIInterval();
            switch(currentBehaviourStateIndex){
                case (int)State.SEARCHING:
                    /*if(canGoOutside){
                        ExitEnterFacility();
                    }*/
                    HuntIfPlayerIsInSight();
                    StopChaseMusicIfNoOneNearbyAndLimitReached();
                    HuntLoneliestPlayer();
                    break;

                case (int)State.HUNTING:
                    if(sneaking){
                        SneakCheck();
                    }
                    SearchIfPlayerIsTooFarAway();
                    break;

                default:
                    break;
            }
            SyncPositionToClients();
        }

        /*
            [SEARCHING]
            If Searching with Chase music on, with play timer reached, turn it off if no one is close enough to hear.
        */
        private void StopChaseMusicIfNoOneNearbyAndLimitReached() {
            if (!chaseSource.isPlaying){
                return;
            }
            if (Time.realtimeSinceStartup - timeAtHuntStart < chaseMusicLimit) {
                return;
            }
            bool isCloseEnoughToHear = FoundClosestPlayerInRange(30f,30f);
            if (!isCloseEnoughToHear) {
                PlaySFXServerRpc((int)SFX.Chasing, false);
                PlaySFXServerRpc((int)SFX.Breathing, false);
            }
        }

        /*
            [SEARCHING]
            FROM STATE, TO STATE = (SEARCHING,SPOTTED/HUNTING)
            If player sees SCP-106 while SCP-106 is Searching, SCP starts hunting them.
        */
        private void HuntIfPlayerIsInSight() {
            if (currentBehaviourStateIndex == (int)State.SPOTTED){
                return;
            }

            PlayerControllerB closestPlayerInSight = GetClosestPlayer(requireLineOfSight: true, cannotBeInShip: true, cannotBeNearShip: true);
            if (closestPlayerInSight == null) {
                return;
            }

            bool playerIsLooking = PlayerLookingAtMe(closestPlayerInSight);
            targetPlayer = closestPlayerInSight;
            StopSearch(currentSearch);
            ChangeTargetPlayerServerRpc((int)closestPlayerInSight.playerClientId);
            if (playerIsLooking) {
                sneaking = false;

                // Check if we should do the Spotted sequence or go straight to Hunting
                if (Time.realtimeSinceStartup - timeAtLastSpotted < spottedSFXCooldown) {
                    ToStateHunting();
                }
                else { // Spotted Sequence
                    timeAtLastSpotted = Time.realtimeSinceStartup;
                    ToStateSpotted();
                }
            } else {
                SneakOnPlayer(true);
            }
        }

        /*
            [SEARCHING]
            Investigates noise if it's close enough to be heard
        */
        public override void DetectNoise(Vector3 noisePosition, float noiseLoudness, int timesPlayedInOneSpot = 0, int noiseID = 0)
        {
            if (currentBehaviourStateIndex != (int)State.SEARCHING) {
                return;
            }
            if (Time.realtimeSinceStartup - timeAtLastNoiseHeard < 15){
                return;
            }

            base.DetectNoise(noisePosition, noiseLoudness, timesPlayedInOneSpot, noiseID);
            float noiseDistance = Vector3.Distance(base.transform.position, noisePosition);
            float noiseSpreadDistance = 30f * noiseLoudness; // Account noise loudness with regards to how far away it was made

            if (Physics.Linecast(base.transform.position, noisePosition, 256)) {
                noiseLoudness /= 2f;
                noiseSpreadDistance /= 2f;
            }
            if (noiseLoudness < 0.25f){
                return;
            }
            // Investigate source of noise iff noise source distance is less than the accounted range the noise could make
            if (noiseDistance < noiseSpreadDistance){
                SetDestinationToPosition(noisePosition);
                PlaySFXServerRpc((int)SFX.Breathing, true);
                timeAtLastNoiseHeard = Time.realtimeSinceStartup;
            }
        }

        /*
            [HUNTING]
            FROM STATE, TO STATE = (HUNTING,SEARCHING)
            If target player out of sight, and too far away, go into Search mode.
        */
        private void SearchIfPlayerIsTooFarAway() {
            float distanceBetweenPlayer = Vector3.Distance(transform.position, targetPlayer.transform.position);
            float maxDistanceToHunt = 30f;
            bool playerInSight = CheckLineOfSightForPosition(targetPlayer.transform.position);
            // If player moves too far away - or out of sight - stop hunting.
            if(!TargetClosestPlayerInAnyCase() || (distanceBetweenPlayer > maxDistanceToHunt && !playerInSight)){
                int fastEmergeChance = UnityEngine.Random.Range(0,10);
                // 20% chance to reappear infront of player
                if(fastEmergeChance<2){ // <2
                    FastEmergeServerRpc();
                    return;
                }
                ToStateSearching();
                return;
            }
            // If player moves to inaccessible location (e.g. outside with config for it disabled)
            if(!targetPlayer.isInsideFactory && !isOutside || 
                targetPlayer.isInsideFactory && isOutside || 
                targetPlayer.isInHangarShipRoom && !canGoInsideShip){
                ToStateSearching();
                return;
            }
            // Sometimes the path can be very long but distance short, e.g. when SCP & Target are on different levels.
            if (pathDistance > 50f){
                ToStateSearching();
                return;
            }

            // Default Hunting Behaviour
            SetDestinationToPosition(targetPlayer.transform.position, checkForPath: false); //TODO: Set checkForPath to FALSE if something bugs
        }

        /*
            [HUNTING]
            FROM STATE, TO STATE (HUNTING,HUNTING)
            Iff hunted player spots SCP during sneak-attack, go into normal hunting mode.
            If not, continue sneaking toward player.
        */
        private void SneakCheck(){
            if(PlayerLookingAtMe(targetPlayer)){
                sneaking = false;
                creatureStep.volume = 0.9f;
                PlaySFXServerRpc((int)SFX.Spotted, true);
                //StartCoroutine(SpottedPlayerEffect());
                PlaySFXServerRpc((int)SFX.Chasing, true);
            }
        }

        /*
            [SEARCHING]
            Only works if CanGoOutside is True.
            Call with True to Enter facility, False to Exit.
            Called if SCP hasn't seen a player in some time, or if hunting a player.
        */
        private void ExitEnterFacility() {
            if(!canGoOutside){
                return;
            }
            if (Time.realtimeSinceStartup - timeAtLastExit < 3f){
                return;
            }
            if (GetClosestPlayer(!isOutside)){
                return;
            }
            // Allow SCP to use the door if last hunt started < 10 seconds ago (targetplayer entered/exited, so follow them)
            // Allow SCP to use the door if last hunt started > 120 seconds ago (no player spotted in a while, check inside/outside)
            if (Time.realtimeSinceStartup - timeAtHuntStart > 10f && Time.realtimeSinceStartup - timeAtHuntStart < 120){
                return;
            }

            Vector3 mainDoorPosition = RoundManager.FindMainEntrancePosition(true,isOutside);
            float distanceFromDoor = Vector3.Distance(transform.position,mainDoorPosition);
            if (GetClosestPlayer() && PathIsIntersectedByLineOfSight(mainDoorPosition,false,false)){
                return;
            }
            // Try to Enter Facility
            if(distanceFromDoor < 1f){
                Vector3 otherDoor = RoundManager.FindMainEntrancePosition(true, !isOutside);
                Vector3 newPos = RoundManager.Instance.GetNavMeshPosition(otherDoor);
                TeleportSCPServerRpc(newPos, !isOutside);
                return;
            }
            if(currentSearch.inProgress){
                StopSearch(currentSearch);
            }
            SetDestinationToPosition(mainDoorPosition);
        }

        // RPC Calls for Entering/Exiting the Facility. Only called if CanGoOutside is True.
        [ServerRpc(RequireOwnership = false)]
        public void TeleportSCPServerRpc(Vector3 newPos, bool setOutside){
            TeleportSCPClientRpc(newPos, setOutside);
        }

        [ClientRpc]
        public void TeleportSCPClientRpc(Vector3 newPos, bool setOutside){
            // Set variables
            agent.enabled = false;
            if (currentSearch.inProgress){
                StopSearch(currentSearch);
            }
            transform.position = newPos;
            agent.enabled = true;
            timeAtLastExit = Time.realtimeSinceStartup;
            SetEnemyOutside(setOutside);

            // Play the audio sound
            EntranceTeleport entranceTeleport = RoundManager.FindMainEntranceScript(setOutside);
            if (entranceTeleport.doorAudios != null && entranceTeleport.doorAudios.Length != 0){
                entranceTeleport.entrancePointAudio.PlayOneShot(entranceTeleport.doorAudios[0]);
            }
            StartSearch(transform.position);
        }

        /* [HUNTING]
            Called when going to a searching state.
        */
        private void ToStateSearching() {
            LogIfDebugBuild("Searching State");
            ChangeTargetPlayerServerRpc(-1);
            if (currentSearch.inProgress){
                StopSearch(currentSearch);
            }
            SwitchToBehaviourServerRpc((int)State.SEARCHING);
            DoAnimationServerRpc((int)State.SEARCHING);
        }

        /*
            [SEARCHING]
            FROM STATE, TO STATE (SEARCHING,SPOTTED)
            Called when going to a Spotted State
        */
        private void ToStateSpotted() {
            // States
            sneaking = false;
            creatureStep.volume = 0.9f;
            if (currentBehaviourStateIndex == (int)State.SPOTTED){
                return;
            }
            LogIfDebugBuild("[SCP-106] SCP-106 has been Spotted!");
            SwitchToBehaviourServerRpc((int)State.SPOTTED);
            DoAnimationServerRpc((int)State.SPOTTED);
        }

        /*
            [SPOTTED]
            FROM STATE, TO STATE (SPOTTED,HUNTING)
        */
        public void ToStateHunting() {
            sneaking = false;
            creatureStep.volume = 0.9f;
            // States
            LogIfDebugBuild("[SCP-106] SCP-106 is hunting!");
            SwitchToBehaviourServerRpc((int)State.HUNTING);
            DoAnimationServerRpc((int)State.HUNTING);
            timeAtHuntStart = Time.realtimeSinceStartup;
            //StartCoroutine(SpottedPlayerEffect());
            PlaySFXServerRpc((int)SFX.Chasing, true);
        }

        /*
            [HUNTING]
            FROM STATE, TO STATE (SEARCHING,HUNTING)
            Called if SCP sees player, but they don't see him.
            Sneaks silently up behind to attack.
        */
        private void SneakOnPlayer(bool changeState = true){
            // Become more silent and change state
            sneaking = true;
            creatureStep.volume = 0.15f;
            PlaySFXServerRpc((int)SFX.Breathing, false);
            PlaySFXServerRpc((int)SFX.Chasing, false);
            if(changeState){
                SwitchToBehaviourState((int)State.HUNTING);
                DoAnimationServerRpc((int)State.HUNTING);
            }
        }

/* * [UNITY ANIMATION EVENTS START] * * /
        /*
        Called everytime SCP-106 lands a foot on the ground (Unity Animation Event)
        */
        public void PlayFootstepSound(){
            AudioClip step = footstepSounds[UnityEngine.Random.Range(0,footstepSounds.Length)];
            creatureStep.PlayOneShot(step);
        }

        /*
            SPOTTED - Called by Unity Animation Event
        */
        public void LookAtPlayer(HeadState state) {
            if (currentBehaviourStateIndex != (int)State.SPOTTED){
                return;
            }
            switch(state) {
                case HeadState.StartLookingAtPlayer:
                    LookServerRpc(true);
                    PlayerControllerB[] allInSight = GetAllPlayersInLineOfSight(360,60,eye);
                    if (allInSight != null){
                        foreach (PlayerControllerB player in allInSight)
                        {
                            if (player == GameNetworkManager.Instance.localPlayerController) {
                                player.JumpToFearLevel(0.9f);
                            }
                        }
                    }
                    break;
                case HeadState.FinishedLookingAtPlayer:
                    LookServerRpc(false);
                    ToStateHunting();
                    break;
                default:
                    break;
            }
        }

/* * [UNITY ANIMATION EVENTS END] * /

/* * [PLAYER RELATED FUNCTIONS START] * */
        /*
            Given a player (within line of sight), returns whether or not they are looking at SCP-106.
        */
        private bool PlayerLookingAtMe(PlayerControllerB player) {
            Vector3 myPosition = transform.position;
            float fovWidth = 45f;
            float proximityAwareness = -1;
            int viewRange = 60;
            return player.HasLineOfSightToPosition(myPosition,fovWidth,viewRange,proximityAwareness);
        }

        /*
            Given player has a Zoom-In-Zoom-Out effect when seeing SCP-106.
            Happens only once per hunt, and only to the player being hunted.
        */
        private IEnumerator SpottedPlayerEffect(){
            LogIfDebugBuild("Entered SpottedPlayerEffect");
            if(targetPlayer == null){
                yield break;
            }

            // Zoom in effect by decreasing FOV
            for (int a = 0; a < 10; a++){
                targetPlayer.gameplayCamera.transform.position = Vector3.Lerp(
                    targetPlayer.gameplayCamera.transform.position,
                    targetPlayer.gameplayCamera.transform.position + targetPlayer.transform.forward*0.15f,
                    a/10f
                );
                yield return null;
            }

            // Zoom out effect by increasing FOV (back to original value)
            for (int b = 0; b < 120; b++){
                targetPlayer.gameplayCamera.transform.position = Vector3.Lerp(
                    targetPlayer.gameplayCamera.transform.position,
                    targetPlayer.cameraContainerTransform.position,
                    b/120f
                );
                yield return null;
            }
            targetPlayer.gameplayCamera.transform.position = targetPlayer.cameraContainerTransform.position;
        }

        /*
            Returns whether or not a player is in view of SCP-106.
        */
        bool FoundClosestPlayerInRange(float range, float senseRange) {
            TargetClosestPlayer(bufferDistance: 1.5f, requireLineOfSight: true);
            if(targetPlayer == null){
                // Couldn't see a player, so we check if a player is in sensing distance instead
                TargetClosestPlayer(bufferDistance: 1.5f, requireLineOfSight: false);
                range = senseRange;
            }
            return targetPlayer != null && Vector3.Distance(transform.position, targetPlayer.transform.position) < range;
        }
        
        /*  
            Returns true if a player is close / near to SCP-106, regardless of being in sights.
        */
        bool TargetClosestPlayerInAnyCase() {
            mostOptimalDistance = 2000f;
            PlayerControllerB checkPlayer = null;
            for (int i = 0; i < StartOfRound.Instance.connectedPlayersAmount + 1; i++)
            {
                tempDist = Vector3.Distance(transform.position, StartOfRound.Instance.allPlayerScripts[i].transform.position);
                if (tempDist < mostOptimalDistance)
                {
                    mostOptimalDistance = tempDist;
                    checkPlayer = StartOfRound.Instance.allPlayerScripts[i];
                }
            }
            if(checkPlayer == null) return false;
            return true;
        }

        /*
            [SEARCHING -> EMERGE]
            Sets the TargetPlayer to the player who is furthest away from the other players.
        */ 
        public void HuntLoneliestPlayer() {
            if (currentBehaviourStateIndex != (int)State.SEARCHING) {
                return;
            }
            if (Time.realtimeSinceStartup - timeAtHuntStart < emergeCooldown) {
                return;
            }
            foreach (PlayerControllerB player in StartOfRound.Instance.allPlayerScripts)
            {
                // This would just be too unfair! ... Maybe
                if (player.isInElevator){
                    return;
                }
                // For the moment unable to emerge into Pocket Dimension, but is planned to work.
                if (pdController.playerIsInPD[(int)player.playerClientId]){
                    return;
                }
                // Note: 'isPlayerAlone' only becomes true if 1. no one near them, 2. hears no one in WalkieTalkie, and 3. >1 player in lobby/game
                if (!player.isPlayerAlone && StartOfRound.Instance.livingPlayers != 1){
                    return;
                }
                if (player.isInsideFactory == isOutside){
                    return;
                }
                if (player.isInHangarShipRoom && !canGoInsideShip){
                    return;
                }
                if (isOutside && Time.realtimeSinceStartup - timeAtHuntStart < emergeCooldownOutside){
                    return;
                }
                StopSearch(currentSearch);
                SwitchToBehaviourServerRpc((int)State.EMERGING);
                ChangeTargetPlayerServerRpc((int)player.playerClientId);
                StartEmergeSequenceServerRpc((int)player.playerClientId);
                return;
            }
        }

        /*
            [EMERGE]
            SCP sinks into the ground and then rises from a node near the player (and behind them).
            SCP then chases this player by default.
        */
        [ServerRpc(RequireOwnership = false)]
        public void StartEmergeSequenceServerRpc(int playerClientId) {
            // Place Corrosion Decal
            bool placeCorrosion = false;
            Vector3 corrosionPosition = Vector3.zero;
            if(Physics.Raycast(transform.position, transform.TransformDirection(Vector3.down), out RaycastHit raycastHitEnter, 0.5f, StartOfRound.Instance.collidersAndRoomMaskAndDefault)){
                placeCorrosion = true;
                corrosionPosition = raycastHitEnter.point;
            }
            StartSinkingClientRpc(placeCorrosion,corrosionPosition);
            StartEmergeSequenceClientRpc(playerClientId);
        }

        [ClientRpc]
        public void StartEmergeSequenceClientRpc(int playerClientId) {
            StartCoroutine(EmergeNearPlayer(playerClientId));
        }

        [ClientRpc]
        private void StartSinkingClientRpc(bool placeCorrosion, Vector3 corrosionPosition){
            if(placeCorrosion){
                this.corrosionSpawned[0].transform.position = corrosionPosition;
                this.corrosionSpawned[0].SetActive(true);
            }
            creatureAnimator.SetTrigger("startSink");
            creatureSFX.PlayOneShot(sinkSFX);
        }

        // Animation for emerging near but out-of-sight for the loneliest player.
        private IEnumerator EmergeNearPlayer(int playerClientId) {
            LogIfDebugBuild("Entered EmergeNearPlayer");
            // Do and wait for Sink Animation to finish
            yield return new WaitForSeconds(3f);

            // Get Player Position
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            Vector3 playerPosition = player.transform.position;
            Vector3 closestNodeToPlayer = ChooseClosestNodeToPosition(playerPosition, true).position;
            
            // If player leaves (and SCP not allowed to leave), then re-appear back to original position.
            // Or, if enters ship and SCP not allowed to enter ship, - .. -
            if (!player.isInsideFactory && !canGoOutside || player.isInHangarShipRoom && !canGoInsideShip){
                LogIfDebugBuild("EmergeNearPlayer bool check failed");
                creatureAnimator.speed = 0.7f;
                creatureAnimator.SetTrigger("startEmerge");
                creatureSFX.PlayOneShot(emergeSFX);
                yield return new WaitForSeconds(10f);
                SwitchToBehaviourClientRpc((int)State.SEARCHING);
                DoAnimationClientRpc((int)State.SEARCHING);
                yield break;
            }
            LogIfDebugBuild("EmergeNearPlayer bool check passed");
            creatureAnimator.speed = 0.7f;
            creatureAnimator.SetTrigger("startEmerge");
            creatureSFX.PlayOneShot(emergeSFX);
            agent.Warp(closestNodeToPlayer);
            agent.speed = 0;
            agent.enabled = false;
            // Place Corrosion Decal
            if(IsServer || IsHost){
                if(Physics.Raycast(transform.position, transform.TransformDirection(Vector3.down), out RaycastHit raycastHitEnter, 0.5f, StartOfRound.Instance.collidersAndRoomMaskAndDefault)){
                    PlaceCorrosionServerRpc(1,raycastHitEnter.point);
                }
            }

            yield return new WaitForSeconds(9f);
            SwitchToBehaviourClientRpc((int)State.HUNTING);
            DoAnimationClientRpc((int)State.HUNTING);
            PlaySFXServerRpc((int)SFX.Chasing, true);
            yield return new WaitForSeconds(1f);
            agent.enabled = true;

            // When fully emerged, start hunting player (or searching if they already ran away)
            timeAtHuntStart = Time.realtimeSinceStartup;
        }

        /*  [EMERGE/HUNTING]
            Fast Emerge, emerge quickly in front of a player.
            - Called if currently hunted player is close with regards to distance, but too far by path.
        */
        [ServerRpc(RequireOwnership = false)]
        private void FastEmergeServerRpc(){
            PlayerControllerB playerToEmergeAt = GetClosestPlayer(false,true,true);
            if(playerToEmergeAt == null || pdController.playerIsInPD[(int)playerToEmergeAt.playerClientId]){
                ToStateSearching();
                return;
            }
            FastEmergeClientRpc((int)playerToEmergeAt.playerClientId);
        }

        [ClientRpc]
        private void FastEmergeClientRpc(int playerClientId){
            StartCoroutine(FastEmerge(playerClientId));
        }

        private IEnumerator FastEmerge(int playerClientId){
            // Sink into the ground
            SwitchToBehaviourState((int)State.EMERGING);
            StopSearch(currentSearch);
            agent.speed = 0f;
            agent.isStopped = true;
            creatureAnimator.speed = 2.5f;
            creatureAnimator.SetTrigger("startSink");
            creatureSFX.PlayOneShot(sinkSFX);

            // Place Corrosion Decal
            if(IsServer || IsHost){
                if(Physics.Raycast(transform.position, transform.TransformDirection(Vector3.down), out RaycastHit raycastHitEnter, 0.5f, StartOfRound.Instance.collidersAndRoomMaskAndDefault)){
                PlaceCorrosionServerRpc(0,raycastHitEnter.point);
            }
            }

            yield return new WaitForSeconds(2f);

            // Find place to emerge within the player's view
            PlayerControllerB playerToEmergeAt = StartOfRound.Instance.allPlayerScripts[playerClientId];
            Vector3 emergeLocation = playerToEmergeAt.gameObject.transform.position + playerToEmergeAt.cameraContainerTransform.forward*4f;
            Vector3 emergePosition = base.ChooseClosestNodeToPosition(emergeLocation).position;
            Vector3 emergeRotation = base.transform.position - playerToEmergeAt.gameObject.transform.position;
            agent.Warp(emergePosition);
            base.transform.rotation = Quaternion.LookRotation(emergeRotation);
            creatureAnimator.SetTrigger("startEmerge");
            creatureSFX.PlayOneShot(corrosionSounds[UnityEngine.Random.Range(0,corrosionSounds.Length)]);

            // Place Corrosion Decal
            if(IsServer || IsHost){
                if(Physics.Raycast(transform.position, transform.TransformDirection(Vector3.down), out RaycastHit raycastHitExit, 0.5f, StartOfRound.Instance.collidersAndRoomMaskAndDefault)){
                PlaceCorrosionServerRpc(1,raycastHitExit.point);
            }
            }

            yield return new WaitForSeconds(3f);

            // <play Horror 10, 14 or 16>
            //AudioClip spottedClip = spottedSounds[UnityEngine.Random.Range(2,5)];
            //creatureSFX.PlayOneShot(spottedClip);
            SneakOnPlayer(false);
            ChangeTargetPlayerClientRpc((int)playerToEmergeAt.playerClientId);
            // Change state
            SwitchToBehaviourClientRpc((int)State.HUNTING);
            DoAnimationClientRpc((int)State.HUNTING);
        }

        /*
            Called when SCP-106 touches / collides with an object (e.g. Player)
        */
        public override void OnCollideWithPlayer(Collider other) {
            if (currentBehaviourStateIndex == (int)State.EMERGING ||
                currentBehaviourStateIndex == (int)State.KILLING){
                return;
            }
            if (killingPlayer){
                return;
            }
            if (Time.realtimeSinceStartup - timeAtHittingPlayer < 1f) {
                return;
            }
            //PlayerControllerB collidedPlayer = MeetsStandardPlayerCollisionConditions(other);
            PlayerControllerB collidedPlayer = other.GetComponent<PlayerControllerB>();

            if (collidedPlayer != null && !collidedPlayer.isPlayerDead)
            {
                if (this.inSpecialAnimationWithPlayer != null){
                    if (collidedPlayer.playerClientId == inSpecialAnimationWithPlayer.playerClientId){
                        return;
                    }
                    this.inSpecialAnimationWithPlayer = collidedPlayer;
                }

                int rollSendToPD = UnityEngine.Random.Range(0,101);
                if (rollSendToPD <= chanceForPocketDimension){
                    LogIfDebugBuild("Collision: Sending to PD!");
                    GrabPlayerServerRpc((int)collidedPlayer.playerClientId,true);
                } else{
                    int rollDeadly = UnityEngine.Random.Range(0,101);
                    if (rollDeadly <= nonDeadlyInteractions){
                        LogIfDebugBuild("Collision: Pushing!");
                        PushPlayerServerRpc((int)collidedPlayer.playerClientId);
                    } else {
                        LogIfDebugBuild("Collision: Killing!");
                        GrabPlayerServerRpc((int)collidedPlayer.playerClientId,false);
                    }
                }
                timeAtHittingPlayer = Time.realtimeSinceStartup;
            }
        }

        /*
            [ANY]
            Do stuff if hit by an enemy. Result can differ by state, such as if killing a player or hunting another.
        */
        public override void HitEnemy(int force = 1, PlayerControllerB playerWhoHit = null, bool playHitSFX = false, int hitID = -1)
        {
            if(currentBehaviourStateIndex == (int)State.SINKING || currentBehaviourStateIndex == (int)State.EMERGING){
                return;
            }
            base.HitEnemy(force, playerWhoHit, playHitSFX, hitID);
            if (!stunnable){
                return;
            }
            if (Time.realtimeSinceStartup - timeAtHitByPlayer < 15f){
                return;
            }
            // If currently killing & haven't been hit too recently, let player survive
            if (currentBehaviourStateIndex == (int)State.KILLING){
                InterruptKilling((int)playerWhoHit.playerClientId);
            }
            // Else, be stunned for a few seconds before hunting whoever hit SCP-106. 
            else {
                StartCoroutine(DelayAndStateClient(3f, (int)State.HUNTING));
                ChangeTargetPlayerClientRpc((int)playerWhoHit.playerClientId);
            }
            timeAtHitByPlayer = Time.realtimeSinceStartup;
        }

        /* [KILLING]
            Called if stunned or interrupted while killing a player.
        */
        private void InterruptKilling(int playerWhoInterrupted){
            try
                {
                    inSpecialAnimation = false;
                    killingPlayer = false;
                    creatureVFX.Stop();
                    // Stop the coroutines
                    StopCoroutine(killCoroutine);
                    StopCoroutine(faceCoroutine);
                    // Cut off the scream
                    PlaySFXClientRpc((int)SFX.Killing,false);
                    RestoreTargetPlayerValues(); // Let the player move again
                    StartCoroutine(DelayAndStateClient(3f, (int)State.HUNTING)); // Let SCP wait a few seconds before hunting again
                    ChangeTargetPlayerClientRpc(playerWhoInterrupted); // Hunt player who hit SCP-106
                }
                catch (System.Exception)
                {
                    throw;
                }
        }

        /* [KILLING]
            If stunned while killing a player, restore that player's config values, e.g. so they can move and jump again.
        */
        public void RestoreTargetPlayerValues() {
            if (inSpecialAnimationWithPlayer != null){
                inSpecialAnimationWithPlayer.disableSyncInAnimation = false;
                inSpecialAnimationWithPlayer.disableLookInput = false;
                inSpecialAnimationWithPlayer.disableMoveInput = false;
                inSpecialAnimationWithPlayer.gameplayCamera.transform.position = inSpecialAnimationWithPlayer.cameraContainerTransform.position;
                inSpecialAnimationWithPlayer.inAnimationWithEnemy = this;
            }
        }

        [ServerRpc(RequireOwnership = false)]
        private void SendPlayerToPocketDimensionServerRpc(int playerClientId, Vector3 reappearPosition){
            // Place Corrosion Decal
            if(Physics.Raycast(reappearPosition, transform.TransformDirection(Vector3.down), out RaycastHit raycastHitExit, 0.5f, StartOfRound.Instance.collidersAndRoomMaskAndDefault)){
                PlaceCorrosionServerRpc(2+playerClientId,raycastHitExit.point);
            }
            pdController.PlayerEnterPocketDimensionServerRpc(playerClientId, reappearPosition);
        }

        /*
        Returns a randomized vector inside the facility for a Player to appear at when escaping from the Pocket Dimension.
        playerEntryPosition is the location where the player was before entering the Pocket Dimension.
        */
        public Vector3 ReturnFromPDRandomLocation(Vector3 playerEntryPosition){
            Transform nodeTransform = ChooseFarthestNodeFromPosition(playerEntryPosition);
            if (nodeTransform == null){
                return playerEntryPosition;
            } else {
                return nodeTransform.position;
            }
        }   

        [ClientRpc]
        private void SendPlayerToPocketDimensionClientRpc(){
            StartCoroutine(SinkWaitEmergeNearPlayer());
        }

        private IEnumerator SinkWaitEmergeNearPlayer(){
            SwitchToBehaviourServerRpc((int)State.EMERGING);
            DoAnimationServerRpc((int)State.SINKING);
            PlaySFXServerRpc((int)SFX.Sinking);
            yield return new WaitForSeconds(4f);
            scpRenderer.enabled = false;
            mapNode.enabled = false;
            scanNode.SetActive(false);
            PlaySFXServerRpc((int)SFX.Chasing,false);
            yield return new WaitForSeconds(15f);
            scpRenderer.enabled = true;
            mapNode.enabled = true;
            scanNode.SetActive(true);
            PlayerControllerB playerToEmergeAt = GetClosestPlayer(false,true,true);
            if (playerToEmergeAt == null){
                DoAnimationServerRpc((int)State.EMERGING);
                PlaySFXServerRpc((int)SFX.Emerging);
                yield return new WaitForSeconds(4f);
                SwitchToBehaviourServerRpc((int)State.SEARCHING);
                DoAnimationServerRpc((int)State.SEARCHING);
                yield break;
            }
            StartEmergeSequenceServerRpc((int)playerToEmergeAt.playerClientId);
        }

/* * [PLAYER RELATED FUNCTIONS END] * */

/* * [KILLING ANIMATIONS START] * */

        // Collision-based killing animation server call
        [ServerRpc(RequireOwnership = false)]
        private void GrabPlayerServerRpc(int playerClientId, bool sendToPD = false){
            // Avoid duplicate collisions
            if(!killingPlayer){
                inSpecialAnimationWithPlayer = StartOfRound.Instance.allPlayerScripts[playerClientId];
                inSpecialAnimationWithPlayer.inAnimationWithEnemy = this;
                killingPlayer = true;
                inSpecialAnimation = true;
                GrabPlayerClientRpc(playerClientId,sendToPD,sneaking);
            }
        }

        [ClientRpc]
        private void GrabPlayerClientRpc(int playerClientId, bool sendToPD = false, bool isSneaking = false){
            SwitchToBehaviourClientRpc((int)State.KILLING);
            if(currentSearch.inProgress){
                StopSearch(currentSearch);
            }
            inSpecialAnimationWithPlayer = StartOfRound.Instance.allPlayerScripts[playerClientId];
            inSpecialAnimationWithPlayer.inAnimationWithEnemy = this;
            if(inSpecialAnimationWithPlayer == GameNetworkManager.Instance.localPlayerController){
                inSpecialAnimationWithPlayer.CancelSpecialTriggerAnimations();
            }
            killingPlayer = true;
            inSpecialAnimation = true;
            agent.enabled = false;
            DoAnimationClientRpc((int)State.KILLING);
            if (killCoroutine != null){
                StopCoroutine(killCoroutine);
            }
            killCoroutine = StartCoroutine(GrabPlayerAnimation(playerClientId,sendToPD,isSneaking));
        }

        // Animation for Killing The Player
        private IEnumerator GrabPlayerAnimation(int playerClientId, bool sendToPD, bool isSneaking = false){
            // Player manipulation
            inSpecialAnimationWithPlayer.disableSyncInAnimation = true;
            inSpecialAnimationWithPlayer.disableLookInput = true;
            inSpecialAnimationWithPlayer.disableMoveInput = true;
            inSpecialAnimationWithPlayer.gameObject.transform.position = this.transform.position + this.transform.forward*1.2f;
            if(faceCoroutine != null){
                StopCoroutine(faceCoroutine);
            }

            // Play SFX based on status
            creatureSFX.volume = 0.95f;
            if(isSneaking){
                creatureSFX.PlayOneShot(spottedSounds[UnityEngine.Random.Range(2,5)]);
            } else {
                creatureSFX.PlayOneShot(killingSounds[UnityEngine.Random.Range(0,4)]);
            }
            faceCoroutine = StartCoroutine(MakePlayerFaceSCP(playerClientId,!isSneaking));
            yield return new WaitForSeconds(2f);
            // Kill player (if not sending them to Pocket Dimension)
            if(!sendToPD){
                inSpecialAnimationWithPlayer.KillPlayer(new(0,0,0),true,CauseOfDeath.Crushing,1);
            }
            // SFX 2
            PlaySFXClientRpc((int)SFX.PlayerKilled,true,1);
            PlaySFXClientRpc((int)SFX.Laughing);
            creatureAnimator.SetTrigger("startStill");

            // Return state (Send to Pocket Dimension)
            if(sendToPD){
                Vector3 reappearPosition = inSpecialAnimationWithPlayer.transform.position;
                FinishAnimation();
                SendPlayerToPocketDimensionServerRpc(playerClientId,reappearPosition);
            } else { // (Killing Player)
                yield return new WaitForSeconds(1.25f);
                FinishAnimation();
            }
        }

        // Restore all values and reset SCP state
        private void FinishAnimation(){
            if(killCoroutine != null){
                StopCoroutine(killCoroutine);
            }
            if(faceCoroutine != null){
                StopCoroutine(faceCoroutine);
            }
            inSpecialAnimation = false;
            killingPlayer = false;
            if(inSpecialAnimationWithPlayer != null){
                inSpecialAnimationWithPlayer.gameplayCamera.transform.position = inSpecialAnimationWithPlayer.cameraContainerTransform.position;
                inSpecialAnimationWithPlayer.disableSyncInAnimation = false;
                inSpecialAnimationWithPlayer.disableLookInput = false;
                inSpecialAnimationWithPlayer.disableMoveInput = false;
                inSpecialAnimationWithPlayer.inAnimationWithEnemy = null;
                inSpecialAnimationWithPlayer = null;
            }
            agent.enabled = true;
            if(IsServer || IsHost){
                SwitchToBehaviourState((int)State.SEARCHING);
                DoAnimationServerRpc((int)State.SEARCHING);
            }
        }

        // Makes the target player turn towards SCP's face
        private IEnumerator MakePlayerFaceSCP(int playerClientId, bool shake = false) {
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            Vector3 myDirection = base.transform.position - player.gameObject.transform.position;
            // Turn player body
            for(int a = 0; a < 12; a++){
                player.gameObject.transform.rotation = Quaternion.Lerp(
                    player.gameObject.transform.rotation,
                    Quaternion.LookRotation(myDirection),
                    a/12f
                );
                yield return null;
            }
            // Turn player camera
            for (int b = 0; b < 16; b++){
                player.gameplayCamera.transform.position = Vector3.Lerp(
                    player.gameplayCamera.transform.position,
                    turnReference.position + transform.forward*0.25f + transform.up*0.1f,
                    b/16f
                );
                yield return null;
            }
            if(!shake){
                yield break;
            }
            // Shake player camera
            Vector3 newPosition = player.gameplayCamera.transform.position;
            float intensityOverTime = 0f;
            while(!player.isPlayerDead && killingPlayer){
                player.gameplayCamera.transform.position = newPosition + UnityEngine.Random.insideUnitSphere * intensityOverTime;
                if(intensityOverTime<0.15f){
                    intensityOverTime += Time.deltaTime*0.02f;
                }
                yield return null;
            }
        }
        
        // Pitches the player's voice down until they're dead, or until Coroutine stops.
        public IEnumerator PitchDownPlayerAudio(int playerClientId) {
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            while (player.health != 0){
                player.currentVoiceChatAudioSource.pitch -= Time.deltaTime * 1 / 1.5f;
                yield return null;
            }
        }

        private void FinishGrabPlayer(){
            if (killCoroutine != null){
                StopCoroutine(killCoroutine);
            }
            killingPlayer = false;
            if (inSpecialAnimationWithPlayer != null){
                inSpecialAnimationWithPlayer.disableSyncInAnimation = false;
                inSpecialAnimationWithPlayer.disableLookInput = false;
                inSpecialAnimationWithPlayer.disableMoveInput = false;
                inSpecialAnimationWithPlayer.gameplayCamera.transform.position = inSpecialAnimationWithPlayer.cameraContainerTransform.position;
                inSpecialAnimationWithPlayer.inAnimationWithEnemy = null;
            }
            if (IsOwner){
                agent.enabled = true;
            }
            if (IsServer || IsHost){
                SwitchToBehaviourState((int)State.SEARCHING);
                DoAnimationServerRpc((int)State.SEARCHING);
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void PushPlayerServerRpc(int playerClientId) {
            PushPlayerClientRpc(playerClientId);
        }

        [ClientRpc]
        public void PushPlayerClientRpc(int playerClientId) {
            StartCoroutine(SmoothPushPlayer(playerClientId));
        }

        /*
            Pushes the player down on the ground.
        */
        private IEnumerator SmoothPushPlayer(int playerClientId) {
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            float distance = 3f;
            Vector3 scpDirection = transform.forward * distance;
            agent.speed = 0f;
            creatureAnimator.speed = 2f;
            creatureAnimator.SetTrigger("startPush");
            yield return new WaitForSeconds(0.2f);
            PlaySFXClientRpc((int)SFX.Laughing);
            player.SpawnPlayerAnimation();
            player.DamagePlayer(25);
            for (int i = 0; i < 12; i++){
                player.thisController.Move(scpDirection * 1/12);
                yield return null;
            }
            yield return new WaitForSeconds(0.5f);
            creatureAnimator.speed = 1f;
            creatureAnimator.SetTrigger("startStill");
            yield return new WaitForSeconds(3f);
            creatureAnimator.SetTrigger("startWalk");
            creatureAnimator.speed = 3f;
            agent.speed = 3f;
        }

/* * [KILLING ANIMATIONS END] * */

/* * [RPC FUNCTIONS START] * */

        [ServerRpc(RequireOwnership = false)]
        public void PlaceCorrosionServerRpc(int corrosionIndex, Vector3 corrosionPosition){
            PlaceCorrosionClientRpc(corrosionIndex,corrosionPosition);
        }

        [ClientRpc]
        private void PlaceCorrosionClientRpc(int corrosionIndex, Vector3 corrosionPosition){
            this.corrosionSpawned[corrosionIndex].transform.position = corrosionPosition;
            this.corrosionSpawned[corrosionIndex].SetActive(true);
        }

        // Change TargetPlayer
        [ServerRpc(RequireOwnership = false)]
        public void ChangeTargetPlayerServerRpc(int newTargetPlayerId) {
            ChangeTargetPlayerClientRpc(newTargetPlayerId);
        }

        [ClientRpc]
        public void ChangeTargetPlayerClientRpc(int newTargetPlayerId) {
            if (newTargetPlayerId == -1){
                targetPlayer = null;
            }
            else {
                targetPlayer = StartOfRound.Instance.allPlayerScripts[newTargetPlayerId];
            }
        }

        // Play SFX
        [ServerRpc(RequireOwnership = false)]
        public void PlaySFXServerRpc(int intSFX, bool play = true, int clipIndex = -1) {
            PlaySFXClientRpc(intSFX,play,clipIndex);
        }
        [ClientRpc]
        public void PlaySFXClientRpc(int intSFX, bool play = true, int clipIndex = -1) {
            switch(intSFX){
                case (int)SFX.Breathing:
                    if (play){
                        creatureVoice.Play();
                    }
                    else {
                        creatureVoice.Stop();
                    }
                    break;
                case (int)SFX.Laughing:
                    creatureVoice.PlayOneShot(base.enemyType.hitBodySFX);
                    break;
                case (int)SFX.Neck:
                    AudioClip neckSFX = neckSounds[UnityEngine.Random.Range(0,neckSounds.Length)];
                    creatureSFX.volume = 0.7f;
                    creatureSFX.PlayOneShot(neckSFX);
                    break;
                case (int)SFX.Spotted:
                    AudioClip spottedSFX = spottedSounds[UnityEngine.Random.Range(0,spottedSounds.Length)];
                    creatureSFX.volume = 0.7f;
                    creatureSFX.PlayOneShot(spottedSFX);
                    break;
                case (int)SFX.Chasing:
                    if (play && !chaseSource.isPlaying) {
                        chaseSource.Play();
                    }
                    else if (!play && chaseSource.isPlaying) {
                        chaseSource.Stop();
                    }
                    break;
                case (int)SFX.Killing:
                    if (play){
                        AudioClip killSFX;
                        if(clipIndex == -1){
                            killSFX = killingSounds[UnityEngine.Random.Range(0,2)]; // Between 0 and 1
                        } else {
                            killSFX = killingSounds[clipIndex];
                        }
                        creatureSFX.volume = 1f;
                        creatureSFX.PlayOneShot(killSFX);
                    } else {
                        if (creatureSFX.isPlaying){
                            creatureSFX.Stop(true);
                        }
                    }
                    break;
                case (int)SFX.PlayerKilled:
                    AudioClip playerSFX;
                    if(clipIndex == -1){
                        playerSFX = playerKilledSounds[UnityEngine.Random.Range(0,playerKilledSounds.Length)];
                    } else{
                        playerSFX = playerKilledSounds[clipIndex];
                    }
                    creatureSFX.volume = 0.7f;
                    creatureSFX.PlayOneShot(playerSFX);
                    break;
                case (int)SFX.Sinking:
                    creatureSFX.volume = 0.7f;
                    creatureSFX.PlayOneShot(sinkSFX);
                    break;
                case (int)SFX.Emerging:
                    creatureSFX.volume = 0.7f;
                    creatureSFX.PlayOneShot(emergeSFX);
                    break;
                default: break;
            }
        }

        // Sync animation
        [ServerRpc(RequireOwnership = false)]
        public void DoAnimationServerRpc(int newStateIndex) {
            DoAnimationClientRpc(newStateIndex);
        }

        [ClientRpc]
        public void DoAnimationClientRpc(int newStateIndex) {
            switch(newStateIndex){
                case (int)State.IDLE:
                    LogIfDebugBuild("IDLE STATE");
                    creatureAnimator.SetTrigger("startStill");
                    agent.speed = 0.5f;
                    break;
                case (int)State.SEARCHING:
                    LogIfDebugBuild("Searching State!");
                    StartSearch(base.transform.position);
                    creatureSFX.volume = 0.7f;
                    creatureAnimator.SetTrigger("startWalk");
                    creatureAnimator.speed = 2f;
                    agent.speed = 2f;
                    agent.isStopped = false;
                    break;
                case (int)State.SPOTTED:
                    LogIfDebugBuild("Spotted State!");
                    creatureAnimator.SetTrigger("startSpotted");
                    creatureAnimator.speed = 1f;
                    agent.speed = 0f;
                    agent.isStopped = true;
                    break;
                case (int)State.HUNTING:
                    LogIfDebugBuild("Hunting State!");
                    killingPlayer = false;
                    creatureAnimator.SetTrigger("startWalk");
                    creatureAnimator.speed = 3f;
                    agent.speed = 3f;
                    agent.isStopped = false;
                    break;
                case (int)State.KILLING:
                    LogIfDebugBuild("Killing State!");
                    creatureAnimator.SetTrigger("startKill");
                    creatureAnimator.speed = 1f;
                    agent.isStopped = true;
                    agent.speed = 0f;
                    break;
                case (int)State.EMERGING:
                    creatureAnimator.SetTrigger("startEmerge");
                    creatureAnimator.speed = 0.7f;
                    agent.isStopped = true;
                    agent.speed = 0f;
                    break;
                case (int)State.SINKING:
                    creatureAnimator.SetTrigger("startSink");
                    creatureAnimator.speed = 1f;
                    agent.isStopped = true;
                    agent.speed = 0f;
                    break;
                default:
                    LogIfDebugBuild("DoAnimation: Went to Inexistent State");
                    break;
            }
        }
/* * [RPC FUNCTIONS END] * */
/* *  [INTERFACE FUNCTIONS START]  * */

        public int GetThreatLevel(Vector3 seenByPosition)
        {
            int threat = 0;
            threat += currentBehaviourStateIndex switch
            {
                (int)State.SEARCHING => 2,
                (int)State.EMERGING => 4,
                (int)State.HUNTING => 5,
                _ => 1,
            };
            return threat;
        }

        public int GetInterestLevel()
        {
            return 1;
        }

        public Transform GetThreatLookTransform()
        {
            return turnReference;
        }

        public Transform GetThreatTransform()
        {
            return transform;
        }

        public Vector3 GetThreatVelocity()
        {
            if (base.IsOwner){
                return agent.velocity;
            }
            return Vector3.zero;
        }

        public float GetVisibility()
        {
            return currentBehaviourStateIndex switch{
                (int)State.EMERGING => 0f,
                (int)State.HUNTING => 2f,
                _ => 1f,
            };
        }

        public int SendSpecialBehaviour(int id)
        {
            return 0;
        }
    }
}