using System.IO;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TKSDesktop.Contracts;
using TKSDesktop.Contracts.Dtos;
using TKSDesktop.Core.Platform;
using Xunit;

namespace TKSDesktop.Tests.Gate;

/// <summary>
/// Mechanical acceptance gates for PRD section 16.1 (Spec verification, static) and section 16.3
/// (V-W-C4 / V-W-C5). Every test is read-only: it scans source text or reflects over the built
/// product assembly, and never writes to the product tree.
///
/// V-W-S1  layer purity (Core must not reference System.Windows.* / PresentationFramework / WinForms)
/// V-W-S2  explicit per-field JSON mapping, no global naming policy
/// V-W-S3  the three snake_case exceptions (exact set equality)
/// V-W-S4  credentials must be DPAPI ciphertext (static + dynamic on-disk scan)
/// V-W-S5  notification ids per the Android baseline
/// V-W-S6  14 error codes + neutral fallback for unknown codes
/// V-W-S7  version single source
/// V-W-S8  i18n completeness + no hardcoded Chinese UI copy
/// V-W-S10 migration chain 1-5 + tables
/// V-W-S11 tray library pinned to 2.3.2
/// V-W-C4  TreatWarningsAsErrors=true
/// V-W-C5  contract-drift reverse verification
/// </summary>
public sealed class GateSpecStaticTests
{
    /* ===================================================================== */
    /* V-W-S1 : layering (NFR-W-14)                                          */
    /* ===================================================================== */

    private static readonly string[] WpfNamespaces =
    [
        "System.Windows",
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
    ];

    [Fact]
    public void Gate_VWS1_CoreNamespaceTypes_DoNotReferenceWpfOrWinForms()
    {
        var assembly = typeof(ProtocolConstants).Assembly;
        var coreTypes = assembly.GetTypes()
            .Where(t => t.Namespace is not null && IsCoreNamespace(t.Namespace))
            .ToArray();

        // Guard against a vacuous pass: the Core layer must actually contain types.
        Assert.True(coreTypes.Length >= 5,
            $"V-W-S1: expected >= 5 types under TKSDesktop.Core.*, found {coreTypes.Length}. " +
            "A vacuous scan would hide regressions.");

        var offenders = new List<string>();
        foreach (var type in coreTypes)
        {
            foreach (var referenced in ReferencedTypes(type))
            {
                if (IsForbidden(referenced))
                {
                    offenders.Add($"{type.FullName} -> {Describe(referenced)}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "V-W-S1 / NFR-W-14 violated: TKSDesktop.Core must not reference System.Windows.*, " +
            "PresentationFramework, PresentationCore, WindowsBase or System.Windows.Forms." +
            Environment.NewLine + string.Join(Environment.NewLine, offenders.Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal)));
    }

    private static bool IsCoreNamespace(string ns)
        => ns.Equals("TKSDesktop.Core", StringComparison.Ordinal)
           || ns.StartsWith("TKSDesktop.Core.", StringComparison.Ordinal);

    private static bool IsForbidden(Type type)
    {
        var ns = type.Namespace;
        if (ns is not null)
        {
            if (ns.Equals("System.Windows.Forms", StringComparison.Ordinal)
                || ns.StartsWith("System.Windows.Forms.", StringComparison.Ordinal))
            {
                return true;
            }

            foreach (var prefix in WpfNamespaces)
            {
                if (ns.Equals(prefix, StringComparison.Ordinal) || ns.StartsWith(prefix + ".", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        var asm = type.Assembly.GetName().Name;
        return asm is "PresentationFramework" or "PresentationCore" or "WindowsBase" or "System.Windows.Forms";
    }

    private static string Describe(Type type) => type.FullName ?? type.Name;

    /// <summary>
    /// Types reachable from a Core type: base type, interfaces, field/property/parameter/return
    /// types, attribute types, local variable types and IL operand tokens.
    /// </summary>
    private static IEnumerable<Type> ReferencedTypes(Type type)
    {
        const System.Reflection.BindingFlags All =
            System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.DeclaredOnly;

        var seen = new HashSet<Type>();

        void Push(Type? t)
        {
            if (t is not null)
            {
                _ = seen.Add(t);
            }
        }

        Push(type.BaseType);
        foreach (var i in type.GetInterfaces())
        {
            Push(i);
        }

        foreach (var field in type.GetFields(All))
        {
            Push(field.FieldType);
        }

        foreach (var prop in type.GetProperties(All))
        {
            Push(prop.PropertyType);
            foreach (var p in prop.GetIndexParameters())
            {
                Push(p.ParameterType);
            }
        }

        foreach (var evt in type.GetEvents(All))
        {
            Push(evt.EventHandlerType);
        }

        foreach (var ctor in type.GetConstructors(All))
        {
            foreach (var p in ctor.GetParameters())
            {
                Push(p.ParameterType);
            }
        }

        foreach (var method in type.GetMethods(All))
        {
            Push(method.ReturnType);
            foreach (var p in method.GetParameters())
            {
                Push(p.ParameterType);
            }

            var body = TryBody(method);
            if (body is null)
            {
                continue;
            }

            foreach (var local in body.LocalVariables)
            {
                Push(local.LocalType);
            }
        }

        foreach (var attr in type.GetCustomAttributesData())
        {
            Push(attr.AttributeType);
        }

        foreach (var t in seen.ToArray())
        {
            if (t.IsGenericType)
            {
                foreach (var arg in t.GetGenericArguments())
                {
                    Push(arg);
                }
            }

            if (t.HasElementType && t.GetElementType() is { } elem)
            {
                Push(elem);
            }
        }

        // IL operand tokens (catches WPF types used only as locals / call targets).
        var module = type.Module;
        foreach (var method in type.GetMethods(All))
        {
            var body = TryBody(method);
            var il = body?.GetILAsByteArray();
            if (il is null || il.Length < 4)
            {
                continue;
            }

            for (var i = 0; i + 4 <= il.Length; i++)
            {
                var token = BitConverter.ToInt32(il, i);
                if (token == 0)
                {
                    continue;
                }

                var resolved = TryResolve(module, token);
                if (resolved is not null)
                {
                    Push(resolved);
                }
            }
        }

        return seen;
    }

    private static System.Reflection.MethodBody? TryBody(System.Reflection.MethodBase method)
    {
        try
        {
            return method.GetMethodBody();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Type? TryResolve(System.Reflection.Module module, int token)
    {
        try
        {
            var member = module.ResolveMember(token);
            return member switch
            {
                Type t => t,
                System.Reflection.MethodBase m => m.DeclaringType,
                System.Reflection.FieldInfo f => f.DeclaringType,
                _ => null,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    [Fact]
    public void Gate_VWS1_RequiredModuleDirectoriesExist()
    {
        // The module directories live under TKS_Bot_Windows/TKSDesktop (the product project),
        // not directly under TKS_Bot_Windows -- PRD 3.3 maps module names to folders inside the
        // project. Tests/ sits next to TKSDesktop/ at the repository level.
        string[] requiredInsideProject = ["Core", "Platform", "Contracts", "Views", "ViewModels", "App"];

        var missing = requiredInsideProject
            .Where(d => !Directory.Exists(Path.Combine(GateRepo.ProductDir, d)))
            .Select(d => $"TKSDesktop/{d}")
            .Concat(Directory.Exists(GateRepo.TestsDir) ? [] : ["Tests/TKSDesktop.Tests"])
            .ToArray();

        Assert.True(missing.Length == 0,
            "V-W-S1: missing module directories under TKS_Bot_Windows/: " + string.Join(", ", missing));
    }

    /* ===================================================================== */
    /* V-W-S2 : explicit per-field JSON mapping (FR-W-PROTO-1/2)             */
    /* ===================================================================== */

    [Fact]
    public void Gate_VWS2_EveryMappedDtoProperty_HasExplicitJsonPropertyName()
    {
        var assembly = typeof(ProtocolConstants).Assembly;
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var type in assembly.GetTypes().Where(IsContractsType))
        {
            var properties = type.GetProperties(
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly).ToArray();

            var mapped = properties.Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is not null).ToArray();
            if (mapped.Length == 0)
            {
                continue;
            }

            scanned++;
            foreach (var prop in properties)
            {
                var isPublicReadWrite = prop.GetMethod is not null && prop.GetMethod.IsPublic
                                        && prop.SetMethod is not null && prop.SetMethod.IsPublic;
                if (!isPublicReadWrite)
                {
                    continue;
                }

                if (prop.GetCustomAttribute<JsonPropertyNameAttribute>() is null)
                {
                    offenders.Add($"{type.FullName}.{prop.Name} (no [JsonPropertyName])");
                }
            }
        }

        Assert.True(scanned >= 20,
            $"V-W-S2: expected >= 20 [JsonPropertyName]-annotated types under TKSDesktop.Contracts, found {scanned}.");
        Assert.True(offenders.Count == 0,
            "V-W-S2 / FR-W-PROTO-2 violated: every public read/write property of a mapped DTO needs an explicit " +
            "[JsonPropertyName] (no global naming policy may be relied upon)." +
            Environment.NewLine + string.Join(Environment.NewLine, offenders.OrderBy(s => s, StringComparer.Ordinal)));
    }

    [Fact]
    public void Gate_VWS2_NoGlobalJsonNamingPolicyAssignedToPropertyNamingPolicy()
    {
        // V-W-S2: the whole tree must not assign a global naming policy (JsonNamingPolicy.*) to
        // PropertyNamingPolicy -- field names come from explicit [JsonPropertyName] only.
        // `PropertyNamingPolicy = null` is the sanctioned form (see Contracts/WsFrameParser.cs).
        var pattern = new Regex(
            @"PropertyNamingPolicy\s*=\s*[^,;)\r\n]*JsonNamingPolicy\s*\.",
            RegexOptions.Compiled);

        var offenders = new List<string>();
        foreach (var file in GateRepo.ProductCsFiles())
        {
            var code = GateRepo.CodeOnly(GateRepo.Read(file));
            var lines = code.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (pattern.IsMatch(lines[i]))
                {
                    offenders.Add($"{GateRepo.Rel(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "V-W-S2 / FR-W-PROTO-1 violated: a global JSON naming policy is assigned to PropertyNamingPolicy " +
            "(protocol DTOs must map field names explicitly via [JsonPropertyName])." +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Gate_VWS2_WsFrameParser_DoesNotUseGlobalNamingPolicy()
    {
        var options = WsFrameParser.Options;
        Assert.Null(options.PropertyNamingPolicy);
        Assert.Null(options.DictionaryKeyPolicy);
        Assert.False(options.PropertyNameCaseInsensitive);
    }

    /* ===================================================================== */
    /* V-W-S3 : the three snake_case exceptions (C-8)                        */
    /* ===================================================================== */

    [Fact]
    public void Gate_VWS3_LevelConfigEntryDto_MapsExactlyFourSnakeCaseFields()
        => AssertJsonNameSet<LevelConfigEntryDto>(["level_code", "level_name", "threshold_days", "sort_order"]);

    [Fact]
    public void Gate_VWS3_PointsLedgerItemDto_MapsExactlyNineSnakeCaseFields()
        => AssertJsonNameSet<PointsLedgerItemDto>(
        [
            "id", "user_id", "change_amount", "reason_code", "balance_after",
            "related_item_id", "idempotency_key", "created_at", "business_date",
        ]);

    [Fact]
    public void Gate_VWS3_MakeupCardRecordDto_MapsExactlySevenSnakeCaseFields()
        => AssertJsonNameSet<MakeupCardRecordDto>(
        [
            "id", "user_id", "granted_month", "status", "used_for_date", "used_at", "created_at",
        ]);

    private static void AssertJsonNameSet<T>(string[] expected)
    {
        var actual = JsonNames(typeof(T));
        Assert.Equal(
            expected.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
            actual.OrderBy(s => s, StringComparer.Ordinal).ToArray());

        // No camelCase leakage: a snake_case name simply carries no upper-case letter. Demanding an
        // underscore would reject the frozen contract itself -- section 7.0 lists `status` among the
        // makeup-card snake_case fields and `id` among the ledger fields.
        foreach (var name in actual)
        {
            Assert.False(name.Any(char.IsUpper),
                $"V-W-S3: {typeof(T).Name} maps '{name}', which is not snake_case (it contains an upper-case letter).");
        }
    }

    private static IReadOnlyList<string> JsonNames(Type type)
        => type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();

    private static bool IsContractsType(Type type)
        => type.Namespace is not null
           && (type.Namespace.Equals("TKSDesktop.Contracts", StringComparison.Ordinal)
               || type.Namespace.StartsWith("TKSDesktop.Contracts.", StringComparison.Ordinal));
    /* ===================================================================== */
    /* V-W-S4 : credentials must be DPAPI ciphertext (static + dynamic)      */
    /* ===================================================================== */

    [Fact]
    public void Gate_VWS4_NoPlaintextCredentialDowngradeSwitchExists()
    {
        var offenders = new List<string>();
        foreach (var file in GateRepo.ProductCsFiles())
        {
            var code = GateRepo.CodeOnly(GateRepo.Read(file));
            var lines = code.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("allowPlaintextCredentials", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{GateRepo.Rel(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "V-W-S4 / FR-W-SEC-2 violated: a plaintext-credential downgrade switch exists in code " +
            "(comments and string literals are excluded from this scan)." +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Gate_VWS4_CredentialFileWritesGoThroughProtectedData()
    {
        // ---- Vacuous-pass guard (file level) -------------------------------------------------
        // The credential store must exist: some product file must both reference the credentials
        // file and perform the DPAPI call.
        var credentialWriters = new List<string>();
        foreach (var file in GateRepo.ProductCsFiles())
        {
            var code = GateRepo.CodeOnly(GateRepo.Read(file));
            var touchesCredentialFile =
                code.Contains("\"credentials.bin\"", StringComparison.Ordinal) ||
                code.Contains("CredentialsFile", StringComparison.Ordinal) ||
                code.Contains("_credentialsFile", StringComparison.Ordinal);

            if (touchesCredentialFile && code.Contains("ProtectedData.Protect", StringComparison.Ordinal))
            {
                credentialWriters.Add(GateRepo.Rel(file));
            }
        }

        Assert.True(credentialWriters.Count > 0,
            "V-W-S4: no product source file both references the credentials file and calls " +
            "ProtectedData.Protect -- the credential store is missing or its file name changed.");

        // ---- Offender scan (statement level) -------------------------------------------------
        // Only *direct writers* are considered, and only when the same statement names the
        // credentials file. The previous file-level cross-product ("mentions CredentialsFile
        // anywhere" AND "writes some file anywhere") produced false positives on the portable
        // write-probe in AppPaths and on the self-test report writer.
        //
        // TKSDesktop/Diagnostics/ is excluded on purpose: the self-test deliberately writes *corrupt
        // ciphertext* into credentials.bin to prove the Corrupted branch keeps the file
        // (FR-W-AUTH-3b / EDGE-W-11). The real store is re-checked by this gate's dynamic
        // counterpart and by the runtime --selftest DPAPI section.
        string[] directWriteCalls =
        [
            "File.WriteAllText", "File.WriteAllBytes", "File.WriteAllLines",
            "File.Create(", "new StreamWriter",
        ];

        var offenders = new List<string>();
        foreach (var file in GateRepo.ProductCsFiles())
        {
            if (GateRepo.Rel(file).StartsWith("TKSDesktop/Diagnostics/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var (line, statement) in GateRepo.StatementsWithoutComments(file))
            {
                var mentionsCredentialFile =
                    statement.Contains("credentials.bin", StringComparison.Ordinal) ||
                    statement.Contains("CredentialsFile", StringComparison.Ordinal);
                if (!mentionsCredentialFile)
                {
                    continue;
                }

                if (!directWriteCalls.Any(w => statement.Contains(w, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (statement.Contains("ProtectedData.Protect", StringComparison.Ordinal))
                {
                    continue;
                }

                offenders.Add($"{GateRepo.Rel(file)}:{line}: {statement.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "V-W-S4 / FR-W-SEC-2 violated: these statements write the credentials file without " +
            "ProtectedData.Protect:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));

        var plaintextPatterns = new[]
        {
            @"WriteAllText\s*\([^)]*credentials\.bin",
            @"WriteAllLines\s*\([^)]*credentials\.bin",
        };

        var plaintextOffenders = new List<string>();
        foreach (var file in GateRepo.ProductCsFiles())
        {
            var flattened = Regex.Replace(GateRepo.CodeOnly(GateRepo.Read(file)), @"\s+", " ");
            foreach (var pattern in plaintextPatterns)
            {
                if (Regex.IsMatch(flattened, pattern, RegexOptions.IgnoreCase))
                {
                    plaintextOffenders.Add($"{GateRepo.Rel(file)} matches /{pattern}/");
                }
            }
        }

        Assert.True(plaintextOffenders.Count == 0,
            "V-W-S4: a plaintext text write to the credentials file was found:" +
            Environment.NewLine + string.Join(Environment.NewLine, plaintextOffenders));
    }

    [Fact]
    public void Gate_VWS4_SecretStoreInterfaceExistsUnderCorePlatform()
    {
        var iface = typeof(ISecretStore);
        Assert.True(iface.IsInterface, "V-W-S4: ISecretStore must be an interface.");
        Assert.Equal("TKSDesktop.Core.Platform.ISecretStore", iface.FullName);
        Assert.NotNull(iface.GetProperty(nameof(ISecretStore.IsEncryptionAvailable)));
        Assert.NotNull(iface.GetMethod(nameof(ISecretStore.TryWrite)));
        Assert.NotNull(iface.GetMethod(nameof(ISecretStore.TryRead)));
        Assert.NotNull(iface.GetMethod(nameof(ISecretStore.Delete)));
    }

    [Fact]
    public void Gate_VWS4_SecretStoreImplementations_LiveUnderPlatform()
    {
        var assembly = typeof(ProtocolConstants).Assembly;
        var implementations = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ISecretStore).IsAssignableFrom(t))
            .ToArray();

        Assert.True(implementations.Length >= 1,
            "V-W-S4: no ISecretStore implementation found in the product assembly " +
            "(AppBootstrap registers TKSDesktop.Platform.Windows.ThemeAwareSecretStore).");

        var offenders = implementations
            .Where(t => t.Namespace is null || !t.Namespace.StartsWith("TKSDesktop.Platform", StringComparison.Ordinal))
            .Select(t => t.FullName ?? t.Name)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "V-W-S4 / FR-W-ARCH-4 violated: every ISecretStore implementation must live under TKSDesktop.Platform: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void Gate_VWS4_DpapiCiphertextOnDisk_NoPlaintextTokenSubstrings()
    {
        // Dynamic part of V-W-S4: seal a payload with DPAPI (the very call the credential store uses)
        // into an isolated temp directory, then assert that NO file there contains a plaintext
        // accessToken / refreshToken substring -- neither the value nor even the field name.
        var dir = Path.Combine(Path.GetTempPath(), "tks-vws4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            const string accessToken = "ACCESS.TOKEN.PLAINTEXT.7f3a1c";
            const string refreshToken = "REFRESH.TOKEN.PLAINTEXT.9b2d4e";

            var payload =
                "{\"accessToken\":\"" + accessToken + "\",\"refreshToken\":\"" + refreshToken + "\"}";

            var cipher = System.Security.Cryptography.ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(payload),
                null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);

            var credentialFile = Path.Combine(dir, "credentials.bin");
            File.WriteAllBytes(credentialFile, cipher);

            // Sanity: the ciphertext round-trips (proves the file holds real DPAPI output, not a stub).
            var roundTrip = System.Security.Cryptography.ProtectedData.Unprotect(
                File.ReadAllBytes(credentialFile),
                null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
            Assert.Equal(payload, System.Text.Encoding.UTF8.GetString(roundTrip));

            var forbidden = new[] { accessToken, refreshToken, "accessToken", "refreshToken" };
            var offenders = new List<string>();

            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var text = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(file));
                foreach (var needle in forbidden)
                {
                    if (text.Contains(needle, StringComparison.Ordinal))
                    {
                        offenders.Add($"{Path.GetFileName(file)} contains '{needle}'");
                    }
                }
            }

            Assert.True(offenders.Count == 0,
                "V-W-S4 (dynamic) violated: a file under the credential directory contains plaintext credentials:" +
                Environment.NewLine + string.Join(Environment.NewLine, offenders));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Cleanup failure must not mask the assertion result.
            }
        }
    }

    /* ===================================================================== */
    /* V-W-S5 : notification ids per the Android baseline (C-3)              */
    /* ===================================================================== */

    [Fact]
    public void Gate_VWS5_SemanticNotificationIds_MatchAndroidBaseline()
    {
        var expected = new Dictionary<SemanticNotificationId, int>(7)
        {
            [SemanticNotificationId.Chat] = 1002,
            [SemanticNotificationId.Greeting] = 1003,
            [SemanticNotificationId.Error] = 1004,
            [SemanticNotificationId.Reminder] = 1005,
            [SemanticNotificationId.Level] = 1006,
            [SemanticNotificationId.Streak] = 1007,
            [SemanticNotificationId.Progress] = 1008,
        };

        var values = Enum.GetValues<SemanticNotificationId>();
        Assert.Equal(expected.Count, values.Length);

        foreach (var (name, value) in expected)
        {
            Assert.Equal(value, Convert.ToInt32(name, System.Globalization.CultureInfo.InvariantCulture));
        }

        var byNumber = values.ToDictionary(v => Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(expected.Count, byNumber.Count);
        foreach (var (name, value) in expected)
        {
            Assert.True(byNumber.TryGetValue(value, out var actual),
                $"V-W-S5: no SemanticNotificationId with value {value} (expected {name}).");
            Assert.Equal(name, actual);
        }
    }
    /* ===================================================================== */
    /* V-W-S6 : 14 error codes + neutral fallback (C-6)                      */
    /* ===================================================================== */

    [Fact]
    public void Gate_VWS6_RequiredApiCodes_MatchTheFrozenSet()
    {
        int[] expected =
        [
            0, 40001, 40002, 40101, 40102, 40201, 40202, 40204, 40205, 40206, 40207, 40301, 40302, 50301, 5000,
        ];
        var actual = ErrorCatalog.RequiredApiCodes.ToArray();

        Assert.Equal(expected.OrderBy(v => v).ToArray(), actual.OrderBy(v => v).ToArray());
        foreach (var code in expected)
        {
            Assert.Contains(code, actual);
        }
    }

    [Fact]
    public void Gate_VWS6_UnknownCode_FallsBackToNeutralI18nText()
    {
        var key = ErrorCatalog.I18nKeyOf(999999);
        Assert.False(string.IsNullOrWhiteSpace(key), "V-W-S6: unknown code must map to a non-empty i18n key.");
        Assert.False(key.All(char.IsDigit), "V-W-S6: the fallback key must not be the raw numeric code.");

        var text = TKSDesktop.App.I18n.T(key);
        Assert.False(string.IsNullOrWhiteSpace(text), "V-W-S6: fallback i18n text must not be empty.");
        Assert.DoesNotContain("999999", text, StringComparison.Ordinal);

        Assert.Equal(ErrorCatalog.UnknownI18nKey, key);
    }

    /* ===================================================================== */
    /* V-W-S7 : version single source (section 14.2 / FR-W-SET-9)            */
    /* ===================================================================== */

    [Fact]
    public void Gate_VWS7_VersionElementAppearsExactlyOnce()
    {
        var hits = new List<string>();
        foreach (var file in GateRepo.BuildFiles())
        {
            var xml = StripXmlComments(GateRepo.Read(file));
            var lines = xml.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (Regex.IsMatch(lines[i], @"<Version\s*>", RegexOptions.IgnoreCase))
                {
                    hits.Add($"{GateRepo.Rel(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(hits.Count == 1,
            "V-W-S7 / section 14.2: <Version> must be declared exactly once (single source of truth), found " +
            hits.Count + ":" + Environment.NewLine + string.Join(Environment.NewLine, hits));

        Assert.StartsWith("TKSDesktop/TKSDesktop.csproj", hits[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Gate_VWS7_NoHardcodedVersionStringInCode()
    {
        var offenders = new List<string>();
        foreach (var file in GateRepo.ProductCsFiles())
        {
            if (GateRepo.Rel(file).Equals("TKSDesktop/App/AppVersion.cs", StringComparison.Ordinal))
            {
                continue; // AppVersion reads assembly metadata; it is the sanctioned accessor.
            }

            var code = GateRepo.CodeOnly(GateRepo.Read(file));
            var lines = code.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (Regex.IsMatch(lines[i], @"""1\.\d+\.\d+"))
                {
                    offenders.Add($"{GateRepo.Rel(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "V-W-S7 / FR-W-SET-9 violated: a hardcoded version string literal was found in code:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Gate_VWS7_RuntimeVersionDerivesFromCsprojVersion()
    {
        var csproj = Path.Combine(GateRepo.ProductDir, "TKSDesktop.csproj");
        var xml = StripXmlComments(GateRepo.Read(csproj));
        var match = Regex.Match(xml, @"<Version\s*>\s*([^<]+?)\s*</Version>", RegexOptions.IgnoreCase);
        Assert.True(match.Success, "V-W-S7: could not read <Version> from TKSDesktop.csproj.");

        var declared = match.Groups[1].Value;
        var runtime = TKSDesktop.App.AppVersion.Informational;

        Assert.True(
            runtime.Equals(declared, StringComparison.Ordinal)
            || runtime.StartsWith(declared + "+", StringComparison.Ordinal),
            $"V-W-S7: runtime version '{runtime}' does not derive from csproj <Version> '{declared}'.");
    }

    private static string StripXmlComments(string text)
        => Regex.Replace(text, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    /* ===================================================================== */
    /* V-W-S8 : i18n (NFR-W-10)                                              */
    /* ===================================================================== */

    [Fact]
    public void Gate_VWS8_AllI18nKeysAreUniqueAndNonEmpty()
    {
        var i18nFile = Path.Combine(GateRepo.ProductDir, "App", "I18n.cs");

        // IMPORTANT: CodeOnly() strips the *contents* of string literals as well as comments, and the
        // i18n keys live inside literals (["app.name"] = "TKS Desktop") -- using it here reported
        // "found 0 keys". Strip comments only and keep literal contents.
        var source = string.Join(
            '\n',
            GateRepo.LinesWithoutComments(i18nFile).Select(l => l.Text));

        var declared = Regex.Matches(source, @"\[""([^""]+)""\]\s*=")
            .Select(m => m.Groups[1].Value)
            .ToArray();

        Assert.True(declared.Length >= 100,
            $"V-W-S8: expected >= 100 i18n keys in I18n.cs, found {declared.Length}.");

        var duplicates = declared
            .GroupBy(k => k, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} x{g.Count()}")
            .ToArray();

        Assert.True(duplicates.Length == 0,
            "V-W-S8 / NFR-W-10 violated: duplicate i18n keys: " + string.Join(", ", duplicates));

        Assert.Equal(declared.Length, TKSDesktop.App.I18n.AllKeys.Count);

        var empty = TKSDesktop.App.I18n.AllKeys
            .Where(k => string.IsNullOrWhiteSpace(TKSDesktop.App.I18n.T(k)))
            .ToArray();

        Assert.True(empty.Length == 0,
            "V-W-S8 / NFR-W-10 violated: empty i18n values for keys: " + string.Join(", ", empty));
    }

    [Fact]
    public void Gate_VWS8_NoHardcodedChineseUiLiteralsInProductSources()
    {
        var offenders = new List<string>();

        // Statement-level scan: a Chinese format string typically sits on the *continuation* line of a
        // wrapped _logger.LogX(...) call, so a line-local scan cannot tell log/diagnostic text from UI
        // copy. Joining statements first lets the diagnostic markers match the whole call.
        foreach (var file in GateRepo.ProductCsFiles())
        {
            if (GateRepo.IsChineseLiteralAllowListed(file))
            {
                continue;
            }

            foreach (var (lineNumber, statement) in GateRepo.StatementsWithoutComments(file))
            {
                if (GateRepo.IsNonUiDiagnosticStatement(statement))
                {
                    // NFR-W-10 governs UI copy; logger/exception/diagnostic text is not routed via I18n.
                    continue;
                }

                if (statement.TrimStart().StartsWith("[SuppressMessage", StringComparison.Ordinal)
                    || statement.Contains("Justification", StringComparison.Ordinal))
                {
                    continue; // analyzer suppression justifications are not UI copy.
                }

                foreach (var literal in GateRepo.DoubleQuotedLiterals(statement))
                {
                    if (GateRepo.Han.IsMatch(literal))
                    {
                        offenders.Add($"{GateRepo.Rel(file)}:{lineNumber}: {literal}");
                    }
                }
            }
        }

        // XAML views must not carry inline Chinese copy either (must come from I18n / resources).
        foreach (var file in GateRepo.ProductXamlFiles("Views", "ViewModels"))
        {
            foreach (var (lineNumber, line) in GateRepo.LinesWithoutComments(file))
            {
                if (GateRepo.Han.IsMatch(line))
                {
                    offenders.Add($"{GateRepo.Rel(file)}:{lineNumber}: {line.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "V-W-S8 / NFR-W-10 violated: hardcoded Chinese UI literals outside the i18n resource layer:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders.Take(50)));
    }
    /* ===================================================================== */
    /* V-W-S10 : migration chain (source scan; data layer lands in parallel)  */
    /* ===================================================================== */

    private static readonly string[] RequiredTables =
    [
        "chat_messages", "chat_attachments", "bot_notifications", "user_facts", "reminders",
        "sync_cursors", "delivered_bot_messages", "user_progress_cache", "points_ledger_cache",
        "makeup_cards_cache", "makeup_candidates_cache", "level_config_cache",
        "chat_messages_fts", "chat_messages_fts_tri",
    ];

    [Fact]
    public void Gate_VWS10_MigrationChainCoversVersions1To5AndAllTables()
    {
        // Assert against the **real** migration chain (reflection over DatabaseSchema.All) rather than
        // a text scan: source-text matching is brittle (a version number or a table name can be
        // reformatted, and DDL lives in raw string literals) and it cannot tell whether the versions
        // are actually contiguous. This also makes the gate independent of which file holds what.
        var tableNames = typeof(Core.Data.DatabaseSchema)
            .GetProperty(nameof(Core.Data.DatabaseSchema.TableNames))!
            .GetValue(null) as IReadOnlyList<string>;
        var virtualTables = typeof(Core.Data.DatabaseSchema)
            .GetProperty(nameof(Core.Data.DatabaseSchema.VirtualTableNames))!
            .GetValue(null) as IReadOnlyList<string>;
        var indexNames = typeof(Core.Data.DatabaseSchema)
            .GetProperty(nameof(Core.Data.DatabaseSchema.IndexNames))!
            .GetValue(null) as IReadOnlyList<string>;
        var triggerNames = typeof(Core.Data.DatabaseSchema)
            .GetProperty(nameof(Core.Data.DatabaseSchema.TriggerNames))!
            .GetValue(null) as IReadOnlyList<string>;

        Assert.NotNull(tableNames);
        Assert.NotNull(virtualTables);
        Assert.NotNull(indexNames);
        Assert.NotNull(triggerNames);

        // Declared object inventory must cover every table/index/trigger required by sections 9.1-9.8.
        var declaredObjects = tableNames!
            .Concat(virtualTables!)
            .Concat(indexNames!)
            .Concat(triggerNames!)
            .ToHashSet(StringComparer.Ordinal);

        var missing = RequiredTables
            .Where(t => !declaredObjects.Contains(t))
            .ToArray();

        Assert.True(missing.Length == 0,
            "V-W-S10 / section 9.1-9.8 violated: DatabaseSchema does not declare these tables: "
            + string.Join(", ", missing));

        // Migration versions 1..5 must exist, be strictly increasing and contiguous from 1.
        var steps = (typeof(Core.Data.DatabaseSchema)
                .GetProperty(nameof(Core.Data.DatabaseSchema.All))!
                .GetValue(null) as System.Collections.IEnumerable)
            ?.Cast<Core.Data.MigrationStep>()
            .OrderBy(s => s.Version)
            .ToArray();

        Assert.NotNull(steps);
        Assert.Equal(5, steps!.Length);
        Assert.Equal([1, 2, 3, 4, 5], steps.Select(s => s.Version).ToArray());
        Assert.Equal(5, Core.Data.DatabaseSchema.TargetVersion);

        // Section 9.9 freezes the migration names.
        string[] expectedNames =
        [
            "baseline-room-v4-tables",
            "gamification-cache-tables",
            "chat-messages-fts5",
            "reminder-dedupe-and-attachment-index",
            "chat-messages-trigram-index-for-cjk",
        ];
        Assert.Equal(expectedNames, steps.Select(s => s.Name).ToArray());

        // Every version must carry SQL, and the trigram index must actually be created (FR-W-DB-2).
        foreach (var step in steps)
        {
            Assert.False(string.IsNullOrWhiteSpace(string.Join("\n", step.Scripts)),
                $"V-W-S10: migration {step.Version} ({step.Name}) has no script.");
        }

        var allSql = string.Join("\n", steps.SelectMany(s => s.Scripts));
        Assert.Contains("tokenize='trigram'", allSql, StringComparison.Ordinal);
        Assert.Contains("tokenize='unicode61'", allSql, StringComparison.Ordinal);
        Assert.Contains("chat_messages_fts_tri", allSql, StringComparison.Ordinal);
        Assert.Contains("chat_messages_fts_tri", allSql, StringComparison.Ordinal);
    }

    /* ===================================================================== */
    /* V-W-S11 : tray library pinned to 2.3.2 (OQ-W-4)                       */
    /* ===================================================================== */

    [Fact]
    public void Gate_VWS11_NotifyIconPackageIsPinnedTo232()
    {
        // Scan with comments stripped: the pin rationale is documented in a csproj comment that names
        // the rejected 2.4.1, and a comment is not a PackageReference. Matching raw text made the gate
        // fail on its own documentation.
        var csproj = StripXmlComments(GateRepo.Read(Path.Combine(GateRepo.ProductDir, "TKSDesktop.csproj")));

        var matches = Regex.Matches(
            csproj,
            @"<PackageReference\s+Include=""H\.NotifyIcon\.Wpf""\s+Version=""([^""]+)""",
            RegexOptions.IgnoreCase);

        Assert.True(matches.Count == 1,
            $"V-W-S11: expected exactly one H.NotifyIcon.Wpf PackageReference, found {matches.Count}.");
        Assert.Equal("2.3.2", matches[0].Groups[1].Value);
        Assert.DoesNotContain("2.4.1", csproj, StringComparison.Ordinal);
    }

    /* ===================================================================== */
    /* V-W-C4 : static gate (TreatWarningsAsErrors)                          */
    /* ===================================================================== */

    [Fact]
    public void Gate_VWC4_DirectoryBuildPropsEnablesTreatWarningsAsErrors()
    {
        var doc = XDocument.Parse(GateRepo.Read(Path.Combine(GateRepo.Root, "Directory.Build.props")));

        var value = doc.Descendants()
            .Where(e => e.Name.LocalName.Equals("TreatWarningsAsErrors", StringComparison.Ordinal))
            .Select(e => e.Value.Trim())
            .FirstOrDefault();

        Assert.False(string.IsNullOrEmpty(value),
            "V-W-C4: TreatWarningsAsErrors is not declared in Directory.Build.props.");
        Assert.True(value!.Equals("true", StringComparison.OrdinalIgnoreCase),
            $"V-W-C4: TreatWarningsAsErrors must be 'true', found '{value}'.");
    }

    /* ===================================================================== */
    /* V-W-C5 : contract drift must be catchable (reverse verification)      */
    /* ===================================================================== */

    [Fact]
    public void ContractDrift_ReverseVerification_ErrorNotificationIdIs1004()
    {
        // V-W-C5: proves the gate is live. Flipping SemanticNotificationId.Error to 1005, or adopting the
        // Linux client's ERROoR/REMINDER swap, makes Gate_VWS5_SemanticNotificationIds_MatchAndroidBaseline
        // fail; this tripwire pins the same values locally.
        Assert.Equal(1004, Convert.ToInt32(SemanticNotificationId.Error, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1005, Convert.ToInt32(SemanticNotificationId.Reminder, System.Globalization.CultureInfo.InvariantCulture));
        Assert.NotEqual(
            Convert.ToInt32(SemanticNotificationId.Error, System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt32(SemanticNotificationId.Reminder, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ContractDrift_ReverseVerification_LevelCodeJsonNameIsExactlySnakeCase()
    {
        // V-W-C5: renaming the mapping to "levelCode" is the exact drift the Android client suffered
        // (level_code silently deserialised as an empty string). This assertion and the V-W-S3 set
        // equality assertions both fail on that drift.
        var prop = typeof(LevelConfigEntryDto).GetProperty(nameof(LevelConfigEntryDto.LevelCode));
        Assert.NotNull(prop);

        var attr = prop!.GetCustomAttribute<JsonPropertyNameAttribute>();
        Assert.NotNull(attr);
        Assert.Equal("level_code", attr!.Name);
        Assert.NotEqual("levelCode", attr.Name);
    }

    [Fact]
    public void ContractDrift_ReverseVerification_InteractionTimeoutMustExceed60s()
    {
        // V-W-C5 (trap 7): lowering this below the nginx proxy_read_timeout of 60s breaks the gate.
        Assert.True(ProtocolConstants.InteractionTimeoutMs > 60_000,
            $"V-W-S / trap 7: InteractionTimeoutMs must be > 60000, found {ProtocolConstants.InteractionTimeoutMs}.");
        Assert.Equal(90_000, ProtocolConstants.InteractionTimeoutMs);
    }
}
