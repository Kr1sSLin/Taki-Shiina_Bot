<#
.SYNOPSIS
  TKS Desktop for Windows —— 一键校验（PRD FR-W-PKG-11 / V-W-C7）。

.DESCRIPTION
  串联四层校验，**任一失败即非 0 退出**（不打印警告了事）：
    ① 构建（TreatWarningsAsErrors，V-W-C4「无编译警告即失败」）
    ② 单元测试 + 机械门禁（V-W-C1 / V-W-C2：≥66 项契约自检）
    ③ i18n 静态扫描（NFR-W-W10 / V-W-S8：禁止硬编码中文）
    ④ 集成自检（V-W-C3：`TKSDesktop.exe --selftest`，无图形界面，≥95 项）
    ⑤ dotnet format 检查（若可用）

  ② 与 ④ 需要 Mock 后端在线时由本脚本自动拉起（`node mock-server/server.mjs`）。
#>

[CmdletBinding()]
param(
    [switch]$SkipIntegration,
    [switch]$SkipFormat,
    [switch]$KeepMockServer
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$scriptDir = $PSScriptRoot
$winRoot = Split-Path $scriptDir -Parent
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    $candidate = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (Test-Path $candidate) { $dotnet = $candidate }
}
if (-not $dotnet) { throw '.NET 8 SDK 未找到（PRD §11.2 前置条件）' }

$proj = Join-Path $winRoot 'TKSDesktop\TKSDesktop.csproj'
$tests = Join-Path $winRoot 'Tests\TKSDesktop.Tests\TKSDesktop.Tests.csproj'
$failures = New-Object System.Collections.Generic.List[string]
$mockProcess = $null

function Write-Step([string]$t) { Write-Host "`n===== $t =====" -ForegroundColor Cyan }
function Write-Pass([string]$t) { Write-Host "  [PASS] $t" -ForegroundColor Green }
function Write-Fail([string]$t) { Write-Host "  [FAIL] $t" -ForegroundColor Red; $script:failures.Add($t) }

function Test-MockReachable {
    try {
        $r = Invoke-WebRequest -Uri 'http://127.0.0.1:8787/healthz' -TimeoutSec 2 -UseBasicParsing
        return ($r.StatusCode -eq 200)
    }
    catch {
        return $false
    }
}

try {
    # ---------- ① 构建 ----------
    Write-Step '① 构建（-warnaserror）'
    & $dotnet build $proj -c Debug --nologo -v minimal -warnaserror
    if ($LASTEXITCODE -ne 0) { Write-Fail '构建失败（含警告即失败）' } else { Write-Pass '构建通过，0 警告 0 错误' }

    # ---------- ② 单元测试 + 机械门禁 ----------
    Write-Step '② 单元测试与机械门禁'
    & $dotnet test $tests -c Debug --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { Write-Fail '单元测试/门禁失败' } else { Write-Pass '单元测试与机械门禁通过' }

    # ---------- ③ i18n 静态扫描 ----------
    Write-Step '③ i18n 静态扫描（禁止硬编码中文）'
    $i18nScript = Join-Path $scriptDir 'check-i18n.ps1'
    if (Test-Path $i18nScript) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File $i18nScript
        if ($LASTEXITCODE -ne 0) { Write-Fail 'i18n 静态扫描失败（存在硬编码中文或 key 重复）' }
        else { Write-Pass 'i18n 扫描通过' }
    }
    else {
        Write-Fail "缺少 $i18nScript"
    }

    # ---------- ④ 集成自检（无图形界面） ----------
    if (-not $SkipIntegration) {
        Write-Step '④ 集成自检 --selftest（无图形界面）'

        # Mock 后端（FR-W-TEST-2）：默认 127.0.0.1:8787，kris/taki
        $mockServer = Join-Path $winRoot '..\TKS_Bot_Linux\mock-server\server.mjs'
        $reachable = $false
        if (Test-Path $mockServer) {
            if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
                Write-Host '  [WARN] 未找到 node，跳过 Mock 后端启动' -ForegroundColor Yellow
            }
            else {
                # 已有实例在监听时不再拉起第二个（避免端口冲突导致「已启动但不可达」）。
                $reachable = Test-MockReachable
                if ($reachable) {
                    Write-Host '  Mock 后端已在运行（127.0.0.1:8787）'
                }
                else {
                    Write-Host '  启动 Mock 后端（127.0.0.1:8787）...'
                    $mockProcess = Start-Process node -ArgumentList $mockServer -PassThru -WindowStyle Hidden

                    # 轮询就绪而不是死等固定 2 秒：启动慢时固定等待会让集成自检假失败。
                    for ($i = 0; $i -lt 30 -and -not $reachable; $i++) {
                        Start-Sleep -Milliseconds 500
                        $reachable = Test-MockReachable
                    }

                    if ($reachable) { Write-Host '  Mock 后端就绪' }
                    else { Write-Host '  [WARN] Mock 后端 15 秒内未就绪，集成自检将如实失败' -ForegroundColor Yellow }
                }
            }
        }
        else {
            Write-Host "  [WARN] 未找到 Mock 后端：$mockServer" -ForegroundColor Yellow
        }

        # 自检走与用户**完全相同**的可执行文件与入口（FR-W-TEST-3 护栏：不得为测试加特例参数）
        $exe = Join-Path $winRoot 'TKSDesktop\bin\Debug\net8.0-windows\TKSDesktop.exe'
        if (-not (Test-Path $exe)) {
            Write-Fail "未找到构建产物：$exe"
        }
        else {
            # ⚠️ 已知 Windows PowerShell 坑：`$ErrorActionPreference='Stop'` 下，原生命令写 stderr 会被
            #    当作终止性错误抛出（自检报告**刻意**同时写 stdout 与 stderr，故必然命中）。
            #    这里临时放宽为 Continue，并只以**退出码**判定成败。
            $previousEap = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $out = & $exe --selftest 2>&1 | Out-String
                $exitCode = $LASTEXITCODE
            }
            finally {
                $ErrorActionPreference = $previousEap
            }

            Write-Host $out
            if ($exitCode -ne 0) { Write-Fail "集成自检失败（退出码 $exitCode）" }
            else { Write-Pass '集成自检通过（退出码 0）' }
        }
    }
    else { Write-Host '  [SKIP] 按参数跳过集成自检' -ForegroundColor DarkGray }

    # ---------- ⑤ dotnet format（V-W-C4：格式必须已规范，否则失败） ----------
    if (-not $SkipFormat) {
        Write-Step '⑤ dotnet format --verify-no-changes'

        # 产品工程与测试工程都要检查。
        # ⚠️ 本层**不再降级为警告**：行尾与缩进由 TKS_Bot_Windows/.editorconfig 固定，
        #    格式检查因此可复现，与其它层同等对待（任一失败即非 0 退出）。
        foreach ($target in @($proj, $tests)) {
            $formatOut = & $dotnet format $target --verify-no-changes --no-restore --verbosity minimal 2>&1 | Out-String
            if ($LASTEXITCODE -ne 0) {
                Write-Host $formatOut
                Write-Fail "dotnet format 存在差异：$(Split-Path $target -Leaf)"
            }
            else {
                Write-Pass "format 无差异：$(Split-Path $target -Leaf)"
            }
        }
    }
    else { Write-Host '  [SKIP] 按参数跳过 format' -ForegroundColor DarkGray }
}
finally {
    if ($mockProcess -and -not $KeepMockServer) {
        try { Stop-Process -Id $mockProcess.Id -Force -ErrorAction SilentlyContinue } catch { }
        Write-Host '  已停止 Mock 后端'
    }
}

# ---------- 汇总 ----------
Write-Host ''
Write-Host ('=' * 60)
if ($failures.Count -eq 0) {
    Write-Host '校验结果：ALL PASS' -ForegroundColor Green
    exit 0
}

Write-Host "校验结果：FAILED（$($failures.Count) 项）" -ForegroundColor Red
foreach ($f in $failures) { Write-Host "  - $f" -ForegroundColor Red }
exit 1
