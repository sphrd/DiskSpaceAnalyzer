# Builds a demo folder tree of SPARSE files (they report large sizes but take almost no disk space), for README
# screenshots that never show a real computer:
#   .\tools\make_demo.ps1 C:\Demo
#   DiskSpaceAnalyzer.exe --cli screenshot <out folder> C:\Demo --demo
#   .\tools\make_demo.ps1 C:\Demo -Remove
param([Parameter(Mandatory)] [string] $Root, [switch] $Remove)
$ErrorActionPreference = 'Stop'

if ($Remove) { if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }; return }

Add-Type @'
using System;
using System.IO;
using System.Runtime.InteropServices;
public static class Sparse
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int ret, IntPtr ov);

    // Creates a file of the given size without allocating it; the first bytes make same-size files differ unless asked.
    public static void Make(string path, long size, int seed, DateTime modified)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
        {
            int ret;
            DeviceIoControl(fs.SafeFileHandle.DangerousGetHandle(), 0x900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out ret, IntPtr.Zero);   // FSCTL_SET_SPARSE
            var head = BitConverter.GetBytes(seed);
            fs.Write(head, 0, size < head.Length ? (int)Math.Max(0, size) : head.Length);
            fs.SetLength(size);
        }
        File.SetLastWriteTime(path, modified);
    }
}
'@

$GB = 1GB; $MB = 1MB
$rnd = [Random]::new(7)
$seed = 1
function F([string] $rel, [long] $size, [int] $monthsAgo = 1, [int] $same = 0) {
    $s = $same; if ($s -eq 0) { $s = $script:seed; $script:seed += 1 }
    [Sparse]::Make((Join-Path $Root $rel), $size, $s, (Get-Date).AddMonths(-$monthsAgo).AddDays(-$rnd.Next(0, 27)))
}
function Many([string] $dir, [string] $pattern, [int] $count, [long] $min, [long] $max, [int] $monthsAgo = 6) {
    for ($i = 1; $i -le $count; $i++) { F (Join-Path $dir ($pattern -f $i)) ([long]($min + $rnd.NextDouble() * ($max - $min))) ($monthsAgo + $rnd.Next(0, 12)) }
}

if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }

# Videos
Many 'Videos\Family 2024' 'clip_{0:000}.mp4' 18 (900 * $MB) (6 * $GB) 8
Many 'Videos\Trip to Iceland' 'GX01{0:0000}.MP4' 22 (1 * $GB) (4 * $GB) 14
Many 'Videos\Projects\Wedding edit\Render' 'render_v{0}.mov' 6 (8 * $GB) (14 * $GB) 3
F 'Videos\Projects\Wedding edit\project.prproj' (180 * $MB) 2
Many 'Videos\Screen recordings' 'recording-{0:00}.mkv' 30 (120 * $MB) (900 * $MB) 4
# Games
F 'Games\SteamLibrary\steamapps\common\Starfield Odyssey\data\content.pak' (62 * $GB) 5
F 'Games\SteamLibrary\steamapps\common\Starfield Odyssey\data\textures.pak' (21 * $GB) 5
F 'Games\SteamLibrary\steamapps\common\Kart Racer 3\assets.bin' (38 * $GB) 9
F 'Games\SteamLibrary\steamapps\common\Puzzle Isles\game.dat' (6 * $GB) 20
Many 'Games\SteamLibrary\steamapps\shadercache' 'cache_{0:000}.bin' 40 (20 * $MB) (180 * $MB) 2
# Virtual machines
F 'Virtual machines\Ubuntu 24.04\ubuntu.vhdx' (64 * $GB) 1
F 'Virtual machines\Windows 11 test\win11.vhdx' (41 * $GB) 7
F 'Virtual machines\ISO\ubuntu-24.04-desktop-amd64.iso' (6 * $GB) 18
# Photos
Many 'Photos\RAW 2023' 'IMG_{0:0000}.CR3' 220 (24 * $MB) (38 * $MB) 20
Many 'Photos\RAW 2024' 'IMG_{0:0000}.CR3' 260 (24 * $MB) (38 * $MB) 8
Many 'Photos\Exports' 'export_{0:000}.jpg' 300 (3 * $MB) (9 * $MB) 6
# Music
Many 'Music\Library\Albums' 'track_{0:000}.flac' 420 (18 * $MB) (60 * $MB) 30
# Projects
Many 'Projects\web-shop\node_modules\.cache' 'pack_{0:00}.pack' 25 (20 * $MB) (90 * $MB) 2
Many 'Projects\web-shop\node_modules\typescript\lib' 'lib_{0:00}.js' 30 (1 * $MB) (9 * $MB) 2
Many 'Projects\ml-model\data' 'part-{0:0000}.parquet' 60 (180 * $MB) (420 * $MB) 5
F 'Projects\ml-model\checkpoints\model-final.pt' (7 * $GB) 3
F 'Projects\ml-model\checkpoints\model-epoch-40.pt' (7 * $GB) 3
F 'Projects\ml-model\checkpoints\model-epoch-30.pt' (7 * $GB) 4
Many 'Projects\app\build\intermediates' 'obj_{0:000}.o' 200 (2 * $MB) (12 * $MB) 1
# Downloads: old installers and a few duplicates of files kept elsewhere
F 'Downloads\VisualStudioSetup.exe' (4 * $MB) 26
F 'Downloads\GameLauncherInstaller.exe' (180 * $MB) 15
F 'Downloads\ubuntu-24.04-desktop-amd64.iso' (6 * $GB) 18 -same 9001
F 'Virtual machines\ISO\ubuntu-24.04-desktop-amd64 (copy).iso' (6 * $GB) 18 -same 9001
F 'Downloads\family-trip.zip' (2.4 * $GB) 13 -same 9002
F 'Backups\Old laptop\family-trip.zip' (2.4 * $GB) 30 -same 9002
F 'Downloads\Tax 2024.pdf' (3 * $MB) 7 -same 9003
F 'Documents\Taxes\Tax 2024.pdf' (3 * $MB) 7 -same 9003
Many 'Downloads' 'driver-pack-{0}.zip' 5 (300 * $MB) (1.5 * $GB) 20
# Backups and documents
F 'Backups\Old laptop\full-image-2022.img' (96 * $GB) 40
F 'Backups\Phone\backup-2024-11.tar' (54 * $GB) 11
Many 'Documents\Work' 'report_{0:00}.docx' 80 (200 * 1KB) (6 * $MB) 10
Many 'Documents\Scans' 'scan_{0:000}.pdf' 150 (400 * 1KB) (4 * $MB) 24
"Demo tree under $Root"
