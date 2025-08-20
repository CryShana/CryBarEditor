<div align="center">
    <a href="https://github.com/CryShana/CryBarEditor"><img src="https://assets.cryshana.me/KaDz0e4q7ubO.png" /></a>
</div>

<div align="center">
<b>CryBarEditor</b> - AoM:Retold resource manager / BAR editor / modding tool
</div>

## Description
The purpose of this tool is to enable fast and easy modding of Age of Mythology Retold.


## Features
### Highlights
- **Read BAR archives**
- **Read FMOD banks** (play and view FMOD events)
- Pick a **Root directory** (usually `\games` folder) for fast switching between BAR and other files
- Pick an **Export root directory** for fast exporting either from BAR or from Root directory
- Supports following formats:
    - BAR (decoding)
    - XMB (decoding + encoding)
    - DDT (decoding + encoding)
- Syntax highlighting for common formats (json,xml,ini,xs,...) and folding support for XML

### Extras
- Replace existing DDT image with custom image on export (params are copied) for easy texture manipulation
- Tools for file manipulation:
  - Convert XML -> XMB
  - Convert XMB -> XML
  - Convert DDT -> TGA
  - Convert image -> DDT
  - Compress with Alz4/L33t
  - Decompress file with Alz4/L33t
- Easily create additive mod for files that support it with right click
- "**Search everything**" tool added that searches text in all files, BAR entries and their contents (for finding references)
- Tool for converting XS trigger scripts to RM-friendly scripts for easy inclusion in random maps
- Remembering last opened directories and files so you can easily resume where you left off

## Usage
- **Load the Root directory** (usually `C:\Program Files (x86)\Steam\steamapps\common\Age of Mythology Retold\game`)
- **Select the export directory** (usually your mod folder `C:\Users\[USER]\Games\Age of Mythology Retold\[YOUR_ID]\mods\local\[MOD_NAME]`)
- Files in root directory will appear in 1st column, **select any file to preview it**
  - if selected file is .BAR, it will immediately load in 2nd column
  - you can select entries from inside .BAR file too, they will be previewed the same
- Any file/BAR entry you wish to modify, **right click and export it**, for most cases you will use `Export X selected (convert)`
  - Relative paths are retained! This also works for exporting from Root directory as long as it contains `\game` in it's path

## Download
For people new to GitHub - the releases are located [here](https://github.com/CryShana/CryBarEditor/releases).

Just download the latest version .zip and extract it into a folder, run the .exe file and that will open the app.

## Planned features
Currently planned:
- Decoding + Encoding TMA and TMM formats
- Exporting FMOD sounds
- More features for additive modding (supporting more files, and generating some template)
- Maybe another side panel for export directory for quickly editing/viewing exported files - all within same ap

## Modding Basics
Please read [Modding Basics](Documentation/Modding.md) document to get started.

I describe the **Standard** and **Additive** modding method.
You should always prefer the Additive method if possible

## XS scripting reference
If you are writing XS scripts, you can also checkout my personal reference I wrote [here](Documentation/XSScriptingReference.md) 

## Screenshots
### Previewing XMB content
![CryBarEditor_1](https://assets.cryshana.me/34Cmg3iPHLA9.png)
### Previewing image
![CryBarEditor_2](https://assets.cryshana.me/g18ndgdKDzLQ.png)
### Previewing DDT image and (old searching)
![CryBarEditor_3](https://assets.cryshana.me/okCtQiWGpAlx.png)
### Exporting selected files
![CryBarEditor_4](https://assets.cryshana.me/pOaZBwQHtRsN.png)
### Creating DDT from selected image
![CryBarEditor_5](https://assets.cryshana.me/RueQpmx0q9L3.png)
### Real-time status if file is overriden (in export directory)
![CryBarEditor_6](https://assets.cryshana.me/HEMu7Ojws84P.png)
### Reading FMOD banks
![CryBarEditor_7](https://assets.cryshana.me/NgsOV5c6VEm8.avif);
### Searching through all files (new searching experience)
![CryBarEditor_8](https://assets.cryshana.me/zFzSNeCHF0YM.avif);

## Thanks
I wish to thank the developers of AoE3 Resource Manager, their code helped me with decoding of XMB and DDT files.
