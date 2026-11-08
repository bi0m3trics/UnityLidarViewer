# Revision history

## v0.1.2 · Viewer improvements

- Start in RGB when available with a 0.7 px radius, neutral contrast, zero emission and bloom, and subtle depth shading.
- Add Alt + wheel point sizing and free flight to the V camera cycle.
- Set streaming defaults to 30 m full detail, 90 m view distance, a 1,024 MB cache, and a four-million-point draw budget.

- Tile and stream the full scan by default. Use `--no-streaming` for an untiled export or set a point cap to sample.
- Hide performance counters at startup. Enable them in Settings.

## v0.1.1 · Product rename

- Renamed the app, launcher, installer, and documentation to Unity LiDAR Viewer.

## v0.1 · First packaged release

- **Export and coordinate setup.** Built a LAS and LAZ converter with Unity-local coordinates, terrain from an aligned DEM, solid collision geometry, and optional PLY output.
- **Walkthrough prototype.** Added first- and third-person movement, camera switching, jumping, respawn, and terrain collision. Refined trunk collision into tight vertical proxies so trees still block the player while gaps remain walkable.
- **Point styling.** Added RGB and scalar field selection, palette controls, point radius, emission, intensity contrast, and MK Glow Lite bloom. Added the QuantitativeEcology watermark and a settings popup opened with Esc.
- **Ground estimation and R port.** Added progressive TIN densification for LAZ-only conversion, then moved the conversion pipeline from Python to R. Preserved the option to supply an elevation GeoTIFF.
- **Large scan rendering.** Added spatial tiling, background reads, distance-based detail, bounded GPU uploads, and runtime draw and cache controls. Preserved compatibility with older scene exports.
- **Startup and character.** Added a gradual point reveal with a progress bar. Retargeted the clothed Adventure Character to the Unity controllers, added a hiking backpack, and tuned avatar height and lighting. Made first person the default and corrected input pairing so mouse look works without switching views.
- **Distribution.** Added Windows setup, a BLK360 forest demo, a bundled R exporter, launch flags including `--help`, install and use instructions, and viewer screenshots.

These milestones describe the development that went into v0.1. Future entries can be added here by release tag.
