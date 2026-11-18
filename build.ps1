# Compilation reproductible (PowerShell, Windows 10, SDK .NET + NSIS + CMake + VS Build Tools installés)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot; $dist = "$root\dist"; New-Item -ItemType Directory -Force $dist\app\tools, $dist\softcam\x64, $dist\softcam\x86 | Out-Null
dotnet publish "$root\src\VirtuStream.App\VirtuStream.App.csproj" -c Release -o "$dist\app"
# 1) Softcam (MIT) : git clone https://github.com/tshino/softcam, puis compiler x64 et x86 avec CMake
#    -> copier softcam.dll dans dist\softcam\x64 et dist\softcam\x86
# 2) FFmpeg (build « essentials » de gyan.dev) -> copier ffmpeg.exe et ffprobe.exe dans dist\app\tools
# 3) (optionnel) VB-Cable : VBCABLE_Setup_x64.exe dans dist\app\vbcable (licence donationware, voir vb-audio.com)
Copy-Item "$root\tools\camtest.html" "$dist\app\tools\" -Force
makensis "$root\installer\VirtuStream.nsi"
Write-Host "Installateur : $dist\VirtuStream-Setup.exe"
