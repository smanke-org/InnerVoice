# Builds the Microsoft Store bundle (InnerVoice.msixbundle) from the published x64 and ARM64
# folders. Runs on Windows (needs makeappx.exe from the Windows SDK, preinstalled on GitHub's
# Windows runners). The bundle is unsigned: the Store signs it after upload.
#   pwsh packaging/pack.ps1 -X64 artifacts/win-x64 -Arm64 artifacts/win-arm64 -Out artifacts/msix
param(
  [Parameter(Mandatory)] [string] $X64,
  [Parameter(Mandatory)] [string] $Arm64,
  [Parameter(Mandatory)] [string] $Out
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$store = Get-Content (Join-Path $here 'store.json') | ConvertFrom-Json
[xml]$props = Get-Content (Join-Path $here '..\Directory.Build.props')
$version = ($props.Project.PropertyGroup.Version | Select-Object -First 1) + '.0'
$makeappx = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\makeappx.exe' | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $makeappx) { throw 'makeappx.exe not found; install the Windows SDK.' }

New-Item -ItemType Directory -Force -Path $Out | Out-Null
$packages = Join-Path $Out 'packages'
New-Item -ItemType Directory -Force -Path $packages | Out-Null

foreach ($pair in @(@('x64', $X64), @('arm64', $Arm64))) {
  $arch = $pair[0]; $src = $pair[1]
  $stage = Join-Path $Out "stage-$arch"
  if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
  Copy-Item -Recurse $src $stage
  # The Store package gets the Visual C++ runtime from VCLibs, not app-local copies.
  Remove-Item -ErrorAction SilentlyContinue (Join-Path $stage 'msvcp140.dll'), (Join-Path $stage 'vcruntime140.dll'), (Join-Path $stage 'vcruntime140_1.dll')
  Remove-Item -ErrorAction SilentlyContinue (Join-Path $stage '*.pdb')
  Copy-Item -Recurse (Join-Path $here 'Assets') (Join-Path $stage 'Assets') -Force
  $manifest = Get-Content (Join-Path $here 'AppxManifest.xml') -Raw
  $manifest = $manifest.Replace('{{IdentityName}}', $store.IdentityName).Replace('{{Publisher}}', $store.Publisher).
    Replace('{{PublisherDisplayName}}', $store.PublisherDisplayName).Replace('{{Version}}', $version).Replace('{{Architecture}}', $arch)
  Set-Content -Path (Join-Path $stage 'AppxManifest.xml') -Value $manifest -Encoding UTF8
  & $makeappx.FullName pack /o /d $stage /p (Join-Path $packages "InnerVoice_${version}_$arch.msix")
  if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed for $arch" }
}
& $makeappx.FullName bundle /o /d $packages /p (Join-Path $Out "InnerVoice_$version.msixbundle")
if ($LASTEXITCODE -ne 0) { throw 'makeappx bundle failed' }
Write-Host "Built InnerVoice_$version.msixbundle"
