param(
    [string]$RepositoryUrl = 'https://github.com/FuryFighter/Baklava'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$versionSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Properties/AssemblyInfo.cs') -Raw
$version = [regex]::Match($versionSource, 'AssemblyVersion\("([^"]+)"\)').Groups[1].Value
if (-not $version) { throw 'AssemblyVersion was not found' }
$artifacts = Join-Path $projectRoot 'artifacts'
$publish = Join-Path $projectRoot ('.build/publish-' + [guid]::NewGuid().ToString('N'))
$stage = Join-Path $projectRoot ('.build/package-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $artifacts,$stage -Force | Out-Null
dotnet publish (Join-Path $projectRoot 'Baklava.csproj') --configuration Release --output $publish --nologo '-clp:ErrorsOnly'
if ($LASTEXITCODE -ne 0) { throw 'Plugin publish failed' }

# All scripts/styles are embedded. Jellyfin supplies runtime dependencies, including SQLite.
# Only these plugin-owned files may be shipped; never copy the full publish directory.
Copy-Item -LiteralPath (Join-Path $publish 'Baklava.dll') -Destination $stage
Copy-Item -LiteralPath (Join-Path $publish 'thumb.png') -Destination $stage
$metadata = [ordered]@{
    guid = '109470b0-d97c-4540-89b7-856d4e5831c7'
    name = 'Baklava'
    description = 'Media requests and Gelato integration for Jellyfin 12.2'
    overview = 'Jellyfin 12.2 compatibility port of j4ckgrey/Baklava'
    owner = 'FuryFighter; original author j4ckgrey'
    category = 'General'
    version = $version
    targetAbi = '12.2.0.0'
    timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
}
$metadata | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stage 'meta.json') -Encoding utf8
$archiveName = "baklava_$version.zip"
$archive = Join-Path $artifacts $archiveName
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $allowed = @('Baklava.dll', 'thumb.png', 'meta.json')
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName -notin $allowed) { throw "Unexpected packaged file: $($entry.FullName)" }
    }
    if ($zip.Entries.Count -ne 3) { throw 'Plugin package must contain exactly three files' }
} finally { $zip.Dispose() }
$entry = [ordered]@{
    version = $version
    changelog = 'Jellyfin 12.2 / .NET 10 port; current Gelato reflection APIs; authenticated request ownership and admin approval.'
    targetAbi = '12.2.0.0'
    sourceUrl = "$($RepositoryUrl.TrimEnd('/'))/releases/download/v$version/$archiveName"
    checksum = (Get-FileHash -LiteralPath $archive -Algorithm MD5).Hash.ToLowerInvariant()
    checksumType = 'md5'
    timestamp = $metadata.timestamp
}
$manifest = [ordered]@{} + $metadata
$manifest.Remove('version')
$manifest.Remove('targetAbi')
$manifest.Remove('timestamp')
$manifest.versions = @($entry)
ConvertTo-Json -InputObject @($manifest) -Depth 8 | Set-Content -LiteralPath (Join-Path $artifacts 'manifest.json') -Encoding utf8
Get-FileHash -LiteralPath $archive -Algorithm SHA256
Write-Output "Package: $archive"
Write-Output 'artifacts/manifest.json is a release candidate; publish its referenced asset before using it as a plugin repository.'
