"""Package the existing Windows build, demo, guide and converter with NSIS."""

import argparse
from pathlib import Path
import subprocess
from tempfile import TemporaryDirectory

ROOT = Path(__file__).resolve().parents[1]
BUILD = ROOT / "Builds/Windows"
DIST = ROOT / "Dist"
UNINSTALL_KEY = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\QuantitativeEcology.LidarWalk"


def nsis_path(path):
    return str(path).replace("$", "$$")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("makensis", type=Path)
    args = parser.parse_args()
    player = BUILD / "Player"
    files = [(BUILD / "LidarWalk.exe", Path("LidarWalk.exe"))]
    files.extend((file, file.relative_to(BUILD)) for file in player.glob("*.dll"))
    for name in ("LidarWalk.exe", "UnityCrashHandler64.exe"):
        files.append((player / name, Path("Player") / name))
    for folder in ("D3D12", "LidarWalk_Data", "MonoBleedingEdge"):
        for file in (player / folder).rglob("*"):
            if file.is_file() and file.suffix.lower() != ".ply":
                files.append((file, file.relative_to(BUILD)))
    for name in ("export_to_unity.R", "las_io.R", "slim_collision.R", "ptd_ground.R", "tile_scene.R", "install_dependencies.R"):
        files.append((ROOT / name, Path("Exporter") / name))
    files.append((DIST / "INSTALL.md", Path("INSTALL.md")))
    if any(not source.is_file() for source, _ in files):
        raise SystemExit("Build the Windows viewer and prepare INSTALL.md before packaging.")
    manifest = Path("Player/LidarWalk_Data/StreamingAssets/LidarScene/manifest.json")
    if not any(target == manifest for _, target in files):
        raise SystemExit("The demo scene is missing from the Windows build.")
    lines = [
        'Unicode true',
        'VIProductVersion 0.1.0.0',
        'VIAddVersionKey "ProductName" "LiDAR Walk"',
        'VIAddVersionKey "FileDescription" "LiDAR Walk Windows Setup"',
        'VIAddVersionKey "FileVersion" "0.1.0.0"',
        'VIAddVersionKey "ProductVersion" "0.1"',
        'Name "LiDAR Walk"',
        f'OutFile "{nsis_path(DIST / "LiDAR-Walk-Setup.exe")}"',
        r'InstallDir "$LOCALAPPDATA\LiDAR Walk"',
        f'InstallDirRegKey HKCU "{UNINSTALL_KEY}" "InstallLocation"',
        'RequestExecutionLevel user',
        'SetCompressor /SOLID lzma',
        '!include "x64.nsh"',
        '!include "MUI2.nsh"',
        '!insertmacro MUI_PAGE_WELCOME',
        '!insertmacro MUI_PAGE_DIRECTORY',
        '!insertmacro MUI_PAGE_INSTFILES',
        '!insertmacro MUI_PAGE_FINISH',
        '!insertmacro MUI_UNPAGE_CONFIRM',
        '!insertmacro MUI_UNPAGE_INSTFILES',
        '!insertmacro MUI_LANGUAGE "English"',
        'Function .onInit',
        '  ${IfNot} ${RunningX64}',
        '    MessageBox MB_OK|MB_ICONSTOP "LiDAR Walk requires 64-bit Windows."',
        '    Abort',
        '  ${EndIf}',
        'FunctionEnd',
        'Section "LiDAR Walk"',
        '  SetShellVarContext current',
    ]
    folders = set()
    previous = None
    for source, target in sorted(files, key=lambda entry: str(entry[1])):
        parent = target.parent
        if parent != previous:
            output = "$INSTDIR" if parent == Path(".") else "$INSTDIR\\" + nsis_path(parent)
            lines.append(f'  SetOutPath "{output}"')
            previous = parent
        lines.append(f'  File "{nsis_path(source)}"')
        folders.update(folder for folder in target.parents if folder != Path("."))
    lines.extend([
        '  SetOutPath "$INSTDIR"',
        r'  WriteUninstaller "$INSTDIR\Uninstall.exe"',
        r'  CreateShortcut "$SMPROGRAMS\LiDAR Walk.lnk" "$INSTDIR\LidarWalk.exe"',
        f'  WriteRegStr HKCU "{UNINSTALL_KEY}" "DisplayName" "LiDAR Walk"',
        f'  WriteRegStr HKCU "{UNINSTALL_KEY}" "DisplayVersion" "0.1"',
        f'  WriteRegStr HKCU "{UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"',
        f'  WriteRegStr HKCU "{UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\\LidarWalk.exe"',
        f'  WriteRegStr HKCU "{UNINSTALL_KEY}" "UninstallString" \'"$INSTDIR\\Uninstall.exe"\'',
        f'  WriteRegDWORD HKCU "{UNINSTALL_KEY}" "NoModify" 1',
        f'  WriteRegDWORD HKCU "{UNINSTALL_KEY}" "NoRepair" 1',
        'SectionEnd',
        'Section "Uninstall"',
        '  SetShellVarContext current',
        r'  Delete "$SMPROGRAMS\LiDAR Walk.lnk"',
    ])
    lines.extend(f'  Delete "$INSTDIR\\{nsis_path(target)}"' for _, target in files)
    lines.extend(f'  RMDir "$INSTDIR\\{nsis_path(folder)}"'
                 for folder in sorted(folders, key=lambda path: (-len(path.parts), str(path))))
    lines.extend([
        r'  Delete "$INSTDIR\Uninstall.exe"',
        '  RMDir "$INSTDIR"',
        f'  DeleteRegKey HKCU "{UNINSTALL_KEY}"',
        'SectionEnd',
    ])
    with TemporaryDirectory(prefix="lidar-walk-installer-") as directory:
        script = Path(directory) / "lidar-walk-setup.nsi"
        script.write_text("\n".join(lines) + "\n", encoding="utf-8-sig")
        subprocess.run([str(args.makensis.resolve()), "/V2", str(script)], check=True)


if __name__ == "__main__":
    main()
