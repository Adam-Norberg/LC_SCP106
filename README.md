# Lethal Company SCP-106 Enemy
Adds a new enemy inspired by *SCP: Containment Breach* to Lethal Company. This AI tries to immitate the behaviour and characteristics as seen by SCP-106 in *SCP: CB* as close as possible.

## Abilities (spoilers)

### Characteristics
>! Unkillable.

### Damage related
>! Grabs & Kill players he comes in contact with.
>! Player can be saved if SCP is stunned.

### Behaviour
>! Detects noise, and will investigate source of noise.
>! Emerges from behind the loneliest player if he hasn't seen someone for a certain amount of time.

## TODO / Ideas

### Walk through doors
- SCP-106 should, similar to in Containment Breach, be able to pass through solid objects such as doors without having to open them.
- Possible configuration option to allow locked doors & blast doors.

### Allow SCP-106 to leave the factory
- Add a config option so that SCP also becomes an outside enemy.
- Requires existing functions to take this possibility into account, such as following a hunted player out the door.
- Possible limitations such as not being able to enter the ship, or not emerging inside the ship.

### SCP-106 leaves corrosion as he wanders
- Small spots of corrosion every X steps.
- Either visual only, or could alert SCP-106.

### Player-oriented kill animations
- If colliding with a player from behind it should perform a different kill animation than if touched from the front.
- If collided during emerge sequence, he can "drag" the player down into the corrosion.

### Sadistic characteristics
- Sticking to lore, SCP-106 enjoys tormenting his victims before killing them.
- Possible ideas include: 
    - Chance for pushing player instead of insta-killing upon collision.
    - Chance for bringing player to his "pocket-dimension".
    - Chance to teleport / bring player to a location far away from other players.

### Sounds
- Adding new random sounds (/insanity sounds) that occur randomly throughout an expedition.
- E.g. SCP-106's laughter, but far away in the distance.


## Credits
Hamunii (and all people listed in their credits!) for the Template - https://github.com/Hamunii/LC-ExampleEnemy
- The Model & inherited Animations for "SCP-106" (https://skfb.ly/o7zoA) by ThatJamGuy is licensed under Creative Commons Attribution (http://creativecommons.org/licenses/by/4.0/). *I, the author of this mod (Dackie), have made some alterations and modifications to existing animations as well as new animation(s) not originally made by the creator (ThatJamGuy) of the model.*