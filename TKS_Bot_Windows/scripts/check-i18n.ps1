<#
.SYNOPSIS
  i18n 静态校验（PRD NFR-W-10 / W-P7 / V-W-S8）。

.DESCRIPTION
  ① 扫描 `TKSDesktop/**/*.cs` 与 `TKSDesktop/**/*.xaml`，若发现**界面文案**中硬编码中文即失败。
  ② 校验 `App/I18n.cs` 的 key 无重复、无空值。

  ⚠️ 判定规则与 xUnit 门禁 `Gate_VWS8_NoHardcodedChineseUiLiteralsInProductSources` **保持一致**，
     两者必须同时通过。规则：

     · **允许清单**（经 PRD 条款明确豁免的非 UI 中文）：`App/I18n.cs`（i18n 资源本体）、
       `Contracts/ProtocolConstants.cs`（C-5 历史分隔符，服务端常量）、
       `Contracts/LevelVisuals.cs`（C-1 等级称号，跨端契约值）、`Diagnostics/**`（NFR-W-15 自检报告文本）。
     · **日志 / 异常 / 诊断文本不算 UI 文案**：NFR-W-10 只管用户读到的界面文案；logger 消息、
       `throw new ...` 的异常消息、自检断言名属开发/诊断面。
     · 注释中的中文一律允许（用于文档说明）。
     · 语句按 `;` / `{` / `}` 归并后再判定：中文格式串常位于被折行的 `_logger.LogX(...)` 续行上，
       逐行扫描无法识别。

  ⚠️ 本脚本以 UTF-8 with BOM 保存：Windows PowerShell 5.1 读取无 BOM 的 UTF-8 文件时会按 ANSI
     解释，导致中文与其后的正则/引号被破坏（脚本本身会直接语法错误）。**不要去掉 BOM**。

.NOTES
  退出码：0 = 通过；1 = 发现违规。
#>

[CmdletBinding()]
param(
    [string]$SourceRoot
)

$ErrorActionPreference = 'Stop'
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)

if (-not $SourceRoot) {
    $SourceRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'TKSDesktop'
}

if (-not (Test-Path $SourceRoot)) { throw "源码目录不存在：$SourceRoot" }

$violations = New-Object System.Collections.Generic.List[string]
# 汉字 + 中日韩标点 + 全角字符
$cjk = '[\u4e00-\u9fff\u3000-\u303f\uff00-\uffef]'

# 允许清单（相对 TKSDesktop 的路径，用 / 分隔）
$allowList = @(
    'App/I18n.cs',
    'Contracts/ProtocolConstants.cs',
    'Contracts/LevelVisuals.cs'
)
$allowListPrefixes = @('Diagnostics/')

# 日志 / 异常 / 诊断标记
$diagnosticMarkers = @(
    'LogTrace', 'LogDebug', 'LogInformation', 'LogWarning', 'LogError', 'LogCritical',
    'logger.', '_logger.', 'ILogger', 'Console.', 'Debug.Write', 'Trace.Write',
    'throw new'
)

function Get-RelativePath {
    param([string]$FullPath)
    $root = (Resolve-Path $SourceRoot).Path.TrimEnd([char[]]@('\', '/'))
    $full = (Resolve-Path $FullPath).Path
    if ($full.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $full.Substring($root.Length).TrimStart([char[]]@('\', '/')).Replace('\', '/')
    }
    return $full
}

function Test-IsAllowListed {
    param([string]$RelativePath)
    if ($allowList -contains $RelativePath) { return $true }
    foreach ($p in $allowListPrefixes) {
        if ($RelativePath.StartsWith($p, [System.StringComparison]::Ordinal)) { return $true }
    }
    return $false
}

function Test-IsDiagnosticStatement {
    param([string]$Statement)
    foreach ($m in $diagnosticMarkers) {
        if ($Statement.IndexOf($m, [System.StringComparison]::Ordinal) -ge 0) { return $true }
    }
    return $false
}

# 去掉注释，保留字符串字面量内容（供文案扫描）。返回行数组（行号隐式保留）。
function Remove-Comments {
    param([string[]]$Lines, [switch]$KeepStringLiterals)

    $result = New-Object System.Collections.Generic.List[string]
    $inBlock = $false

    foreach ($line in $Lines) {
        $sb = New-Object System.Text.StringBuilder
        $i = 0
        while ($i -lt $line.Length) {
            if ($inBlock) {
                $end = $line.IndexOf('*/', $i)
                if ($end -lt 0) { $i = $line.Length; break }
                $inBlock = $false
                $i = $end + 2
                continue
            }

            $start = $line.IndexOf('/*', $i)
            $lineComment = $line.IndexOf('//', $i)

            if ($lineComment -ge 0 -and ($start -lt 0 -or $lineComment -lt $start)) {
                [void]$sb.Append($line.Substring($i, $lineComment - $i))
                $i = $line.Length
                break
            }

            if ($start -ge 0) {
                [void]$sb.Append($line.Substring($i, $start - $i))
                $inBlock = $true
                $i = $start + 2
                continue
            }

            [void]$sb.Append($line.Substring($i))
            $i = $line.Length
        }

        $result.Add($sb.ToString())
    }

    return $result
}

# 把行归并为语句（按 ; { } 断句），以便识别被折行的 LogX(...) 调用。
function Merge-Statements {
    param([string[]]$Lines)

    $result = New-Object System.Collections.Generic.List[object]
    $buffer = New-Object System.Text.StringBuilder
    $startLine = 0

    for ($n = 0; $n -lt $Lines.Count; $n++) {
        $text = $Lines[$n].Trim()
        if ($text -eq '') { continue }

        if ($buffer.Length -eq 0) { $startLine = $n + 1 }
        [void]$buffer.Append(' ').Append($text)

        if ($text.IndexOf(';') -ge 0 -or $text.EndsWith('{') -or $text.EndsWith('}') -or $buffer.Length -ge 8000) {
            $result.Add([pscustomobject]@{ Line = $startLine; Text = $buffer.ToString() })
            [void]$buffer.Clear()
        }
    }

    if ($buffer.Length -gt 0) {
        $result.Add([pscustomobject]@{ Line = $startLine; Text = $buffer.ToString() })
    }

    return $result
}

function Get-DoubleQuotedLiterals {
    param([string]$Text)

    $literals = New-Object System.Collections.Generic.List[string]
    $i = 0
    while ($i -lt $Text.Length) {
        $start = $Text.IndexOf('"', $i)
        if ($start -lt 0) { break }

        $sb = New-Object System.Text.StringBuilder
        $i = $start + 1
        $closed = $false
        while ($i -lt $Text.Length) {
            if ($Text[$i] -eq '\' -and $i + 1 -lt $Text.Length) {
                [void]$sb.Append($Text[$i]).Append($Text[$i + 1])
                $i += 2
                continue
            }
            if ($Text[$i] -eq '"') { $closed = $true; $i++; break }
            [void]$sb.Append($Text[$i])
            $i++
        }
        if ($closed) { $literals.Add($sb.ToString()) }
    }

    return $literals
}

Write-Host '  扫描 C# 与 XAML 中的硬编码中文...'
$files = Get-ChildItem -Path $SourceRoot -Recurse -Include *.cs, *.xaml -File |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' }

$scanned = 0
foreach ($file in $files) {
    $relative = Get-RelativePath -FullPath $file.FullName
    if (Test-IsAllowListed -RelativePath $relative) { continue }

    $scanned++
    $lines = Remove-Comments -Lines ([System.IO.File]::ReadAllLines($file.FullName))

    if ($file.Extension -eq '.xaml') {
        # XAML：逐行即可（无折行日志调用）；XML 注释已在 Remove-Comments 中按 // 处理，
        # 这里额外跳过 <!-- -->  的行。
        for ($n = 0; $n -lt $lines.Count; $n++) {
            $line = $lines[$n]
            $trimmed = $line.Trim()
            if ($trimmed.StartsWith('<!--')) { continue }
            if ($trimmed -match $cjk) {
                $violations.Add(("{0}:{1}: {2}" -f $relative, ($n + 1), $trimmed))
            }
        }
        continue
    }

    foreach ($stmt in (Merge-Statements -Lines $lines)) {
        if (Test-IsDiagnosticStatement -Statement $stmt.Text) { continue }
        if ($stmt.Text.TrimStart().StartsWith('[SuppressMessage')) { continue }
        if ($stmt.Text.IndexOf('Justification') -ge 0) { continue }

        foreach ($literal in (Get-DoubleQuotedLiterals -Text $stmt.Text)) {
            if ($literal -match $cjk) {
                $violations.Add(("{0}:{1}: {2}" -f $relative, $stmt.Line, $literal))
            }
        }
    }
}
Write-Host ("  已扫描 {0} 个文件（允许清单与自检目录已排除）" -f $scanned)

# ---- I18n key 重复/空值校验 ----
$i18nPath = Join-Path $SourceRoot 'App\I18n.cs'
$i18nIssue = $false

if (Test-Path $i18nPath) {
    Write-Host '  校验 I18n key 无重复、无空值...'
    $text = [System.IO.File]::ReadAllText($i18nPath)
    $keys = [regex]::Matches($text, '\["([^"]+)"\]\s*=') | ForEach-Object { $_.Groups[1].Value }

    $dupes = $keys | Group-Object | Where-Object { $_.Count -gt 1 }
    foreach ($d in $dupes) {
        $i18nIssue = $true
        Write-Host ("    [FAIL] 重复 key：{0}（{1} 次）" -f $d.Name, $d.Count) -ForegroundColor Red
    }

    $empty = $keys | Where-Object { [string]::IsNullOrWhiteSpace($_) }
    if ($empty) {
        $i18nIssue = $true
        Write-Host '    [FAIL] 存在空 key' -ForegroundColor Red
    }

    if (-not $dupes -and -not $empty) {
        Write-Host ("    [OK] {0} 个 key，无重复、无空值" -f $keys.Count) -ForegroundColor Green
    }
}
else {
    Write-Host "  [WARN] 未找到 $i18nPath，跳过 key 校验" -ForegroundColor Yellow
}

# ---- 汇总 ----
Write-Host ''
if ($violations.Count -gt 0) {
    Write-Host ("i18n 校验失败：发现 {0} 处硬编码中文界面文案（界面文案必须走 App/I18n.cs）" -f $violations.Count) -ForegroundColor Red
    $violations | Select-Object -First 40 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    if ($violations.Count -gt 40) { Write-Host ("  ...（另有 {0} 处）" -f ($violations.Count - 40)) -ForegroundColor Red }
    exit 1
}

if ($i18nIssue) {
    Write-Host 'i18n 校验失败：key 存在重复或空值' -ForegroundColor Red
    exit 1
}

Write-Host 'i18n 校验通过' -ForegroundColor Green
exit 0
