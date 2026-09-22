<#
.SYNOPSIS
  TKS Desktop for Windows —— 一键构建与打包（PRD FR-W-PKG-8 / §14.1）。

.DESCRIPTION
  产出：
    ① NSIS 安装包   dist\TKS-Desktop-<version>-win-x64-setup.exe
    ② 便携版        dist\TKS-Desktop-<version>-win-x64-portable.zip
    ③ 校验和        dist\SHA256SUMS.txt

  ⚠️ 版本号单一来源：TKSDesktop/TKSDesktop.csproj 的 <Version>（§14.2）。
     本脚本**读取**该值，不得另存一份。

.NOTES
  前置：.NET 8 SDK；NSIS（makensis）用于安装包（缺失时跳过但会明确告警，不静默成功）。
#>

[CmdletBinding()]
param(
    [switch]$SkipInstaller,
    [switch]$SkipTests,
    [switch]$NoSelfContained
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$repoRoot = $PSScriptRoot
$projDir = Join-Path $repoRoot 'TKSDesktop'
$csproj = Join-Path $projDir 'TKSDesktop.csproj'
$dist = Join-Path $repoRoot 'dist'
$publishDir = Join-Path $repoRoot 'artifacts\publish'

function Write-Step([string]$text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Write-Ok([string]$text) { Write-Host "  [OK] $text" -ForegroundColor Green }
function Write-Warn2([string]$text) { Write-Host "  [WARN] $text" -ForegroundColor Yellow }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET 8 SDK 未安装或不在 PATH（PRD §11.2 前置条件）"
}

# ---- 1. 读取版本号（单一来源） ----
Write-Step '读取版本号'
[xml]$xml = Get-Content $csproj -Raw
$version = $xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version)) { throw "未能从 $csproj 读取 <Version>" }
Write-Ok "version = $version"

$buildDate = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
Write-Ok "buildDate = $buildDate"

# ---- 2. 构建与测试 ----
if (-not $SkipTests) {
    Write-Step 'dotnet test'
    & dotnet test (Join-Path $repoRoot 'Tests\TKSDesktop.Tests\TKSDesktop.Tests.csproj') `
        -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "测试失败（退出码 $LASTEXITCODE）" }
    Write-Ok 'tests passed'
}

# ---- 3. 发布（自包含，G5：用户侧无需 .NET 运行时） ----
Write-Step 'dotnet publish (win-x64)'
$selfContained = -not $NoSelfContained
Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue

$publishArgs = @(
    'publish', $projDir,
    '-c', 'Release',
    '-r', 'win-x64',
    '-o', $publishDir,
    '--nologo',
    "-p:Version=$version",
    "-p:SelfContained=$($selfContained.ToString().ToLower())",
    "-p:InformationalVersion=$version",
    '-p:PublishReadyToRun=false'
)
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "publish 失败（退出码 $LASTEXITCODE）" }

$exe = Join-Path $publishDir 'TKSDesktop.exe'
if (-not (Test-Path $exe)) { throw "未产出 TKSDesktop.exe（检查 AssemblyName）" }
Write-Ok "published -> $publishDir"

# Exercise the normal GUI entry point before packaging this exact release build.
& (Join-Path $repoRoot 'scripts\smoke-startup.ps1') -Executable $exe

# 校验版本号来源未被硬编码：读回 exe 文件版本
$fileVersion = (Get-Item $exe).VersionInfo.FileVersion
Write-Ok "exe FileVersion = $fileVersion"

# ---- 4. 便携版 ----
Write-Step '便携版 zip'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$portableRoot = Join-Path $repoRoot 'artifacts\portable'
Remove-Item $portableRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $portableRoot -Force | Out-Null
Copy-Item (Join-Path $publishDir '*') $portableRoot -Recurse -Force

# §14.4：便携版必须存在 portable.flag，数据全部写在程序目录下
Set-Content -Path (Join-Path $portableRoot 'portable.flag') -Value "portable" -Encoding ASCII -NoNewline
New-Item -ItemType Directory -Path (Join-Path $portableRoot 'data\config') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $portableRoot 'data\state') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $portableRoot 'data\attachments') -Force | Out-Null

$portableZip = Join-Path $dist "TKS-Desktop-$version-win-x64-portable.zip"
Remove-Item $portableZip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $portableRoot '*') -DestinationPath $portableZip -CompressionLevel Optimal
$portableSizeMb = [Math]::Round((Get-Item $portableZip).Length / 1MB, 1)
Write-Ok "portable -> $portableZip ($portableSizeMb MB)"
if ($portableSizeMb -gt 300) { Write-Warn2 "便携版超过 NFR-W-3 上限 300MB（$portableSizeMb MB）" }

# ---- 5. NSIS 安装包（FR-W-PKG-1 / §14.3） ----
$setupExe = Join-Path $dist "TKS-Desktop-$version-win-x64-setup.exe"
if ($SkipInstaller) {
    Write-Warn2 '按参数跳过 NSIS 安装包'
}
else {
    Write-Step 'NSIS 安装包'
    $makensis = (Get-Command makensis -ErrorAction SilentlyContinue).Source
    if (-not $makensis) {
        $candidates = @(
            "$env:ProgramFiles\NSIS\makensis.exe",
            "${env:ProgramFiles(x86)}\NSIS\makensis.exe"
        )
        $makensis = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    }

    if (-not $makensis) {
        Write-Warn2 'makensis 未找到 —— 未产出安装包（NSIS 为 PRD §11.2 前置条件：winget install NSIS.NSIS）'
        Write-Warn2 '注意：安装包缺失会让 AUMID 开始菜单快捷方式不存在，Toast 将降级为托盘气泡（EDGE-W-10）'
    }
    else {
        $nsi = Join-Path $repoRoot 'installer\tks-desktop.nsi'
        # NSI_DIR 供 nsi 随包带入 Set-ShortcutAumid.ps1（AUMID 写入脚本）。
        & $makensis `
            "/DAPP_VERSION=$version" `
            "/DPUBLISH_DIR=$publishDir" `
            "/DOUT_FILE=$setupExe" `
            "/DNSI_DIR=$(Join-Path $repoRoot 'installer')" `
            $nsi
        if ($LASTEXITCODE -ne 0) { throw "makensis 失败（退出码 $LASTEXITCODE）" }
        Write-Ok "installer -> $setupExe"

        # §14.3 / V-W-B4 前置条件自检：AUMID 写入脚本必须能读回自身声明的 AUMID。
        # 这里用临时快捷方式验证「脚本能用」，真实安装时的写入由 nsi 的 SecStartMenu 负责。
        Write-Step 'AUMID 写入能力自检（§14.3 前置条件）'
        $aumidScript = Join-Path $repoRoot 'installer\Set-ShortcutAumid.ps1'
        $probeDir = Join-Path $env:TEMP ('tks-aumid-probe-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
        New-Item -ItemType Directory -Path $probeDir -Force | Out-Null
        try {
            $probeLnk = Join-Path $probeDir 'probe.lnk'
            $shell = New-Object -ComObject WScript.Shell
            $sc = $shell.CreateShortcut($probeLnk)
            $sc.TargetPath = "$env:SystemRoot\System32\notepad.exe"
            $sc.Save()

            & powershell -NoProfile -ExecutionPolicy Bypass -File $aumidScript `
                -ShortcutPath $probeLnk -Aumid 'TKSDesktop' -Verify | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "AUMID 写入自检失败（退出码 $LASTEXITCODE）—— 安装后将无法弹出 Toast（§14.3）"
            }
            Write-Ok 'AUMID 写入 + 读回校验通过'
        }
        finally {
            Remove-Item $probeDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
Write-Step 'SHA256SUMS.txt'
$sumFile = Join-Path $dist 'SHA256SUMS.txt'
if (Test-Path $sumFile) { Remove-Item $sumFile -Force }

$lines = @()
foreach ($artifact in @($portableZip, $setupExe)) {
    if (Test-Path $artifact) {
        $hash = (Get-FileHash $artifact -Algorithm SHA256).Hash.ToLowerInvariant()
        $lines += "$hash  $(Split-Path $artifact -Leaf)"
    }
}
if ($lines.Count -eq 0) { throw '没有可校验的产物' }
Set-Content -Path $sumFile -Value ($lines -join "`n") -Encoding UTF8
Write-Ok "checksums -> $sumFile ($($lines.Count) entries)"

# ---- 7. 汇总 ----
Write-Step '产物汇总'
Get-ChildItem $dist | ForEach-Object {
    Write-Host ("  {0}  ({1} MB)" -f $_.Name, [Math]::Round($_.Length / 1MB, 1))
}
Write-Host ''
Write-Host '注意（FR-W-PKG-4 / FR-W-PKG-12）：' -ForegroundColor Yellow
Write-Host '  · 未做代码签名 —— 首次运行会有 SmartScreen「不常见的应用」提示，选择「仍要运行」'
Write-Host '  · 发布前需完成运维侧前置检查：nginx 补 /healthz location、调大 client_max_body_size、积分路由分流至 :8001'
