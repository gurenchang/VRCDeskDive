# VRCDeskDive の SteamVR ドライバーをビルドする。
# MSVC (Visual Studio / Build Tools) があればそれを、無ければ zig を使う。
# 出力: driver\out\vrcdeskdive\ (SteamVR に登録するフォルダ)
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$out = Join-Path $root 'out\vrcdeskdive'
$bin = Join-Path $out 'bin\win64'
$dll = Join-Path $bin 'driver_vrcdeskdive.dll'
$src = Join-Path $root 'src\driver.c'

New-Item -ItemType Directory -Force $bin | Out-Null
Copy-Item (Join-Path $root 'driver.vrdrivermanifest') $out -Force

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = if (Test-Path $vswhere) {
    & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
}

if ($vs) {
    $vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
    $objDir = Join-Path $root 'out\obj'
    New-Item -ItemType Directory -Force $objDir | Out-Null
    cmd /c "`"$vcvars`" >nul && cl /nologo /std:c11 /O2 /W3 /LD `"$src`" /Fo`"$objDir\\`" /Fe`"$dll`" /link kernel32.lib"
    if ($LASTEXITCODE -ne 0) { throw 'MSVC でのビルドに失敗しました' }
}
else {
    $zig = (Get-Command zig -ErrorAction SilentlyContinue).Source
    if (-not $zig) {
        $local = Join-Path $root '..\tools\zig\zig.exe'
        if (Test-Path $local) { $zig = $local }
    }
    if (-not $zig) { throw 'C コンパイラーが見つかりません（Visual Studio Build Tools か zig が必要です）' }
    & $zig cc -target x86_64-windows-gnu -std=c11 -O2 -shared -o $dll $src -lkernel32
    if ($LASTEXITCODE -ne 0) { throw 'zig でのビルドに失敗しました' }
    Remove-Item (Join-Path $bin '*.pdb'), (Join-Path $bin '*.lib') -ErrorAction SilentlyContinue
}

Write-Host "ビルド完了: $dll"
