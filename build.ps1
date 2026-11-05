# build.ps1 -- build Islet without .NET SDK / MSBuild.
# This machine has no dotnet SDK, so we drive the .NET Framework 4.8 csc.exe directly.
# No XAML compiler is available, therefore the whole UI is built in C# code.
#
# Usage (from repo root):
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\islet\build.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\islet\build.ps1 -Test
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\islet\build.ps1 -SelfTest
#
# Output goes to <repo>\build (not islet\build) to avoid that folder's permission issues.

param(
    [switch]$Test,
    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$out  = Join-Path (Split-Path -Parent $root) 'build'
$csc  = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$gac  = 'C:\Windows\Microsoft.NET\assembly'

if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

New-Item -ItemType Directory -Force -Path $out | Out-Null

$wpfRefs = @(
    "$gac\GAC_MSIL\PresentationFramework\v4.0_4.0.0.0__31bf3856ad364e35\PresentationFramework.dll",
    "$gac\GAC_64\PresentationCore\v4.0_4.0.0.0__31bf3856ad364e35\PresentationCore.dll",
    "$gac\GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll",
    "$gac\GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll"
)
foreach ($r in $wpfRefs) {
    if (-not (Test-Path $r)) { throw "missing reference assembly: $r" }
}

$winRt = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Runtime.WindowsRuntime.dll'
if (-not (Test-Path $winRt)) { throw "missing WinRT bridge: $winRt" }

function Get-Sources([string]$dir) {
    $p = Join-Path $root $dir
    if (-not (Test-Path $p)) { return @() }
    return @(Get-ChildItem $p -Filter *.cs -Recurse | Select-Object -ExpandProperty FullName)
}

# ---- 1. Islet.Core: pure logic, no UI dependency ----
$coreDll  = Join-Path $out 'Islet.Core.dll'
$coreArgs = @('/nologo', '/target:library', "/out:$coreDll", '/langversion:5', '/optimize+',
              '/r:System.dll', '/r:System.Core.dll') + (Get-Sources 'src\Islet.Core')
& $csc $coreArgs
if ($LASTEXITCODE -ne 0) { throw 'Core build failed' }
Write-Host '[OK] Islet.Core.dll'

# ---- 2. Unit tests ----
$testExe = Join-Path $out 'Islet.Core.Tests.exe'
$testSrc = Get-Sources 'tests\Islet.Core.Tests'
if ($testSrc.Count -gt 0) {
    $testArgs = @('/nologo', '/target:exe', "/out:$testExe", '/langversion:5', '/optimize+',
                  '/r:System.dll', '/r:System.Core.dll', "/r:$coreDll") + $testSrc
    & $csc $testArgs
    if ($LASTEXITCODE -ne 0) { throw 'test build failed' }
    Write-Host '[OK] Islet.Core.Tests.exe'
}

# ---- 3. Islet.App: WPF shell + providers ----
$appSrc = @()
$appSrc += Get-Sources 'src\Islet.App'
$appSrc += Get-Sources 'src\Islet.Sources'
if ($appSrc.Count -eq 0) { throw 'no application sources found' }

function New-AppArgs([string]$exe, [string]$target) {
    $a = @('/nologo', "/target:$target", "/out:$exe", '/langversion:5', '/optimize+')
    $mf = Join-Path $root 'src\Islet.App\app.manifest'
    if (Test-Path $mf) { $a += "/win32manifest:$mf" }

    # 应用图标：嵌进 exe，资源管理器 / 任务栏 / 快捷方式都读这一份。
    # ICO 由 tools\IcoGen 生成（8 个尺寸，含 256px PNG 压缩帧）。
    #
    # 这里**不能**用 $root / $script:root：实测在函数内取到的是 $null，
    # Test-Path 会直接抛 "Cannot bind argument ... because it is null"。
    # $PSScriptRoot 在函数里也指向调用者的目录，不可靠；
    # 所以路径在这里用已知的脚本位置重算一遍。
    $here = Split-Path -Parent $MyInvocation.MyCommand.Path
    if ([string]::IsNullOrEmpty($here)) { $here = $PSScriptRoot }
    if ([string]::IsNullOrEmpty($here)) { $here = $root }
    $iconPath = Join-Path $here 'src\Islet.App\Resources\islet.ico'
    if ($iconPath -and (Test-Path -LiteralPath $iconPath)) {
        $a += "/win32icon:$iconPath"
    }

    foreach ($r in $wpfRefs) { $a += "/r:$r" }
    $a += "/r:$coreDll", "/r:$winRt"
    $a += '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll',
          '/r:System.Windows.Forms.dll'
    $a += $appSrc
    return $a
}

$appExe = Join-Path $out 'Islet.exe'
& $csc (New-AppArgs $appExe 'winexe')
if ($LASTEXITCODE -ne 0) { throw 'App build failed' }
Write-Host '[OK] Islet.exe'

# 运行时配置：关并发 GC、压工作集。必须与 exe 同名同目录才生效
$cfgSrc = Join-Path $root 'src\Islet.App\Islet.exe.config'
if (Test-Path $cfgSrc) {
    Copy-Item $cfgSrc (Join-Path $out 'Islet.exe.config') -Force
    Write-Host '[OK] Islet.exe.config'
}

Write-Host ''
Write-Host "Build complete -> $out"
Get-ChildItem $out -Include *.exe, *.dll -File |
    Select-Object Name, @{ n = 'KB'; e = { [math]::Round($_.Length / 1KB, 1) } } |
    Format-Table -AutoSize | Out-String | Write-Host

if ($Test) {
    Write-Host 'Running unit tests...'
    & $testExe
    exit $LASTEXITCODE
}

if ($SelfTest) {
    Write-Host 'Capturing render snapshots...'
    $sd  = Join-Path $out 'selftest'
    $dbg = Join-Path $out 'IsletDbg.exe'
    # console target so startup exceptions surface
    & $csc (New-AppArgs $dbg 'exe')
    if ($LASTEXITCODE -ne 0) { throw 'debug build failed' }
    & $dbg --selftest $sd
    Write-Host "Snapshots -> $sd"
    Get-ChildItem $sd -Filter *.png -ErrorAction SilentlyContinue |
        Select-Object Name, @{ n = 'KB'; e = { [math]::Round($_.Length / 1KB, 1) } } |
        Format-Table -AutoSize | Out-String | Write-Host
}
