# OpenVersus Mod Client

This is the source repository of the OpenVersus client-side DLL, a fork based on the publicly-available original work of [thethiny](https://github.com/thethiny) and [multiversuskoth](https://github.com/multiversuskoth). The original repository is [located here](https://github.com/thethiny/MVSIASI/releases).

The source code for the OpenVersus client-side DLL is available here, as well as prebuilt binary releases of the client. Binary releases are also maintained [in this repo](https://github.com/christopher-conley/openversus) for backward-compatibility.

The current release is built with Visual Studio 2022 and 2026, using toolchain version 145. Opening the solution and building is all that's necessary at this point to locally build a version of the client. A more comprehensive `README` will be committed in the near future.


The `README` file of the original source repository is as follows:

---

# MVSI Loader
This will transform MVS game into MVS Infinite

### Current Features:
- Anti Pak Signature Patch. Allows you to run `.pak` files with invalid `.sig` signatures.
- Anti uToc Signature Patch. Allows you to use `uToc` files (and `.uCas`) with invalid Signature Headers
- Custom Server: Allows you to change the server endpoint
- Allows you to bypass game death date
- Supports using the PopUp Dialog

### How to use:
Download the Latest MVSI version, and select the mods you want in the provided ini file. When a game updates, you might need to change the patches in the [Patterns] section. The updated patterns will be provided by me below, and in the downloaded file.
The zip file will contain 3 files. MVSI.asi, MVSI.ini, and dsound.dll. You need to place the files into your game's folder, next to tbb.dll. `MVSFolder/Binaries/Win64/`.

### All Ini Settings
You can find the [ini file](sample.ini) in this repo. It is automatically bundled in the zip file in the release.

## Download
[Download Here](https://github.com/thethiny/MVSIASI/releases)

## Credits
- Ermaccer for the [dll loader](https://github.com/ermaccer/MK1Hook/releases/)
- doubleaa (multiversuskoth) as a colaborator
- The Generic SigCheck bypass for UE5 by unknown
