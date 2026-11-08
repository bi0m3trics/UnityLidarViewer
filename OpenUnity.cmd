@echo off
setlocal
set "LIDAR_UNITY_EDITOR=C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe"
if not exist "%LIDAR_UNITY_EDITOR%" (
    echo Unity 6000.4.11f1 was not found at the expected location.
    echo Install this version through Unity Hub, then run this launcher again.
    pause
    exit /b 1
)
if not exist "%~dp0Artifacts" mkdir "%~dp0Artifacts"
start "" "%LIDAR_UNITY_EDITOR%" -projectPath "%~dp0UnityProject" -logFile "%~dp0Artifacts\unity-editor.log"
