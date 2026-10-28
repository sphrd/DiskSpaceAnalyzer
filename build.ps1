# Builds DiskSpaceAnalyzer.exe from src\ with the C# compiler that ships with Windows (.NET Framework 4.x),
# moves it to the repository root and removes the intermediate build folder. No SDK, NuGet or Visual Studio needed.
$ErrorActionPreference = 'Stop'
$name = 'DiskSpaceAnalyzer.exe'
$obj = Join-Path $PSScriptRoot 'bin'
$built = Join-Path $obj $name
$final = Join-Path $PSScriptRoot $name

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw '.NET Framework 4.x C# compiler (csc.exe) not found.' }

# Artwork is regenerated on every build so it always matches the design language.
& "$PSScriptRoot\tools\make_icon.ps1" | Out-Null
$icon = "$PSScriptRoot\assets\app.ico"

New-Item -ItemType Directory -Force -Path $obj | Out-Null
$sources = Get-ChildItem "$PSScriptRoot\src\*.cs" | ForEach-Object { $_.FullName }
try {
    & $csc /nologo /target:winexe /platform:anycpu /optimize+ /unsafe+ /debug- `
        "/out:$built" "/win32icon:$icon" "/win32manifest:$PSScriptRoot\src\app.manifest" `
        /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
        $sources
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
    try { Move-Item $built $final -Force }
    catch { throw "Could not replace $final (close the running app and build again): $($_.Exception.Message)" }
}
finally { Remove-Item $obj -Recurse -Force -ErrorAction SilentlyContinue }

Get-Item $final | Select-Object FullName, Length, LastWriteTime
