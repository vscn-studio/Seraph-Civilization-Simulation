param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$GamePath = 'E:\vintagestory\Vintagestory',
    [string]$DeployPath = 'C:\Users\29596\AppData\Roaming\MVL\Modpack\测试服务器\Mods',
    [switch]$NoDeploy
)
$ErrorActionPreference = 'Stop'
dotnet build (Join-Path $PSScriptRoot 'SeraphCivilizationSimulation.csproj') -c $Configuration "-p:VINTAGE_STORY=$GamePath"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$output = Join-Path $PSScriptRoot "bin\$Configuration\Mods\vsscsmod\net10.0"
$version = (Get-Content (Join-Path $PSScriptRoot 'modinfo.json') -Raw | ConvertFrom-Json).version
$artifactDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts'))
New-Item -ItemType Directory -Force $artifactDirectory | Out-Null
$stage = Join-Path $artifactDirectory ('package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $stage | Out-Null
try {
    foreach ($file in @('vsscsmod.dll', 'Markdig.dll', 'AngleSharp.dll')) {
        Copy-Item -LiteralPath (Join-Path $output $file) -Destination $stage
    }
    foreach ($file in @('modinfo.json', 'LICENSE', 'THIRD-PARTY-NOTICES.txt')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $stage
    }
    foreach ($directory in @('assets')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $directory) -Destination $stage -Recurse
    }
    $temporary = Join-Path $artifactDirectory ('package-' + [Guid]::NewGuid().ToString('N') + '.zip')
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $temporary
    Move-Item -LiteralPath $temporary -Destination (Join-Path $artifactDirectory "vsscsmod_$version.zip") -Force
} finally {
    if (-not [IO.Path]::GetFullPath($stage).StartsWith($artifactDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid staging path.' }
    Remove-Item -LiteralPath $stage -Recurse -Force
}
Write-Host "Created $(Join-Path $artifactDirectory "vsscsmod_$version.zip")"
$package = Join-Path $artifactDirectory "vsscsmod_$version.zip"
if ($NoDeploy) { return }
if (Get-Process -Name 'Vintagestory*' -ErrorAction SilentlyContinue) {
    throw 'Exit Vintage Story and its server completely before deploying. Build with -NoDeploy while the game is running.'
}
New-Item -ItemType Directory -Force -Path $DeployPath | Out-Null
$deployed = Join-Path $DeployPath "vsscsmod_$version.zip"
$oldPackages = @(Get-ChildItem -LiteralPath $DeployPath -Filter '*.zip' -File | Where-Object {
    $zip = [IO.Compression.ZipFile]::OpenRead($_.FullName)
    try {
        $manifest = $zip.GetEntry('modinfo.json')
        if ($manifest) {
            $reader = [IO.StreamReader]::new($manifest.Open())
            try { ($reader.ReadToEnd() | ConvertFrom-Json).modid -eq 'vsscsmod' }
            finally { $reader.Dispose() }
        }
    } finally { $zip.Dispose() }
})
if ((Test-Path -LiteralPath $deployed) -and $deployed -notin $oldPackages.FullName) { throw 'Target filename belongs to another mod.' }
$backup = Join-Path $artifactDirectory ('deploy-backups/' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss-ffff'))
foreach ($old in $oldPackages) {
    New-Item -ItemType Directory -Force -Path $backup | Out-Null
    $saved = Join-Path $backup $old.Name
    Copy-Item -LiteralPath $old.FullName -Destination $saved
    if ((Get-FileHash -LiteralPath $saved).Hash -ne (Get-FileHash -LiteralPath $old.FullName).Hash) { throw 'Backup hash mismatch.' }
}
Copy-Item -LiteralPath $package -Destination $deployed -Force
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $deployed -Algorithm SHA256).Hash) {
    throw 'Deployed package hash mismatch.'
}
foreach ($old in $oldPackages) {
    if (-not $old.FullName.Equals([IO.Path]::GetFullPath($deployed), [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $old.FullName }
}
Write-Host "Deployed and verified $deployed"
if ($oldPackages.Count) { Write-Host "Backup: $backup" }
