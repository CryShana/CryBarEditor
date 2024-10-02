## Modding Basics
## Setup

- Create a mod folder in `C:\Users\[USER]\Games\Age of Mythology Retold\[YOUR_ID]\mods\local\[MOD_NAME]`
- Set your mod folder as export Root directory in CryBarEditor (any exported file will be copied to the mod folder while retaining all relative paths)
- There are 2 ways of modifying game files:
    - **STANDARD METHOD**: Provide an alternative file on the same relative path as used by game, it will be used instead
    - **ADDITIVE METHOD**: Provide a `_mods.xml` file that will partially change a base file without replacing it (less conflicts with other mods + less chance of it breaking on game updates)

Additive method is preferred, but (to my knowledge) only a few files support it. Others can only be replaced entirely using Standard method. Mod priority then decides which mod takes priority when replacing such files.

Most game files are packaged within **BAR archives**, luckily for us, CryBarEditor can read them and export relevant files that we want to modify. We don't need to create whole new BAR archives, we can just export the original file from BAR archive and modify just that file.

- Also set the root Directory when using CryBarEditor for easier workflow, the root Directory should bethe game's `\game` directory. This is usually `C:\Program Files (x86)\Steam\steamapps\common\Age of Mythology Retold\game`

With both root directory and export root directory set, you are ready to start using CryBarEditor.

---
## Standard modding method
The standard method is quite straight forward. If your mod folder contains a file with same relative path as used by the game, it will be used instead.

This means if game uses `game\art\something.png`, if your mod folder also contains `game\art\something.png`, the version your mod uses will replace the base version.


### Example 1: Adding new unit
To add a new unit or modify any existing one, you will want to modify the `proto.xml` file. This file is contained within `Data.bar` archive.
- Find `Data.bar` and open it
- Inside it, find `proto.xml.XMB` file. You will notice it has `.XMB` extension. We can not edit this directly, so we export it and convert it. This will create a `proto.xml` file in our mod folder
- We can now edit that `proto.xml` file as we wish and the game will use it
- To add a new unit:
    - Simply add a new `<unit>...</unit>` entry to the list of other units, you can copy an existing unit to use as starting point
    - To set a unique name, you will need to add a new string ID into the string table. For that you need to export `string_table.txt` for your language and add it there

**NOTE:** It is strongly recommended to modify the proto file using the ADDITIVE METHOD and not the standard method shown here (the additive method is shown later)

### Example 2: Modifying/Adding translations
Already mentioned in earlier example, you want to export the relevant `string_table.txt` file (also within `Data.bar` archive)

Once exported, just open it and you will see there is a certain format to be followed. To add a new entry you can just add the following line anywhere:
```
ID = "YOUR_STRING_ID"   ;   Str = "Your translation here"
```
And then you can use the `YOUR_STRING_ID` somewhere else, for example in proto file when adding a new unit.

### Example 3: Modifying God powers
Let's say we want to make the greek god power "Curse" affect everyone, even heroes.

For this we want to export the file `greek.godpowers.XMB` (also within `Data.bar` archive), make sure to convert it because it's encoded as XMB.

**WARNING! This is a special case**, with `proto.xml.XMB` we were able to use `proto.xml` directly, but with `greek.godpowers.XMB` there is no `.xml` extension, so we can't use it directly as .xml! This means after we are finished editing the exported file, we need to convert it back to XMB! (CryBarEditor has a tool for this `Convert > XML to XMB`)

So anyway, once you exported `greek.godpowers`:
- Open the file, find where `Curse` is defined
- You will notice that `Hero` and `AbstractTitan` (and others) are excluded from being targeted using tag `<explicitlyrestrictedattacktargettype>`
    - Just remove these tags from it
- At this point you can modify anything else here, like cost, radius, who it affects, etc...
- Once done, convert the file back to XMB, so you get `greek.godpowers.XMB` and that's it

### Example 4: UI changes
So far we have been using `Data.bar` archive a lot, because most important data-related things are there. But for art/textures etc we have plenty other BAR archives, mainly because they are quite big.

For example we want to modify the hoplite icon. This is related to the UI, so we open `UIResources.bar` archive.

- Find `hoplite_icon.png` inside the archive
- Export the image, this will create the relative path and everything in our mod folder
- Now you can just replace that image with your own image (make sure resolution is the same if you don't want unexpected behavior ingame)

We can also easily replace images for gods, minor gods, technologies here, etc...

### Example 5: Texture changes
Textures are a bit unique because they use a special image format called DDT. DDT format contains multiple (lower res) versions of the same image, these are called mipmaps or mips. For this reason it's a bit complicated on how to handle it as there are many parameters related to DDT.

The easiest way to override a DDT file is to simply use CryBarEditor's replace feature called `Replace image and export DDT` which will copy all DDT parameters but replace the image. This way you don't need to worry about all DDT parameters.

For example we want to modify the Atlantean temple texture:
- Open `ArtAtlanteanTextures.bar` archive
- Search for `temple`, you will notice many DDT images for it, per age and per type of texture (BaseColor, Details, Masks, Normals, ...)
- We are interested in `_BaseColor`, we can right click on the relevant entry and press `Replace and export DDT` - this will ask for a new image, you can select it and the exported DDT will have your selected image

### Note when creating new anim files
`anim` files are used to specify what models units use in `proto.xml`. But if you were to create your own anim file, you will notice it's not loaded by the game. I haven't yet figured out why, I think it has to do with `simdata.simjson` file that links these anim files with tactics, but I haven't delved into this yet.

---
## Additive modding method
The additive method is meant to solve 2 main problems of the standard method:
- Game updates breaking your mod
- Other mods having conflicting changes with your mod

Only certain files support additive modding, I confirmed only the following:

| File name | Additive mod name | Root element
| --------- | ----------------- | -------------
| `proto.xml` | `proto_mods.xml` | `<protomods>`
| `techtree.xml` | `techtree_mods.xml` | `<techtreemods>`
| `string_table.txt` | `stringmods.txt` | (Is not XML)

See also "Check the result"-section, most of the files you find there are changeable additive (not all (yet?)).


### How it works
- The additive mod **usually** is just the original file name suffixed with `_mods`
- The additive mod **usually** is an `.xml` file (I only found the string mods not to be XML so far)
- The additive mod **usually** has a unique root element (check table above) and uses same syntax as the files it's overriding
- Tags now accept a new attribute called `mergeMode`. It defines how certain tags should be handled when merged with base file. It has following possible values:
    - `modify` - replace if exists, otherwise add tag to existing object (default merge mode)
    - `replace` - replace an existing tag within existing object
    - `remove` - remove an existing tag from existing object
    - `add` - add a new tag to existing object

The idea is that whatever you define in this additive mod file, will be merged together with the base file. So instead of replacing the whole `proto.xml` file, you will just apply the changes defined in `proto_mods.xml`. This way even if `proto.xml` is updated or another mod replaces it - your mod will continue to work because it will modify whatever is used and not replace it entirely.

### Example 1: Adding/Modifying unit
Before we needed to export the entire `proto.xml` file and edit just a small portion of it. Now, we don't export anything, we just create a `proto_mods.xml` file in the SAME DIRECTORY as `proto.xml` is in --- this means `game\data\gameplay\`.
(but you still could export the original xml files to check what is written in there, to know what you want to change or how the structure should look like. You can use this tool https://forums.ageofempires.com/t/v-0-7-resource-manager-age-of-myth-retold-bar-extractor/260136 to eg extract the Data.bar file in "Age of Mythology Retold\game\data" folder)

- To add a new unit called `CoolerHoplite`, the file would have the following content:
```xml
<protomods>
    <!-- This adds a new unit, no mergeMode necessary -->
    <unit name="CoolerHoplite">
        <displaynameid>STR_UNIT_COOLER_HOPLITE_NAME</displaynameid>
		<rollovertextid>STR_UNIT_COOLER_HOPLITE_LR</rollovertextid>
		<shortrollovertextid>STR_UNIT_COOLER-HOPLITE_SR</shortrollovertextid>
		<icon>resources\greek\player_color\units\cooler_hoplite_icon.png</icon>
		<animfile>greek\units\infantry\hoplite\hoplite.xml</animfile>
		<soundsetfile>greek\vo\hoplite\hoplite.xml</soundsetfile>
        <!-- AND SO ON .... -->
    </unit>  

    <!-- This removes unit -->
    <unit mergeMode="remove" name="Hoplite" />

    <!-- This modifies existing unit -->
    <unit name="House">
        <!-- Existing buildlimit tag is replaced with this one -->
        <buildlimit mergeMode="replace">32</buildlimit>
    </unit>
</protomods>
```

### Example 2: Modifying tech tree
The relevant file for modifying tech trees is `techtree.xml`, but because we are making an additive mod, we create a `tecttree_mods.xml` file instead.

Example of making Hera mythical age cheaper:
```xml
<techtreemods>
	<tech name="MythicAgeHera">
		<cost mergeMode="replace" resourcetype="Food">200.0000</cost>
		<cost mergeMode="replace" resourcetype="Gold">300.0000</cost>
	</tech>
</techtreemods>
```

### Example 3: Overriding/Adding strings
The relevant file for overriding English strings is `\game\data\strings\English\string_table.txt`.
To make an additive mod for this we need to create `\game\data\strings\English\stringmods.txt` with
the following content:

```
ID = "STR_UNIT_HOPLITE_NAME"   ;   Str = "MyNameForHoplite"
```

### Special note how mergeMode checks for existing:

According to my tests (so may be partly wrong, feel free to add more info), most often it only checks for the xml attributes (the words within `<>`).  
**modify**  
So if you want to add your new effect to a tech eg. to enable your new unit and use:
```xml
<effect type="Data" amount="1.00" subtype="Enable" relativity="Absolute">
  <target type="ProtoUnit">MyNewUnit</target>
</effect>
```
The game defaults to "modify" since we did not mention any mergeMode. And now the game compares the existing effects of this tech with yours.
But many techs (especially techs about the age) already include effects which enable new units and their attributes look 100% identical.
Therefore if you add your effect without mergeMode="add" here, it is very likely that you will overwrite a vanilla effect, instead of simply adding yours.

**remove**  
This seems to work a little different than modify, because something like:
```xml
<flag mergeMode="remove">NotSelectable</flag>
```  
works as intendend and does not remove the very first flag found, but the one with "NotSelectable".  
And with remove it also seems to be possible to leave out up to one attribute. Eg. we want to change the following effect from RelicAnkhofRa tech:
```xml
<effect type="Data" amount="0.05" subtype="ResourceTrickleRate" resource="Favor" relativity="Absolute">
  <target type="Player">
  </target>
</effect>
```
We only want to change eg. the amount, nothing more.  
I think "replace" can only change the content of xml, not the attributes (correct me if I'm wrong). Therefore the only way to change the value in this case is to remove this effect entirely and add a new one.
To remove this specific effect one would usually do:  
```xml
<effect mergeMode="remove" type="Data" amount="0.05" subtype="ResourceTrickleRate" resource="Favor" relativity="Absolute" />
```
But what if the devs themself change the value in a future update? Then this remove command will no longer work, since it is looking for an amount of 0.05.
Luckily remove also work when dismissing one of the attributes, so this also works and continues to work if the amount was changed:
```xml
<effect mergeMode="remove" type="Data" subtype="ResourceTrickleRate" resource="Favor" relativity="Absolute" />
```
And now we can use mergeMode="add" to add the effect with our adjusted amount.

### Repetitive xml elements

It seems the devs added some way to make additive modding work for repetitive elements, like `<protoaction>`. In this case if you want to change a specific protoaction, you have no attribute to tell the game which one you want to modify.  
In this case it always worked to mention the name of the protoaction and then the values you want to add/change, eg. here changing the maxrange of RangedAttack:
```xml
<unit name="VillagerEgyptian">
  <protoaction>
    <name>RangedAttack</name>
    <maxrange>0.300000</maxrange>
  </protoaction>
</unit>
```
(we dont need any mergeMode here, because "modify" is exactly what we want it to do)  
You can confirm that this is a special behavior the devs had to implement, because this behaviour does not (yet?) exist for the major_gods.xml file. There we have the same problem of repetitive elements, but it is currently impossible to change a specific civ, it always only changes the first civ found.

(It of course would be much better if the devs would extend the modding commands, so we can exactly define which xml element we want to modify.. eg. like Anno1800: https://github.com/jakobharder/anno1800-mod-loader/blob/main/doc/modop-guide.md#modop-guide )


### Add/change tactics/abilities/godpowers

These files are formatted like xml files, while the extension is not xml but tactics/abilities/godpowers.
I assume they are not changeable additive, since they are not generated by DebugOutputGameData (not tested though).
You can overwrite files in the standard way or if you only want to add something (not change existing), you do it by simply creating a new file with the correct extension, put at the same folder structure like the original with your custom unique name.
Tactics already work this way, while for abilities/godpowers you have to include your filenames in god_power.xml. This can be done additive by creating a powers_mods.xml eg with this content:
```xml
<powersmod>
	<include mergeMode="add">god_powers\MyCustomGodPowers.godpowers</include>
	<include mergeMode="add">abilities\MyCustomAbilities.abilities</include>
</powersmod>
```

### Check the result

To check if your additive code resulted in the intended changes, you can open "[installfolder eg. steam]..\Age of Mythology Retold\game\config\production.cfg" (could also be possible to create a user.cfg file instead of changing production.cfg, think both works) and add the following line to it and save:  
`DebugOutputGameData`  
After that, whenever you start the game with mods already enabled, you will find many additive-changeable files at this location: "...AppData\Local\Temp\Age of Mythology Retold\Data". Then you can eg compare this generated proto.xml with the vanilla one, to check if you additive changes were applied correctly.  

There are more commands you can add to this cfg file, most are helpful for AI, trigger and map scripting like:
```
aiDebug
showAIEchoes
developer
showAIOutput
generateAIConstants
AIShowBPValueText
```
And one command helpful for normal modding:  
`generateTRConstants`  
generateTRConstants creates a MythTRConstants file also in the Temp folder mentioned above and contains eg. all valid flags for units or effecttypes for techtree and so on. It is not complete and does not include an explanation unfortunately, but still better than nothing.


