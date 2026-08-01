$ErrorActionPreference = 'Stop'

$projectRoot = $PSScriptRoot
$version = '2.2.0.0'
$nugetCache = Join-Path $projectRoot '.nuget'
$artifactRoot = Join-Path $projectRoot 'artifacts'
$publishRoot = Join-Path $artifactRoot 'publish'
$packageName = "Cowabunga Custom Artwork_$version"
$packageRoot = Join-Path $artifactRoot $packageName
$archivePath = Join-Path $artifactRoot "Cowabunga.Custom.Artwork_$version.zip"

New-Item -ItemType Directory -Path $nugetCache, $artifactRoot -Force | Out-Null

docker run --rm `
    -v "${projectRoot}:/src" `
    -v "${nugetCache}:/root/.nuget/packages" `
    -w /src `
    mcr.microsoft.com/dotnet/sdk:9.0 `
    dotnet publish /src/src/Jellyfin.Plugin.CustomArtwork/Jellyfin.Plugin.CustomArtwork.csproj `
        -c Release `
        -o /src/artifacts/publish

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $publishRoot 'Jellyfin.Plugin.CustomArtwork.dll') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'packaging\logo.png') -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'packaging\meta.json') -Destination $packageRoot
Compress-Archive -LiteralPath $packageRoot -DestinationPath $archivePath -CompressionLevel Optimal -Force

$hash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
$catalogHash = Get-FileHash -LiteralPath $archivePath -Algorithm MD5
Write-Host "Built: $archivePath"
Write-Host "SHA-256: $($hash.Hash)"
Write-Host "Jellyfin catalog checksum (MD5): $($catalogHash.Hash.ToLowerInvariant())"
