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
- **Select the export directory** (usually your mod folder `C:\Users\adamv\Games\Age of Mythology Retold\[YOUR_ID]\mods\local\[MOD_NAME]`)
- Files in root directory will appear in 1st column, **select any file to preview it**
  - if selected file is .BAR, it will immediately load in 2nd column
  - you can select entries from inside .BAR file too, they will be previewed the same
- Any file/BAR entry you wish to modify, **right click and export it**, for most cases you will use `Export X selected (convert)`
  - Relative paths are retained! This also works for exporting from Root directory as long as it contains `\game` in it's path
   
## Planned features
Currently planned:
- Converting DDT -> TGA where user can specify custom params (right now we only have conversion of existing DDT file with copied params)
- Creating BAR archive from selected files
- Creating modified BAR archive by only replacing selected entries in existing BAR archive
- (Support for other relevant formats, such as .TMA, .TMM and .DATA)

## Note
This is a personal project made in span of few days. I work on it in my free time, don't expect much.
Format support is focused on Age of Mythology Retold, so it may not work for other Age titles.

## Screenshots
### Previewing XMB content
![CryBarEditor_rCP7YoQNoY](https://github.com/user-attachments/assets/1af49461-0d1d-41ab-a82e-4f70dbdb58f7)
### Previewing image
![CryBarEditor_MqdkEqNUit](https://github.com/user-attachments/assets/da732a48-cc53-4911-9ed1-5ab6b4ef23e8)
### Searching in everything
![CryBarEditor_D6fNnVhNxQ](https://github.com/user-attachments/assets/9725302b-0eb2-4710-bcfe-bf5593411c0c)
### Exporting selected files
![CryBarEditor_NhvzYFEvOX](https://github.com/user-attachments/assets/e8deca7e-f652-4d41-b388-e20e870ccd9e)
Copy = exports them as they are without doing anything else

Convert = optionally decompresses and then converts to readable file (like XMB -> XML or DDT -> TGA)

Copy+Convert = will do both and export 2 files, meant for editing and having the original nearby

