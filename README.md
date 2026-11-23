## Kitsune HDiff Patcher

A lightweight file update tool powered by binary diff technology, featuring file verification and incremental patching.


## Compile Instructions

1. Install [.NET 11.0 SDK](https://dotnet.microsoft.com/download/dotnet/11.0)  
2. Run `Compile.bat`  
3. Output will be in `bin` folder.


## How to use

1. Place `Kitsune.exe` in the same folder as the game executable and update archives.
   For example:

```
├── StarRail_Data/
├── config.ini
├── GameAssembly.dll
├── HoYoKProtect.sys
├── mhypbase.dll
├── pkg_version
├── StarRail.exe
├── audio_en-us_4.3.0_4.4.0_hdiff_AvVBHumYjUCykkeT.7z
├── game_4.3.0_4.4.0_hdiff_DfvdjuyyZStZvKfL.7z
├── Kitsune.exe
```

```
├── ZenlessZoneZero_Data/
├── amd_ags_x64.dll
├── amd_fidelityfx_dx12.dll
├── Audio_English(US)_pkg_version
├── config.ini
├── file_category_launcher
├── GameAssembly.dll
├── HoYoKProtect.sys
├── ......
├── audio_en-us_3.2.0_3.3.0_hdiff_QpOlvHcpYcYnLQOw.zip
├── game_3.2.0_3.3.0_hdiff_htoMnlEfFUcPOmag.zip
├── UnityPlayer.dll
├── version_info
├── ZenlessZoneZero.exe
├── Kitsune.exe
```

```
├── GenshinImpact_Data/
├── Audio_English(US)_pkg_version
├── config.ini
├── GenshinImpact.exe
├── HoYoKProtect.sys
├── mhypbase.dll
├── pkg_version
├── audio_en-us_5.4.0_5.5.0_hdiff_HveRbpmNrNejbGYL.zip
├── game_5.4.0_5.5.0_hdiff_IlvHovyEdpXnwiCH.zip
├── Kitsune.exe
```

2. Run `Kitsune.exe`.

3. Kitsune will:
   - Extract the required update data.
   - Apply binary diff patches.
   - Verify the resulting files after the update process.

4. Once the process is completed, your game files are updated.

- Overview of the update process:

    ```
    Extract required update data
            |
            v
    Banks0.pck (59.5 MB)        // current file
            +
    Banks0.pck.hdiff (3.0 MB)   // incremental update data
            |
            v
    Apply binary diff
            |
            v
    Banks0.pck (62.5 MB)        // updated file
            |
            v
    Verify updated files
    ```


### Command-line reference

| Command | Description |
|---|---|
| `Kitsune.exe --verify` or `-v` | Verify files only; do not apply patches. |
| `Kitsune.exe --no-pause` | Apply updates without waiting for a key at exit. |
| `Kitsune.exe --help` or `-h` | Display available options and usage information. |

With no arguments, Kitsune extracts update archives in the current directory, applies available HDiff patches, and verifies the updated files. If no patch inputs are found but a supported package manifest is present, it automatically runs file verification only.


## Disclaimer

This project is developed and maintained independently as open-source software. It is not affiliated with any game developer, publisher, or official launcher. The software is intended exclusively for educational, research, archival, testing, and personal asset management purposes.


## Credits

- [7-Zip](https://www.7-zip.org)
- [HDiffPatch](https://github.com/sisong/HDiffPatch)
