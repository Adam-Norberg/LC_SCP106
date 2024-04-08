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
        public AudioSource stepSource;  // Footstep SFX
        // creatureSFX - Special SFX's (e.g. Spotted)
        // creatureVoice - Breathing, Laughing

        public AudioClip[] footstepSounds; // Footstep sounds
        public AudioClip[] spottedSounds; // SFX when spotted by a player
        public AudioClip[] neckSounds; // SFX for when turning head towards player, and when twisting player's neck

        public ParticleSystem creatureVFX;

        public bool lookAtPlayer = false;
        private bool KillingPlayer = false;

        #pragma warning restore 0649
        float timeSinceHittingLocalPlayer;
        float timeSinceSpottedLocalPlayer = 60;
        readonly float spottedSFXCooldown = 60; // Cooldown in Seconds between doing the surprised "Spotted" sequence
        readonly float chaseMusicLimit = 60; // Play chase music for 60 seconds, then check if we can turn it off (only if no one nearby)
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
            Spotted,
            Neck,
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

            InitSCPValuesServerRpc();
            StartSearch(transform.position);
            timeSinceHittingLocalPlayer = 0;
        }

        [ServerRpc(RequireOwnership = false)]
        public void InitSCPValuesServerRpc() {
            InitSCPValuesClientRpc();
        }

        [ClientRpc]
        public void InitSCPValuesClientRpc() {
            creatureAnimator.SetTrigger("startWalk");
            creatureAnimator.speed = 2;
            currentBehaviourStateIndex = (int)State.SEARCHING;
        }

        /*
            Update is called every frame (light calculations only)
        */
        public override void Update() {
            base.Update();
            timeSinceHittingLocalPlayer += Time.deltaTime;
            timeSinceSpottedLocalPlayer += Time.deltaTime;
            timeSinceHuntStart += Time.deltaTime;
            var state = currentBehaviourStateIndex;
            if (targetPlayer != null && (state == (int)State.HUNTING)){
                
            }
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
            creatureSFX.PlayOneShot(spottedSFX);
        }
        /*
            DoAIInterval runs every X seconds as defined in Unity (not every frame, allows heavier calculations)
        */
        public override void DoAIInterval() {
            base.DoAIInterval();
            switch(currentBehaviourStateIndex){
                case (int)State.SEARCHING:
                    StopChaseMusicIfNoOneNearbyAndLimitReached();
                    HuntIfPlayerIsInSight();
                    break;

                case (int)State.SPOTTED:
                    break;

                case (int)State.HUNTING:
                    SearchIfPlayerIsTooFarAway();
                    break;

                case (int)State.KILLING:
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
            if (chaseSource.isPlaying){
                return;
            }
            if (timeSinceHuntStart < chaseMusicLimit) {
                return;
            }
            bool isCloseEnoughToHear = FoundClosestPlayerInRange(30f,30f);
            if (!isCloseEnoughToHear) {
                chaseSource.Stop();
            }
        }

        /*
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
            if (playerIsLooking) {
                StopSearch(currentSearch);
                ChangeTargetPlayerServerRpc((int)closestPlayerInSight.playerClientId);

                // Check if we should do the Spotted sequence or go straight to Hunting
                if (timeSinceSpottedLocalPlayer < spottedSFXCooldown) {
                    ToStateHunting();
                }
                else { // Spotted Sequence
                    timeSinceSpottedLocalPlayer = 0;
                    ToStateSpotted();
                }
            }
            //SetMovingTowardsTargetPlayer()
        }

        /*
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
                StartSearch(transform.position);
                SwitchToBehaviourServerRpc((int)State.SEARCHING);
                return;
            }
            SetDestinationToPosition(targetPlayer.transform.position, checkForPath: false);
        }

        /*
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
            FROM STATE, TO STATE (SPOTTED,HUNTING)
        */
        public void ToStateHunting() {
            // States
            LogIfDebugBuild("[SCP-106] SCP-106 is hunting!");
            SwitchToBehaviourServerRpc((int)State.HUNTING);
            DoAnimationServerRpc((int)State.HUNTING);
            timeSinceHuntStart = 0;

            // Fear Level
            PlayerControllerB[] allInSight = GetAllPlayersInLineOfSight(45,60,eye);
            if (allInSight != null){
                foreach (PlayerControllerB player in allInSight)
                {
                    player.JumpToFearLevel(0.5f);
                }
            }

            // SFX
            if (!chaseSource.isPlaying) {
                chaseSource.Play();
            }
        }

/* * UNITY ANIMATION EVENTS START * * /
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

        public void AnimationEventStareKill() {
            StartCoroutine(ConsumePlayerCamera());
        }

        public IEnumerator ConsumePlayerCamera() {
            while (KillingPlayer && targetPlayer.health != 0) {
                Vector3.Lerp(targetPlayer.gameObject.transform.position, transform.position, 5f * Time.deltaTime);
                yield return null;
            }
        }

/* * UNITY ANIMATION EVENTS END * /

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
            Sets the TargetPlayer to the player who is furthest away from the other players.
            TODO: If timeSinceHuntStart is more than 2 min (i.e haven't seen any player in 2 min), "emerge" from beneath
            close to a NavMesh node to a player who is alone / a player who is alone & furthest away from others.
        */ 
        private void HuntLoneliestPlayer() {
            // PlayerControllerB "isPlayerAlone" bool
        }

        /*
            Called when SCP-106 touches / collides with an object (e.g. Player)
        */
        public override void OnCollideWithPlayer(Collider other) {
            if (KillingPlayer){
                LogIfDebugBuild("Collision ignored, already killing a player");
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
            StartCoroutine(GrabPlayer(playerClientId));
        }

        /* [CALLED BY SERVER RPC] (make Client calls only inside here)
            Grabs and kills the target player
        */
        public IEnumerator GrabPlayer(int playerClientId) {
            LogIfDebugBuild("Grab Player Called");
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            if (!KillingPlayer){
                // SCP Object Manipulation
                KillingPlayer = true;
                SwitchToBehaviourClientRpc((int)State.KILLING);
                DoAnimationClientRpc((int)State.KILLING);

                // Player Model Manipulation
                player.movementSpeed = 0;
                StartCoroutine(MakePlayerFaceSCP(playerClientId));

                // Wait for animation to Finish
                yield return new WaitForSeconds(1.5f);

                // Kill
                Vector3 velocity = new(0,3,0);
                player.KillPlayer(velocity,true,CauseOfDeath.Suffocation,1);

                // Return state
                SwitchToBehaviourClientRpc((int)State.SEARCHING);
                DoAnimationClientRpc((int)State.SEARCHING);
                KillingPlayer = false;
            }
        }

        // Makes the target player turn towards SCP's face
        public IEnumerator MakePlayerFaceSCP(int playerClientId) {
            LogIfDebugBuild("Make Player Face SCP Called");
            PlayerControllerB player = StartOfRound.Instance.allPlayerScripts[playerClientId];
            Vector3 myDirection = transform.position - player.gameObject.transform.position;
            while (player.health != 0){
                player.gameObject.transform.rotation = Quaternion.Lerp(player.gameObject.transform.rotation, Quaternion.LookRotation(myDirection), Time.deltaTime *5f);
                yield return null;
            }
        }


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
                    // Our death sound will be played through creatureVoice when KillEnemy() is called.
                    // KillEnemy() will also attempt to call creatureAnimator.SetTrigger("KillEnemy"),
                    // so we don't need to call a death animation ourselves.

                    //StopCoroutine(SwingAttack());
                    // We need to stop our search coroutine, because the game does not do that by default.
                    StopCoroutine(searchCoroutine);
                    KillEnemyOnOwnerClient();
                }
            }
        }

/* * RPC FUNCTIONS * */

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
        [ClientRpc]
        public void PlaySFXClientRpc() {

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
            }
        }

        [ClientRpc]
        public void SwingAttackHitClientRpc() {
            LogIfDebugBuild("SwingAttackHitClientRPC");
            /*int playerLayer = 1 << 3; // This can be found from the game's Asset Ripper output in Unity
            Collider[] hitColliders = Physics.OverlapBox(attackArea.position, attackArea.localScale, Quaternion.identity, playerLayer);
            if(hitColliders.Length > 0){
                foreach (var player in hitColliders){
                    PlayerControllerB playerControllerB = MeetsStandardPlayerCollisionConditions(player);
                    if (playerControllerB != null)
                    {
                        LogIfDebugBuild("Swing attack hit player!");
                        timeSinceHittingLocalPlayer = 0f;
                        playerControllerB.DamagePlayer(40);
                    }
                }
            }*/
        }
    }
}