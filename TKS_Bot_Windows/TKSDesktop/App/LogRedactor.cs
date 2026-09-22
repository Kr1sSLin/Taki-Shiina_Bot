using System.Globalization;
using System.Text;

namespace TKSDesktop.App;

/// <summary>
/// 日志脱敏（PRD FR-W-SEC-3 / FR-W-LOG-2）。
///
/// 规则**逐条继承** Linux 端 `paths.ts::redactForLog`：
/// <list type="number">
///   <item>键名匹配 <c>token|password|secret|authorization|dataBase64|base64</c>（忽略大小写）→ 值替换为 <c>[redacted]</c>；</item>
///   <item>字符串长度 &gt; <see cref="Contracts.ProtocolConstants.LogRedactMaxChars"/>（400）→ 截断为前
///         <see cref="Contracts.ProtocolConstants.LogRedactKeepChars"/>（64）字符 + <c>…(redacted N chars)</c>；</item>
///   <item><b>递归</b>处理数组与嵌套对象。</item>
/// </list>
///
/// ⚠️ 硬要求：日志**禁止**输出 Token、密码、图片 base64。
/// </summary>
public static class LogRedactor
{
    /// <summary>被替换后的占位文本。</summary>
    public const string RedactedPlaceholder = "[redacted]";

    /// <summary>需要整体替换的键名（不含正则元字符，全部小写比较）。</summary>
    private static readonly string[] SensitiveKeyFragments =
    [
        "token", "password", "secret", "authorization", "database64", "base64",
    ];

    /// <summary>键名是否为敏感键（大小写不敏感的子串匹配）。</summary>
    public static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        foreach (var fragment in SensitiveKeyFragments)
        {
            if (key.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>对单个字符串值执行截断（规则 ②）。</summary>
    public static string RedactString(string value)
    {
        if (value.Length <= Contracts.ProtocolConstants.LogRedactMaxChars)
        {
            return value;
        }

        var keep = Contracts.ProtocolConstants.LogRedactKeepChars;
        var removed = value.Length - keep;
        return string.Concat(
            value.AsSpan(0, keep),
            string.Create(CultureInfo.InvariantCulture, $"…(redacted {removed} chars)"));
    }

    /// <summary>
    /// 递归脱敏一个标量值（字符串走规则 ①②）。
    /// </summary>
    public static object? RedactValue(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string s:
                return RedactString(s);
            case bool:
            case byte:
            case sbyte:
            case short:
            case ushort:
            case int:
            case uint:
            case long:
            case ulong:
            case float:
            case double:
            case decimal:
                return value;
            case System.Collections.IDictionary dict:
                return RedactDictionary(dict);
            case System.Collections.IEnumerable enumerable:
                return RedactEnumerable(enumerable);
            default:
                return RedactString(value.ToString() ?? string.Empty);
        }
    }

    private static Dictionary<string, object?> RedactDictionary(System.Collections.IDictionary source)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in source)
        {
            var key = entry.Key?.ToString() ?? string.Empty;
            result[key] = IsSensitiveKey(key) ? RedactedPlaceholder : RedactValue(entry.Value);
        }

        return result;
    }

    private static List<object?> RedactEnumerable(System.Collections.IEnumerable source)
    {
        var result = new List<object?>();
        foreach (var item in source)
        {
            result.Add(RedactValue(item));
        }

        return result;
    }

    /// <summary>
    /// 对「键 → 值」集合整体脱敏（日志写入前的统一入口）。
    /// </summary>
    public static Dictionary<string, object?> Redact(IEnumerable<KeyValuePair<string, object?>> fields)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in fields)
        {
            result[key] = IsSensitiveKey(key) ? RedactedPlaceholder : RedactValue(value);
        }

        return result;
    }

    /// <summary>
    /// 对一段文本做兜底脱敏：用于无法结构化处理的自由文本（如异常堆栈）。
    /// 会把形如 `Bearer xxx` / `"accessToken":"xxx"` 的片段替换掉。
    /// </summary>
    public static string RedactText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var line in text.Split('\n'))
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(RedactTextLine(line));
        }

        return RedactString(builder.ToString());
    }

    private static string RedactTextLine(string line)
    {
        var result = line;

        // `Bearer <token>`
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"(?i)bearer\s+[A-Za-z0-9\-._~+/]+=*",
            $"Bearer {RedactedPlaceholder}");

        // `"accessToken":"..."`（键名敏感的 JSON 片段）
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"(?i)""[A-Za-z0-9_]*(token|password|secret|authorization|base64)[A-Za-z0-9_]*""\s*:\s*""[^""]*""",
            $"\"$0-value\":\"{RedactedPlaceholder}\"");

        return result;
    }
}
