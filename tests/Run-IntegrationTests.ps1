[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$manager = Join-Path $project 'dist\VlcPatchManager.exe'
$fixture = Join-Path $PSScriptRoot 'fixture'
$catalog = Join-Path $fixture 'catalog.json'
$package = Join-Path $project 'patches\vlc-torrent-plugin-vlc-3.0.23-x64-preview.zip'
$patchId = 'native-torrent-vlc-3.0.23-x64'
$target = Join-Path $fixture 'plugins\access\libtorrent_plugin.dll'
$state = Join-Path $fixture "vlc-patch-manager\patches\$patchId"

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Invoke-Manager([string[]]$Arguments, [int]$ExpectedExitCode) {
    $fullArguments = @($Arguments) + @('--catalog-file', $catalog)
    $process = Start-Process -FilePath $manager -ArgumentList $fullArguments -Wait -PassThru
    Assert-True ($process.ExitCode -eq $ExpectedExitCode) "Expected exit code $ExpectedExitCode, got $($process.ExitCode): $Arguments"
}

function Reset-Fixture {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
    New-Item -ItemType Directory -Path (Join-Path $fixture 'plugins\access') -Force | Out-Null
    & $compiler /nologo /target:exe /platform:x64 /out:"$fixture\vlc.exe" "$PSScriptRoot\MockVlc.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Mock VLC compilation failed.' }
    & $compiler /nologo /target:exe /platform:x64 /out:"$fixture\vlc-cache-gen.exe" "$PSScriptRoot\MockCacheGen.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Mock cache generator compilation failed.' }
    $packageUri = ([Uri]$package).AbsoluteUri
    $packageHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
    $document = @{
        schemaVersion = 1
        updatedUtc = '2026-07-19T00:00:00Z'
        patches = @(@{
            id = $patchId
            name = 'Native Torrent Playback'
            description = 'Integration test patch.'
            patchVersion = '0.1.0-preview.1'
            vlcVersion = '3.0.23.0'
            architecture = 'x64'
            downloadUrl = $packageUri
            packageSha256 = $packageHash
            payloadEntry = 'payload/libtorrent_plugin.dll'
            relativeTarget = 'plugins/access/libtorrent_plugin.dll'
            cacheMarker = 'libtorrent_plugin.dll'
        })
    }
    $document | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $catalog -Encoding UTF8
}

& (Join-Path $project 'Build.ps1') | Out-Null

# Clean install and modified-file protection.
Reset-Fixture
Invoke-Manager @('--install', $patchId, '--vlc', "$fixture\vlc.exe") 0
Assert-True (Test-Path -LiteralPath $target) 'Install did not create the plugin.'
Assert-True (Test-Path -LiteralPath (Join-Path $state 'state.xml')) 'Install did not create state.'
Assert-True (([Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes((Join-Path $fixture 'plugins\plugins.dat')))).Contains('libtorrent_plugin.dll')) 'Plugin cache does not contain the torrent plugin.'
[IO.File]::AppendAllText($target, 'modified')
Invoke-Manager @('--remove', $patchId, '--vlc', "$fixture\vlc.exe") 1
Assert-True (Test-Path -LiteralPath $target) 'Protected removal deleted a modified plugin.'
Invoke-Manager @('--remove', $patchId, '--vlc', "$fixture\vlc.exe", '--force') 0
Assert-True (-not (Test-Path -LiteralPath $target)) 'Forced removal left the plugin behind.'
Assert-True (-not (Test-Path -LiteralPath $state)) 'Forced removal left patch state behind.'

# Existing-file collision must be restored byte-for-byte.
Reset-Fixture
[IO.File]::WriteAllText($target, 'original fixture plugin')
$originalHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
Invoke-Manager @('--install', $patchId, '--vlc', "$fixture\vlc.exe") 0
Invoke-Manager @('--remove', $patchId, '--vlc', "$fixture\vlc.exe") 0
Assert-True ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $originalHash) 'Removal did not restore the original file.'

# A missing original backup must not delete the active patch.
Reset-Fixture
[IO.File]::WriteAllText($target, 'original fixture plugin')
Invoke-Manager @('--install', $patchId, '--vlc', "$fixture\vlc.exe") 0
$installedHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
Remove-Item -LiteralPath (Join-Path $state 'backup\libtorrent_plugin.dll') -Force
Invoke-Manager @('--remove', $patchId, '--vlc', "$fixture\vlc.exe") 1
Assert-True ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $installedHash) 'Missing-backup removal changed the active plugin.'
Assert-True (Test-Path -LiteralPath (Join-Path $state 'state.xml')) 'Missing-backup removal deleted patch state.'

# Failed cache generation during removal must restore the installed state.
Reset-Fixture
Invoke-Manager @('--install', $patchId, '--vlc', "$fixture\vlc.exe") 0
$installedHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
[IO.File]::WriteAllText((Join-Path $fixture 'fail-cache'), 'fail')
Invoke-Manager @('--remove', $patchId, '--vlc', "$fixture\vlc.exe") 1
Assert-True ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $installedHash) 'Failed removal did not restore the plugin.'
Assert-True (Test-Path -LiteralPath (Join-Path $state 'state.xml')) 'Failed removal deleted patch state.'
Remove-Item -LiteralPath (Join-Path $fixture 'fail-cache') -Force
Invoke-Manager @('--remove', $patchId, '--vlc', "$fixture\vlc.exe") 0

# Failed cache generation must roll installation back.
Reset-Fixture
[IO.File]::WriteAllText((Join-Path $fixture 'fail-cache'), 'fail')
Invoke-Manager @('--install', $patchId, '--vlc', "$fixture\vlc.exe") 1
Assert-True (-not (Test-Path -LiteralPath $target)) 'Failed install left its plugin behind.'
Assert-True (-not (Test-Path -LiteralPath $state)) 'Failed install left patch state behind.'

# A package with the wrong catalog hash must never be extracted or installed.
Reset-Fixture
$catalogText = [IO.File]::ReadAllText($catalog)
$packageHash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
[IO.File]::WriteAllText($catalog, $catalogText.Replace($packageHash, ('0' * 64)))
Invoke-Manager @('--install', $patchId, '--vlc', "$fixture\vlc.exe") 1
Assert-True (-not (Test-Path -LiteralPath $target)) 'Hash verification failure installed a plugin.'
Assert-True (-not (Test-Path -LiteralPath $state)) 'Hash verification failure left patch state behind.'

Write-Output 'All VLC Patch Manager integration tests passed.'
