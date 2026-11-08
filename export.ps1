[CmdletBinding(DefaultParameterSetName = 'Tiled')]
param(
    [string]$Laz = "$PSScriptRoot/BLK360_ctrl.laz",
    [string]$Dem = '',
    [string]$Out = "$PSScriptRoot/exports/$(Get-Date -Format 'yyyyMMdd-HHmmss')",
    [string]$Field = 'intensity',
    [ValidateRange(1, [long]::MaxValue)]
    [long]$MaxPoints,
    [double]$Voxel = 0.5,
    [Parameter(ParameterSetName = 'Tiled')]
    [switch]$Streaming,
    [Parameter(ParameterSetName = 'NonStreaming')]
    [switch]$NoStreaming,
    [double]$TileSize = 16
)

$exportArguments = @("$PSScriptRoot/export_to_unity.R", $Laz)
if ($Dem) { $exportArguments += $Dem }
$exportArguments += @('--out', $Out, '--field', $Field, '--voxel', $Voxel, '--ply')
if ($PSBoundParameters.ContainsKey('MaxPoints')) { $exportArguments += @('--max-points', $MaxPoints) }
if ($NoStreaming -or ($PSBoundParameters.ContainsKey('Streaming') -and !$Streaming)) {
    $exportArguments += '--no-streaming'
} else {
    $exportArguments += @('--tile-size', $TileSize)
}
& Rscript @exportArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Output "Export ready: $Out"
