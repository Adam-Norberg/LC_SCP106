using System.Collections;
using System.Diagnostics;
using System.Linq;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UIElements;

namespace SCP106 {

    // You may be wondering, how does the Example Enemy know it is from class ExampleEnemyAI?
    // Well, we give it a reference to to this class in the Unity project where we make the asset bundle.
    // Asset bundles cannot contain scripts, so our script lives here. It is important to get the
    // reference right, or else it will not find this file. See the guide for more information.

    class SCPAI : EnemyAI
    {
        // We set these in our Asset Bundle, so we can disable warning CS0649:
        // Field 'field' is never assigned to, and will always have its default value 'value'
        #pragma warning disable 0649
        public Transform boneHead; // Head object reference of the model
        public Transform boneNeck; // Neck object reference
        public Transform turnReference; // Invisible object pointing towards player, as reference to smoothly track head & neck
        public AudioSource chaseSource;
        public AudioClip[] footstepSounds;
        public AudioClip[] spottedSounds;
        public AudioClip[] neckSounds;

        public bool lookAtPlayer = false;

        #pragma warning restore 0649
        float timeSinceHittingLocalPlayer;
        float timeSinceSpottedLocalPlayer = 60;
        readonly float spottedSFXCooldown = 60; // Cooldown in Seconds between doing the surprised "Spotted" sequence
        float chaseMusicLimit = 60; // Play chase music for 60 seconds, then check if we can turn it off (only if no one nearby)
        private float timeSinceHuntStart;
        enum State {
            IDLE,
            SEARCHING,
            SPOTTED,
            HUNTING,
        }
        public enum HeadState {
            StartLookingAtPlayer,
            FinishedLookingAtPlayer,
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

            timeSinceHittingLocalPlayer = 0;
            creatureAnimator.SetTrigger("startWalk");
            // NOTE: Add your behavior states in your enemy script in Unity, where you can configure fun stuff
            // like a voice clip or an sfx clip to play when changing to that specific behavior state.
            currentBehaviourStateIndex = (int)State.SEARCHING;
            // We make the enemy start searching. This will make it start wandering around.
            StartSearch(transform.position);
        }

        /*
            Update is called every frame (light calculations only)
        */
        public override void Update() {
            base.Update();
            timeSinceHittingLocalPlayer += Time.deltaTime;
            timeSinceSpottedLocalPlayer += Time.deltaTime;
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

        /*
            SPOTTED - Called by Unity Animation Event
        */
        public void LookAtPlayer(HeadState state) {
            switch(state) {
                case HeadState.StartLookingAtPlayer:
                    lookAtPlayer = true;
                    break;
                case HeadState.FinishedLookingAtPlayer:
                    lookAtPlayer = false;
                    break;
                default:
                    LogIfDebugBuild("Unmatched HeadState case!");
                    break;
            }
        }
        /*
            DoAIInterval runs every X seconds as defined in Unity (not every frame, allows heavier calculations)
        */
        public override void DoAIInterval() {
            base.DoAIInterval();
            switch(currentBehaviourStateIndex){
                case (int)State.SEARCHING:
                    agent.speed = 2f;
                    creatureAnimator.SetFloat("speedMultiplier", 2f);
                    StopChaseMusicIfNoOneNearbyAndLimitReached();
                    HuntIfPlayerIsInSight();
                    break;

                case (int)State.SPOTTED:
                    agent.speed = 0f;
                    creatureAnimator.SetFloat("speedMultiplier", 1f);
                    break;

                case (int)State.HUNTING:
                    agent.speed = 3f;
                    creatureAnimator.SetFloat("speedMultiplier", 3f);
                    float distanceBetweenPlayer = Vector3.Distance(transform.position, targetPlayer.transform.position);
                    float maxDistanceToHunt = 20f;
                    bool playerInSight = HasLineOfSightToPosition(targetPlayer.transform.position);
                    // If player moves too far away - or out of sight - stop hunting.
                    if(!TargetClosestPlayerInAnyCase() || (distanceBetweenPlayer > maxDistanceToHunt && !playerInSight)){
                        LogIfDebugBuild("Stop Target Player");
                        targetPlayer = null;
                        StartSearch(transform.position);
                        SwitchToBehaviourClientRpc((int)State.SEARCHING);
                        //chaseSource.Stop();
                        return;
                    }
                    SetDestinationToPosition(targetPlayer.transform.position, checkForPath: false);
                    break;
                default:
                    LogIfDebugBuild("Went to inexistent state!");
                    break;
            }
        }

        /*
            [SEARCHING]
            If Searching with Chase music on, with play timer reached, turn it off if no one is close enough to hear.
        */
        private void StopChaseMusicIfNoOneNearbyAndLimitReached() {
            float currentTime = Time.time;
            if (currentTime - timeSinceHuntStart < chaseMusicLimit) {
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
            PlayerControllerB closestPlayerInSight = GetClosestPlayer(requireLineOfSight: true, cannotBeInShip: true, cannotBeNearShip: true);
            if (closestPlayerInSight == null) {
                return;
            }
            bool playerIsLooking = PlayerLookingAtMe(closestPlayerInSight);
            if (playerIsLooking) {
                StopSearch(currentSearch);
                targetPlayer = closestPlayerInSight;

                // Check if we should do the Spotted sequence or go straight to Hunting
                if (timeSinceSpottedLocalPlayer < spottedSFXCooldown) {
                    ToStateHunting();
                }
                else { // Spotted Sequence
                    ToStateSpotted();
                }
            }
            //SetMovingTowardsTargetPlayer()
        }

        /*
            FROM STATE, TO STATE (SEARCHING,SPOTTED)
            Called when going to a Spotted State
        */
        private void ToStateSpotted() {
            // States
            if (currentBehaviourStateIndex == (int)State.SPOTTED){
                LogIfDebugBuild("ToStateSpotted called while already in Spotted State!");
                return;
            }
            LogIfDebugBuild("SCP-106 has been Spotted!");
            timeSinceSpottedLocalPlayer = 0;
            SwitchToBehaviourClientRpc((int)State.SPOTTED);
            creatureAnimator.SetTrigger("startSpotted");
        }

        /*
            Called by Unity Animation Event (Spotted)
        */
        public void SpottedSequence() {
            // SFX
            AudioClip spottedSFX = spottedSounds[Random.Range(0,spottedSounds.Length)];
            creatureSFX.PlayOneShot(spottedSFX);
        }

        /*
            FROM STATE, TO STATE (SPOTTED,HUNTING)
            Called by end of Unity Animation Event (Spotted)
        */
        public void ToStateHunting() {
            // States
            LogIfDebugBuild("SCP-106 is hunting!");
            SwitchToBehaviourClientRpc((int)State.HUNTING);
            timeSinceHuntStart = Time.time;
            creatureAnimator.SetTrigger("startWalk");
            // SFX
            if (!chaseSource.isPlaying) {
                chaseSource.Play();
            }
        }

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

        // Called everytime SCP-106 lands a foot on the ground (Unity Animation Event)
        public void PlayFootstepSound(){
            AudioClip step = footstepSounds[Random.Range(0,footstepSounds.Length)];
            creatureSFX.PlayOneShot(step);
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
        */
        private void HuntLoneliestPlayer() {
            // PlayerControllerB "isPlayerAlone" bool
        }

        /*
            Called when SCP-106 touches / collides with an object (e.g. Player)
        */
        public override void OnCollideWithPlayer(Collider other) {
            if (timeSinceHittingLocalPlayer < 1f) {
                return;
            }
            PlayerControllerB playerControllerB = MeetsStandardPlayerCollisionConditions(other);
            if (playerControllerB != null)
            {
                LogIfDebugBuild("SCP-106 Collision with Player!");
                timeSinceHittingLocalPlayer = 0f;
                playerControllerB.DamagePlayer(20);
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

        [ClientRpc]
        public void DoAnimationClientRpc(string animationName) {
            LogIfDebugBuild($"Animation: {animationName}");
            creatureAnimator.SetTrigger(animationName);
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