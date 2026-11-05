# package.ps1 -- 把浮屿打成可分发的单文件安装程序。
#
# 产物：build\dist\Islet-Setup-<版本>.exe
#
# 为什么不用 IExpress（旧方案）：
#   它的产物是「解压 + 弹一个黑框 cmd」，用户看到的是满屏英文命令刷过、
#   最后等 timeout；安全软件也常把自解压包判成可疑。
#   现在改成一个正经的 WPF 安装程序，Islet 的文件以**资源**嵌在 exe 里，
#   分发出去就一个文件，双击出图形界面。
#
# 两步编译（本机没有 .NET SDK / MSBuild，只有 csc.exe）：
#   1. 把 Islet 的产物做成 .resources 资源文件；
#   2. 用 /resource: 把它嵌进安装程序，编译出 Setup.exe。
#
# 用法（仓库根目录）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\islet\package.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\islet\package.ps1 -SelfTest

param(
    [string]$Version = '1.0.0',
    [string]$Channel = 'stable',
    [switch]$SelfTest,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$root   = Split-Path -Parent $MyInvocation.MyCommand.Definition
$repo   = Split-Path -Parent $root
$build  = Join-Path $repo 'build'
$dist   = Join-Path $build 'dist'
$setup  = Join-Path $root 'src\Islet.Setup'
$csc    = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$gac    = 'C:\Windows\Microsoft.NET\assembly'

if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

# ---------------------------------------------------------------
# 1. 先确保 Islet 本身是最新的
# ---------------------------------------------------------------
Write-Host '=== 1. Build Islet ==='
if ($SkipBuild) {
    Write-Host '[SKIP] -SkipBuild 指定，使用现有产物'
} else {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Islet build failed' }
}

$payload = @('Islet.exe', 'Islet.Core.dll', 'Islet.exe.config', 'islet.ico')
foreach ($f in $payload) {
    $p = Join-Path $build $f
    if (-not (Test-Path $p)) { throw "missing build artifact: $f -- run build.ps1 first" }
}
Write-Host ('[OK] payload: ' + ($payload -join ', '))

New-Item -ItemType Directory -Force -Path $dist | Out-Null

# ---------------------------------------------------------------
# 2. 把载荷打成 .resources
#
# 资源名必须与 Installer.PreparePayload 里的 "Islet.Setup.payload.<文件名>" 对应。
# 直接用 .NET 的 ResourceWriter，不需要额外编译一个中间工具。
# （PowerShell 的 here-string 里含 C# 代码容易被当脚本解析，绕开这个坑。）
# ---------------------------------------------------------------
Write-Host ''
Write-Host '=== 2. Pack payload into .resources ==='

$resPath = Join-Path $build 'payload.resources'
if (Test-Path $resPath) { Remove-Item $resPath -Force }

$writer = $null
try {
    $writer = New-Object System.Resources.ResourceWriter $resPath
    foreach ($f in $payload) {
        $p = Join-Path $build $f
        if (-not (Test-Path $p)) { throw "missing payload file: $p" }
        $bytes = [System.IO.File]::ReadAllBytes($p)
        # 名字带 "Islet.Setup.payload." 前缀，与解包代码对齐
        $writer.AddResource("Islet.Setup.payload.$f", $bytes)
    }
    $count = $payload.Count
}
finally {
    if ($writer) { $writer.Close() }
}
Write-Host "[OK] $resPath ($count resources)"

# ---------------------------------------------------------------
# 3. 编译安装程序
# ---------------------------------------------------------------
Write-Host ''
Write-Host '=== 3. Compile installer ==='

$wpfRefs = @(
    "$gac\GAC_MSIL\PresentationFramework\v4.0_4.0.0.0__31bf3856ad364e35\PresentationFramework.dll",
    "$gac\GAC_64\PresentationCore\v4.0_4.0.0.0__31bf3856ad364e35\PresentationCore.dll",
    "$gac\GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll",
    "$gac\GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll"
)
foreach ($r in $wpfRefs) {
    if (-not (Test-Path $r)) { throw "missing reference assembly: $r" }
}

$setupSrc = @(Get-ChildItem $setup -Filter *.cs -Recurse | Select-Object -ExpandProperty FullName)
if ($setupSrc.Count -eq 0) { throw 'no installer sources found' }

$fileName = "Islet-Setup-$Version.exe"
$targetExe = Join-Path $dist $fileName
if (Test-Path $targetExe) { Remove-Item $targetExe -Force }

$icon = Join-Path $root 'src\Islet.App\Resources\islet.ico'
$manifest = Join-Path $setup 'setup.manifest'

$cscArgs = @('/nologo', '/target:winexe', "/out:$targetExe", '/langversion:5', '/optimize+')
if (Test-Path $manifest) { $cscArgs += "/win32manifest:$manifest" }
if (Test-Path $icon)     { $cscArgs += "/win32icon:$icon" }
foreach ($r in $wpfRefs) { $cscArgs += "/r:$r" }
$cscArgs += '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll',
         '/r:System.Windows.Forms.dll'
# 载荷资源
$cscArgs += "/resource:$resPath,Islet.Setup.payload.resources"
$cscArgs += $setupSrc

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw 'installer compile failed' }

if (-not (Test-Path $targetExe)) { throw 'installer exe not produced' }

$size = [math]::Round((Get-Item $targetExe).Length / 1KB, 1)
Write-Host "[OK] $targetExe ($size KB)"

# ---------------------------------------------------------------
# 4. 校验：资源真的嵌进去了吗
#
# 只信 exit code 是不够的 —— 编译成功但资源没进包，安装时会报「缺少文件」。
# 这里用反射把资源名读出来核对。
# 注意：ReflectionOnlyLoadFrom 对「含嵌入资源的 exe」在部分环境会失败，
# 失败不算致命（下面自检会真的跑一遍安装程序），只作为提前预警。
# ---------------------------------------------------------------
Write-Host ''
Write-Host '=== 4. Verify embedded resources ==='
try {
    $asm = [System.Reflection.Assembly]::ReflectionOnlyLoadFrom($targetExe)
    $names = @($asm.GetManifestResourceNames())
    Write-Host ('resources in exe: ' + ($names -join ', '))
    $hit = $names | Where-Object { $_ -like '*payload*' }
    if ($hit) { Write-Host '[OK] payload embedded' }
    else { Write-Warning 'payload resource NOT found -- check pack step' }
} catch {
    Write-Warning ("resource check skipped: " + $_.Exception.Message)
}

# ---------------------------------------------------------------
# 5. 自检（可选）
# ---------------------------------------------------------------
if ($SelfTest) {
    Write-Host ''
    Write-Host '=== 5. Installer self-test ==='
    $sd = Join-Path $build 'setuptest'
    & $targetExe --selftest $sd
    Write-Host "Result -> $sd"
    $txt = Join-Path $sd 'setuptest.txt'
    if (Test-Path $txt) { Get-Content $txt -Encoding UTF8 | Write-Host }
}

# ---------------------------------------------------------------
# 6. 一并生成「免安装版」
#
# 有人不想装，只想拿一个文件夹跑。这不算额外维护成本：
# 同样四个文件压一个 zip 就行，但需要说明它不带卸载/快捷方式。
# ---------------------------------------------------------------
Write-Host ''
Write-Host '=== 6. Portable zip ==='
$portableDir = Join-Path $build 'portable\Islet'
if (Test-Path (Join-Path $build 'portable')) {
    Remove-Item (Join-Path $build 'portable') -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $portableDir | Out-Null
foreach ($f in $payload) { Copy-Item (Join-Path $build $f) $portableDir -Force }

$readme = @"
浮屿 Islet $Version · 免安装版

直接双击 Islet.exe 即可运行，不会写入注册表、不创建快捷方式、不开机自启。
配置文件与日志会写在 %LOCALAPPDATA%\Islet\ 下。

想彻底清除：退出程序后，删掉这个文件夹和 %LOCALAPPDATA%\Islet\ 即可。

推荐用安装版（Islet-Setup-$Version.exe）：它会创建快捷方式、
登记到"应用和功能"里，卸载有入口。
"@
Set-Content -Path (Join-Path $portableDir '使用说明.txt') -Value $readme -Encoding UTF8

$zipPath = Join-Path $dist "Islet-Portable-$Version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    (Join-Path $build 'portable'), $zipPath)
$zsize = [math]::Round((Get-Item $zipPath).Length / 1KB, 1)
Write-Host "[OK] $zipPath ($zsize KB)"

# ---------------------------------------------------------------
# 7. 校验和 + 分发说明
# ---------------------------------------------------------------
Write-Host ''
Write-Host '=== 7. Checksums ==='
$hashes = @()
foreach ($f in @($fileName, "Islet-Portable-$Version.zip")) {
    $p = Join-Path $dist $f
    if (-not (Test-Path $p)) { continue }
    $h = (Get-FileHash $p -Algorithm SHA256).Hash
    $hashes += [pscustomobject]@{ File = $f; SizeKB = [math]::Round((Get-Item $p).Length/1KB,1); SHA256 = $h }
}
$hashes | Format-Table -AutoSize | Out-String | Write-Host

$sumPath = Join-Path $dist 'SHA256SUMS.txt'
$lines = foreach ($h in $hashes) { "$($h.SHA256)  $($h.File)" }
Set-Content -Path $sumPath -Value $lines -Encoding ASCII
Write-Host "[OK] $sumPath"

$note = @"
浮屿 Islet $Version ($Channel)
构建时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')

分发内容
  $fileName          —— 安装版：双击 → 图形界面 → 安装并启动
  Islet-Portable-$Version.zip —— 免安装版：解压即用
  SHA256SUMS.txt                    —— 文件校验和

安装版做了什么
  · 复制程序到 %LOCALAPPDATA%\Programs\Islet（按用户安装，不需要管理员权限）
  · 创建桌面 / 开始菜单快捷方式（可勾选取消）
  · 在「设置 → 应用 → 已安装的应用」里登记卸载入口
  · 可选开机自启（写在 HKCU，随时可在浮屿设置里关掉）

不会做什么
  · 不改系统设置、不动防火墙、不装服务、不写 HKLM
  · 不收集任何数据，不联网上报
  · 卸载时配置与日志默认保留，会单独询问

运行要求
  · Windows 10 1809 及以上 / Windows 11
  · .NET Framework 4.8（Win10 1903 起系统自带；更老的系统装的时候会提示）
  · 液态玻璃的亚克力背板需要 Windows 11 22H2+，老系统上会自动降级为普通浅色窗口

给接收方的一句话
  下载 $fileName，双击，点「安装」。它会出现在屏幕右上角的托盘区（
  右键托盘图标可进设置）。热键 Ctrl+Alt+I 显示/隐藏。
"@
$notePath = Join-Path $dist 'README.txt'
Set-Content -Path $notePath -Value $note -Encoding UTF8
Write-Host "[OK] $notePath"

# 清理中间产物（.resources 已嵌进 exe，留着只会让 build 目录更乱）
if (Test-Path $resPath) { Remove-Item $resPath -Force }

Write-Host ''
Write-Host '=== Done ===' -ForegroundColor Green
Get-ChildItem $dist | Select-Object Name,
    @{ n = 'KB'; e = { [math]::Round($_.Length / 1KB, 1) } } |
    Format-Table -AutoSize | Out-String | Write-Host
Write-Host "分发目录：$dist"
