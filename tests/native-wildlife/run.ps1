param(
    [string]$GamePath = 'E:\MVL\Release\1.22.7',
    [string]$ModpackPath = 'C:\Users\29596\AppData\Roaming\MVL\Modpack\测试服务器\Mods',
    [switch]$Vanilla,
    [string]$SeedWorldPath = '',
    [int]$Port = 42441
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$nativeGame = [IO.Path]::GetFullPath($GamePath)
if (!(Test-Path -LiteralPath (Join-Path $nativeGame 'VintagestoryServer.exe'))) { throw 'Native server executable not found.' }
if (!(Test-Path -LiteralPath $ModpackPath -PathType Container)) { throw 'Modpack directory not found.' }
dotnet build (Join-Path $repo 'SeraphCivilizationSimulation.csproj') -c Release "-p:VINTAGE_STORY=$nativeGame"
if ($LASTEXITCODE -ne 0) { throw 'Production build failed.' }

# Each run creates a fresh world under artifacts. Never load the live server save.
$runPath = Join-Path $repo ('artifacts\wildlife-native-' + $(if ($Vanilla) { 'vanilla-' } else { 'modpack-' }) + [DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
$modsPath = Join-Path $runPath 'Mods'
$productionPath = Join-Path $modsPath 'vsscsmod'
$harnessPath = Join-Path $modsPath 'wildlifesmoke'
New-Item -ItemType Directory -Path $productionPath, $harnessPath -Force | Out-Null
if ($SeedWorldPath) {
    $seedWorld = [IO.Path]::GetFullPath($SeedWorldPath)
    $artifactRoot = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts')) + [IO.Path]::DirectorySeparatorChar
    if (!$seedWorld.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Only disposable worlds under this repository artifacts directory may seed a regression run.'
    }
    New-Item -ItemType Directory -Path (Join-Path $runPath 'Saves') -Force | Out-Null
    Copy-Item -LiteralPath $seedWorld -Destination (Join-Path $runPath 'Saves\wildlife-regression.vcdbs')
}
foreach ($file in Get-ChildItem -LiteralPath $ModpackPath) {
    if ($file.PSIsContainer) {
        if (!$Vanilla) { Copy-Item -LiteralPath $file.FullName -Destination $modsPath -Recurse }
        continue
    }
    if ($file.Extension -ne '.zip') {
        if (!$Vanilla) { Copy-Item -LiteralPath $file.FullName -Destination $modsPath }
        continue
    }
    $archive = [IO.Compression.ZipFile]::OpenRead($file.FullName)
    try {
        $metadata = $archive.GetEntry('modinfo.json')
        if (!$metadata) { throw "Package lacks root modinfo.json: $($file.Name)" }
        $reader = [IO.StreamReader]::new($metadata.Open())
        try { $info = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($info.modid -eq 'vscncivilizationsimulation') { continue }
        if (!$Vanilla -and $info.modid -ne 'vsscsmod') {
            Copy-Item -LiteralPath $file.FullName -Destination $modsPath
        }
    } finally { $archive.Dispose() }
}
$output = Join-Path $repo 'bin\Release\Mods\vsscsmod\net10.0'
foreach ($name in @('vsscsmod.dll', 'Markdig.dll', 'AngleSharp.dll')) {
    Copy-Item -LiteralPath (Join-Path $output $name) -Destination $productionPath
}
Copy-Item -LiteralPath (Join-Path $repo 'modinfo.json') -Destination $productionPath
Copy-Item -LiteralPath (Join-Path $repo 'assets') -Destination $productionPath -Recurse
dotnet build (Join-Path $PSScriptRoot 'WildlifeSmoke.csproj') -c Release "-p:VINTAGE_STORY=$nativeGame" -o $harnessPath
if ($LASTEXITCODE -ne 0) { throw 'Native harness build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'modinfo.json') -Destination $harnessPath

$config = @{
    Ip = '127.0.0.1'; Port = $Port; AdvertiseServer = $false; Upnp = $false
    VerifyPlayerAuth = $false; MaxClients = 1; PassTimeWhenEmpty = $true
    MapSizeX = 1024000; MapSizeY = 256; MapSizeZ = 1024000
    DefaultRoleCode = 'suplayer'
    Roles = @(@{ Code = 'suplayer'; Name = 'Survival Player'; PrivilegeLevel = 0; DefaultGameMode = 1; Privileges = @('chat', 'build', 'useblock', 'attackcreatures') })
    ModPaths = @((Join-Path $nativeGame 'Mods'), $modsPath)
    WorldConfig = @{
        SaveFileLocation = (Join-Path $runPath 'Saves\wildlife-regression.vcdbs')
        WorldName = 'Isolated native wildlife regression'; WorldType = 'standard'
        PlayStyle = 'surviveandbuild'; AllowCreativeMode = $true
        Seed = '415768'; WorldConfiguration = @{}
    }
}
$config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runPath 'serverconfig.json') -Encoding utf8
$console = Join-Path $runPath 'console.txt'
$stderr = Join-Path $runPath 'stderr.txt'
$server = Start-Process -FilePath (Join-Path $nativeGame 'VintagestoryServer.exe') -ArgumentList @('--dataPath', ('"{0}"' -f $runPath)) -WindowStyle Hidden -PassThru -RedirectStandardOutput $console -RedirectStandardError $stderr
Write-Host "Native regression running in $runPath (PID $($server.Id))"
try {
    $deadline = [DateTime]::UtcNow.AddMinutes(10)
    while (!$server.WaitForExit(1000)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw "Isolated native regression timed out. Logs: $console" }
    }
} finally {
    if (!$server.HasExited) { Stop-Process -Id $server.Id }
}
$log = Get-Content -LiteralPath $console -Raw
$log -split '\r?\n' | Where-Object { $_ -match 'WILDLIFE (CASE RESULT|SMOKE RESULT)' } | Write-Host
if ($log -notmatch 'WILDLIFE SMOKE RESULT: PASS' -or $log -match 'WILDLIFE SMOKE RESULT: FAIL') {
    throw "Native wildlife regression failed. Inspect $console and $stderr"
}
Write-Host "Native regression passed. Evidence: $console"
