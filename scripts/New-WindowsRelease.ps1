[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuildVersion,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$RestoreSource,
    [string]$ArtifactsPath
)

$ErrorActionPreference = 'Stop'
$versionPattern = '\A(?<prefix>(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4}))(?:-ci\.[1-9][0-9]*\.[1-9][0-9]*)?\z'
if ($BuildVersion -cnotmatch $versionPattern) {
    throw "Expected X.Y.Z or X.Y.Z-ci.RUN.ATTEMPT, got '$BuildVersion'."
}
$prefix = $Matches.prefix
if ($prefix.Split('.') | Where-Object { [int]$_ -gt 65534 }) {
    throw 'Each numeric version component must be at most 65534.'
}

$repo = Split-Path -Parent $PSScriptRoot
$app = Join-Path $repo 'src/PzServerUi/PzServerUi.csproj'
$name = "PzServerUi-v$BuildVersion-win-x64"
$out = New-Item -ItemType Directory -Force $OutputDirectory
$publish = Join-Path $out.FullName $name
$zip = "$publish.zip"
if ((Test-Path -LiteralPath $publish) -or (Test-Path -LiteralPath $zip)) {
    throw "Choose a fresh output directory: $name already exists in $($out.FullName)."
}

# An explicit Version avoids a default project VersionSuffix leaking into a stable build.
$properties = @(
    "-p:Version=$BuildVersion",
    "-p:VersionPrefix=$prefix",
    '-p:RuntimeIdentifierOverride=win-x64',
    '-p:WindowsPackageType=None',
    '-p:WindowsAppSDKSelfContained=true',
    '-p:SelfContained=true'
)
if ($ArtifactsPath) { $properties += "-p:ArtifactsPath=$ArtifactsPath" }
if ($RestoreSource) { $properties += "-p:RestoreSources=$RestoreSource" }

Push-Location $repo
try {
    $commit = git rev-parse --verify HEAD
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '\A[0-9a-f]{40}\z') { throw 'Cannot resolve source commit.' }
    dotnet publish $app -c Release -o $publish @properties
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
} finally {
    Pop-Location
}

foreach ($filename in 'PzServerUi.exe', 'PzServerUi.dll', 'coreclr.dll', 'Microsoft.UI.Xaml.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $filename) -PathType Leaf)) {
        throw "Incomplete Windows publish: $filename is missing."
    }
}
$info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'PzServerUi.dll'))
if ($info.ProductVersion -cne $BuildVersion) {
    throw "Expected DLL product version $BuildVersion, got $($info.ProductVersion)."
}
[ordered]@{ version = $BuildVersion; commit = $commit; runtime = 'win-x64' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publish 'version.json') -Encoding utf8

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip)
Write-Host "Created $zip"
