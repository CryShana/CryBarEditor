# CryBarEditor (for AOM Retold)
Tool for fast and easy resource management, focused on AOMR.

## Features
- **Read BAR archives**
- Pick a **Root directory** (usually `\games` folder) for fast switching between BAR and other files
- Editor supports previewing various formats:
  - XMB
  - DDT
  - Any text or common image type
- Syntax highlighting for common formats (json,xml,ini,xs,...)
- Pick an **export Root directory** for fast exporting either from BAR or from Root directory
- Replace any existing DDT image with custom image on export (all other params are copied)
- Tools for direct file manipulation:
  - Convert XML -> XMB
  - Convert XMB -> XML
  - Convert DDT -> TGA
  - Compress with Alz4/L33t
  - Decompress file with Alz4/L33t
- Tool for converting XS trigger scripts to RM-friendly scripts for easy inclusion in random maps
- "**Search everything**" tool added that searches for query in all files and all BAR entries (useful for finding references)
- Remembering last selected Root directory and export Root directory, so you can **easily return to where you left off**

## Usage
- **Load the Root directory** (usually `C:\Program Files (x86)\Steam\steamapps\common\Age of Mythology Retold\game`)
- **Select the export directory** (usually your mod folder `C:\Users\[USER]\Games\Age of Mythology Retold\[YOUR_ID]\mods\local\[MOD_NAME]`)
- Files in root directory will appear in 1st column, **select any file to preview it**
  - if selected file is .BAR, it will immediately load in 2nd column
  - you can select entries from inside .BAR file too, they will be previewed the same
- Any file/BAR entry you wish to modify, **right click and export it**, for most cases you will use `Export X selected (convert)`
  - Relative paths are retained! This also works for exporting from Root directory as long as it contains `\game` in it's path
   
## Planned features
Currently planned:
- Support for easily making additive mods (if they work, haven't tested it yet)
- Converting DDT -> TGA where user can specify custom params (right now we only have conversion of existing DDT file with copied params)
- Creating BAR archive from selected files
- Creating modified BAR archive by only replacing selected entries in existing BAR archive
- (Support for other relevant formats, such as .TMA, .TMM and .DATA)

## Note
This is a personal project made in span of few days. I work on it in my free time, don't expect much.
Format support is focused on Age of Mythology Retold, so it may not work for other Age titles.

## Modding Basics
- To create a mod, just create a folder in local mods folder, usually here `C:\Users\[USER]\Games\Age of Mythology Retold\[YOUR_ID]\mods\local\`
- Set your mod folder as the export Root directory (this is where all exported files will be copied to, retaining relative paths)
- To override a file used by the game, you need to first determine it's relative path, for example `\game\art\something.png` - and then have a file in your mod folder with **same relative path**
- Most game files are inside **.BAR** archives, but every file in those archives has a relative path that we can override - we don't need to create a whole new .BAR file!

### Example 1 - New unit
If you wish to add a new unit to the game, you will want to override `proto.xml` file. This file is located within `Data.bar` archive
- Open `Data.bar` archive 
- Find `proto.xml` file within this archive - in our case it will be encoded as XMB, so you will see `proto.xml.XMB` instead
- Because we can't modify XMB directly, we have to convert it to XML, so when exporting it, make sure to also Convert it!
- Once exported, there should be a `proto.xml` file in our export folder. Just modify it as you wish, that is it.
- For adding a new unit, simply add a new `<unit>...</unit>` element at the end of the unit list - you can copy one of the existing units as starting point
  - You can modify it's name by setting the string ID (you will also need to define this string ID in `string_table.txt`, which is also in `Data.bar`)
  - You can modify what animation file is used (this includes model)
  - You can modify everything else you see there (HP, other stats, abilities, sounds ...)

Just to make sure, the `proto.xml` needs to have the relative path `game\data\gameplay\proto.xml` in order to work. This is the path used within the `Data.bar` archive.
You can always copy the relative path of an entry if unsure. If you selected the export directory correctly, the app will retain all relative paths correctly, so you don't need to worry about it.

### Example 2 - Translation changes
If you wish to adjust any translations/names/descriptions, you will want to override the relevant `string_table.txt` for your language. This is also within `Data.bar` archive
- Open `Data.bar` archive
- Find `string_table.txt` file for your language (there is one for each language)
- Export it, we can just Copy it out because the format is already readable (converting won't do anything)
- Once exported, there should be a `string_table.txt` in our export folder. Just modify it as you wish.
  - You can either add your own strings or modify existing ones
  - To add your own string, just follow the existing format `ID = "YOUR_STRING_ID"   ;   Str = "Your translation here"`

### Example 3 - God powers
We want to make the greek god power "Curse" to affect everyone, Heroes included.
For this we want to look for `greek.godpowers`, within `Data.bar` archive.
- Open `Data.bar` file
- Find `greek.godpowers` - it's encoded as XMB, so it will be `greek.godpowers.XMB` (**Notice how it doesn't have `.xml` extension**)
- This is a unique case, unlike with `proto.xml.XMB` where we just exported and converted it and it worked - with this case, it only works as XMB
- For now we just export it while converting it to XML as usual, so we can edit it
- We adjust the converted XML version:
  - Find the "Curse" god power definition
  - You can see that `Hero`, `AbstractTitan`, etc... are excluded from being targeted using the tag `<explicitlyrestrictedattacktargettype>`
  - We can just remove those tags and that's it. We can also change the cost, radius, etc... we can even make it affect our own units.
- Once we are happy with our changes, we need to convert this **XML back to XMB**!!! Our final file has to be `greek.godpowers.XMB` in the export folder
  - You can do this by using the convert XML->XMB tool in the app
 
### Example 4 - UI changes
Want to change a god portrait, unit or tech image? For that you want to look at the `UI...` files:
- Open `UIResources.bar` archive
- If you want to change icon for Hoplite, you can find `hoplite_icon.png` inside the archive
- If you want to change icon for minor god Hera, you can find both `hera_icon.png` and `hera_portrait.png` inside the archive
- Anything you wish to change, just export to folder and replace the image with your own - make sure the resolution is the same to not get any weird behaviour ingame

### Example 5 - Texture changes
Textures are usually in any `...Textures.bar` archive. Atlantean temple textures will be in `ArtAtlanteanTextures.bar`.
- Open `ArtAtlanteanTextures.bar` archive
- You will notice MANY textures for `temple` for different ages, and different usage type (BaseColor, Details, Masks, Masks2, Normals...)
- You will most likely be interested in `_BaseColor` because that contains the actual colors of the texture
- Texture files are DDT files, which are special image files that contain multiple smaller versions of themselves, called mipmaps or mips
- The easiest way to modify them at the moment is to right click and `Replace image and export DDT` - then you select your own image you wish to replace it with
- This will create a new DDT and export it to your export folder

### Additive mods
I haven't tested this yet, but it has been brought to my attention that AoMR may support additive mods just like AoE3.
This means instead of modifying whole files which can break with future game updates, you could modify the base file partially with an additive mod.
This would most likely not work for all files, just certain common ones, just like in AoE3.

### Modding note
I am still not fully familiar with how modding works, all examples I gave above are from me playing around.
I have also encountered some problems trying to create custom anim files, they don't seem to be loaded by the game.
Maybe you have to register them somewhere, unsure yet. I will update this document as I learn more. If you know something I don't, please contact me.

## Screenshots
### Previewing XMB content
![CryBarEditor_1](https://assets.cryshana.me/34Cmg3iPHLA9.png)
### Previewing image
![CryBarEditor_2](https://assets.cryshana.me/g18ndgdKDzLQ.png)
### Previewing DDT image and searching
![CryBarEditor_3](https://assets.cryshana.me/okCtQiWGpAlx.png)
### Exporting selected files
![CryBarEditor_4](https://assets.cryshana.me/pOaZBwQHtRsN.png)

When exporting, you can either `Copy` export it by just copying the data to export directory as is.
Or you can `Convert` it by optionally converting data to readable format (such as DDT->TGA or XMB->XML)
