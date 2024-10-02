## Modding Basics
## Tools
Highly recommend you prepare a tool for reading BAR archives, so far I am aware of just these two:
- my own tool, **CryBarEditor** ([link to Releases page](https://github.com/CryShana/CryBarEditor/releases))
- or the **AoE3 Resource Manager 0.7** or later version ([link to download post](https://forums.ageofempires.com/t/v-0-7-resource-manager-age-of-myth-retold-bar-extractor/260136))

This guide is written with CryBarEditor in mind, so if you are not using it, you can just ignore those parts of this guide.

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
| `powers.xml` | `powers_mods.xml` | `<powersmod>`

If you are uncertain whether your additive mod of a custom file was accepted by the game or not,
please check [Debugging Tips](#debugging-tips)

### How it works
- The additive mod **usually** is just the original file name suffixed with `_mods`
- The additive mod **usually** is an `.xml` file (I only found the string mods not to be XML so far)
- The additive mod **usually** has a unique root element (check table above) and uses same syntax as the files it's overriding
- Tags now accept a new attribute called `mergeMode`. It defines how certain tags should be handled when merged with base file. It has following possible values:
    - `modify` - replace if exists, otherwise add tag to existing object (default merge mode if no mode is provided)
    - `replace` - replace an existing tag within existing object
    - `remove` - remove an existing tag from existing object
    - `add` - add a new tag to existing object

The idea is that whatever you define in this additive mod file, will be merged together with the base file. So instead of replacing the whole `proto.xml` file, you will just apply the changes defined in `proto_mods.xml`. This way even if `proto.xml` is updated or another mod replaces it - your mod will continue to work because it will modify whatever is used and not replace it entirely.

### Example 1: Adding/Modifying unit
Before we needed to export the entire `proto.xml` file and edit just a small portion of it. Now, we don't export anything, we just create a `proto_mods.xml` file in the SAME DIRECTORY as `proto.xml` is in --- this means `game\data\gameplay\`.
- CryBarEditor supports right cicking on some entries (like `proto.xml.XMB`) and clicking `Create additive mod` which will automatically do this
- It is recommended to have the original `proto.xml` opened somewhere while editing the mod, so you know what you can edit and what not (If you want to export it to view with an external tool, make sure to export it outside the mod directory, you don't want to accidentally leave the `proto.xml` in your mod, making your additive mod useless)

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

## More examples and notes

As mentioned before, not specifying `mergeMode` explicitly will default to `modify` behaviour. This means if you are not careful, you may override a vanilla item with your own, when you thought you were adding it. So it's a good idea to explicitly set the `add` mergeMode when adding new items.

Example of adding new effect:
```xml
<effect type="Data" amount="1.00" subtype="Enable" relativity="Absolute" mergeMode="add">
  <target type="ProtoUnit">MyNewUnit</target>
</effect>
```

### How nodes are matched?
From my understanding, XML nodes are matched based on **tag**, **attributes** and lastly, **inner value**. In that order of importance. 

If you specify a `<unit name="something">`, the game will try to first match `<unit>` and then narrow that down based on value of the attribute `name`. If no node is matched or more than 1 is matched, then a new unit is created - otherwise that single matched one is modified.

Flags, for example, do not have attributes, so they are identified by their inner value. Here is an example of **a removing flag**:
```xml
<flag mergeMode="remove">NotSelectable</flag>
```  
above works as intended and does not remove the very first flag found, but the one with value "`NotSelectable`". How does this work? It first tried matching against `<flag>` but multiple nodes were found, then it narrowed it down based on inner value, and it got 1 match. And hence, it removed that one.

So with that in mind, whenever you wish to modify an existing item, you need a way to uniquely match it, so there is only 1 match!
This can be hard with certain items that don't have many identifying features. For those items it's better to remove the one you can match, and add your own!

Keep in mind that not all values are used for matching. It's still not very clear which are and which are not. Check below example with `<protoaction>`. Game uses their inner value of `<name>` element to be match with existing proto actions, but `<maxrange>` is conveniently ignored and not used to match. This allows us to easily modify proto actions like this:
```xml
<unit name="VillagerEgyptian">
  <protoaction>
    <name>RangedAttack</name>
    <maxrange>0.300000</maxrange>
  </protoaction>
</unit>
```


Here is an example of an effect:
```xml
<effect type="Data" amount="0.05" subtype="ResourceTrickleRate" resource="Favor" relativity="Absolute">
  <target type="Player">
  </target>
</effect>
```
You can remove it like so, it's uniquely matched based on attributes:
```xml
<effect mergeMode="remove" type="Data" amount="0.05" subtype="ResourceTrickleRate" resource="Favor" relativity="Absolute" />
```
And then you can add your own effect.

But what if developers decided to change the `amount` attribute? Then our remove command would no longer match the node. In that case we need to adjust how we match the node.

User `Serpens66` has tested this and apparently you can dismiss certain attributes, as long as the node is uniquely identifiable by other attributes, it will be matched. So you could adjust the above remove command to this one and will still match:
```xml
<effect mergeMode="remove" type="Data" subtype="ResourceTrickleRate" resource="Favor" relativity="Absolute" />
```
After removing the effect, we can add our own effect.



### Add/change Tactics/Abilities/Godpowers
Additive mods for files that do not end with `.xml` should also not end with `.xml` even if they are XML formatted.
Example of such a file is `atlantean.godpowers.XMB`, you notice unlike `proto.xml.XMB` it does not end with .xml.

You can mod `powers.xml` by simply creating a `powers_mod.xml` file. You can then include your custom `.godpowers` file, similar to `atlantean.godpowers`

You can include your custom powers (or abilities) like so: (inside `powers_mod.xml`)
```xml
<powersmod>
  <include mergeMode="add">god_powers\MyCustomGodPowers.godpowers</include>
  <include mergeMode="add">abilities\MyCustomAbilities.abilities</include>
</powersmod>
```

### Debugging Tips

To check if your additive mod resulted in intended changes, open the following file in your installation directory:
`"[InstallPath]\Age of Mythology Retold\game\config\production.cfg`" (creating `user.cfg` also works)

Add the following line to this file and save it:
```
DebugOutputGameData
```
Now whenever you start the game with mods enabled, you will find many additive-changeable files at this location: `%AppData%\Local\Temp\Age of Mythology Retold\Data`. You can compare the generated files with original ones from the BAR archives to check if your additive changes were applied correctly.  

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
```
generateTRConstants
``` 

## XS scripting reference
If you are writing XS scripts, you can also checkout my personal reference I wrote [here](XSScriptingReference.md) 