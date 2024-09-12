<div align="center">
    <a href="https://github.com/CryShana/CryBarEditor"><img src="https://assets.cryshana.me/KaDz0e4q7ubO.png" /></a>
</div>

<div align="center">
<b>CryBarEditor</b> - AoM:Retold resource manager / BAR editor / modding tool
</div>

## Description
The purpose of this tool is to enable fast and easy modding of Age of Mythology Retold.


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
- Support for easily making additive mods with right clicking on supported files
- Converting DDT -> TGA where user can specify custom params (right now we only have conversion of existing DDT file with copied params)
- Creating BAR archive from selected files
- Creating modified BAR archive by only replacing selected entries in existing BAR archive
- (Support for other relevant formats, such as .TMA, .TMM and .DATA)

## Modding Basics
Please read [Modding Basics](Documentation/Modding.md) document to get started.

I describe the **Standard** and **Additive** modding method.
You should always prefer the Additive method if possible

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
