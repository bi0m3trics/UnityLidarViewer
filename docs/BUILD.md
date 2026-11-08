# Build LiDAR Walk

Use Unity **6000.4.11f1** with Windows Build Support. Open `UnityProject` through Unity Hub. The Unity Package Manager restores URP, Cinemachine, and Input System from the project manifest.

## Prepare dependencies

Import the three required Asset Store packages into the Unity project.

| Package | Expected asset folder |
| --- | --- |
| [MK Glow Lite](https://assetstore.unity.com/packages/vfx/shaders/fullscreen-camera-effects/mk-glow-lite-155643), tested with 4.9.7 | `Assets/MK/MKGlowLite` |
| [First Person + Third Person](https://assetstore.unity.com/packages/3d/characters/first-person-third-person-character-controllers-196526) | `Assets/Starter Assets/Runtime` |
| [Adventure Character](https://assetstore.unity.com/packages/3d/characters/humanoids/humans/adventure-character-201384) | `Assets/Adventure_Character` |

Generate the local jacket and pants textures after importing Adventure Character. Run the commands from the repository root.

```powershell
python -m pip install Pillow
python tools/recolor_adventure.py
```

## Prepare a scene

Use your own LAS or LAZ, or the local `BLK360_ctrl.laz` example if you have it. Install the R dependencies and export into the Unity project’s StreamingAssets folder. Use a fresh output folder when regenerating data.

```powershell
Rscript install_dependencies.R
Rscript export_to_unity.R .\BLK360_ctrl.laz --field intensity --out .\UnityProject\Assets\StreamingAssets\LidarScene
```

In Unity, choose **LiDAR → Create Walkthrough Scene** and open `Assets/Scenes/LidarWalk.unity`. Press Play to preview it. Choose **LiDAR → Build Windows Viewer** to write the Windows launcher and player to `Builds/Windows`. Keep the `Player` folder beside the launcher.

To package the build with NSIS on Windows, run the following from the repository root. The setup program is written to `Dist/LiDAR-Walk-Setup.exe`.

```powershell
python tools/package_windows.py "C:\Program Files (x86)\NSIS\makensis.exe"
```

The editable vendor assets, generated Unity content, scan files, and compiled binaries are local build inputs or outputs. Git tracks the project scripts, shaders, settings, documentation, and rendered screenshots.
