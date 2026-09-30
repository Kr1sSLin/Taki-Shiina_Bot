<#
.SYNOPSIS
    Taki_Shiina_Bot 后端 —— Windows 本地开发启动脚本（纯开发辅助，不参与 Linux 部署链路）。

.DESCRIPTION
    推荐使用统一入口：

        统一 API      python unified_api.py       BOT_UNIFIED_PORT，默认 8000

    兼容模式仍可独立启动 3 个 FastAPI 进程：认证 8002、HTTP 8000、WebSocket 8001。
    为什么不能“随便找个目录”启动：本仓库的导入风格是混用的——
      * main.py 用绝对导入 `from Taki_Shiina_Bot.core...`  → 需要「仓库根」在 sys.path 上；
      * ws_api.py / http_api.py 及其依赖用顶层导入 `from auth_utils / from services...`
        → 需要「项目目录」在 sys.path 上。
    因此脚本显式设置 PYTHONPATH=<项目目录>;<仓库根>，并按各服务的常规目录启动
    （认证服务在仓库根、http_api/ws_api 在项目目录），与用户当前所在的目录无关。

    本脚本不改动服务代码，只负责：前置检查（.env / DATA_ENC_KEY / AUTH_* / 鉴权 token /
    缺失依赖 / 端口）→ 打印可执行提示 → 按参数启动。各入口默认仅绑定 127.0.0.1；
    如确需非回环监听，必须显式设置对应 BOT_*_HOST 并自行配置防火墙/TLS 反代。

.PARAMETER Target
    check（默认）：只做前置检查并打印启动命令；
    unified：检查通过后启动推荐的单进程统一入口；
    auth / http / ws：启动兼容模式对应进程；all：同时启动兼容模式三个进程。

.PARAMETER DryRun
    只打印将要执行的命令，不真正启动（用于验证环境与参数拼装）。

.EXAMPLE
    # 1) 先只检查环境（推荐第一次这么用）
    powershell -ExecutionPolicy Bypass -File .\Taki_Shiina_Bot\scripts\run_dev.ps1

.EXAMPLE
    # 2) 启动认证服务；自检：Invoke-RestMethod http://127.0.0.1:8002/api/v1/health
    powershell -ExecutionPolicy Bypass -File .\Taki_Shiina_Bot\scripts\run_dev.ps1 auth

.EXAMPLE
    # 3) 三个进程一起起（各自一个窗口，Ctrl+C 或关窗口即停止）
    powershell -ExecutionPolicy Bypass -File .\Taki_Shiina_Bot\scripts\run_dev.ps1 all
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('check', 'unified', 'auth', 'http', 'ws', 'all')]
    [string]$Target = 'check',

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# ==================== 定位路径 ====================
$ScriptPath = $PSCommandPath
$ScriptDir = Split-Path -Parent $ScriptPath          # ...\Taki_Shiina_Bot\scripts
$ProjectDir = Split-Path -Parent $ScriptDir          # ...\Taki_Shiina_Bot（main.py / http_api.py / ws_api.py 所在）
$RepoRoot = Split-Path -Parent $ProjectDir           # ...\Taki-Shiina_Bot（git 根，import Taki_Shiina_Bot.* 需要）
$VenvPython = Join-Path $ProjectDir '.venv\Scripts\python.exe'
$EnvFile = Join-Path $ProjectDir '.env'
$EnvExample = Join-Path $ProjectDir '.env.example'

$VenvSetupHint = @(
    '    cd "{0}"' -f $ProjectDir
    '    py -3.14 -m venv .venv        # 或： python -m venv .venv'
    '    .\.venv\Scripts\python.exe -m pip install -U pip'
    '    .\.venv\Scripts\python.exe -m pip install -r requirements.txt -r requirements-dev.txt'
) -join "`n"

# ==================== 输出小工具 ====================
function Write-Head([string]$Text) {
    Write-Host ''
    Write-Host "== $Text ==" -ForegroundColor Cyan
}
function Write-Ok([string]$Text) { Write-Host "[OK]   $Text" -ForegroundColor Green }
function Write-Note([string]$Text) { Write-Host "[提示] $Text" -ForegroundColor Yellow }
function Write-Bad([string]$Text) { Write-Host "[错误] $Text" -ForegroundColor Red }

# ==================== .env 解析（简化 KEY=VALUE，支持 export 与成对引号） ====================
function Read-DotEnv {
    param([string]$Path)

    $map = @{}
    if (-not (Test-Path -LiteralPath $Path)) { return $map }

    foreach ($line in (Get-Content -LiteralPath $Path -Encoding UTF8)) {
        $text = $line.Trim()
        if ($text.Length -eq 0) { continue }
        if ($text.StartsWith('#')) { continue }
        if ($text.StartsWith('export ')) { $text = $text.Substring(7).Trim() }

        $eq = $text.IndexOf('=')
        if ($eq -lt 1) { continue }

        $key = $text.Substring(0, $eq).Trim()
        $value = $text.Substring($eq + 1).Trim()
        if ($value.Length -ge 2) {
            $first = $value.Substring(0, 1)
            $last = $value.Substring($value.Length - 1, 1)
            if (($first -eq '"' -and $last -eq '"') -or ($first -eq "'" -and $last -eq "'")) {
                $value = $value.Substring(1, $value.Length - 2)
            }
        }
        $map[$key] = $value
    }
    return $map
}

# 生效值 = 进程环境变量 > .env > 默认值（与 python-dotenv 的 load_dotenv(override=False) 一致）
function Resolve-Setting {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Map,
        [Parameter(Mandatory = $true)][string]$Key,
        [string]$Default = ''
    )

    $fromEnv = [System.Environment]::GetEnvironmentVariable($Key, 'Process')
    if (-not [string]::IsNullOrWhiteSpace($fromEnv)) {
        return [pscustomobject]@{ Key = $Key; Value = $fromEnv.Trim(); Source = '进程环境变量' }
    }
    if ($Map.ContainsKey($Key) -and -not [string]::IsNullOrWhiteSpace($Map[$Key])) {
        return [pscustomobject]@{ Key = $Key; Value = $Map[$Key].Trim(); Source = '.env' }
    }
    return [pscustomobject]@{ Key = $Key; Value = $Default; Source = '默认值' }
}

# 与 secure_storage.load_encryption_config 对齐的粗略校验：base64(urlsafe) 或 hex，解出 16/24/32 字节
function Test-DataKeyFormat {
    param([Parameter(Mandatory = $true)][string]$Value)

    $normalized = $Value.Replace('-', '+').Replace('_', '/')
    switch ($normalized.Length % 4) {
        2 { $normalized += '==' }
        3 { $normalized += '=' }
    }
    try {
        $bytes = [Convert]::FromBase64String($normalized)
        if ($bytes.Length -in 16, 24, 32) { return $true }
    } catch { }

    if ($Value.Length % 2 -eq 0 -and $Value -match '^[0-9a-fA-F]+$') {
        try {
            $hexBytes = New-Object byte[] ($Value.Length / 2)
            for ($i = 0; $i -lt $hexBytes.Length; $i++) {
                $hexBytes[$i] = [Convert]::ToByte($Value.Substring($i * 2, 2), 16)
            }
            if ($hexBytes.Length -in 16, 24, 32) { return $true }
        } catch { }
    }
    return $false
}

# ==================== 依赖检查（一次子进程里 import 全部关键包） ====================
$DependencyList = @(
    'fastapi', 'uvicorn', 'dotenv', 'cryptography', 'pydantic', 'httpx', 'openai', 'websockets'
)
# 仅 ws_api.py 顶层 `import google.generativeai`（requirements.txt 的 google-generativeai 提供）
$WsOnlyDependency = 'google.generativeai'

function Get-MissingDependencies {
    param(
        [Parameter(Mandatory = $true)][string]$Python,
        [Parameter(Mandatory = $true)][string[]]$Names
    )

    $quoted = ($Names | ForEach-Object { "'$_'" }) -join ', '
    $code = @"
import importlib
missing = []
for name in [$quoted]:
    try:
        importlib.import_module(name)
    except Exception:
        missing.append(name)
print('MISSING:' + ','.join(missing))
"@
    # PowerShell 5.1 的坑：$ErrorActionPreference='Stop'（本脚本第 52 行）时，原生命令写 stderr 的
    # 任何内容都会被当成终止性错误抛出。这里是"探测依赖"的调用，Python 的 FutureWarning 之类
    # 警告完全正常，不应中断检查。故局部降级为 Continue，只看 stdout 里的 MISSING: 行。
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $result = $code | & $Python - 2>$null
    }
    finally {
        $ErrorActionPreference = $prevEap
    }
    $line = ($result | Where-Object { $_ -like 'MISSING:*' } | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($line)) { return @() }
    $names = $line.Substring('MISSING:'.Length)
    if ([string]::IsNullOrWhiteSpace($names)) { return @() }
    return @($names -split ',')
}

# ==================== 前置检查 ====================
Write-Head 'Taki_Shiina_Bot 开发环境检查'
Write-Host "仓库根    : $RepoRoot"
Write-Host "项目目录  : $ProjectDir"
Write-Host "虚拟环境  : $VenvPython"
Write-Host "启动目标  : $Target"

$fatal = New-Object System.Collections.Generic.List[string]

# 1) 虚拟环境
if (-not (Test-Path -LiteralPath $VenvPython)) {
    Write-Bad "未找到虚拟环境解释器: $VenvPython"
    Write-Host '请先创建虚拟环境并安装依赖：'
    Write-Host $VenvSetupHint
    exit 1
}
Write-Ok "虚拟环境存在: $VenvPython"

# 2) .env
$DotEnv = @{}
if (Test-Path -LiteralPath $EnvFile) {
    $DotEnv = Read-DotEnv -Path $EnvFile
    Write-Ok ".env 存在（解析到 $($DotEnv.Count) 个键）: $EnvFile"
    if ($DotEnv.Count -eq 0) {
        Write-Note ".env 里没有解析到任何 KEY=VALUE，请确认文件内容是否正常"
    }
} else {
    Write-Note "未找到 .env：$EnvFile"
    Write-Host '       从模板复制一份再填值（缺 DATA_ENC_KEY / AUTH_* / DEEPSEEK_API_KEY 会让服务在 import 阶段直接失败）：'
    Write-Host "         Copy-Item `"$EnvExample`" `"$EnvFile`""
    Write-Host '       至少要设置：DATA_ENC_KEY（base64 或 hex，16/24/32 字节）、AUTH_JWT_SECRET、'
    Write-Host '                   AUTH_USERNAME、AUTH_PASSWORD（或 AUTH_PASSWORD_HASH + AUTH_PASSWORD_SALT）、'
    Write-Host '                   BOT_HTTP_TOKEN、BOT_WS_TOKEN、DEEPSEEK_API_KEY（本地可填占位值）'
}

# 3) DATA_ENC_KEY（secure_storage.SecureJsonStore.__init__ 会立即调用 load_encryption_config）
$encKey = Resolve-Setting -Map $DotEnv -Key 'DATA_ENC_KEY'
if ([string]::IsNullOrWhiteSpace($encKey.Value)) {
    $fatal.Add('DATA_ENC_KEY 未设置（secure_storage 会在 import / 首次构造 SecureJsonStore 时抛 DataKeyError）') | Out-Null
    Write-Bad 'DATA_ENC_KEY 未设置'
    Write-Host '       生成一个本地开发用的 key（示例，勿用于生产）：'
    Write-Host '         .\.venv\Scripts\python.exe -c "import base64,os;print(base64.urlsafe_b64encode(os.urandom(32)).decode())"'
    Write-Host "       然后写入 .env（$EnvFile）"
} elseif (-not (Test-DataKeyFormat -Value $encKey.Value)) {
    Write-Note "DATA_ENC_KEY 已设置（来源：$($encKey.Source)），但格式看起来不是 16/24/32 字节的 base64 或 hex —— 服务启动时可能抛 DataKeyError"
} else {
    Write-Ok "DATA_ENC_KEY 已设置（来源：$($encKey.Source)，格式校验通过）"
}

# 4) AUTH_*（api/v1/auth.py 第 42-52 行：不满足则 import 期 RuntimeError）
$authJwt = Resolve-Setting -Map $DotEnv -Key 'AUTH_JWT_SECRET'
$authUser = Resolve-Setting -Map $DotEnv -Key 'AUTH_USERNAME'
$authPwd = Resolve-Setting -Map $DotEnv -Key 'AUTH_PASSWORD'
$authHash = Resolve-Setting -Map $DotEnv -Key 'AUTH_PASSWORD_HASH'
$authSalt = Resolve-Setting -Map $DotEnv -Key 'AUTH_PASSWORD_SALT'

$authMissing = New-Object System.Collections.Generic.List[string]
if ([string]::IsNullOrWhiteSpace($authJwt.Value)) { $authMissing.Add('AUTH_JWT_SECRET') | Out-Null }
if ([string]::IsNullOrWhiteSpace($authUser.Value)) { $authMissing.Add('AUTH_USERNAME') | Out-Null }
$hasHashPair = (-not [string]::IsNullOrWhiteSpace($authHash.Value)) -and (-not [string]::IsNullOrWhiteSpace($authSalt.Value))
if ((-not $hasHashPair) -and [string]::IsNullOrWhiteSpace($authPwd.Value)) {
    $authMissing.Add('AUTH_PASSWORD_HASH + AUTH_PASSWORD_SALT（或 AUTH_PASSWORD）') | Out-Null
}
if ($authMissing.Count -gt 0) {
    $fatal.Add("缺少认证配置: $($authMissing -join '、')") | Out-Null
    Write-Bad "缺少认证配置：$($authMissing -join '、')"
    Write-Host '       这三个值在 .env 里补齐即可（api/v1/auth.py 在 import 时会直接报 Auth config invalid）'
} else {
    Write-Ok 'AUTH_JWT_SECRET / AUTH_USERNAME / 口令校验方式 均已配置'
}

# 5) 鉴权 token（缺失只是“客户端无法通过 token 鉴权”，服务本身能起，故为提示）
$tokenMissing = New-Object System.Collections.Generic.List[string]
foreach ($name in @('BOT_HTTP_TOKEN', 'BOT_WS_TOKEN')) {
    $item = Resolve-Setting -Map $DotEnv -Key $name
    if ([string]::IsNullOrWhiteSpace($item.Value)) { $tokenMissing.Add($name) | Out-Null }
}
if ($tokenMissing.Count -gt 0) {
    Write-Note "未配置：$($tokenMissing -join '、')（服务能启动，但客户端用 token 鉴权会失败）"
} else {
    Write-Ok 'BOT_HTTP_TOKEN / BOT_WS_TOKEN 均已配置'
}

# 6) 外部服务 key
#    DEEPSEEK_API_KEY 对 http_api / ws_api 是**必须非空**的：两者在模块顶层构造
#    AsyncOpenAI(api_key=...)（http_api.py:71、ws_api.py:136），新版 openai 包在 key 为空时
#    直接抛 OpenAIError（已实测）。认证服务不含该客户端，故仅作提示。
$deepseek = Resolve-Setting -Map $DotEnv -Key 'DEEPSEEK_API_KEY'
if ([string]::IsNullOrWhiteSpace($deepseek.Value)) {
    if ($Target -in @('unified', 'http', 'ws', 'all')) {
        $fatal.Add('DEEPSEEK_API_KEY 未设置（http_api.py:71 / ws_api.py:136 在 import 期构造 AsyncOpenAI，空 key 会抛 OpenAIError）') | Out-Null
        Write-Bad 'DEEPSEEK_API_KEY 未设置（http_api / ws_api 会在 import 阶段失败）'
        Write-Host '       本地开发随便填一个非空占位值即可（不要填真实密钥到版本库）：'
        Write-Host '         $env:DEEPSEEK_API_KEY = "dev-only-dummy-key"   # 或写进 .env'
    } else {
        Write-Note 'DEEPSEEK_API_KEY 未设置（认证服务不受影响；http_api / ws_api 启动需要它非空）'
    }
} else {
    Write-Ok "DEEPSEEK_API_KEY 已设置（来源：$($deepseek.Source)）"
}

$optionalMissing = New-Object System.Collections.Generic.List[string]
foreach ($name in @('GEMINI_API_KEY', 'GOOGLE_API_KEY', 'QWEATHER_API_KEY', 'QWEATHER_API_HOST')) {
    $item = Resolve-Setting -Map $DotEnv -Key $name
    if ([string]::IsNullOrWhiteSpace($item.Value)) { $optionalMissing.Add($name) | Out-Null }
}
if ($optionalMissing.Count -gt 0) {
    Write-Note "未配置外部服务 key：$($optionalMissing -join '、')（对应 AI/天气功能会失败，本地开发可忽略；已实测不影响 http_api import）"
}

# 7) 端口
$httpPort = Resolve-Setting -Map $DotEnv -Key 'BOT_HTTP_PORT' -Default '8000'
$wsPort = Resolve-Setting -Map $DotEnv -Key 'BOT_WS_PORT' -Default '8001'
$authPort = Resolve-Setting -Map $DotEnv -Key 'BOT_AUTH_PORT' -Default '8002'
$unifiedPort = Resolve-Setting -Map $DotEnv -Key 'BOT_UNIFIED_PORT' -Default '8000'
$httpHost = Resolve-Setting -Map $DotEnv -Key 'BOT_HTTP_HOST' -Default '127.0.0.1'
$wsHost = Resolve-Setting -Map $DotEnv -Key 'BOT_WS_HOST' -Default '127.0.0.1'
$unifiedHost = Resolve-Setting -Map $DotEnv -Key 'BOT_UNIFIED_HOST' -Default '127.0.0.1'

Write-Host ''
Write-Host "推荐统一入口 $($unifiedHost.Value):$($unifiedPort.Value)（$($unifiedPort.Source)）；兼容模式：认证 $($authPort.Value) / HTTP $($httpPort.Value) / WS $($wsPort.Value)"

# 8) 第三方依赖
Write-Head '依赖检查'
$allDeps = @($DependencyList) + @($WsOnlyDependency)
$missingDeps = @(Get-MissingDependencies -Python $VenvPython -Names $allDeps)
$missingCore = @($missingDeps | Where-Object { $DependencyList -contains $_ })
$missingWs = @($missingDeps | Where-Object { $_ -eq $WsOnlyDependency })

if ($missingDeps.Count -eq 0) {
    Write-Ok '关键依赖齐全（fastapi/uvicorn/dotenv/cryptography/pydantic/httpx/openai/websockets/google.generativeai）'
} else {
    Write-Note ("当前虚拟环境缺少：" + ($missingDeps -join '、'))
    Write-Host '       安装/补齐（两条 requirements 都要装）：'
    Write-Host '         .\.venv\Scripts\python.exe -m pip install -r requirements.txt -r requirements-dev.txt'

    if ($Target -in @('unified', 'auth', 'http', 'all') -and $missingCore.Count -gt 0) {
        $fatal.Add("核心依赖缺失: $($missingCore -join '、')") | Out-Null
    }
    if ($Target -in @('unified', 'ws')) {
        if ($missingCore.Count -gt 0) { $fatal.Add("核心依赖缺失: $($missingCore -join '、')") | Out-Null }
        if ($missingWs.Count -gt 0) {
            # ws_api.py 顶层 `import google.generativeai`：这个包缺失时 ws_api 无法启动
            $fatal.Add("ws_api 需要 $($missingWs -join '、')（requirements.txt 中的 google-generativeai）") | Out-Null
        }
    }
}

# ==================== 汇总 ====================
Write-Head '结论'
if ($fatal.Count -gt 0) {
    Write-Bad '存在必须修复的问题，已停止启动：'
    foreach ($item in $fatal) { Write-Host "       - $item" }
    Write-Host ''
    Write-Host '       修好上面的项后重新执行本脚本（默认 check 模式会再次体检）。'
    exit 1
}
Write-Ok '前置检查通过'

$cmdUnified = "cd `"$ProjectDir`"; `$env:PYTHONPATH=`"$ProjectDir;$RepoRoot`"; & `"$VenvPython`" unified_api.py"
$cmdAuth = "cd `"$RepoRoot`"; `$env:PYTHONPATH=`"$ProjectDir;$RepoRoot`"; & `"$VenvPython`" -m uvicorn main:app --port $($authPort.Value)"
$cmdHttp = "cd `"$ProjectDir`"; `$env:PYTHONPATH=`"$ProjectDir;$RepoRoot`"; & `"$VenvPython`" http_api.py"
$cmdWs = "cd `"$ProjectDir`"; `$env:PYTHONPATH=`"$ProjectDir;$RepoRoot`"; & `"$VenvPython`" ws_api.py"

Write-Host ''
Write-Host '等价手写命令：'
Write-Host "  统一入口(推荐): $cmdUnified"
Write-Host "  认证服务(兼容): $cmdAuth"
Write-Host "  HTTP API (兼容): $cmdHttp"
Write-Host "  WebSocket(兼容): $cmdWs"
Write-Host ''
Write-Host "自检：统一入口 GET http://127.0.0.1:$($unifiedPort.Value)/healthz"

if ($Target -eq 'check') {
    Write-Host ''
    Write-Host "启动方式：本脚本加参数 unified（推荐）或 auth / http / ws / all"
    exit 0
}

# ==================== 启动 ====================
function Set-DevPythonPath {
    $existing = [System.Environment]::GetEnvironmentVariable('PYTHONPATH', 'Process')
    if ([string]::IsNullOrWhiteSpace($existing)) {
        $env:PYTHONPATH = "$ProjectDir;$RepoRoot"
    } else {
        $env:PYTHONPATH = "$ProjectDir;$RepoRoot;$existing"
    }
}

# ==================== 启动阶段：解除 Stop ====================
# 前置检查需要在出错时立刻停下，所以脚本顶部设了 $ErrorActionPreference='Stop'。
# 但**服务启动是另一回事**：uvicorn / http_api / ws_api 的日志默认全部走 stderr，
# 在 Stop 下第一条日志就会被 PowerShell 当成终止性错误抛出，表现为"服务刚起来就被打断"。
# 因此进入启动阶段前降级为 Continue：日志正常透传，进程生命周期由服务自身与 Ctrl+C 控制。
$ErrorActionPreference = 'Continue'

if ($Target -eq 'all') {
    Write-Head '启动全部三个进程（各占一个新窗口；关窗口 = 停止该进程）'
    if ($DryRun) {
        Write-Note 'DryRun：不真正启动'
        exit 0
    }
    $hostExe = (Get-Process -Id $PID).Path
    foreach ($item in @('auth', 'http', 'ws')) {
        # -File 的路径含空格时会被拆成多个参数，故显式加引号
        $argumentList = @('-NoExit', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $ScriptPath + '"'), $item)
        Start-Process -FilePath $hostExe -ArgumentList $argumentList -WorkingDirectory $ProjectDir | Out-Null
        Write-Ok "已在新窗口启动: $item"
    }
    Write-Host ''
    Write-Host '提示：窗口里 Ctrl+C 或直接关窗口即可停止对应进程。'
    exit 0
}

switch ($Target) {
    'unified' {
        Write-Head "启动统一 API（python unified_api.py，$($unifiedHost.Value):$($unifiedPort.Value)）"
        Write-Host "命令: $cmdUnified"
        if ($DryRun) { Write-Note 'DryRun：未启动'; exit 0 }
        Set-DevPythonPath
        Set-Location -LiteralPath $ProjectDir
        & $VenvPython (Join-Path $ProjectDir 'unified_api.py')
    }
    'auth' {
        Write-Head "启动认证服务（uvicorn main:app --port $($authPort.Value)，工作目录=仓库根）"
        Write-Host "命令: $cmdAuth"
        if ($DryRun) { Write-Note 'DryRun：未启动'; exit 0 }
        Set-DevPythonPath
        Set-Location -LiteralPath $RepoRoot
        & $VenvPython -m uvicorn main:app --port $authPort.Value
    }
    'http' {
        Write-Head "启动 HTTP API（python http_api.py，端口由 BOT_HTTP_PORT 决定：$($httpPort.Value)）"
        Write-Host "命令: $cmdHttp"
        if ($DryRun) { Write-Note 'DryRun：未启动'; exit 0 }
        Set-DevPythonPath
        Set-Location -LiteralPath $ProjectDir
        & $VenvPython (Join-Path $ProjectDir 'http_api.py')
    }
    'ws' {
        Write-Head "启动 WebSocket API（python ws_api.py，端口由 BOT_WS_PORT 决定：$($wsPort.Value)）"
        Write-Host "命令: $cmdWs"
        if ($DryRun) { Write-Note 'DryRun：未启动'; exit 0 }
        Set-DevPythonPath
        Set-Location -LiteralPath $ProjectDir
        & $VenvPython (Join-Path $ProjectDir 'ws_api.py')
    }
}
