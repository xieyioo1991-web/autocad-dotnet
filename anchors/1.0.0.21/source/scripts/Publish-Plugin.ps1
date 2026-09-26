param(
    [Parameter(Mandatory = $true)][string]$SourceDll,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-ContentHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha256.ComputeHash($stream)) }
    finally { $sha256.Dispose(); $stream.Dispose() }
}

$sourcePath = (Resolve-Path -LiteralPath $SourceDll).Path
$assembly = [Reflection.AssemblyName]::GetAssemblyName($sourcePath)
$pluginName = $assembly.Name
$version = $assembly.Version
$artifactRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
$artifactRoot = (Resolve-Path -LiteralPath $artifactRoot).Path

# Publish first. An identical rebuild leaves the released DLL untouched, which
# also works when AutoCAD has loaded it. Changed code requires a new version.
foreach ($extension in @('.dll', '.pdb')) {
    $sourceFile = [IO.Path]::ChangeExtension($sourcePath, $extension)
    if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) { continue }
    $destination = Join-Path $artifactRoot ("{0}-{1}{2}" -f $pluginName, $version, $extension)
    if (Test-Path -LiteralPath $destination) {
        $sourceHash = Get-ContentHash $sourceFile
        $destinationHash = Get-ContentHash $destination
        if ($sourceHash -ne $destinationHash) {
            throw "Version $version already exists with different contents. Increase the project Version before publishing."
        }
    }
    else {
        Copy-Item -LiteralPath $sourceFile -Destination $destination
    }
}

$pattern = '^' + [regex]::Escape($pluginName) + '-(?<version>\d+\.\d+\.\d+\.\d+)\.(dll|pdb)$'
$files = @(Get-ChildItem -LiteralPath $artifactRoot -File)
$latestVersion = $version
foreach ($file in $files) {
    if ($file.Name -match $pattern -and $file.Extension -eq '.dll') {
        $fileVersion = [version]$Matches['version']
        if ($fileVersion -gt $latestVersion) { $latestVersion = $fileVersion }
    }
}

# Preserve the old unversioned files under their original names. The DLL gives
# their archive group; do not rename its PDB to imply a verified version match.
$legacyVersion = $version
$legacyDll = Join-Path $artifactRoot ($pluginName + '.dll')
if (Test-Path -LiteralPath $legacyDll -PathType Leaf) {
    $legacyVersion = [Reflection.AssemblyName]::GetAssemblyName($legacyDll).Version
}

$moved = 0
foreach ($file in $files) {
    if ($file.Name -match $pattern) {
        $fileVersion = [version]$Matches['version']
        if ($fileVersion -eq $latestVersion) { continue }
    }
    elseif ($file.Name -eq ($pluginName + '.dll') -or $file.Name -eq ($pluginName + '.pdb')) {
        $fileVersion = $legacyVersion
    }
    else { continue }

    $group = '{0}.{1}.{2}' -f $fileVersion.Major, $fileVersion.Minor, $fileVersion.Build
    $archiveDirectory = Join-Path (Join-Path $artifactRoot '历史版本') $group
    $destination = [IO.Path]::GetFullPath((Join-Path $archiveDirectory $file.Name))
    # Every move is restricted to files within this artifact directory.
    $rootPrefix = $artifactRoot.TrimEnd('\') + '\'
    if (-not $file.FullName.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not $destination.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Archive path escapes the artifact directory: $destination"
    }
    New-Item -ItemType Directory -Path $archiveDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $destination) {
        # Never overwrite a historical file on a repeated release or rollback.
        $suffix = [Guid]::NewGuid().ToString('N').Substring(0, 8)
        $destination = Join-Path $archiveDirectory ($file.BaseName + '-' + $suffix + $file.Extension)
    }
    Move-Item -LiteralPath $file.FullName -Destination $destination
    $moved++
}

Write-Output "Published $pluginName $version; latest $latestVersion; archived $moved file(s)."
