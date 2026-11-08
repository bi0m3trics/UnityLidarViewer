param(
    [string]$Laz = "$PSScriptRoot/BLK360_ctrl.laz",
    [string]$Dem = '',
    [string]$Out = "$PSScriptRoot/exports/$(Get-Date -Format 'yyyyMMdd-HHmmss')",
    [string]$Field = 'intensity',
    [int]$MaxPoints = 2000000,
    [double]$Voxel = 0.5,
    [switch]$Streaming,
    [double]$TileSize = 16
)

$exportArguments = @("$PSScriptRoot/export_to_unity.R", $Laz)
if ($Dem) { $exportArguments += $Dem }
$exportArguments += @('--out', $Out, '--field', $Field, '--max-points', $MaxPoints, '--voxel', $Voxel, '--ply')
if ($Streaming) { $exportArguments += @('--streaming', '--tile-size', $TileSize) }
& Rscript @exportArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Output "Export ready: $Out"
