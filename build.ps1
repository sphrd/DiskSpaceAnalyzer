# Builds DiskSpaceAnalyzer.exe from src\ with the C# compiler that ships with Windows (.NET Framework 4.x),
# then copies it to the repository root. No SDK, NuGet or Visual Studio needed.
param([string]$Out = "$PSScriptRoot\bin\DiskSpaceAnalyzer.exe")
$ErrorActionPreference = 'Stop'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw '.NET Framework 4.x C# compiler (csc.exe) not found.' }

# Artwork is regenerated on every build so it always matches the design language.
& "$PSScriptRoot\tools\make_icon.ps1" | Out-Null
$icon = "$PSScriptRoot\assets\app.ico"

New-Item -ItemType Directory -Force -Path (Split-Path $Out) | Out-Null
$sources = Get-ChildItem "$PSScriptRoot\src\*.cs" | ForEach-Object { $_.FullName }

& $csc /nologo /target:winexe /platform:anycpu /optimize+ /unsafe+ /debug- `
    "/out:$Out" "/win32icon:$icon" "/win32manifest:$PSScriptRoot\src\app.manifest" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    $sources
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }

$rootExe = Join-Path $PSScriptRoot (Split-Path $Out -Leaf)
try { Copy-Item $Out $rootExe -Force }
catch { throw "Could not copy to $rootExe (close the running app and build again): $($_.Exception.Message)" }

Get-Item $Out, $rootExe | Select-Object FullName, Length, LastWriteTime
