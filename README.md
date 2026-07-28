## Kitsune HDiff Patcher

A lightweight file update tool powered by binary diff technology, featuring file verification and incremental patching.


## Compile Instructions

1. Install [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)  
2. Run `Compile.bat`  
3. Output will be in `bin` folder.


### How to use

1. Place `Kitsune.exe` in the same folder as the game executable.
   For example

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
├── audio_en-us_2.8.0_3.0.0_hdiff_IGmmwJHVnkQfcVau.zip
├── game_2.8.0_3.0.0_hdiff_BvCYYtlnQuZZQaFw.zip
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


## Disclaimer

This project is developed and maintained independently as open-source software.
It is not affiliated with any game developer, publisher, or official launcher.
The software is intended exclusively for educational, research, archival, testing, and personal asset management purposes.


## Credits

- [7-Zip](https://www.7-zip.org)
- [HDiffPatch](https://github.com/sisong/HDiffPatch)
