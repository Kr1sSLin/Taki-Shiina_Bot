#Requires -Version 5.1
<#
.SYNOPSIS
    TKS 一键发布（Windows / PowerShell 版）—— scripts/release.sh 的 Windows 对等实现。

.DESCRIPTION
    本脚本是为了让仓库能在 Windows 上正常开发与构建而新增的，**不替换** scripts/release.sh：
    Linux 侧继续使用 release.sh，两边脚本共用同一套版本号与产物命名约定。

    产物输出到 ~/Desktop/Release（可用 TKS_RELEASE_DIR 覆盖），命名：
        TKS-Android-<versionName>-<versionCode>.apk

    版本策略（与 release.sh 完全一致）：
        - 每次发布前先递增版本号，再构建，因此产物文件名里的版本号永远是"本次"的。
        - 补丁位 +1：versionName 1.2.1 -> 1.2.2，同时 versionCode +1。

    ⚠️ 关于 deb：electron-builder 的 Linux 目标（deb / AppImage / rpm）依赖 dpkg、fakeroot、
    appimagetool 等 Linux 工具链，**无法在 Windows 上构建**，electron-builder 亦不支持
    从 Windows 交叉构建 Linux 包。在 Windows 上请只发 apk；deb 请在 Linux 机器或 WSL 里
    执行 scripts/release.sh deb。

.PARAMETER Rest
    deb | apk | all（可多个；不传默认 apk）。同时接受 --no-bump / -NoBump 两种写法。

.PARAMETER NoBump
    不递增版本号，按当前版本号重新构建。-NoBump 与 --no-bump 等价。

.EXAMPLE
    .\scripts\release.ps1                 # 构建 release APK（自动递增版本号）
    .\scripts\release.ps1 apk             # 同上
    .\scripts\release.ps1 --no-bump       # 按当前版本号重新构建，不递增
    .\scripts\release.ps1 -Help           # 显示本帮助

.NOTES
    环境变量（全部可选，未设置时自动探测）：
        TKS_RELEASE_DIR            产物输出目录（默认 ~/Desktop/Release）
        JAVA_HOME                  JDK 17 或 21（未设置时自动探测 Android Studio 自带 JBR）
        ANDROID_HOME / ANDROID_SDK_ROOT   Android SDK（未设置时默认 %LOCALAPPDATA%\Android\Sdk）
        TKS_ANDROID_KEYSTORE       密钥库绝对路径（默认 $HOME\1.jks）
        TKS_ANDROID_KEYSTORE_PASS  storePassword 与 keyPassword（默认 123456，回退时会告警）
        TKS_ANDROID_KEY_ALIAS      密钥别名（默认 "1"）
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Rest = @(),

    [switch]$NoBump,

    [switch]$Help
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- 输出helpers
function Write-Step([string]$Message) {
    Write-Host ""
    Write-Host "▶ $Message" -ForegroundColor Cyan
}
function Write-Ok([string]$Message) {
    Write-Host "  ✓ $Message" -ForegroundColor Green
}
function Write-Warn2([string]$Message) {
    Write-Host "  ! $Message" -ForegroundColor Yellow
}
function Write-Err([string]$Message) {
    Write-Host "  ✗ $Message" -ForegroundColor Red
}

# 写 UTF-8 且**不带 BOM**。PowerShell 5.1 的 Set-Content -Encoding UTF8 会写 BOM，
# 那会让 build.gradle.kts 被 Gradle 解析异常，必须用 .NET API 显式关掉 BOM。
function Write-TextNoBom([string]$Path, [string]$Text) {
    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Text, $encoding)
}

# ---------------------------------------------------------------- 参数解析
$ShowHelp = $Help
$Targets = @()
$Bump = -not $NoBump

foreach ($arg in $Rest) {
    switch -Regex ($arg) {
        '^(deb|apk|all)$' { $Targets += $arg.ToLowerInvariant() }
        '^(--no-bump|-NoBump)$' { $Bump = $false }
        '^(-h|--help|-Help)$' { $ShowHelp = $true }
        default {
            Write-Err "未知参数：$arg"
            Write-Host "  用法：.\scripts\release.ps1 [deb|apk|all] [--no-bump]"
            exit 2
        }
    }
}
if ($Targets.Count -eq 0) { $Targets = @('apk') }
if ($Targets -contains 'all') { $Targets = @('apk', 'deb') }
# 去重但保持顺序
$Targets = $Targets | Select-Object -Unique

if ($ShowHelp) {
    Get-Help $PSCommandPath -Detailed
    exit 0
}

# ---------------------------------------------------------------- 路径定位
$RepoRoot = Split-Path -Parent $PSScriptRoot
$AndroidDir = Join-Path $RepoRoot 'TKS_Bot_Android'
$LinuxDir = Join-Path $RepoRoot 'TKS_Bot_Linux'
$GradleFile = Join-Path $AndroidDir 'app\build.gradle.kts'

if (Test-Path env:TKS_RELEASE_DIR) {
    $ReleaseDir = $env:TKS_RELEASE_DIR
}
else {
    # 用 .NET 取桌面路径，能正确处理 OneDrive 重定向过的桌面目录
    $desktop = [Environment]::GetFolderPath('Desktop')
    if ([string]::IsNullOrWhiteSpace($desktop)) {
        $desktop = Join-Path $env:USERPROFILE 'Desktop'
    }
    $ReleaseDir = Join-Path $desktop 'Release'
}

# ---------------------------------------------------------------- JDK 定位
# Gradle 8.13 只支持在 Java 8~23 上运行；AGP 8.13.2 需要 JDK 17+。
# 因此 JDK 26 这类过新的版本会直接失败，必须挑一个 17 或 21。
function Resolve-JavaHome {
    if ($env:JAVA_HOME -and (Test-Path (Join-Path $env:JAVA_HOME 'bin\java.exe'))) {
        return @{ Path = $env:JAVA_HOME; Source = 'JAVA_HOME 环境变量' }
    }

    $candidates = @(
        # Android Studio 自带的 JetBrains Runtime（通常是 21），最省事
        'C:\Program Files\Android\Android Studio\jbr',
        'C:\Program Files\Android\Android Studio1\jbr',
        "$env:LOCALAPPDATA\Programs\Android Studio\jbr"
    )

    # 常见 JDK 安装根目录下挑 17 / 21
    foreach ($root in @('C:\Program Files\Java', 'C:\Program Files\Eclipse Adoptium',
            'C:\Program Files\Microsoft\jdk', 'C:\Program Files\Zulu',
            'C:\Program Files\Amazon Corretto', "$env:USERPROFILE\.jdks",
            "$env:LOCALAPPDATA\Programs\Eclipse Adoptium")) {
        if (Test-Path $root) {
            $dirs = Get-ChildItem -Path $root -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match '(jdk|jbr)[-_.]?(17|21)' }
            foreach ($d in $dirs) { $candidates += $d.FullName }
        }
    }

    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c 'bin\java.exe')) {
            return @{ Path = $c; Source = "自动探测：$c" }
        }
    }
    return $null
}

function Resolve-AndroidSdk {
    foreach ($var in @('ANDROID_HOME', 'ANDROID_SDK_ROOT')) {
        $v = [Environment]::GetEnvironmentVariable($var)
        if ($v -and (Test-Path (Join-Path $v 'platforms'))) {
            return @{ Path = $v; Source = "$var 环境变量" }
        }
    }
    $guess = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
    if (Test-Path (Join-Path $guess 'platforms')) {
        return @{ Path = $guess; Source = '自动探测：%LOCALAPPDATA%\Android\Sdk' }
    }
    return $null
}

# ---------------------------------------------------------------- 版本号递增
function Get-AndroidVersionInfo {
    $text = [System.IO.File]::ReadAllText($GradleFile)
    $codeMatch = [regex]::Match($text, 'versionCode\s*=\s*(\d+)')
    $nameMatch = [regex]::Match($text, 'versionName\s*=\s*"([^"]+)"')
    if (-not $codeMatch.Success -or -not $nameMatch.Success) {
        throw "无法从 $GradleFile 解析出 versionCode / versionName"
    }
    return @{
        Code = [int]$codeMatch.Groups[1].Value
        Name = $nameMatch.Groups[1].Value
    }
}

function Add-PatchVersion([string]$Version) {
    $parts = $Version.Split('.')
    while ($parts.Count -lt 3) { $parts += '0' }
    $patch = [int]$parts[2] + 1
    return "$($parts[0]).$($parts[1]).$patch"
}

function Step-AndroidVersion {
    $cur = Get-AndroidVersionInfo
    $nextName = Add-PatchVersion $cur.Name
    $nextCode = $cur.Code + 1

    $text = [System.IO.File]::ReadAllText($GradleFile)
    # 只替换首个匹配，避免动到注释里的同名文本之外的其它内容
    $text = [regex]::Replace($text, "versionCode\s*=\s*$($cur.Code)", "versionCode = $nextCode", 1)
    $escaped = [regex]::Escape($cur.Name)
    $text = [regex]::Replace($text, "versionName\s*=\s*`"$escaped`"", "versionName = `"$nextName`"", 1)
    Write-TextNoBom -Path $GradleFile -Text $text

    Write-Ok "Android 版本：$($cur.Name)($($cur.Code)) → $nextName($nextCode)"
    return @{ Name = $nextName; Code = $nextCode }
}

# ---------------------------------------------------------------- 构建 APK
function Build-Apk {
    Write-Step "构建 Android APK（release，含签名）"

    if (-not (Test-Path $GradleFile)) {
        throw "未找到 $GradleFile —— 仓库结构不完整？"
    }

    $java = Resolve-JavaHome
    if (-not $java) {
        throw @"
未找到可用的 JDK 17 / 21。

Gradle 8.13 无法在 JDK 26 等过新版本上运行（会报 Unsupported class file major version）。
请任选其一：
  1) 安装 Android Studio（自带 JBR 21，脚本会自动探测）；
  2) 安装 JDK 17（如 Temurin）并设置 JAVA_HOME，例如：
       `$env:JAVA_HOME = 'C:\Program Files\Eclipse Adoptium\jdk-17.0.x-hotspot'
"@
    }
    $env:JAVA_HOME = $java.Path
    Write-Ok "JDK：$($java.Path)  （$($java.Source)）"

    $sdk = Resolve-AndroidSdk
    if (-not $sdk) {
        throw @"
未找到 Android SDK。

请任选其一：
  1) 安装 Android Studio 并在 SDK Manager 中安装 Android SDK Platform 34 与 Build-Tools；
  2) 设置 ANDROID_HOME，例如：
       `$env:ANDROID_HOME = "`$env:LOCALAPPDATA\Android\Sdk"
"@
    }
    $env:ANDROID_HOME = $sdk.Path
    $env:ANDROID_SDK_ROOT = $sdk.Path
    Write-Ok "Android SDK：$($sdk.Path)  （$($sdk.Source)）"

    if (-not (Test-Path (Join-Path $sdk.Path 'platforms\android-34'))) {
        Write-Warn2 "SDK 中未找到 platforms;android-34（compileSdk=34），构建可能失败"
        Write-Warn2 "可用 sdkmanager 安装：sdkmanager `"platforms;android-34`""
    }

    # 签名：与 release.sh 保持同一套默认值，保证能覆盖安装已装的旧版本。
    #
    # ⚠️ 关键坑（已实测）：app/build.gradle.kts 判定 hasReleaseSigning 时**只看环境变量是否有值**，
    # 并不校验密钥文件是否存在：
    #     val hasReleaseSigning = !releaseKeystorePath.isNullOrBlank() && !releaseKeystorePass.isNullOrBlank()
    # 因此一旦在这里把变量设上、而文件其实不存在，Gradle 会创建出 signingConfig 并在
    # :app:validateSigningRelease 直接**硬失败**：
    #     Keystore file '...\1.jks' not found for signing config 'release'.
    # 这与其「缺密钥就退化为未签名产物」的设计意图相反。正确做法是：文件不存在时
    # **把变量清空**，让构建真正走 unsigned 分支。
    $keystore = if ($env:TKS_ANDROID_KEYSTORE) { $env:TKS_ANDROID_KEYSTORE }
    else { Join-Path $env:USERPROFILE '1.jks' }

    if (Test-Path -LiteralPath $keystore -PathType Leaf) {
        $env:TKS_ANDROID_KEYSTORE = $keystore
        if (-not $env:TKS_ANDROID_KEY_ALIAS) {
            $env:TKS_ANDROID_KEY_ALIAS = '1'
        }
        if (-not $env:TKS_ANDROID_KEYSTORE_PASS) {
            $env:TKS_ANDROID_KEYSTORE_PASS = '123456'
            Write-Warn2 "TKS_ANDROID_KEYSTORE_PASS 未设置，回退到内置默认口令（与 release.sh 一致）"
            Write-Warn2 "生产发布建议显式设置该环境变量，不要依赖默认值"
        }
        Write-Ok "签名密钥：$keystore（别名 $($env:TKS_ANDROID_KEY_ALIAS)）"
    }
    else {
        # 清空而非留空值：Gradle 侧是 isNullOrBlank() 判定，空字符串同样会误判为"有配置"
        Remove-Item Env:\TKS_ANDROID_KEYSTORE -ErrorAction SilentlyContinue
        Remove-Item Env:\TKS_ANDROID_KEYSTORE_PASS -ErrorAction SilentlyContinue
        Remove-Item Env:\TKS_ANDROID_KEY_ALIAS -ErrorAction SilentlyContinue
        Write-Warn2 "未找到签名密钥 $keystore —— 已清除签名相关环境变量，将产出未签名 APK"
        Write-Warn2 "未签名 APK 无法覆盖安装已装的 release 版"
    }

    $gradlew = Join-Path $AndroidDir 'gradlew.bat'
    if (-not (Test-Path $gradlew)) {
        throw "未找到 $gradlew"
    }

    Push-Location $AndroidDir
    try {
        # PowerShell 5.1 的已知坑：当 $ErrorActionPreference = 'Stop'（脚本顶部所设）时，
        # **原生命令写到 stderr 的任何输出都会被当作终止性错误抛出**。Gradle 会把
        # "Downloading ..."、警告、进度等写到 stderr，于是构建刚起步就被 PowerShell
        # 打断，而且看不到真实报错。这里局部降级为 Continue，改用 $LASTEXITCODE
        # 判定成败——这才是原生命令的正确判定方式。
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            Write-Host "  执行：gradlew.bat :app:assembleRelease" -ForegroundColor DarkGray
            # 注意两处易踩的坑：
            # 1) `| Out-Host`：原生命令的 stdout 会进入 PowerShell 的**输出流**，若不拦住，
            #    它会被当作本函数的"返回值"一起被 `$artifacts += Build-Apk` 捕获，最终污染
            #    产物列表（表现为 Get-FileHash 收到空字符串而报错）。Out-Host 既能保持
            #    进度实时可见，又不进管道。
            # 2) `-D...` 参数**必须加引号**：不加引号时 PowerShell 会把它拆成
            #    `-Dkotlin` 与 `.compiler.execution.strategy=in-process` 两段，Gradle 会把
            #    后者当成任务名并报 `Task '.compiler...' not found`。
            & $gradlew ':app:assembleRelease' '--console=plain' '-Dkotlin.compiler.execution.strategy=in-process' | Out-Host
            $gradleExit = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $prevEap
        }
    }
    finally {
        Pop-Location
    }
    if ($gradleExit -ne 0) {
        throw "Gradle 构建失败（退出码 $gradleExit）"
    }

    $signed = Join-Path $AndroidDir 'app\build\outputs\apk\release\app-release.apk'
    $unsigned = Join-Path $AndroidDir 'app\build\outputs\apk\release\app-release-unsigned.apk'
    $src = $null
    if (Test-Path $signed) { $src = $signed }
    elseif (Test-Path $unsigned) { $src = $unsigned }
    if (-not $src) {
        throw "未找到 apk 产物（已查找 app-release.apk 与 app-release-unsigned.apk）"
    }

    $version = Get-AndroidVersionInfo
    New-Item -ItemType Directory -Force -Path $ReleaseDir | Out-Null
    $dst = Join-Path $ReleaseDir "TKS-Android-$($version.Name)-$($version.Code).apk"
    Copy-Item -Force -Path $src -Destination $dst

    $sizeMb = [math]::Round((Get-Item $dst).Length / 1MB, 1)
    Write-Ok "apk → $dst ($sizeMb MB)"

    # 签名校验
    $buildTools = Get-ChildItem -Path (Join-Path $sdk.Path 'build-tools') -Directory -ErrorAction SilentlyContinue |
        Sort-Object { [version]($_.Name -replace '[^0-9.]', '') } |
        Select-Object -Last 1
    if ($buildTools) {
        $apksigner = Join-Path $buildTools.FullName 'apksigner.bat'
        if (Test-Path $apksigner) {
            # 同上：apksigner 也会往 stderr 写内容，必须避免触发 Stop。
            $prevEap2 = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $certOut = & $apksigner verify --print-certs $dst 2>&1
            }
            finally {
                $ErrorActionPreference = $prevEap2
            }
            $certs = $certOut | Select-String -Pattern 'certificate DN' | Select-Object -First 1
            if ($certs) {
                Write-Ok "签名：$($certs.ToString().Trim())"
            }
            else {
                Write-Warn2 "APK 未签名（无法覆盖安装已装的 release 版）"
            }
        }
    }

    return $dst
}

# ---------------------------------------------------------------- deb：Windows 上不可构建
function Invoke-DebTarget {
    Write-Step "Linux deb"

    $wslExe = Get-Command wsl.exe -ErrorAction SilentlyContinue
    $hasWslDistro = $false
    if ($wslExe) {
        # 未安装任何发行版时 wsl -l -q 会输出帮助文本而非发行版名
        $list = & wsl.exe --list --quiet 2>$null
        # 原生命令的失败退出码会污染脚本自身的退出码，这里取走后立刻归零
        $wslExit = $LASTEXITCODE
        $LASTEXITCODE = 0
        if ($wslExit -eq 0 -and $list) {
            $names = @($list | Where-Object { $_ -and $_.Trim() -ne '' })
            if ($names.Count -gt 0) { $hasWslDistro = $true }
        }
    }

    Write-Warn2 "electron-builder 的 Linux 目标（deb/AppImage/rpm）依赖 dpkg、fakeroot 等 Linux 工具链，"
    Write-Warn2 "无法在 Windows 上构建，也不支持从 Windows 交叉构建。本机未执行 deb 构建。"

    if ($hasWslDistro) {
        Write-Host ""
        Write-Host "  检测到 WSL 发行版。可在 WSL 中执行：" -ForegroundColor Cyan
        Write-Host "      wsl -- bash -lc 'cd `$(wslpath ''$RepoRoot'') && scripts/release.sh deb'" -ForegroundColor DarkGray
    }
    else {
        Write-Host ""
        Write-Host "  请在 Linux 机器上执行：scripts/release.sh deb" -ForegroundColor Cyan
    }
    return $null
}

# ---------------------------------------------------------------- 主流程
Write-Host ""
Write-Host "TKS 发布（Windows）　输出目录：$ReleaseDir" -ForegroundColor White
if ($Targets -contains 'apk') {
    Write-Step $(if ($Bump) { '递增版本号' } else { '跳过版本号递增（--no-bump）' })
    }

$artifacts = @()

foreach ($t in $Targets) {
    switch ($t) {
        'apk' {
            if ($Bump) {
                $newVersion = Step-AndroidVersion
            }
            else {
                $cur = Get-AndroidVersionInfo
                $newVersion = @{ Name = $cur.Name; Code = $cur.Code }
            }
            $artifacts += Build-Apk
        }
        'deb' {
            $debPath = Invoke-DebTarget
            if ($debPath) { $artifacts += $debPath }
        }
    }
}

Write-Step "产物清单"
Get-ChildItem -Path $ReleaseDir -ErrorAction SilentlyContinue | ForEach-Object {
    $mb = [math]::Round($_.Length / 1MB, 1)
    Write-Host ("  {0,-50} {1,8} MB" -f $_.Name, $mb)
}

if ($artifacts.Count -gt 0) {
    Write-Step "SHA256"
    foreach ($a in $artifacts) {
        if (Test-Path $a) {
            $hash = (Get-FileHash -Algorithm SHA256 -Path $a).Hash.ToLowerInvariant()
            Write-Host ("  {0}  {1}" -f $hash, (Split-Path -Leaf $a))
        }
    }
}

Write-Host ""
Write-Host "发布完成" -ForegroundColor Green
Write-Host ""

# 显式退出 0：脚本内部调用过原生命令（wsl / gradlew / apksigner），
# 若不显式退出，$LASTEXITCODE 会把它们的残留码当成脚本退出码，破坏 CI 判定。
exit 0
