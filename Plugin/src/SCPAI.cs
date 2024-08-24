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

namespace SCP106 {

    class SCPAI : EnemyAI, IVisibleThreat, INoiseListener
    {
        /*TODO:
        * SCP-106 invisible bug
            - When spawned, players not inside don't render him (on test map)
        * Better GrabPlayer animation
            - Simpler animation for those not affected (those watching/not grabbed)
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
        * Sneak State
            - If SCP sees player but player dont see them, Sneak attack
            - Surprise attack from behind, chance to send to PocketDimension
            - Horror10 SFX if target player spots them
        * Personal Audio
            - Similar to L4D2's Sound System, highly personalised experience
            - If one player spots SCP, don't play that sound for everyone
            - Only play audio for those relevant
        * Carry Head after Kill
            - DeadBodyInfo body, body.attachedTo / body.attackedLimb = SCP Hands
        */
        // We set these in our Asset Bundle, so we can disable warning CS0649:
        // Field 'field' is never assigned to, and will always have its default value 'value'
        #pragma warning disable 0649

        public GameObject pocketdimension; // Pocket Dimension Prefab
        private GameObject pocketd; // Pocket Dimension Spawned
        private PocketDimController pdController; // Pocket Dimension Controller

        public GameObject corrosion; // Prefab
        private GameObject[] corrosionSpawned; // Instantiated & Spawned (1 for each player + 2 for SCP)

        public Transform boneHead; // Head object reference of the model
        public Transform boneNeck; // Neck object reference
        public Transform turnReference; // Invisible object pointing towards player, as reference to smoothly track head & neck
        public GameObject scanNode;
        public MeshRenderer mapNode;
        public SkinnedMeshRenderer scpRenderer;

        // creatureSFX - Special SFX's (e.g. Spotted)
        // creatureVoice - Breathing, Laughing
        public AudioSource creatureStep; // Footstep Source
        public AudioSource chaseSource; // [] Chase Music
        public AudioClip[] footstepSounds; // [StepSCP1, -2, -3, -4]
        public AudioClip[] spottedSounds; // [Horror1, Horror2, Horror10, Horror14, Horror16] When player sees SCP (First 2 Intense)
        public AudioClip[] neckSounds; // [NeckSnap, NeckTurn] 
        public AudioClip[] killingSounds; // [GrabbedSFX, Damage5, Horror9, Horror14] SCP's SFX
        public AudioClip[] playerKilledSounds; // [KillSFX, Damage2] Player's SFX
        public AudioClip[] corrosionSounds; // [WallDecay1, -2, -3] When seeing a player killed
        public AudioClip sinkSFX; // Decay3
        public AudioClip emergeSFX; // Decay0

        public ParticleSystem creatureVFX; // "Puke" / Corrosion VFX
        private GameObject[] doors = []; // All doors on the map (inside & outside).

        private Coroutine killCoroutine;
        private Coroutine faceCoroutine;

        public bool lookAtPlayer = false;
        public bool killingPlayer = false;
        private bool sneaking = false;

        #pragma warning restore 0649

        // TODO: use timestamps instead of counting (for optimization)
        float timeAtHittingPlayer;
        float timeAtHitByPlayer;
        float timeAtLastSpotted;
        float timeAtLastNoiseHeard;
        float timeAtHuntStart;
        float timeAtLastExit;

        readonly float spottedSFXCooldown = 60; // Cooldown in Seconds between doing the surprised "Spotted" sequence
        readonly float chaseMusicLimit = 60; // Play chase music for 60 seconds, then check if we can turn it off (only if no one nearby)
        readonly float emergeCooldown = 120; // After this many seconds of not seeing a player, emerge near the loneliest one.
        readonly float emergeCooldownOutside = 240; // Same as above but for when outside, occurs left often by default to not incessently annoy people in ship

        private float targetPlayerMovementSpeed; // Restore original movement speed for target player (e.g. after stunned during kill animation)
        private float targetPlayerJumpForce;

        private System.Random rnd = new();

        // Configuration settings
        private int nonDeadlyInteractions = 15; // Chance to push, etc
        private int chanceForPocketDimension = 20;
        private bool stunnable = true;
        private bool canGoOutside = false;
        private bool canGoInsideShip = false;

        public ThreatType type => throw new System.NotImplementedException();

        public enum State { // SCP Creature States
            IDLE,
            SEARCHING,
            SPOTTED,
            HUNTING,
            KILLING,
            EMERGING, // "Teleport" To a Player
            SINKING,
        }
        public enum HeadState { // SCP Head State (Animation)
            StartLookingAtPlayer,
            FinishedLookingAtPlayer,
        }
        public enum SFX {
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
        public enum KillState { // SCP Kill States (Kill Animations / Kill Events, e.g. "Death by PukingAtPlayer")
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

        [ServerRpc]
        public void InitSCPValuesServerRpc() {
            int deadly = Math.Clamp(Plugin.BoundConfig.NonDeadlyInteractions.Value,0,100);
            int pdChance = Math.Clamp(Plugin.BoundConfig.ChanceForPocketDimension.Value,0,100);
            bool stun = Plugin.BoundConfig.Stunnable.Value;
            bool outside = Plugin.BoundConfig.CanGoOutside.Value;
            bool ship = Plugin.BoundConfig.CanGoInsideShip.Value;

            // Pocket dimension
            Vector3 spawnPos = base.transform.position;

            this.pocketd = Instantiate(pocketdimension,spawnPos - new Vector3(0,600,0),Quaternion.identity,base.transform);
            pocketd.GetComponent<NetworkObject>().Spawn(); // Spawns Pocket dimension for all players.
            this.pdController = pocketd.GetComponentInChildren<PocketDimController>();
            this.pdController.RegisterSCPServerRpc(pocketd.transform.position);

            /*this.corrosionSpawned = new GameObject[StartOfRound.Instance.allPlayerScripts.Length + 2];
            for(int i = 0; i < corrosionSpawned.Length; i++){
                GameObject corr = Instantiate(corrosion, new Vector3(0,0,0),Quaternion.identity,base.transform);
                this.corrosionSpawned[i] = corr;
                corr.GetComponent<NetworkObject>().Spawn();
                corr.SetActive(false);
            }*/

            InitSCPValuesClientRpc(deadly,pdChance,stun,outside,ship);
        }

        [ClientRpc]
        public void InitSCPValuesClientRpc(int deadly, int pdChance, bool stun, bool outside, bool ship) {
            // Setup the default values, e.g. config values
            nonDeadlyInteractions = deadly;
            chanceForPocketDimension = pdChance;
            stunnable = stun;
            enemyType.canBeStunned = stun;
            canGoOutside = outside;
            canGoInsideShip = ship;

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
            
            //AvoidHazards();

            // Start spawn animation
            creatureAnimator.SetTrigger("startStill");
            StartCoroutine(DelayAndStateClient(3f, (int)State.SEARCHING));
        }

        // Waits for specified time and then changes to specified state.
        private IEnumerator DelayAndStateServer(float timeToWait, int newStateInt) {
            yield return new WaitForSeconds(timeToWait);
            SwitchToBehaviourServerRpc(newStateInt);
            DoAnimationServerRpc(newStateInt);
        }

        // Waits for specified time and then changes to specified state.
        private IEnumerator DelayAndStateClient(float timeToWait, int newStateInt) {
            yield return new WaitForSeconds(timeToWait);
            SwitchToBehaviourClientRpc(newStateInt);
            DoAnimationClientRpc(newStateInt);
        }

        /*
            Gets and saves all gameobjects with component "DoorLock".
            When <HUNTING> a player, SCP will ignore locked doors (Normal & Big).
            Needed since AI will find alternate path around these normally, but here we animate him phasing through it.
            TODO: If investigating source behind locked door, he should phase through that too.
        */
        private void FindAndIgnoreAllDoors() {
            openDoorSpeedMultiplier = 0;
            DoorLock[] allDoors = UnityEngine.Object.FindObjectsOfType<DoorLock>();
            foreach(DoorLock door in allDoors){
                doors.AddItem<GameObject>(door.gameObject);
            }
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
                    ExitEnterFacility();
                    StopChaseMusicIfNoOneNearbyAndLimitReached();
                    HuntIfPlayerIsInSight();
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
                //ToStateHunting();
                
                SneakOnPlayer();
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

        // If Target Player is running, then SCP will speed up a little too.
        private void RunIfPlayerRuns(){

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
                if(fastEmergeChance<2){
                    ChangeTargetPlayerServerRpc(-1); // Null targetplayer
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
            SetDestinationToPosition(targetPlayer.transform.position, checkForPath: true); //TODO: Set checkForPath to FALSE if something bugs
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
                StartCoroutine(SpottedPlayerEffect());
                PlaySFXServerRpc((int)SFX.Chasing, true);
            }
        }

        private IEnumerator PhaseThroughDoor(GameObject door) {
            creatureAnimator.SetTrigger("startWalk");
            creatureAnimator.speed = 0.5f;
            agent.speed = 0.5f;
            yield return null;
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
            SetDestinationToPosition(mainDoorPosition);
        }

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
            StartCoroutine(SpottedPlayerEffect());
            PlaySFXServerRpc((int)SFX.Chasing, true);
        }

        /*
            [HUNTING]
            FROM STATE, TO STATE (SEARCHING,HUNTING)
            Called if SCP sees player, but they don't see him.
            Sneaks silently up behind to attack.
        */
        private void SneakOnPlayer(){
            // Become more silent and change state
            sneaking = true;
            creatureStep.volume = 0.25f;
            SwitchToBehaviourState((int)State.HUNTING);
            PlaySFXServerRpc((int)SFX.Breathing, false);
            PlaySFXServerRpc((int)SFX.Chasing, false);
            DoAnimationServerRpc((int)State.HUNTING);
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
                if (player.isInsideFactory == isOutside){
                    return;
                }
                // Note: 'isPlayerAlone' only becomes true if 1. no one near them, 2. hears no one in WalkieTalkie, and 3. >1 player in lobby/game
                if (!player.isPlayerAlone && StartOfRound.Instance.livingPlayers != 1){
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
            StartSinkingClientRpc();
            StartEmergeSequenceClientRpc(playerClientId);
        }

        [ClientRpc]
        public void StartEmergeSequenceClientRpc(int playerClientId) {
            StartCoroutine(EmergeNearPlayer(playerClientId));
        }

        [ClientRpc]
        private void StartSinkingClientRpc(){
            creatureAnimator.SetTrigger("startSink");
            creatureSFX.PlayOneShot(sinkSFX);
        }

        private IEnumerator EmergeNearPlayer(int playerClientId) {
            // Do and wait for Sink Animation to finish
            yield return new WaitForSeconds(3f);

            // Get Player Position
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            Vector3 playerPosition = player.transform.position;
            Vector3 closestNodeToPlayer = ChooseClosestNodeToPosition(playerPosition, true).position;
            
            // If player leaves (and SCP not allowed to leave), then re-appear back to original position.
            // Or, if enters ship and SCP not allowed to enter ship, - .. -
            if (!player.isInsideFactory && !canGoOutside || player.isInHangarShipRoom && !canGoInsideShip){
                creatureAnimator.speed = 0.7f;
                creatureAnimator.SetTrigger("startEmerge");
                creatureSFX.PlayOneShot(emergeSFX);
                yield return new WaitForSeconds(10f);
                SwitchToBehaviourClientRpc((int)State.SEARCHING);
                DoAnimationClientRpc((int)State.SEARCHING);
                yield break;
            }
            // Create Corrosion at playerPosition (TODO), and Emerge from it
            /*if(IsHost || IsServer){
                StartCoroutine(GrowCorrosion(1));
            }*/
            creatureAnimator.speed = 0.7f;
            creatureAnimator.SetTrigger("startEmerge");
            creatureSFX.PlayOneShot(emergeSFX);
            agent.Warp(closestNodeToPlayer);
            agent.speed = 0;
            agent.enabled = false;

            yield return new WaitForSeconds(10f);

            // When fully emerged, start hunting player (or searching if they already ran away)
            agent.enabled = true;
            SwitchToBehaviourClientRpc((int)State.HUNTING);
            DoAnimationClientRpc((int)State.HUNTING);
            PlaySFXServerRpc((int)SFX.Chasing, true);
            timeAtHuntStart = Time.realtimeSinceStartup;
        }

        /*  [EMERGE]
            Fast Emerge, emerge quickly in front of a random player.
        */
        [ServerRpc(RequireOwnership = false)]
        private void FastEmergeServerRpc(){
            PlayerControllerB playerToEmergeAt = GetClosestPlayer(false,true,true);
            if(playerToEmergeAt == null){
                return;
            }
            FastEmergeClientRpc((int)playerToEmergeAt.playerClientId);
        }

        [ClientRpc]
        private void FastEmergeClientRpc(int playerClientId){
            StartCoroutine(FastEmerge(playerClientId));
        }

        private IEnumerator FastEmerge(int playerClientId){
            SwitchToBehaviourState((int)State.EMERGING);
            StopSearch(currentSearch);
            agent.speed = 0f;
            agent.isStopped = true;
            creatureAnimator.speed = 2f;
            creatureAnimator.SetTrigger("startSink");
            yield return new WaitForSeconds(2f);

            PlayerControllerB playerToEmergeAt = StartOfRound.Instance.allPlayerScripts[playerClientId];
            Vector3 emergeLocation = playerToEmergeAt.gameObject.transform.position + playerToEmergeAt.cameraContainerTransform.forward*4f;
            Vector3 emergePosition = base.ChooseClosestNodeToPosition(emergeLocation).position;
            Vector3 emergeRotation = base.transform.position - playerToEmergeAt.gameObject.transform.position;
            agent.Warp(emergePosition);
            base.transform.rotation = Quaternion.LookRotation(emergeRotation);
            creatureAnimator.SetTrigger("startEmerge");
            creatureSFX.PlayOneShot(corrosionSounds[0]);
            yield return new WaitForSeconds(4f);

            // <play Horror 10, 14 or 16>
            AudioClip spottedClip = spottedSounds[UnityEngine.Random.Range(2,5)];
            creatureSFX.PlayOneShot(spottedClip);
            ChangeTargetPlayerClientRpc((int)playerToEmergeAt.playerClientId);
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

                int rollSendToPD = rnd.Next(0,101);
                if (rollSendToPD <= chanceForPocketDimension){
                    GrabPlayerServerRpc((int)collidedPlayer.playerClientId,true);
                    //GrabAndSendPlayerToPocketDimensionServerRpc((int)collidedPlayer.playerClientId);
                } else{
                    int rollDeadly = rnd.Next(0,101);
                    if (rollDeadly <= nonDeadlyInteractions){
                        PushPlayerServerRpc((int)collidedPlayer.playerClientId);
                    } else {
                        GrabAndKillPlayerServerRpc((int)collidedPlayer.playerClientId);
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
            LogIfDebugBuild($"Player {NetworkManager.LocalClientId} is in SendPlayerToPocketDimensionServerRpc!");
            //PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            //Ray interactRay = new(player.transform.position + player.transform.up * 2f, Vector3.down);
            /*if (Physics.Raycast(player.transform.position, player.transform.TransformDirection(Vector3.down), out RaycastHit rayHit, 6f, StartOfRound.Instance.collidersAndRoomMaskAndDefault, QueryTriggerInteraction.Ignore)){
                this.corrosionSpawned[playerClientId].transform.position = rayHit.point;
                this.corrosionSpawned[playerClientId].SetActive(true);
                StartCoroutine(GrowCorrosion(playerClientId+2));
            }*/
            //SendPlayerToPocketDimensionClientRpc();
            pdController.PlayerEnterPocketDimensionServerRpc(playerClientId, reappearPosition);
        }

        [ClientRpc]
        private void SendPlayerToPocketDimensionClientRpc(){
            StartCoroutine(SinkWaitEmergeNearPlayer());
        }

        [ServerRpc(RequireOwnership = false)]
        private void GrabAndSendPlayerToPocketDimensionServerRpc(int playerClientId){
            if(killingPlayer || inSpecialAnimation){
                return;
            }
            killingPlayer = true;
            inSpecialAnimation = true;
            GrabAndSendPlayerToPocketDimensionClientRpc(playerClientId);
        }

        [ClientRpc]
        private void GrabAndSendPlayerToPocketDimensionClientRpc(int playerClientId){
            killCoroutine = StartCoroutine(GrabPlayerShort(playerClientId,true));
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

        private IEnumerator GrowCorrosion(int corrosionIndex){
            float timeSinceStarted = Time.realtimeSinceStartup;
            while(Time.realtimeSinceStartup - timeSinceStarted < 2f){
                this.corrosionSpawned[corrosionIndex].transform.localScale = Vector3.Lerp(new(0,0,0),new(5,5,0),2f * Time.deltaTime);
                yield return null;
            }
        }

/* * [PLAYER RELATED FUNCTIONS END] * */

/* * [KILLING ANIMATIONS START] * */

        [ServerRpc(RequireOwnership = false)]
        private void GrabPlayerServerRpc(int playerClientId, bool sendToPD = false){
            if(killingPlayer || inSpecialAnimation){
                return;
            }
            killingPlayer = true;
            inSpecialAnimation = true;
            inSpecialAnimationWithPlayer = StartOfRound.Instance.allPlayerScripts[playerClientId];
            inSpecialAnimationWithPlayer.inAnimationWithEnemy = this;
            GrabPlayerClientRpc(playerClientId,sendToPD);
        }

        [ClientRpc]
        private void GrabPlayerClientRpc(int playerClientId, bool sendToPD = false){
            killingPlayer = true;
            inSpecialAnimation = true;
            inSpecialAnimationWithPlayer = StartOfRound.Instance.allPlayerScripts[playerClientId];
            inSpecialAnimationWithPlayer.inAnimationWithEnemy = this;
            if(inSpecialAnimationWithPlayer == null || inSpecialAnimationWithPlayer.isPlayerDead){
                FinishGrabPlayer();
                return;
            }
            killCoroutine = StartCoroutine(GrabPlayerAnimation(playerClientId,sendToPD));
        }

        /*
            SCP forces targetPlayer to face them, enters into an animation and then kills the player.
        */
        [ServerRpc(RequireOwnership = false)]
        public void GrabAndKillPlayerServerRpc(int playerClientId) {
            if(inSpecialAnimation){
                return;
            }
            GrabAndKillPlayerClientRpc(playerClientId,sneaking);
        }
        [ClientRpc]
        public void GrabAndKillPlayerClientRpc(int playerClientId, bool isSneaking = false) {
            inSpecialAnimationWithPlayer = StartOfRound.Instance.allPlayerScripts[playerClientId];
            if (inSpecialAnimationWithPlayer == GameNetworkManager.Instance.localPlayerController){
                inSpecialAnimationWithPlayer.CancelSpecialTriggerAnimations();
            }
            inSpecialAnimation = true;
            if (isSneaking){
                killCoroutine = StartCoroutine(GrabPlayerShort(playerClientId));
            } else{
                killCoroutine = StartCoroutine(GrabPlayer(playerClientId));
            }
        }

        /* [CALLED BY SERVER RPC] (make Client calls only inside here)
            Grabs and kills the target player
        */
        private IEnumerator GrabPlayer(int playerClientId) {
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            if (!killingPlayer && !player.isPlayerDead){
                // Restore variables
                targetPlayerMovementSpeed = inSpecialAnimationWithPlayer.movementSpeed;
                targetPlayerJumpForce = inSpecialAnimationWithPlayer.jumpForce;

                // Player Model Manipulation
                if(inSpecialAnimationWithPlayer.isHoldingObject){
                    inSpecialAnimationWithPlayer.DiscardHeldObject();
                }
                inSpecialAnimationWithPlayer.disableSyncInAnimation = true;
                inSpecialAnimationWithPlayer.disableLookInput = true;
                inSpecialAnimationWithPlayer.disableMoveInput = true;
                inSpecialAnimationWithPlayer.Crouch(false);
                Vector3 oldPlayerPosition = inSpecialAnimationWithPlayer.gameObject.transform.position;
                inSpecialAnimationWithPlayer.gameObject.transform.position = new Vector3(oldPlayerPosition.x,base.transform.position.y,oldPlayerPosition.z);
                faceCoroutine = StartCoroutine(MakePlayerFaceSCP(playerClientId,true));

                // SCP Object Manipulation
                killingPlayer = true;
                agent.enabled = false;
                turnReference.LookAt(inSpecialAnimationWithPlayer.transform.position);
                base.transform.LookAt(inSpecialAnimationWithPlayer.transform.position);
                SetDestinationToPosition(agent.transform.position);
                SwitchToBehaviourClientRpc((int)State.KILLING);
                DoAnimationClientRpc((int)State.KILLING);
                PlaySFXClientRpc((int)SFX.Neck);
                
                // Wait for Head to turn before playing scream
                yield return new WaitForSeconds(0.20f);
                if(sneaking){
                    PlaySFXClientRpc((int)SFX.Killing,true,UnityEngine.Random.Range(2,4));
                } else {
                    PlaySFXClientRpc((int)SFX.Killing);
                }
                creatureVFX.Play();
                //Coroutine voiceCoroutine = StartCoroutine(PitchDownPlayerAudio(playerClientId));
                
                yield return new WaitForSeconds(0.25f);
                inSpecialAnimationWithPlayer.AddBloodToBody();
                inSpecialAnimationWithPlayer.DamagePlayer(inSpecialAnimationWithPlayer.health/2);
                inSpecialAnimationWithPlayer.DropBlood(default,true,true);
                // Wait for animation to Finish
                yield return new WaitForSeconds(1.25f);
                creatureVFX.Stop();

                // Kill
                Vector3 velocity = new(3,0,0);
                inSpecialAnimationWithPlayer.KillPlayer(velocity,true,CauseOfDeath.Crushing,1);
                PlaySFXClientRpc((int)SFX.PlayerKilled);
                PlaySFXClientRpc((int)SFX.Laughing);

                // Return state Player
                RestoreTargetPlayerValues();

                // Wait a little before continuing
                creatureAnimator.SetTrigger("startStill");
                yield return new WaitForSeconds(3f);
                agent.enabled = true;

                // Return state SCP
                SwitchToBehaviourClientRpc((int)State.SEARCHING);
                DoAnimationClientRpc((int)State.SEARCHING);
                inSpecialAnimation = false;
                killingPlayer = false;
                FinishGrabPlayer();
                // Stop Coroutines, just in case
                StopCoroutine(faceCoroutine);
                //StopCoroutine(voiceCoroutine);
            }
        }

        /*
            [For all] Change SCP State to KILLING
            [For all] Play killing animation
            [For all] Turn affected player towards SCP
            [For all] Start FaceRoutine
            [For all] Wait 2 seconds
            [For all] Stop FaceRoutine
                - [For all] Send player to Pocket Dimension
                - [For all] Kill Player
            [For all] Restore affected player values
            [For all] Wait 2 seconds
            [For all] Change SCP State to SEARCHING
            [For all] Play searching animation
        */
        private IEnumerator GrabPlayerAnimation(int playerClientId, bool sendToPD = false){
            if (!killingPlayer){
                yield break;
            }
            LogIfDebugBuild($"Player {(int)NetworkManager.Singleton.LocalClientId} playing Grab Animation");
            SwitchToBehaviourClientRpc((int)State.KILLING);
            DoAnimationClientRpc((int)State.KILLING); // setTrigger, animatorSpeed, agentStopped true, agentSpeed 0

            // [For all] Turn affected player towards SCP
            inSpecialAnimationWithPlayer.disableSyncInAnimation = true;
            inSpecialAnimationWithPlayer.disableLookInput = true;
            inSpecialAnimationWithPlayer.disableMoveInput = true;
            inSpecialAnimationWithPlayer.Crouch(false);
            inSpecialAnimationWithPlayer.gameObject.transform.position = this.transform.position + this.transform.forward*1.2f;
            Coroutine localFaceRoutine = StartCoroutine(MakePlayerFaceSCP(playerClientId));

            yield return new WaitForSeconds(2f);
            StopCoroutine(localFaceRoutine);

            // Either send player to Pocket Dimension or Kill
            if (sendToPD && playerClientId == (int)NetworkManager.Singleton.LocalClientId){
                LogIfDebugBuild($"Player {(int)NetworkManager.Singleton.LocalClientId} calling SendToPDServerRpc!");
                PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
                SendPlayerToPocketDimensionServerRpc(playerClientId,player.gameObject.transform.position);
            }
            else if (!sendToPD){
                inSpecialAnimationWithPlayer.KillPlayer(new(0,0,0),true,CauseOfDeath.Crushing,0);
            }

            // Return to Searching State.
            yield return new WaitForSeconds(2f);
            FinishGrabPlayer();
        }

        /*
            Shorter variant of GrabPlayer. Used in the Sneak Kill and Sending Player to Pocket Dimension.
        */
        private IEnumerator GrabPlayerShort(int playerClientId, bool sendToPD = false){
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            inSpecialAnimationWithPlayer = player;
            // SCP
            killingPlayer = true;
            agent.enabled = false;
            SwitchToBehaviourClientRpc((int)State.KILLING);
            DoAnimationClientRpc((int)State.KILLING);
            // Update Restore variables
            targetPlayerMovementSpeed = inSpecialAnimationWithPlayer.movementSpeed;
            targetPlayerJumpForce = inSpecialAnimationWithPlayer.jumpForce;

            // Player Model Manipulation
            if(sendToPD){
                inSpecialAnimationWithPlayer.DropAllHeldItems(true,false);
            } else if(inSpecialAnimationWithPlayer.isHoldingObject){
                inSpecialAnimationWithPlayer.DiscardHeldObject();
            }

            inSpecialAnimationWithPlayer.disableSyncInAnimation = true;
            inSpecialAnimationWithPlayer.disableLookInput = true;
            inSpecialAnimationWithPlayer.disableMoveInput = true;
            inSpecialAnimationWithPlayer.Crouch(false);
            inSpecialAnimationWithPlayer.gameObject.transform.position = this.transform.position + this.transform.forward*1.2f;
            if(playerClientId == (int)NetworkManager.Singleton.LocalClientId){
                faceCoroutine = StartCoroutine(MakePlayerFaceSCP(playerClientId,false));
            }

            // Sound Effect
            int clipIndex = UnityEngine.Random.Range(2,5);
            PlaySFXClientRpc((int)SFX.Spotted,true,clipIndex);
            yield return new WaitForSeconds(2f);

            // Kill Player, or Send to Pocket Dimension
            if(playerClientId == (int)NetworkManager.Singleton.LocalClientId){
                StopCoroutine(faceCoroutine);
            }
            if(sendToPD){
                SendPlayerToPocketDimensionServerRpc(playerClientId,inSpecialAnimationWithPlayer.gameObject.transform.position);
            } else{
                inSpecialAnimationWithPlayer.KillPlayer(new(0,0,0),true,CauseOfDeath.Crushing,1);
            }
            PlaySFXClientRpc((int)SFX.PlayerKilled,true,1);
            PlaySFXClientRpc((int)SFX.Laughing);
            yield return new WaitForSeconds(1.25f);

            // Return state Player
            RestoreTargetPlayerValues();
            inSpecialAnimation = false;

            // Return state SCP
            killingPlayer = false;
            SwitchToBehaviourClientRpc((int)State.SEARCHING);
            DoAnimationClientRpc((int)State.SEARCHING);
            FinishGrabPlayer();
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