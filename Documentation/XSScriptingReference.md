## XS scripting Reference
This is **NOT a coding guide**. This also **NOT a COMPLETE reference**. 

This is just a collection of all functions and useful snippets/constants that I collected while making custom scripts for my customs scenarios.

### Disclaimer
All notes you find here are my **personal notes** I wrote while collecting this information. If you think it's incorrect or you want to add your own information, make a pull request or open an issue.

### Temp trigger code
If you are inside the game Editor and you run it, it will generate a `trigtemp.xs` file for all your triggers that you defined in-editor. This can be helpful for figuring out how stuff works.

For example, you could load a campaign scenario, run it, then open that file to view how those triggers look in XS format.

This file is usually located in `[USER]\Games\Age of Mythology Retold\[UID]\trigger` directory.

### Function parameters
**All functions have default values** set for all parameters. This means you can omit parameters you don't need. This also means when defining your own functions, you need to specify default values!

If there is a function `doSomething(id, param1, param2)` you can simply do `doSomething(id)` and the rest will use default values. This will be useful for functions with a ton of parameters you don't really need to touch.

---

## XS functions
These are utility functions used to help with scripting. They do not do anything ingame nor create anything.

### Common
|Return|Function signature|Note
| --- | --- | ---
|void|xsSetContextPlayer(int player_id)|Sets context player. `-1` to clear it. `0` is Gaia. `1` is Player 1, etc
|int|xsGetContextPlayer()|gets current context player
|int|xsGetTime()|gets time since game started in seconds (rounded)
|int|xsGetTimeMS()|gets time since game started in miliseconds
|void|xsDisableRule(string name)|disables rule with given name
|void|xsEnableRule(string NAME)|enables rule with given name
|void|xsDisableSelf()|disables self (when used within a rule)
|void|xsRuleIgnoreIntervalOnce(string name)|ignores a rule interval once
|void|xsSetRuleMinIntervalSelf(int seconds)|sets new min interval value for rule within which it's called
|void|xsEnableRuleGroup(string name)|enables a group of rules
|void|xsDisableRuleGroup(string name)|disables a group of rules

### Random
|Return|Function signature|Note
| --- | --- | ---
|void|xsRandBool()|returns random boolean value
|int|xsRandInt(int from, int to)|returns random integer within range (inclusive range)
|float|xsRandFloat(int from, int to)|returns random float within range
|void|xsRandSetSeed(int seed)|set seed for random functions

### Utility
|Return|Function signature|Note
| --- | --- | ---
|float|xsIntToFloat(int)|converts int to float
|float|xsVectorLength(vector)|returns length of vector
|float|xsVectorDistanceSqr(a, b)|returns squared distance between given vectors
|vector|xsVectorNormalize(vector)|normalizes given vector
|float|xsVectorDistanceXZ(a, b)|distance between given vectors on the ground, height is ignored
|float|xsVectorDistanceXZSqr(a, b)|same as above, but squared

## KB functions
Knowledge-Base functions. Use these to interact with game database. 
**These functions operate under the CONTEXT PLAYER**, so make sure to set it (`xsSetContextPlayer`)

### Note!
I noticed these functions seem to use **cached data**. They are **not suited for getting latest data at higher frequencies** (such as unit position). This affects querying units as well etc...

If you try running unit queries every tick, they will run, but results will only update occassionally
because of cached unit positions/state. This will lead to unexpected behaviour if you don't handle it properly.

I needed high frequency querying, to get over this limitation, I just used a wider radius query and then further filtered units based on distance from center. This way, even if query returns outdated information, it will get it BEFORE relevant units step inside the relevant query, and I can go from there using TR functions to get latest positions for those units.

### Units
|Return|Function signature|Note
| --- | --- | ---
|int|kbUnitGetProtoUnitID(int UNIT_ID)|Gets protounit ID of given unit ID
|int|kbUnitGetPlayerID(int UNIT_ID)|Gets owner's player ID of given unit ID 
|string|kbProtoUnitGetName(int PROTO_ID)|Gets proto unit name (Note: this does not return the translation, so greek villagers will return `VillagerGreek`, not `Villager`, etc)
|bool|kbProtoUnitIsType(int PROTO_ID, int TYPE)|true if proto unit is given type or sub-type (for villagers you can use `cUnitTypeAbstractVillager` for type_id and will return true for any villager, greek or otherwise)
|bool|kbUnitIsType(int UNIT_ID, int TYPE)|true if unit is given type or sub-type (Note: I had some problems with this, I prefer using the proto unit function above)
|int|kbUnitCount(int TYPE, int PLAYER_ID, int STATE)|returns number of units from given player in given state
|int|kbUnitTypeCountInArea(int PROTO_NAME, int PLAYER_ID, int STATE, int CENTER_UNIT, float RADIUS)|self-explanatory, example: `kbUnitTypeCountInArea("Unit", 1, cUnitStateAlive, centerUnitID, 8)`
|int|kbUnitGetActionType(int UNIT_ID)| get action type the given unit is in 
|int|kbUnitGetIdleTime(int UNIT_ID)      | get time in seconds how long a unit has been idle
|float|kbUnitGetStatFloat(int UNIT_ID, int STAT)   | get stat for given unit, such as `cUnitStatMaxHP`
|int|kbUnitGetStatInt(int UNIT_ID, int STAT)   | ^ but for int stats
|bool|kbUnitGetStatBool(int UNIT_ID, int STAT)   | ^ but for bool stats
|vector|kbUnitGetPosition(int UNIT_ID)  | get unit position (prefer `trUnitGetPosition` for latest info)
|float|kbUnitGetDistanceToPoint(int UNIT_ID, vector pos)   | distance to given position
|int|kbUnitGetHeading(int UNIT_ID)    | returns heading in degrees
|int|kbUnitGetTargetUnitID(int UNIT_ID)  | returns target of this unit
|int|kbUnitGetMovementType(int UNIT_TYPE)  | returns movement type of given unit type (check against |Passability types)   
|bool|kbGetIsLocationOnMap(vector pos)   | validate location
|bool|kbUnitGetIsIDValid(int UNIT_ID)  | checks if given unit ID is valid (if dead, ID becomes invalid eventually)
|vector|kbPlayerGetStartingPosition(int PLAYER_ID)  | player's starting position
|float |  kbUnitGetCarryCapacity(int UNIT_ID, int RESOURCE_ID) | get max carry capacity for given unit and |resource
|float |  kbUnitGetResourceAmount(int UNIT_ID, int RESOURCE_ID)  | get resource amount in this unit
|float  | kbUnitGetResourceAutoGatherRate(int UNIT_ID, int RESOURCE_ID)| get auto gather rate (aka fatten rate)
|int   |  kbUnitGetNumberContained(int UNIT_ID)      |  Returns the number of units contained within |the given unit
|int  |   kbUnitGetNumberContainedOfType(int UNIT_ID, int UNIT_TYPE) | ^ but for particular type
|int  |  kbFunctionUnitGetByIndex(int CIV_ID, int UNIT_TYPE, int INDEX) | gets unit ID of certain type for specific civ. Example: `Villager = kbFunctionUnitGetByIndex(cMyCiv, cUnitFunctionGatherer, 0);`

### Unit querying
As noted before, keep in mind these queries do not return latest info, but are good enough for most use cases.
|Return|Function signature|Note
| --- | --- | ---
|int  | kbUnitQueryCreate(string NAME)                         | creates new unit query with given name . Returns QUERY_ID that you use with other functions (if -1, is invalid)
|void | kbUnitQuerySetPlayerID(int QUERY_ID, int PLAYER_ID)     | sets player ID for which to query for (can use -1 to clear this condition)
|void | kbUnitQuerySetPlayerRelation(int QUERY_ID, int RELATION)| sets player relation for which to query for (Note: **Set either player ID or relation, NOT BOTH**), example for relation is `cPlayerRelationEnemy` (more below) - **this is relative to context player**!
|void | kbUnitQuerySetActionType(int QUERY_ID, int ACTION_TYPE) | set action type for query
|void | kbUnitQuerySetState(int QUERY_ID, int STATE)            | example is `cUnitStateAlive` for state
|void | kbUnitQuerySetUnitType(int QUERY_ID, int TYPE)          | example is `cUnitTypeSpearman` for type
|void | kbUnitQuerySetPosition(int QUERY_ID, vector POSITION)   | sets center position of query
|void | kbUnitQuerySetMaximumDistance(int QUERY_ID, float RADIUS)| sets radius around position to query for
|void | kbUnitQueryExecute(int QUERY_ID)                       | executes configured query
|int  | kbUnitQueryNumberResults(int QUERY_ID)                 | get number of results from executed query
|int  | kbUnitQueryGetResult(int QUERY_ID, int INDEX)          | get unit ID at 0-based index in the query results
|void | kbUnitQueryResetResults(int QUERY_ID)                  | clears all results (use when recycling query)
|void | kbUnitQueryResetData(int QUERY_ID)                     | resets data AND results of a query, for complete recycle
|void | kbUnitQueryDestroy(int QUERY_ID)                       | destroy query after usage

#### Query notes:
- Context player MUST be set to >= 0 before query is created (otherwise I got weird behaviour)
- Query seems to be created FOR the context player, if context player changes, query has to be recreated or you get weird behaviour
- Queries seem to work best with context player 0 and relation Any - seems most consistent - I do filtering later
- You can't specify multiple unit types or other constraints, just one (check the `Unit picker` out if you want that)
    - so if you want to generally pick a unit type, use their parent type, like `cUnitTypeMilitaryUnit`
- **Unit type** and **Player relation/ID** is required to be set, anything else can be omitted
- If reusing queue, make sure to RESET the results before executing the query, or you will get old results from previous query
- If not reusing, destroy it! Otherwise... you guessed it, weird behaviour, possibly from existence of same-named queue on context player.
- Tip: You can make the query variable `static` for easy reuse

### Player
|Return|Function signature|Note
| --- | --- | ---
| int | kbPlayerGetAge(int PLAYER_ID) | get player's age (can compare with constants like `cAge1` etc...)
| int   |  kbTechGetStatus(int TECH)  | get tech status
| string | kbTechGetName(int TECH)    | get tech name
| int   |  kbPlaterGetPop(int PLAYER_ID)    | get player's population
| int   |  kbPlayerGetPopCap(int PLAYER_ID) | get player's max population
| bool  |  kbPlayerIsHuman(int PLAYER_ID)   | true if player is human
| bool |   kbPlayerHasLost(int PLAYER_ID)   | true if player has lost
| int  |   kbResourceGet(int RESOURCE_ID)   | get resource count for context player
| bool |   kbProtoUnitCanTrain(int PLAYER_ID, int PROTO_ID) | true if given player can train given proto unit / unit type
|float  |  kbGodPowerGetCost(int POWER_ID, int PLAYER_ID, bool includePrePurchased)| returns current cost of the given God Power for the given player, check possible god powers below
|int|kbGodPowerGetID(string NAME)|gets god power ID from name (example: `"LightningStorm"`)
|bool|kbGodPowerCheckActiveForAnyPlayer(int POWER_ID)|check if god power is active for any player

### Base (AI relevant)
|Return|Function signature|Note
| --- | --- | ---
| int   |  kbBaseGetMainID(int PLAYER_ID)  | get player's main base ID
| int  |   kbBaseGetIDByIndex(int PLAYER_ID, int INDEX)  | get base ID based on index among players' bases (-1 if invalid)
| vector | kbBaseGetLocation(int PLAYER_ID, int BASE_ID)           | get location of player's base ID
| void  |  kbBaseSetDistance(int PLAYER_ID, int BASE_ID, float RANGE)   | sets gather range to given range -- ??? not sure if this is just for AI, did not test it
| float |  kbGetAmountValidResourcesByPosition(vector BASE_POS, int RESOURCE_ID, int RES_AI_SUBTYPE, float RANGE) | get number of resources for given resource types - Example params: `basePosition, cResourceFood, cAIResourceSubTypeHunt, range`
| string | kbBaseGetNameByID(int PLAYER_ID, int BASE_ID)           | get base name for base ID of player
| int    | kbBaseGetOwner(int BASE_ID)   | get player ID that owns the base
| int   |  kbFindClosestBase(int PLAYER_TO_ATTACK, int PLAYER_RELATION, vector POSITION, bool military_only)  |  if player to attack is set, relation should be -1, and vice versa


### Unit Picker
Unlike Unit query, the picker is focused on targeting behaviour rather than radius/type.

|Return|Function signature|Note
| --- | --- | ---
|int   |  kbUnitPickCreate(string NAME) |  create unit picker, returns PICKER_ID
|bool  |  kbUnitPickGetIsIDValid(int PICKER_ID)  | check if returned picker ID is valid (for reuse)
|void  |  kbUnitPickResetAll(int PICKER_ID) | resets all configuration (when reusing it)
|void  |  kbUnitPickSetMovementType(int PICKER_ID, TYPE)  | for example `cPassabilityAmphibious` or `cPassabilityAir` (can call multiple times to add extra constraints)
|void  |  kbUnitPickSetPreferenceFactor(int PICKER_ID, UNIT_TYPE, FACTOR) |can set factor to 0.0 for units we want to ignore like `cUnitTypeDryad`
|int  |   kbUnitPickRun(int PICKER_ID)  |executes picker and returns number of results
|int |    kbUnitPickGetResult(int PICKER_ID, INDEX) | get unit ID at 0-based index

You can check usage examples within existing game scripts.

## RM functions
Random-Map functions, usually for map initialization/constrainging, building areas/adding triggers. This is used in random maps, you can find those in your game folder and check there for examples.

I haven't used this much, so I don't have much notes here. Some relevant functions below:

|Return|Function signature|Note
| --- | --- | ---
|int|rmGetPlayerCiv(int PLAYER_ID)|gets player's picked civ
|void|rmSetPlayerResource(int PLAYER_ID, int RESOURCE_ID, float AMOUNT)|sets player's resources to given amount
|void|rmTriggerAddScriptLine(string TRIGGER_CODE)|adds trigger code to random map (this is how you can include your custom scenario XS scripts in random maps - CryBarEditor has functionality to wrap your XS scripts for random map usage)


## TR functions
Trigger functions. Same functions that triggers use in the editor. They can cheat and run game changing events. This is where most of the fun is.

### Note
As mentioned earlier, trigger functions are only supported for custom scenarios. However, you can make them work in random maps by wrapping the entire code with `rmTriggerAddScriptLine` and calling that when loading the random map.

You can add this line into `rm_core.xs` to make it load for ALL random maps. Because all random maps load this file. I made a script that makes this process easier: [repository](https://github.com/CryShana/XStoRM), but it's functionality is also built into [CryBarEditor](https://github.com/CryShana/CryBarEditor).

### Unit
Most (if not all) **unit trigger functions work on ALL SELECTED UNITS**. This is why you don't need to provide an ID when calling them.
First you select all relevant units, and then any subsequent unit trigger function will affect all of them (until selection is cleared).

|Return|Function signature|Note
| --- | --- | ---
|void  |  trUnitSelectClear()  | clears any unit selection
|void  |  trUnitSelectByID(int UNIT_ID)  | adds given unit to the selection (**there is a limit** of units selected - 30? or more, did not test it)
|void  |  trUnitHighlight(float DURATION, bool flash)  | highlights unit with white for specified duration
|bool  |  trUnitAlive()  | true if selected units are all alive (Haven't tested this one, I think it returns false if even a single unit is dead and others alive)
|void  |  trSetUnitIdleProcessing(bool)       | if units should do their idle processing   
|void  |  trUnitChangeProtoUnit(string PROTO_NAME)   | change units to given proto unit (use editor names)
|void  |  trUnitChangeName(string NAME)      | changes unit name (it will be displayed in uppercase)
|void  |  trUnitConvert(int TO_PLAYER_ID)     | converts unit to given player id      
|bool  |  trUnitHasLOS(int PLAYER_ID)         | true if player can see selected units
|bool  |  trUnitVisToPlayer(int PLAYER_ID)    | true if player can see selected units AND they are on their screen 
|bool  |  trUnitIsOwnedBy(int PLAYER_ID)      | true if unit owned by player
|bool  |  trUnitIsSelected()                  | true if unit selected
|void  |  trUnitSetAnimation(string ANIM_NAME, bool LOOP, int EVENT_ID) | ...
|void  |  trUnitSetHeading(int DEGREES)   | sets unit orientation
|void  |  trUnitSetHitpoints(float HP)           | sets unit HP to given value
|void |	trUnitSetStance(string STANCE)    | sets unit stance
|void | trUnitPatrolToPoint(float x, float y, float z, bool UnitRun, float runSpeedMultiplier)|makes unit patrol
|void  |  trUnitTeleport(float X, float Y, float Z)         | teleports units to given coordinates
|bool  |  trUnitTypeIsSelected(PROTO_ID)    | true if specified proto unit id is selected (similar to `kbUnitIsType`)
|bool  |  trUnitDead()                       | true if all selected units are dead
|void  |  trUnitDelete(bool REMOVE)         | kills or removes selected units
|void   | trUnitSetVariation(int variation)  | sets variation for units (male=0/female=1 usually)
|int    | trUnitPercentComplete()     | returns 0 to 100 in percent completed
|void   | trUnitRemoveControlAction()  | removes current control action so next thing takes effect immediately
|vector | trUnitGetPosition(int UNIT_ID) | gets unit position (always works, unlike kbUnitGetPosition)
|void   | trUnitMutate(string PROTO_NAME, bool fullHitpoints)  | transforms selected units into the given protoUnit through PU mutation, maintaining original BUnit pointer
|void   | trUnitReveal(int PLAYER_ID, bool reveal)  | reveals selected unit to player
|void   | trUnitModifyResourceInventory(int RESOURCE_ID, float delta, int relativity)  | example `trUnitModifyResourceInventory(cResourceFood, 200, 0)` adds 200 food to inventory
|void   | trUnitCreate(string protoName, float x, float y, float z, int heading, int playerID, bool skipBirth)| can be used for spawning VFX as well - this may not work online, have not tested it - is really finnicky, sometimes it despawns immediately if player id = 0 - sometimes doesn't show at all
|void  | trUnitApplyEffectProtoUnit(int effectID, float duration, int playerID, string protoName) |  I prefer using this for spawning VFX on top of selected units, they get removed automatically after passed duration - relevant effectID for `Attach` is `12` - what you will use mostly
|void|trDamageUnit(float damage)| does specific amount of damage to HP instantly
|void|trDamageUnitPercent(float damagePercent)| does % percent of a unit's total HP instantly
|void|trDamageUnitsInArea(int playerID, string unitTypeName, float range, float dmg)|All units within dist of the selected ref object are damaged by given factor
### Unit modifiers
I got some of these values by just setting them in-editor and then reading them from `trigtemp.xs`.
It is incomplete, but at least you get an idea

|Return|Function signature|Note
| --- | --- | ---
|void |   trUnitAddModifier(int modifyType, int dmgType, float amount, float duration)|adds unit modifier
|void  |  trUnitAdjustModifier(int modifyType, int dmgType, float delta, int relativity)|adjusts unit modifier

- `0.5` delta is usually 50% of current value, but sometimes it just adds the delta to the value
    - this behavioru depends on the modifier type AND relativity (prefer `Absolute` in most cases)
    
- Here are SOME known modify type values you can use:
    - 0 = Speed
    - 1 = MaxHp
    - 2 = Damage
    - 4 = UnitDamage
    - 5 = LOS
    - 6 = Armor
    - 9 = GatherRate
    - 12 = AutoGatherRate (fatten rate for animals)
    - 14 = BaseHP
    - 19 = HealRate
    - 22 = TrainingRate
    - 37 = FavorGatherRate

- Here are SOME damage types you can use:
    - -1 = None
    - 0 = Hack
    - 1 = Pierce
    - 2 = Crush
    - 3 = Divine

- Possible relativity values:
    - 0 = Absolute
    - 1 = Assign
    - 2 = Percent
    - 3 = BasePercent

I usually used Absolute relativity and just played around with it.

### Chat
|Return|Function signature|Note
| --- | --- | ---
|void  |  trChatHistoryClear() | clears chat history
|void |   trChatSend(int FROM_PLAYER_ID, string MSG) | sends message as player
|void |   trChatSendToPlayer(int FROM_PLAYER_ID, int TO_PLAYER_ID, string MSG) | sends message privately from and to given player
|bool  |  trChatHistoryContains(string MSG, int PLAYER_ID) | returns true if chat history contains given message from player (could be used for custom cheat codes perhaps, not sure how to ignore already processed messages though)
|void  |  trChatSendSpoofed(int FROM_PLAYER_ID, string MSG)  | - sends message but does not append the player

### Misc
|Return|Function signature|Note
| --- | --- | ---
|bool |trPlayerIsDefeatedOrResigned(int PLAYER_ID) | returns true/false if the player has resigned or has been defeated
|void| trGetWorldDifficulty() | Returns difficulty number (check below for values)
|void| trSoundsetPlay(string NAME) | plays given soundest (example: `trSoundsetPlay("AthenaGrunt")`)
|void| trSoundsetPlayPlayer(int PLAYER_ID, string NAME) |  plays soundest for given player
|void| trPlayerAllowAgeUpSpawning(int PLAYER_ID, bool ENABLED) | if false, disables myth unit spawning from temples on age up
|void| trPlayerAllowStartingUnitsSpawning(int PLAYER_ID, bool ENABLED) | if false, nothing spawns from TC when game starts
|void| trTechSetStatus(int PLAYER_ID, int TECH, int STATUS) | sets tech status for tech id for player
|void| trTechStatusCheck(int PLAYER_ID, int TECH, int STATUS)| returns true if tech has given status
|void| trModifyProtounitData(string PROTO_NAME, int PLAYER_ID, int puField, float delta, int relativity) | modifies protounit... I recommend you just set this in editor and let it generate, I never write these manually
|void| trForbidProtounit(int PLAYER_ID, string PROTO_NAME)   | forbids this proto unit for player
|void| trTechModifyResearchPoints(int TECH_ID, int PLAYER_ID, float DELTA, int techRelativity)  |  modifies tech cost
|void| trPlayerGrantResources(int PLAYER_ID, int RESOURCE_ID, float AMOUNT)      |   grants player these resources
|void| trOverlayText(string rawText, float time, float textSize, int colorR, int colorG, int colorB ) | overlays text

### God powers
|Return|Function signature|Note
| --- | --- | ---
|void|trGodPowerInvoke(int PLAYER_ID, string POWER_NAME, vector pos1, vector pos2)|invokes god power as given player, setting positions is only needed if power requires it - if player is 0 (Gaia), power can be called anytime - otherwise MAKE SURE players has the power **available** AND **area visibility** for powers that require it
|void|trGodPowerSetCooldown(int PLAYER_ID, string POWER_NAME, int SECONDS)| sets god power cooldown in seconds for player
|void|trGodPowerSetCost(int PLAYER_ID, string POWER_NAME, float INITIAL_COST, float REPEAT_COST) |sets god power favor cost (Note below)
|void|trGodPowerSetUseCount(int PLAYER_ID, string POWER_NAME, int COUNT) | sets how many times can power be used (-1 = unlimited)
|void|trGodPowerCancelAll()| cancels all currently active god powers
|void|trPlayerKillAllGodPowers(int PLAYER_ID)|removes all god powers
|void|trGodPowerGrantAtSlot(int PLAYER_ID, string POWER_NAME, int COUNT, int POSITION, int COOLDOWN, bool USECOST, bool REPEATATEND)|grants specified god power at specified slot - Example: `trGodPowerGrantAtSlot(1, "Meteor", 0, 2, 5, true)`
|void|trGodPowerGrant(int PLAYER_ID, string POWER_NAME, int COUNT, int COOLDOWN, bool USECOST, bool REPEATATEND)  |grants specified god power at first available slot (Example: `trGodPowerGrant(1, "Curse", 0, 5, true)`)

- To have them be reusable, set count to `0` and use cost to `true` (I'm still uncertain how the last two boolean parameters behave, it's a bit weird, test it out)
- Slot position is 0-based! Position 2 is third slot. But if there are free slots before, it will place it there instead!
- Setting into slot will OVERRIDE whatever was there before, so setting to position 0 will override starting power
    - HOWEVER, when overriding same power, it will not change the cooldown (and maybe not other things) - use other functions to set that


## Math functions
Here are some math functions I stumbled upon and seem to work nicely:

- `round(float val)`
- `sqrt(float val)`
- `pow(float pow, float val)`
- `ceil(float val)`
- `log2(float val)`
- ... and possibly more

## Common Constants
I will just dump some of my most used constant values, sorted by category:


### MISC constants

- `cNumberPlayers` - number of ingame players (can combine with `kbPlayerIsHuman` and `kbPlayerHasLost` to filter them)
- `cDifficultyCurrent`  - current difficulty (not sure how this works online)
- `cInvalidVector` - a useful default value for vectors when they are not set

NOTE: for some reason constants like "`cMyID`" that refer to current player are NOT available within custom scenario scripts


### ACTION TYPE constants

- `cActionTypeIdle`
- `cActionTypeGather`
- `cActionTypeHunting`

### UNIT STATE constants
- `cUnitStateAlive`
- `cUnitStateAny`
- `cUnitStateABQ`
- `cUnitStateNone`
- `cUnitStateBuilding`
- `cUnitStateDead`
- `cUnitStateQueued`

### UNIT STAT constants
- `cUnitStatMaxHP`
- `cUnitStatCurrHP`
- `cUnitStatLOS`
- `cUnitStatRepairable`
- `cUnitStatMaxVelocity`
- `cUnitStatCurrentVelocity`
- `cUnitStatArmorHack`
- `cUnitStatArmorCrush`
- `cUnitStatArmorPierce`
- `cUnitStatVeterancyRank`
- `cUnitStatBuildProgressPercent`
- `cUnitStatRepairRate`
- `cUnitStatBuildPoints`
- `cUnitStatTrainingRateBonus`
- `cUnitStatBuildingWorkRate`
- `cUnitStatHPRatio`
- `cUnitStatHPRatioPercent`
- ...possibly more

### PLAYER RELATION constants
- `cPlayerRelationEnemyNotGaia`
- `cPlayerRelationAny`
- `cPlayerRelationAlly`
- `cPlayerRelationAllyExcludingSelf`
- `cPlayerRelationEnemy`
- `cPlayerRelationSelf`


### TECH STATUS constants

- `cTechStatusActive`
- `cTechStatusObtainable`
- `cTechStatusUnobtainable`

### CIVS constants

- `cCivZeus`
- `cCivHades`
- `cCivPoseidon`
- `cCivRa`
- `cCivIsis`
- `cCivSet`
- `cCivOdin`
- `cCivLoki`
- `cCivThor`
- `cCivFreyr`
- `cCivGaia`
- `cCivKronos`
- `cCivOranos`
- ...more in the future

### AGES constants
- `cAge1` - dark age
- `cAge2` - classical age
- `cAge3` - heroic age
- `cAge4` - mythic age
- `cAge5` - wonder age

### RESOURCE constants
- `cResourceFood`
- `cResourceWood`
- `cResourceGold`
- `cResourceFavor`

###  PASSABILITY / MOVEMENT TYPE
- `cPassabilityAir`
- `cPassabilityAmphibious`
- `cPassabilityLand`
- `cPassabilityWater`

(can combine like so "`cPassabilityLand | cPassabilityWater`" when checking passability)

### GOD POWERS 
NOTE: These seem to be defined manually per use case and are not actually available globally.
Use the god power string names for selecting them! 

So just use whatever is after `cGP` as the name... 

Define your own constants like so:
`const string cGPCeaseFire = "CeaseFire"`

- `cGPCeaseFire`
- `cGPSentinel`
- `cGPRestoration`
- `cGPCurse`
- `cGPTartarianGate`
- `cGPSeedofGaia` (`of` is surprisingly lowercase here)
- `cGPPlentyVault`
- `cGPRain`
- `cGPChaos`
- `cGPGuardianBolt`
- ... etc

### DIFFICULTY constants
- `cDifficultyEasy` = 0
- `cDifficultyModerate` = 1
- `cDifficultyHard` = 2
- `cDifficultyTitan` = 3
- `cDifficultyExtreme` = 4
- `cDifficultyLegendary` = 5

### UNIT TYPE = PROTO UNIT ID constants
- `cUnitTypeTownCenter`
- `cUnitTypeSentryTower`
- `cUnitTypeArmory`
- `cUnitTypeMarket`
- `cUnitTypePalace`
- `cUnitTypeMilitaryBarracks`
- `cUnitTypeTemple`
- `cUnitTypeVillagerGreek`
- `cUnitTypeVillagerAtlantean`
- `cUnitTypeManor`
- `cUnitTypeOracle`
- `cUnitTypeTurma`
- `cUnitTypeKatapeltes`
- `cUnitTypeMurmillo`
- `cUnitTypeFanatic`
- `cUnitTypeFireSiphon`
- `cUnitTypeCentaur`
- `cUnitTypeMinotaur`
- `cUnitTypePeltast`
- `cUnitTypeHoplite`
- `cUnitTypeHypaspist`
- `cUnitTypeArcheryRange`
- `cUnitTypeStable`
- `cUnitTypeMilitaryAcademy`
- ...
- `cUnitTypeAbstractVillager` (abstract types are used for [kbUnitIsType] or [kbProtoUnitIsType])
- `cUnitTypeAbstractInfantry`
- `cUnitTypeAbstractSiegeWeapon`
- `cUnitTypeAbstractWarship`
- `cUnitTypeAbstractWall`
- `cUnitTypeAbstractFishingShip`
- `cUnitTypeAbstractTownCenter`
- `cUnitTypeAbstractSocketedTownCenter`
- `cUnitTypeAbstractOracle`
- `cUnitTypeAbstractTransportShip`
- `cUnitTypeAbstractSettlement`
- `cUnitTypeAbstractTitan`
- `cUnitTypeAbstractFarm`
- `cUnitTypeAbstractFortress`
- ...
- `cUnitTypeAll`            (includes both units and buildings, not VFX though)
- `cUnitTypeUnit`           (excludes buildings)
- `cUnitTypeBuilding`       (excludes units)
- `cUnitTypeMilitaryUnit`

### TECH constants
- `cTechCopperWeapons`
- `cTechCopperArmor`
- `cTechBronzeArmor`
- `cTechBronzeWeapons`
- `cTechMediumCavalry`
- `cTechHeavyCavalry`
- `cTechLabyrinthOfMinos`
- `cTechRoarOfOrthus`
- `cTechHeavyInfantry`
- `cTechBronzeShields`
- `cTechHeavyArchers`
- `cTechArchitects`
- `cTechBoilingOil`
- `cTechMasons`
- `cTechChampionInfantry`
- `cTechIronShields`
- `cTechEngineers`
- `cTechPetrification`
- `cTechIronWall`
- `cTechCitadelWall`
- `cTechBronzeWall`
- `cTechSecretsOfTheTitans`
- `cTechTaxCollectors`
- `cTechCoinage`
- `cTechAmbassadors`
- `cTechClassicalAgeLeto`
- `cTechWatchTower`
- `cTechHandAxe`
- `cTechClassicalAgeOceanus`
- `cTechHeroicAgeHyperion`
- `cTechHeroicAgeRheia`
- `cTechHeroicAgeTheia`
- `cTechMythicAgeAtlas`
- `cTechMythicAgeHelios`
- `cTechMythicAgeHekate`
- `cTechClassicalAgeAthena`
- `cTechClassicalAgeAres`
- `cTechClassicalAgeHermes`
- ... and more, just guess them from their name

## How Rules work
Rules are just like triggers in the editor. They can be active or not, loop or not, etc...

Here is a very basic example:
```c
rule RULE_NAME
active
{
    // this code will run every tick in a loop
}
```

Here is an example with more parameters added:
```c
rule RULE_NAME
group RULE_GROUP_NAME
inactive
minInterval 1  // this value is an integer (can not be float) in seconds
maxInterval 3
{
    // once activated, will run once every 1 to 3 seconds.
    // it is also part of a group 
    // groups are useful for enabling/disabling multiple rules at once
}
```

You can make an initialization function that runs at beginning of a game like so:
```c
rule MyMod_Initialize
{
    // initialize stuff here, maybe enable certain rules/groups...

    xsDisableSelf();  // this disables it and will not loop
}
```
You can also specify `highFrequency` under rule definition to make sure it runs at high frequency but personally I have no idea what difference does it make in practice.

You can also set `priority [integer]` to set priority. Behaves same as triggers do. **Rules are executed in order of priority.**

## String manipulation
String manipulation is really basic, I would not recommend doing it. One thing to remember is that **ALL STRINGS ARE CASE INSENSITIVE** - this means `"a" == "A"` - I don't like it, but it is what it is.

The following snippet showcases all functions I have found so far:
```c
string a = "test";
string b = a + " -> " + a;  // concatenation works nicely
int length = a.length();    // can get string length

string c = a.substring(2, 3); // substrings! (from_index_inclusive, to_index_inclusive);
string character = c.charAt(0); // we can even get characters! But they are all string type.
```

## Random snippets/examples

### Querying units
Here is an example of querying units

```c
// setting context player is really important! Must be before query is created!
xsSetContextPlayer(1);    

int unitQueryId = kbUnitQueryCreate("some_query_name");

// we want to query my own units, that are alive, and are spearmen
kbUnitQuerySetPlayerRelation(unitQueryId, cPlayerRelationSelf);
kbUnitQuerySetState(unitQueryId, cUnitStateAlive);
kbUnitQuerySetUnitType(unitQueryId,cUnitTypeSpearman);

// query is executed here
kbUnitQueryExecute(unitQueryId);

// clear any previous unit selection
trUnitSelectClear(); 

int queryCount = kbUnitQueryNumberResults(unitQueryId);
for (int i = 0; i < queryCount; i++)
{
    int unitId = kbUnitQueryGetResult(unitQueryId, i);

    // add unit to selection
    trUnitSelectByID(unitId);     
}

// do following to all selected units
trUnitHighlight(1);
trDamageUnitPercent(50);

// ... at this point you should destroy the query
kbUnitQueryDestroy(unitQueryId);

// OR reuse it by making it a static variable, check next example
```

Here is an example of **reusing** a query object
```c
static int query = -1; // static variables persist across function calls
if (query == -1)
{
    query = kbUnitQueryCreate("some_query_name");

    // set params you will not change
    kbUnitQuerySetState(query, cUnitStateAlive);
    kbUnitQuerySetUnitType(query, cUnitTypeUnit);
}

// set changeable params
kbUnitQuerySetPlayerRelation(query, relation);
kbUnitQuerySetMaximumDistance(query, radius); // when setting radius, position must be set too!
kbUnitQuerySetPosition(query, position);

// reset results from previous runs
kbUnitQueryResetResults(query); 

// execute
kbUnitQueryExecute(query);

// ... handle same way as above, except now you don't need to destroy it

```

### Arrays
Arrays act like lists, they dynamically resize.

Out-of-bounds operations simply return -1.
```c
int[] results = new int(0, 0); // (SIZE, INITIAL_VALUE)   
results.add(resultPUID);
if (results.size() == 0)
{
    // ...
}

int[] arr = new int(5, 2); // (SIZE, INITIAL_VALUE)
arr.size(); // returns 5
arr[5];     // out of bounds returns -1
arr.add(6); // adds to array, expands it (works like a list)
arr[5]      // returns 6

static int[] arr = default; // static array, use `default` (same as (0,0))
// can use it normally

// other array functions:
arr.clear();
arr.removeIndex(0);
// ...
```
When passing arrays to function, make sure to do so by reference, otherwise they are copied!
```c
// function:
int my_function(ref int[] array) {}

// usage:
my_function(my_array);
```