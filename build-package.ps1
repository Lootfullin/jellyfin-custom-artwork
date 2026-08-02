[CmdletBinding()]
param(
    [string]$Version = '2.3.5.0',
    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw 'Version must contain four numeric components.'
}

$projectRoot = $PSScriptRoot
$nugetCache = Join-Path $projectRoot '.nuget'
$artifactRoot = Join-Path $projectRoot 'artifacts'
$publishRoot = Join-Path $artifactRoot 'publish'
$packageName = "Cowabunga Custom Artwork_$Version"
$packageRoot = Join-Path $artifactRoot $packageName
$archivePath = Join-Path $artifactRoot "Cowabunga.Custom.Artwork_$Version.zip"

$dotnet = if (-not [string]::IsNullOrWhiteSpace($DotnetPath)) {
    (Resolve-Path -LiteralPath $DotnetPath -ErrorAction Stop).Path
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

New-Item -ItemType Directory -Path $nugetCache, $artifactRoot -Force | Out-Null

& $dotnet test `
    (Join-Path $projectRoot 'tests/Jellyfin.Plugin.CustomArtwork.Tests/Jellyfin.Plugin.CustomArtwork.Tests.csproj') `
    -c Release `
    -p:Version=$Version
if ($LASTEXITCODE -ne 0) {
    throw "dotnet test failed with exit code $LASTEXITCODE."
}

if (Test-Path -LiteralPath $publishRoot) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}

& $dotnet publish `
    (Join-Path $projectRoot 'src/Jellyfin.Plugin.CustomArtwork/Jellyfin.Plugin.CustomArtwork.csproj') `
    -c Release `
    -p:Version=$Version `
    -o $publishRoot
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $publishRoot 'Jellyfin.Plugin.CustomArtwork.dll') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'packaging/logo.png') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'packaging/meta.json') -Destination $packageRoot
Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $archivePath -CompressionLevel Optimal -Force

$publishedDll = Join-Path $packageRoot 'Jellyfin.Plugin.CustomArtwork.dll'
if ((Get-Item -LiteralPath $publishedDll).VersionInfo.FileVersion -ne $Version) {
    throw 'Packaged DLL version does not match the requested version.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$package = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $entries = @($package.Entries | Where-Object { $_.Name } | ForEach-Object FullName)
    $expectedEntries = @('Jellyfin.Plugin.CustomArtwork.dll', 'logo.png', 'meta.json')
    if (@(Compare-Object $entries $expectedEntries).Count -ne 0) {
        throw "Package root is invalid: $($entries -join ', ')."
    }

    $metaEntry = $package.GetEntry('meta.json')
    $reader = [System.IO.StreamReader]::new($metaEntry.Open())
    try {
        $packagedMeta = $reader.ReadToEnd() | ConvertFrom-Json
    } finally {
        $reader.Dispose()
    }

    if ($packagedMeta.autoUpdate -ne $true) {
        throw 'Packaged meta.json must set autoUpdate to true.'
    }
    if ($packagedMeta.version -ne $Version) {
        throw "Packaged version is '$($packagedMeta.version)'."
    }
    if ($packagedMeta.targetAbi -ne '10.11.0.0') {
        throw "Packaged target ABI is '$($packagedMeta.targetAbi)'."
    }
} finally {
    $package.Dispose()
}

$hash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
$catalogHash = Get-FileHash -LiteralPath $archivePath -Algorithm MD5
$sha256Path = "$archivePath.sha256"
"$($hash.Hash.ToLowerInvariant())  $(Split-Path -Leaf $archivePath)" |
    Set-Content -LiteralPath $sha256Path -Encoding ascii
Write-Host "Built: $archivePath"
Write-Host "SHA-256: $sha256Path"
Write-Host "Jellyfin catalog checksum (MD5): $($catalogHash.Hash.ToLowerInvariant())"
