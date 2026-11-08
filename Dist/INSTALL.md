# Unity LiDAR Viewer for Windows

Unity LiDAR Viewer is a first- and third-person viewer for exploring LiDAR scans. In third person you play as Adventure Character, wearing a dark orange jacket, khaki pants, and a dark gray backpack. The included forest scene has solid terrain and tight trunk collision. You can color the points by RGB or an exported LiDAR field, choose a palette, and adjust point radius and bloom.

## Install and run

If you installed v0.1, uninstall it from Windows Installed apps before installing v0.1.1. Keep any scene exports in their own folders.

1. Run **Unity-LiDAR-Viewer-Setup.exe** on 64-bit Windows 10 or 11 with a DirectX 11 or 12 graphics device.
2. Follow the setup prompts. The default destination is **%LOCALAPPDATA%\Unity LiDAR Viewer** for the current user.
3. Launch **Unity LiDAR Viewer** from the Start menu. The included forest scene opens in first person. Press **V** to switch to third person.

The installer includes the Unity runtime, MK Glow Lite, controllers, and demo scene. Viewing an exported scene requires only the installed app and scene folder. To uninstall, use **Settings > Apps > Installed apps > Unity LiDAR Viewer**.

From the folder containing the viewer, run `.\UnityLidarViewer.exe --help` in PowerShell to see its launch options.

## Controls

| Control | Action |
| --- | --- |
| WASD / Shift / Space | Move / sprint / jump |
| Mouse | Look around |
| Esc | Open / close the settings popup and release / capture the mouse |
| V / R | Switch perspective / respawn |
| Field dropdown / F | Select / cycle through exported fields |
| Palette dropdown / P | Select / cycle through palettes |
| G / T / C | Toggle ground points / terrain surface / collision preview |

The settings popup has a dark, nearly opaque background. The optional performance overlay shows FPS, drawn points, point-cache memory and pending loads. For streaming scenes, use the settings sliders to change the visible point budget, cache size, full-detail distance and viewing distance.

At startup, a progress bar stays centered while the viewer prepares the scene and reveals the point cloud. Press **Esc** during the reveal to skip it. Mouse look becomes available when the opening view is ready.

The field menu shows the values present in the dataset, including RGB where available, intensity, classification, and scalar extra fields such as TreeID. HAG (from DEM) is calculated by the exporter. RGB uses the original point colors. The palette menu applies to scalar fields and includes Viridis, Magma, Plasma, Inferno, Cividis, Blue to red, High contrast, Turbo, Coolwarm, and Grayscale.

## Convert and open your own LAZ

Install 64-bit R 4.4 or later to run the bundled converter. Make `Rscript` available on PATH, or use its full path such as `C:\Program Files\R\R-4.6.0\bin\Rscript.exe`. Open PowerShell and run the following commands with your own input and output paths. Use a new output folder outside the installation directory.

```powershell
$app = Join-Path $env:LOCALAPPDATA 'Unity LiDAR Viewer'
Rscript "$app\Exporter\install_dependencies.R"
Rscript "$app\Exporter\export_to_unity.R" "D:\Scans\site.laz" --out "D:\Exports\site" --streaming
& "$app\UnityLidarViewer.exe" --scene "D:\Exports\site"
```

If you chose a custom installation directory, set `$app` to that location. Dependency installation needs an internet connection and stores the packages beside the exporter in `.r-library`. The converter uses rlas, terra, data.table, jsonlite, geometry, and RANN. Keep the complete exported folder together when moving it to another computer. Older exports still load with the fields they contain. Re-export from the LAZ to populate the full field menu.

LAZ conversion temporarily decompresses the complete source to a LAS file on the output drive, then reads it in chunks of 250,000 points. Allow free space for that temporary LAS plus the export. The temporary file is removed when conversion finishes or reports an error. An interrupted R process can leave a `lidar-r-*` temporary folder beside the output, which you can remove after the process has stopped.

With a LAZ alone, the converter uses grid-thinned progressive TIN densification to estimate the ground and saves it as `generated-dem.tif`. Check the terrain in the viewer with **T**, especially where the ground is hidden by canopy or buildings. This method needs ground returns and can leave gaps or misestimate the surface when coverage is poor. The LAZ should use projected or local Cartesian coordinates, and `--scale 0.3048` converts feet to metres.

To use your own aligned elevation GeoTIFF, pass it as the second positional argument.

```powershell
Rscript "$app\Exporter\export_to_unity.R" "D:\Scans\site.laz" "D:\Scans\site_dem.tif" --out "D:\Exports\site-with-dem" --streaming
```

Use `--help` for the PTD cell size, seed spacing, distance, and angle settings.

## Large point clouds

Add `--streaming` to export spatial tiles. The viewer loads visible tiles in the background, reduces detail with distance and spreads GPU uploads across frames. Defaults are a three-million-point draw budget, a 256 MB point cache, full detail within 20 m and a 300 m viewing distance. These limits apply to points. Terrain, collision geometry and the Unity runtime use additional memory.

The export still defaults to a two-million-point sample. Raise `--max-points` to retain more source detail. To retain every point, set it to the source point count or a higher value. Preparation time and disk use increase with that limit. For example, this keeps up to 50 million display points.

```powershell
Rscript "$app\Exporter\export_to_unity.R" "D:\Scans\site.laz" --out "D:\Exports\site-streaming" --max-points 50000000 --streaming
& "$app\UnityLidarViewer.exe" --scene "D:\Exports\site-streaming"
```

You can tile an existing export with field sidecars, using a new output folder. This preserves its current point sample, normalized fields, terrain and collisions.

```powershell
Rscript "$app\Exporter\tile_scene.R" "D:\Exports\site" --out "D:\Exports\site-streaming"
& "$app\UnityLidarViewer.exe" --scene "D:\Exports\site-streaming"
```

If **Point budget** or **View distance** is missing in Esc settings, check `version` in the scene's `manifest.json`. Use the tiling command above to convert a version 1 export with field sidecars, then open the new version 2 folder.

Streaming scenes use manifest version 2 and require this updated viewer. Older version 1 scenes continue to load through the original renderer. Palette changes are immediate. Changing the selected field loads matching values in the background, so tiles can briefly disappear during the switch. Lower detail levels use a stable shuffled sample and keep every field aligned with its point.
