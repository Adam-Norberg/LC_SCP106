# Set by Server/Host/Owner:
- Agent Enabled True
- Behaviour State

# Set by affected client only:
- CancelSpecialTriggerAnimations

# Set by all
- Everything else

# General Help

## RPC Calls
- Server sets values like inSpecialAnimation and such before calling ClientRpc.

## Animations (Player affecting other player values)
- Set "*inSpecialAnimationWithPlayer.disableSyncInAnimation = true*" **before** doing any changes to position, rotation, etc!
- Good values to set before & after animation:
* inSpecialAnimationWithPlayer.disableSyncInAnimation
* inSpecialAnimationWithPlayer.disableLookInput
* inSpecialAnimationWithPlayer.inSpecialInteractAnimation
* inSpecialAnimationWithPlayer.snapToServerPosition
* inSpecialAnimationWithPlayer.inAnimationWithEnemy