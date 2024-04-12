using System.Collections;
using System.Diagnostics;
using System.Linq;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UIElements;

namespace SCP106 {

    class SCPAI : EnemyAI
    {
        // We set these in our Asset Bundle, so we can disable warning CS0649:
        // Field 'field' is never assigned to, and will always have its default value 'value'
        #pragma warning disable 0649
        public Transform boneHead; // Head object reference of the model
        public Transform boneNeck; // Neck object reference
        public Transform turnReference; // Invisible object pointing towards player, as reference to smoothly track head & neck

        public AudioSource chaseSource; // Music
        // creatureSFX - Special SFX's (e.g. Spotted)
        // creatureVoice - Breathing, Laughing

        public AudioClip[] footstepSounds; // Footstep sounds
        public AudioClip[] spottedSounds; // SFX when spotted by a player
        public AudioClip[] neckSounds; // SFX for when turning head towards player, and when twisting player's neck
        public AudioClip[] killingSounds; // SCP sounds when they kill a player
        public AudioClip[] playerKilledSounds; // Player sounds when they are killed
        public AudioClip sinkSFX;
        public AudioClip emergeSFX;

        public ParticleSystem creatureVFX;
        public GameObject scpMask; // For emerging, so people below don't see his body if it goes through the floor.
        public GameObject scpModel; // SCP Model that has the SkinnedMeshRenderer component
        public SkinnedMeshRenderer modelRenderer;

        private Coroutine killCoroutine;

        public bool lookAtPlayer = false;
        public bool KillingPlayer = false;

        #pragma warning restore 0649
        float timeSinceHittingLocalPlayer = 0;
        float timeSinceSpottedPlayer = 60;
        float timeSinceHeardNoise = 15;
        readonly float spottedSFXCooldown = 60; // Cooldown in Seconds between doing the surprised "Spotted" sequence
        readonly float chaseMusicLimit = 5; // Play chase music for 60 seconds, then check if we can turn it off (only if no one nearby)
        readonly float emergeCooldown = 70; // After this many seconds of not seeing a player, emerge near the loneliest one.
        private float timeSinceHuntStart = 0;
        public enum State { // SCP Creature States
            IDLE,
            SEARCHING,
            SPOTTED,
            HUNTING,
            KILLING,
            EMERGING, // "Teleport" To a Player
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
            LogIfDebugBuild("SCP-106 has Spawned");

            scpMask.GetComponent<MeshRenderer>().enabled = false; // Has SCP's height but is below him

            InitSCPValuesServerRpc();
            timeSinceHittingLocalPlayer = 0;
            timeSinceHeardNoise = 15;
        }

        [ServerRpc(RequireOwnership = false)]
        public void InitSCPValuesServerRpc() {
            InitSCPValuesClientRpc();
        }

        [ClientRpc]
        public void InitSCPValuesClientRpc() {
            creatureAnimator.SetTrigger("startStill");
            StartCoroutine(SpawnDelay());
        }

        // To prevent a Weird bug when a player looks at SCP right as he spawns.
        private IEnumerator SpawnDelay() {
            yield return new WaitForSeconds(3f);
            SwitchToBehaviourClientRpc((int)State.SEARCHING);
            DoAnimationClientRpc((int)State.SEARCHING);
        }

        /*
            Update is called every frame (light calculations only)
        */
        public override void Update() {
            base.Update();
            timeSinceHittingLocalPlayer += Time.deltaTime;
            timeSinceSpottedPlayer += Time.deltaTime;
            timeSinceHuntStart += Time.deltaTime;
            timeSinceHeardNoise += Time.deltaTime;
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
            AudioClip spottedSFX = spottedSounds[Random.Range(0,spottedSounds.Length)];
            AudioClip neckSFX = neckSounds[Random.Range(0,neckSounds.Length)];
            creatureSFX.PlayOneShot(spottedSFX);
            creatureSFX.PlayOneShot(neckSFX);
        }
        /*
            DoAIInterval runs every X seconds as defined in Unity (not every frame, allows heavier calculations)
        */
        public override void DoAIInterval() {
            base.DoAIInterval();
            switch(currentBehaviourStateIndex){
                case (int)State.IDLE:
                    break;
                case (int)State.SEARCHING:
                    StopChaseMusicIfNoOneNearbyAndLimitReached();
                    HuntIfPlayerIsInSight();
                    HuntLoneliestPlayer();
                    break;

                case (int)State.SPOTTED:
                    break;

                case (int)State.HUNTING:
                    SearchIfPlayerIsTooFarAway();
                    break;

                case (int)State.KILLING:
                    break;
                case (int)State.EMERGING:
                    break;
                default:
                    LogIfDebugBuild("Went to inexistent state!");
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
            if (timeSinceHuntStart < chaseMusicLimit) {
                return;
            }
            bool isCloseEnoughToHear = FoundClosestPlayerInRange(30f,30f);
            if (!isCloseEnoughToHear) {
                //chaseSource.Stop();
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
            if (playerIsLooking) {
                ChangeTargetPlayerServerRpc((int)closestPlayerInSight.playerClientId);

                // Check if we should do the Spotted sequence or go straight to Hunting
                if (timeSinceSpottedPlayer < spottedSFXCooldown) {
                    ToStateHunting();
                }
                else { // Spotted Sequence
                    timeSinceSpottedPlayer = 0;
                    ToStateSpotted();
                }
            } else {
                ToStateHunting();
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
            if (timeSinceHeardNoise < 15){
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
                LogIfDebugBuild("Investigating noise!");
                SetDestinationToPosition(noisePosition);
                PlaySFXServerRpc((int)SFX.Breathing, true);
                timeSinceHeardNoise = 0;
            }
        }

        /*
            [HUNTING]
            FROM STATE, TO STATE = (HUNTING,SEARCHING)
            If target player out of sight, and too far away, go into Search mode.
        */
        private void SearchIfPlayerIsTooFarAway() {
            float distanceBetweenPlayer = Vector3.Distance(transform.position, targetPlayer.transform.position);
            float maxDistanceToHunt = 20f;
            bool playerInSight = HasLineOfSightToPosition(targetPlayer.transform.position);
            // If player moves too far away - or out of sight - stop hunting.
            if(!TargetClosestPlayerInAnyCase() || (distanceBetweenPlayer > maxDistanceToHunt && !playerInSight)){
                LogIfDebugBuild("Searching State");
                ChangeTargetPlayerServerRpc(-1);
                SwitchToBehaviourServerRpc((int)State.SEARCHING);
                return;
            }
            SetDestinationToPosition(targetPlayer.transform.position, checkForPath: false);
        }

        /*
            [SEARCHING]
            FROM STATE, TO STATE (SEARCHING,SPOTTED)
            Called when going to a Spotted State
        */
        private void ToStateSpotted() {
            // States
            if (currentBehaviourStateIndex == (int)State.SPOTTED){
                LogIfDebugBuild("[SCP-106] ToStateSpotted called while already in Spotted State!");
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
            // States
            LogIfDebugBuild("[SCP-106] SCP-106 is hunting!");
            SwitchToBehaviourServerRpc((int)State.HUNTING);
            DoAnimationServerRpc((int)State.HUNTING);
            timeSinceHuntStart = 0;

            // SFX
            /*if (!chaseSource.isPlaying) {
                chaseSource.Play();
            }*/
            PlaySFXServerRpc((int)SFX.Chasing, true);
        }

/* * [UNITY ANIMATION EVENTS START] * * /
        /*
        Called everytime SCP-106 lands a foot on the ground (Unity Animation Event)
        */
        public void PlayFootstepSound(){
            AudioClip step = footstepSounds[Random.Range(0,footstepSounds.Length)];
            creatureSFX.PlayOneShot(step);
        }

        /*
            SPOTTED - Called by Unity Animation Event
        */
        public void LookAtPlayer(HeadState state) {
            LogIfDebugBuild($"LookAtPlayer: {(int)state}");
            if (currentBehaviourStateIndex != (int)State.SPOTTED){
                LogIfDebugBuild("LookAtPlayer ignored, not in Spotted state!");
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
                    LogIfDebugBuild("Unmatched HeadState case!");
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
            TODO: If timeSinceHuntStart is more than 2 min (i.e haven't seen any player in 2 min), "emerge" from beneath
            close to a NavMesh node to a player who is alone / a player who is alone & furthest away from others.
        */ 
        public void HuntLoneliestPlayer() {
            if (currentBehaviourStateIndex != (int)State.SEARCHING || timeSinceSpottedPlayer < emergeCooldown) {
                return;
            }
            foreach (PlayerControllerB player in StartOfRound.Instance.allPlayerScripts)
            {
                // Note: 'isPlayerAlone' only becomes true if 1. no one near them, and 2. hears no one in WalkieTalkie
                if (player.isInsideFactory){
                    LogIfDebugBuild("Warping to loneliest player");
                    StopSearch(currentSearch);
                    SwitchToBehaviourServerRpc((int)State.EMERGING);
                    ChangeTargetPlayerServerRpc((int)player.playerClientId);
                    StartEmergeSequenceServerRpc((int)player.playerClientId);
                }
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void StartEmergeSequenceServerRpc(int playerClientId) {
            StartEmergeSequenceClientRpc(playerClientId);
        }

        [ClientRpc]
        public void StartEmergeSequenceClientRpc(int playerClientId) {
            StartCoroutine(EmergeNearPlayer(playerClientId));
        }

        public IEnumerator EmergeNearPlayer(int playerClientId) {
            // Do and wait for Sink Animation to finish
            scpMask.GetComponent<MeshRenderer>().enabled = true;
            scpMask.GetComponent<MeshRenderer>().material.renderQueue = 2451;
            scpMask.transform.position = new Vector3(scpMask.transform.position.x, scpMask.transform.position.y + 1, scpMask.transform.position.z);

            creatureAnimator.SetTrigger("startSink");
            creatureSFX.PlayOneShot(sinkSFX);
            yield return new WaitForSeconds(3f);

            // Get Player Position
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            Vector3 playerPosition = player.transform.position;
            // Create Corrosion at playerPosition (TODO), and Emerge from it
            creatureAnimator.speed = 0.7f;
            creatureAnimator.SetTrigger("startEmerge");
            creatureSFX.PlayOneShot(emergeSFX);
            agent.Warp(playerPosition);
            agent.speed = 0;
            agent.enabled = false;

            yield return new WaitForSeconds(10f);

            // When fully emerged, start hunting player (or searching if they already ran away)
            //modelRenderer.material.renderQueue = oldRenderQueue;
            agent.enabled = true;
            scpMask.GetComponent<MeshRenderer>().enabled = false;
            SwitchToBehaviourClientRpc((int)State.HUNTING);
            DoAnimationClientRpc((int)State.HUNTING);
            PlaySFXServerRpc((int)SFX.Chasing, true);
            timeSinceHuntStart = 0;
        }

        /*
            Called when SCP-106 touches / collides with an object (e.g. Player)
        */
        public override void OnCollideWithPlayer(Collider other) {
            if (currentBehaviourStateIndex == (int)State.EMERGING ||
                currentBehaviourStateIndex == (int)State.KILLING){
                return;
            }
            if (KillingPlayer){
                return;
            }
            if (timeSinceHittingLocalPlayer < 1f) {
                return;
            }
            PlayerControllerB playerControllerB = MeetsStandardPlayerCollisionConditions(other);
            if (playerControllerB != null && !playerControllerB.isPlayerDead)
            {
                GrabAndKillPlayerServerRpc((int)playerControllerB.playerClientId);
                timeSinceHittingLocalPlayer = 0f;
            }
        }

/* * [PLAYER RELATED FUNCTIONS END] * */

/* * [KILLING ANIMATIONS START] * */

        /*
            SCP forces targetPlayer to face them, enters into an animation and then kills the player.
            TODO: Allow to be interrupted by hitting SCP, saving the player
        */
        [ServerRpc(RequireOwnership = false)]
        public void GrabAndKillPlayerServerRpc(int playerClientId) {
            GrabAndKillPlayerClientRpc(playerClientId);
        }
        [ClientRpc]
        public void GrabAndKillPlayerClientRpc(int playerClientId) {
            inSpecialAnimationWithPlayer = StartOfRound.Instance.allPlayerScripts[playerClientId];
            if (inSpecialAnimationWithPlayer == GameNetworkManager.Instance.localPlayerController){
                inSpecialAnimationWithPlayer.CancelSpecialTriggerAnimations();
            }
            inSpecialAnimation = true;
            killCoroutine = StartCoroutine(GrabPlayer(playerClientId));
        }

        /* [CALLED BY SERVER RPC] (make Client calls only inside here)
            Grabs and kills the target player
        */
        public IEnumerator GrabPlayer(int playerClientId) {
            LogIfDebugBuild("Grab Player Called");
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            if (!KillingPlayer && !player.isPlayerDead){
                // Restore variables
                float playerMovementSpeed = inSpecialAnimationWithPlayer.movementSpeed;
                float playerJumpForce = inSpecialAnimationWithPlayer.jumpForce;

                // Player Model Manipulation
                inSpecialAnimationWithPlayer.disableSyncInAnimation = true;
                inSpecialAnimationWithPlayer.disableLookInput = true;
                inSpecialAnimationWithPlayer.movementSpeed = 0;
                inSpecialAnimationWithPlayer.jumpForce = 0;
                inSpecialAnimationWithPlayer.Crouch(false);
                Vector3 oldPlayerPosition = inSpecialAnimationWithPlayer.gameObject.transform.position;
                inSpecialAnimationWithPlayer.gameObject.transform.position = new Vector3(oldPlayerPosition.x,base.transform.position.y,oldPlayerPosition.z);
                Coroutine faceCoroutine = StartCoroutine(MakePlayerFaceSCP(playerClientId));

                // SCP Object Manipulation
                KillingPlayer = true;
                agent.enabled = false;
                turnReference.LookAt(inSpecialAnimationWithPlayer.transform.position);
                base.transform.LookAt(inSpecialAnimationWithPlayer.transform.position);
                SetDestinationToPosition(agent.transform.position);
                SwitchToBehaviourClientRpc((int)State.KILLING);
                DoAnimationClientRpc((int)State.KILLING);
                PlaySFXClientRpc((int)SFX.Neck);
                
                // Wait for Head to turn before playing scream
                yield return new WaitForSeconds(0.25f);
                PlaySFXClientRpc((int)SFX.Killing);
                creatureVFX.Play();
                Coroutine voiceCoroutine = StartCoroutine(PitchDownPlayerAudio(playerClientId));
                
                yield return new WaitForSeconds(0.25f);
                inSpecialAnimationWithPlayer.AddBloodToBody();
                inSpecialAnimationWithPlayer.DamagePlayer(inSpecialAnimationWithPlayer.health/2);
                inSpecialAnimationWithPlayer.DropBlood(default,true,true);
                // Wait for animation to Finish
                yield return new WaitForSeconds(1.25f);
                creatureVFX.Stop();

                // Kill
                Vector3 velocity = new(3,0,0);
                inSpecialAnimationWithPlayer.KillPlayer(velocity,true,CauseOfDeath.Suffocation,1);
                PlaySFXClientRpc((int)SFX.PlayerKilled);
                PlaySFXClientRpc((int)SFX.Laughing);

                // Return state Player
                inSpecialAnimationWithPlayer.movementSpeed = playerMovementSpeed;
                inSpecialAnimationWithPlayer.jumpForce = playerJumpForce;
                inSpecialAnimationWithPlayer.disableSyncInAnimation = false;
                inSpecialAnimationWithPlayer.disableLookInput = false;

                // Wait a little before continuing
                creatureAnimator.SetTrigger("startStill");
                yield return new WaitForSeconds(3f);
                agent.enabled = true;

                // Return state SCP
                SwitchToBehaviourClientRpc((int)State.SEARCHING);
                DoAnimationClientRpc((int)State.SEARCHING);
                inSpecialAnimationWithPlayer = null;
                inSpecialAnimation = false;
                KillingPlayer = false;
                FinishGrabPlayer();
                // Stop Coroutines, just in case
                StopCoroutine(faceCoroutine);
                StopCoroutine(voiceCoroutine);
            }
        }

        // Makes the target player turn towards SCP's face
        public IEnumerator MakePlayerFaceSCP(int playerClientId) {
            LogIfDebugBuild("Make Player Face SCP Called");
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            Vector3 myDirection = base.transform.position - player.gameObject.transform.position;
            while (player.health != 0){
                player.gameObject.transform.rotation = Quaternion.Lerp(player.gameObject.transform.rotation, Quaternion.LookRotation(myDirection), Time.deltaTime *5f);
                player.gameplayCamera.transform.position = Vector3.Lerp(player.gameplayCamera.transform.position,turnReference.position, Time.deltaTime *0.4f);
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
            StopCoroutine(killCoroutine);
        }

        private void PushPlayer() {
            
        }

/* * [KILLING ANIMATIONS END] * */

        /*
            Called when a Player hits / attacks SCP-106
        */
        public override void HitEnemy(int force = 1, PlayerControllerB playerWhoHit = null, bool playHitSFX = false) {
            base.HitEnemy(force, playerWhoHit, playHitSFX);
            if(isEnemyDead){
                return;
            }
            enemyHP -= force;
            if (IsOwner) {
                if (enemyHP <= 0 && !isEnemyDead) {
                    StopCoroutine(searchCoroutine);
                    KillEnemyOnOwnerClient();
                }
            }
        }

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
                LogIfDebugBuild("Target : Null");
            }
            else {
                targetPlayer = StartOfRound.Instance.allPlayerScripts[newTargetPlayerId];
                LogIfDebugBuild($"Target : {newTargetPlayerId}");
            }
        }

        // Play SFX
        [ServerRpc(RequireOwnership = false)]
        public void PlaySFXServerRpc(int intSFX, bool play = true) {
            PlaySFXClientRpc(intSFX,play);
        }
        [ClientRpc]
        public void PlaySFXClientRpc(int intSFX, bool play = true) {
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
                    AudioClip neckSFX = neckSounds[Random.Range(0,neckSounds.Length)];
                    creatureSFX.volume = 0.7f;
                    creatureSFX.PlayOneShot(neckSFX);
                    break;
                case (int)SFX.Spotted:
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
                    //AudioClip killSFX = killingSounds[Random.Range(0,killingSounds.Length)];
                    AudioClip killSFX = killingSounds[1];
                    creatureSFX.volume = 1f;
                    creatureSFX.PlayOneShot(killSFX);
                    break;
                case (int)SFX.PlayerKilled:
                    AudioClip playerSFX = playerKilledSounds[Random.Range(0,playerKilledSounds.Length)];
                    creatureSFX.volume = 0.7f;
                    creatureSFX.PlayOneShot(playerSFX);
                    break;
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
                case (int)State.SEARCHING:
                    LogIfDebugBuild("Searching State!");
                    StartSearch(base.transform.position);
                    creatureSFX.volume = 0.7f;
                    targetPlayer = null;
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
                    creatureAnimator.SetTrigger("startWalk");
                    creatureAnimator.speed = 3f;
                    timeSinceSpottedPlayer = 0f;
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
                    break;
                default:
                    LogIfDebugBuild("DoAnimation: Went to Inexistent State");
                    break;
            }
        }

/* * [RPC FUNCTIONS END] * */
    }
}