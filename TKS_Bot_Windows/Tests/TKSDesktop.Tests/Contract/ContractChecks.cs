using TKSDesktop.App;
using TKSDesktop.Contracts;
using Xunit;

namespace TKSDesktop.Tests.Contract;

/// <summary>
/// Registry of the contract self-check assertions (PRD section 16.3 V-W-C2, "at least 66 items").
///
/// The checks are offline: the mock backend is not started. The contract surface is asserted against
/// the frozen DTO / constant / i18n artifacts -- the same ones the mock backend
/// (mock-server/server.mjs) and the real backend both serve -- plus literal JSON frames parsed by the
/// real WsFrameParser.
///
/// Groups: "dto" (section 5.2/5.3 DTOs), "dto7" (section 7.0 gamification DTOs),
/// "proto" (protocol constants / level visuals / error catalogue),
/// "trap" (section 5.6 traps), "ws" (frame parsing and NFR-W-12 forward compatibility).
/// </summary>
internal static partial class ContractChecks
{
    private static readonly List<ContractCheck> Registered = Register();

    /// <summary>All registered checks.</summary>
    public static IReadOnlyList<ContractCheck> All => Registered;

    /// <summary>Total registered assertions -- the number reported for V-W-C2.</summary>
    public static int Total => Registered.Count;

    /// <summary>Assertions skipped because their owning artifact has not landed yet.</summary>
    public static int Skipped => Registered.Count(c => c.SkipReason is not null);

    private static List<ContractCheck> Register()
    {
        var result = new List<ContractCheck>();
        result.AddRange(EnvelopeDtoChecks());
        result.AddRange(GamificationDtoChecks());
        result.AddRange(TrapChecks());
        result.AddRange(WsForwardCompatibilityChecks());
        result.AddRange(ProtocolConstantChecks());
        result.AddRange(LevelVisualChecks());
        result.AddRange(ErrorCatalogChecks());
        return result;
    }

    private static ContractCheck Check(string id, Action body, string? skip = null)
        => new("proto", id, body, skip);

    /// <summary>Protocol constants of PRD section 5.4 / C-2 that the mock backend must agree on.</summary>
    private static IEnumerable<ContractCheck> ProtocolConstantChecks()
    {
        yield return Check("c2_heartbeat_25s", () => Assert.Equal(25_000, ProtocolConstants.HeartbeatIntervalMs));
        yield return Check("c2_reconnect_base_1s", () => Assert.Equal(1_000, ProtocolConstants.ReconnectBaseMs));
        yield return Check("c2_reconnect_cap_60s", () => Assert.Equal(60_000, ProtocolConstants.ReconnectMaxMs));
        yield return Check("c2_reconnect_max_15_attempts", () => Assert.Equal(15, ProtocolConstants.ReconnectMaxAttempts));
        yield return Check("c2_ws_close_code_4001", () => Assert.Equal(4001, ProtocolConstants.WsCloseInvalidToken));
        yield return Check("timeout_rest_30s", () => Assert.Equal(30_000, ProtocolConstants.RestTimeoutMs));
        yield return Check("timeout_interaction_90s", () => Assert.Equal(90_000, ProtocolConstants.InteractionTimeoutMs));
        yield return Check("timeout_streaming_150s", () => Assert.Equal(150_000, ProtocolConstants.StreamingTimeoutMs));
        yield return Check("timeout_connectivity_10s", () => Assert.Equal(10_000, ProtocolConstants.ConnectivityTimeoutMs));
        yield return Check("auth_proactive_refresh_60s", () => Assert.Equal(60_000, ProtocolConstants.ProactiveRefreshThresholdMs));
        yield return Check("auth_offline_idle_7_days", () => Assert.Equal(7L * 24 * 60 * 60 * 1000, ProtocolConstants.OfflineCredentialMaxIdleMs));
        yield return Check("sync_limit_300", () => Assert.Equal(300, ProtocolConstants.SyncLimit));
        yield return Check("server_history_limit_300", () => Assert.Equal(300, ProtocolConstants.ServerHistoryLimit));
        yield return Check("local_first_page_50", () => Assert.Equal(50, ProtocolConstants.LocalFirstPageSize));
        yield return Check("images_max_3", () => Assert.Equal(3, ProtocolConstants.MaxImageCount));
        yield return Check("images_max_20mb_each", () => Assert.Equal(20L * 1024 * 1024, ProtocolConstants.MaxImageBytes));
        yield return Check("images_max_24mb_total", () => Assert.Equal(24L * 1024 * 1024, ProtocolConstants.MaxTotalAttachmentBytes));
        yield return Check("images_total_below_3x_single", () => Assert.True(ProtocolConstants.MaxTotalAttachmentBytes < 3 * ProtocolConstants.MaxImageBytes));
        yield return Check("images_mime_set_exact", () =>
            Assert.Equal(["image/jpeg", "image/png"], ProtocolConstants.AllowedImageMime));
        yield return Check("reminder_stale_30_minutes", () => Assert.Equal(30L * 60 * 1000, ProtocolConstants.ReminderStaleMs));
        yield return Check("notification_body_160_chars", () => Assert.Equal(160, ProtocolConstants.NotificationBodyMaxChars));
        yield return Check("log_retention_7_days", () => Assert.Equal(7, ProtocolConstants.LogRetentionDays));
        yield return Check("log_max_file_10mb", () => Assert.Equal(10L * 1024 * 1024, ProtocolConstants.LogMaxFileBytes));
        yield return Check("log_redact_400_64", () =>
        {
            Assert.Equal(400, ProtocolConstants.LogRedactMaxChars);
            Assert.Equal(64, ProtocolConstants.LogRedactKeepChars);
        });
        yield return Check("default_api_base_url", () =>
            Assert.Equal("https://takishiinabot.top/api/v1/", ProtocolConstants.DefaultApiBaseUrl));
        yield return Check("default_ws_base_url", () =>
            Assert.Equal("wss://takishiinabot.top", ProtocolConstants.DefaultWsBaseUrl));
        yield return Check("ws_path", () => Assert.Equal("/ws/chat", ProtocolConstants.WsChatPath));
        yield return Check("single_session_id", () => Assert.Equal("default_session", ProtocolConstants.DefaultSessionId));
        yield return Check("c5_history_separator_verbatim", () =>
            Assert.Equal("──── 新的一天 ────", ProtocolConstants.HistorySeparator));
        yield return Check("c5_is_history_separator_positive", () =>
            Assert.True(ProtocolConstants.IsHistorySeparator(ProtocolConstants.HistorySeparator)));
        yield return Check("c5_is_history_separator_trims_whitespace", () =>
            Assert.True(ProtocolConstants.IsHistorySeparator("  " + ProtocolConstants.HistorySeparator + "\n")));
        yield return Check("c5_is_history_separator_rejects_ordinary_text", () =>
            Assert.False(ProtocolConstants.IsHistorySeparator("hello")));
        yield return Check("c5_is_history_separator_rejects_empty", () =>
        {
            Assert.False(ProtocolConstants.IsHistorySeparator(string.Empty));
            Assert.False(ProtocolConstants.IsHistorySeparator(null));
        });
        yield return Check("default_global_shortcut", () =>
            Assert.Equal("Control+Alt+T", ProtocolConstants.DefaultGlobalShortcut));
        yield return Check("aumid_without_dots", () => Assert.Equal("TKSDesktop", ProtocolConstants.Aumid));
        yield return Check("sync_cursor_keys_are_distinct", () =>
        {
            Assert.Equal("chat", ProtocolConstants.CursorKeyChat);
            Assert.Equal("memory", ProtocolConstants.CursorKeyMemory);
            Assert.NotEqual(ProtocolConstants.CursorKeyChat, ProtocolConstants.CursorKeyMemory);
        });
        yield return Check("message_status_vocabulary", () =>
        {
            Assert.Equal("sending", ProtocolConstants.StatusSending);
            Assert.Equal("sent", ProtocolConstants.StatusSent);
            Assert.Equal("received", ProtocolConstants.StatusReceived);
            Assert.Equal("streaming", ProtocolConstants.StatusStreaming);
            Assert.Equal("error", ProtocolConstants.StatusError);
        });
        yield return Check("client_error_codes", () =>
        {
            Assert.Equal("SEND_FAILED", ProtocolConstants.ClientErrorSendFailed);
            Assert.Equal("TIMEOUT", ProtocolConstants.ClientErrorTimeout);
            Assert.Equal("CONNECTION_LOST", ProtocolConstants.ClientErrorConnectionLost);
        });
        yield return Check("attachment_free_space_reserve_64mb", () =>
            Assert.Equal(64L * 1024 * 1024, ProtocolConstants.AttachmentFreeSpaceReserveBytes));
    }
}
/// <summary>Level visuals of PRD section 6.10 (C-1) and the error catalogue of section 7.0 (C-6).</summary>
internal static partial class ContractChecks
{
    private static IEnumerable<ContractCheck> LevelVisualChecks()
    {
        yield return Check("c1_seven_levels", () => Assert.Equal(7, LevelVisuals.All.Count));
        yield return Check("c1_level_codes_in_order", () => Assert.Equal(
            ["PANDA_LV1", "PANDA_LV2", "PANDA_LV3", "PANDA_LV4", "PANDA_LV5", "PANDA_LV6", "PANDA_LV7"],
            LevelVisuals.All.Select(v => v.Code).ToArray()));
        yield return Check("c1_thresholds", () => Assert.Equal(
            [3, 7, 15, 30, 60, 100, 200],
            LevelVisuals.All.Select(v => v.FallbackThresholdDays).ToArray()));
        yield return Check("c1_emoji_values", () => Assert.Equal(
            ["🥚", "🌱", "🍃", "🌊", "✨", "💜", "🌈"],
            LevelVisuals.All.Select(v => v.Emoji).ToArray()));
        yield return Check("c1_gradients", () => Assert.Equal(
            ["#D9D9D9/#BFC3C7", "#C9E7A8/#8FCB6B", "#8FDCA0/#37A65C", "#3E6FA8/#1B3B63",
             "#F7D774/#D8A32B", "#B48CE0/#7A4FBF", "#FF6B6B/#FFD93D"],
            LevelVisuals.All.Select(v => v.GradientFrom + "/" + v.GradientTo).ToArray()));
        yield return Check("c1_accents", () => Assert.Equal(
            ["#B9BDC1", "#8FCB6B", "#37A65C", "#2C5A8C", "#D8A32B", "#8B5CD6", "#FF8A5B"],
            LevelVisuals.All.Select(v => v.Accent).ToArray()));
        yield return Check("c1_only_top_level_animated", () => Assert.Equal(
            [false, false, false, false, false, false, true],
            LevelVisuals.All.Select(v => v.Animated).ToArray()));
        yield return Check("c1_default_visual_is_neutral", () =>
        {
            Assert.Equal("NONE", LevelVisuals.Default.Code);
            Assert.Equal(0, LevelVisuals.Default.FallbackThresholdDays);
            Assert.Equal("🐾", LevelVisuals.Default.Emoji);
        });
        yield return Check("c1_resolve_known_code", () =>
            Assert.Equal("PANDA_LV5", LevelVisuals.Resolve("PANDA_LV5").Code));
        yield return Check("c1_resolve_unknown_and_empty_falls_back", () =>
        {
            Assert.Equal("NONE", LevelVisuals.Resolve("VZZZ").Code);
            Assert.Equal("NONE", LevelVisuals.Resolve("").Code);
            Assert.Equal("NONE", LevelVisuals.Resolve("   ").Code);
            Assert.Equal("NONE", LevelVisuals.Resolve(null).Code);
        });
        yield return Check("c1_all_visuals_have_non_empty_text", () =>
        {
            foreach (var visual in LevelVisuals.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(visual.FallbackName), $"{visual.Code} fallback name is empty.");
                Assert.False(string.IsNullOrWhiteSpace(visual.Emoji), $"{visual.Code} emoji is empty.");
            }
        });
    }

    private static IEnumerable<ContractCheck> ErrorCatalogChecks()
    {
        yield return Check("c6_api_code_values", () =>
        {
            Assert.Equal(0, ErrorCatalog.CodeOk);
            Assert.Equal(40001, ErrorCatalog.ParamMissing);
            Assert.Equal(40002, ErrorCatalog.CityEmpty);
            Assert.Equal(40101, ErrorCatalog.AuthFailed);
            Assert.Equal(40102, ErrorCatalog.TokenInvalid);
            Assert.Equal(40301, ErrorCatalog.DeviceNotAllowed);
            Assert.Equal(40302, ErrorCatalog.DeviceLimitExceeded);
            Assert.Equal(40201, ErrorCatalog.PointsInsufficient);
            Assert.Equal(40202, ErrorCatalog.ItemUnavailable);
            Assert.Equal(40204, ErrorCatalog.InteractionAiFailed);
            Assert.Equal(40205, ErrorCatalog.MakeupCardInsufficient);
            Assert.Equal(40206, ErrorCatalog.DateAlreadyHasActivity);
            Assert.Equal(40207, ErrorCatalog.InvalidTargetDate);
            Assert.Equal(50301, ErrorCatalog.ServiceUnavailable);
            Assert.Equal(5000, ErrorCatalog.ServerError);
        });
        yield return Check("c6_ws_error_codes_registered", () =>
        {
            foreach (var code in new[]
                     {
                         ErrorCatalog.InvalidJson, ErrorCatalog.UnknownType, ErrorCatalog.EmptyMessage,
                         ErrorCatalog.VisionImageCountExceeded, ErrorCatalog.VisionInvalidMime,
                         ErrorCatalog.VisionInvalidBase64, ErrorCatalog.VisionImageTooLarge,
                         ErrorCatalog.GeminiNotConfigured, ErrorCatalog.AiTimeout, ErrorCatalog.InternalError,
                     })
            {
                var info = ErrorCatalog.Resolve(code);
                Assert.NotEqual(ErrorCatalog.UnknownI18nKey, info.I18nKey);
                Assert.Equal(code, info.Key);
            }
        });
        yield return Check("c6_retryable_flags", () =>
        {
            Assert.True(ErrorCatalog.Resolve(40101).Retryable);
            Assert.True(ErrorCatalog.Resolve(40102).Retryable);
            Assert.True(ErrorCatalog.Resolve(50301).Retryable);
            Assert.True(ErrorCatalog.Resolve(5000).Retryable);
            Assert.False(ErrorCatalog.Resolve(40201).Retryable);
            Assert.False(ErrorCatalog.Resolve(40302).Retryable);
        });
        yield return Check("c6_auth_failure_detection", () =>
        {
            Assert.True(ErrorCatalog.IsAuthFailure(40101));
            Assert.True(ErrorCatalog.IsAuthFailure(40102));
            Assert.False(ErrorCatalog.IsAuthFailure(40301));
            Assert.False(ErrorCatalog.IsAuthFailure(null));
        });
        yield return Check("c6_unknown_numeric_code_is_neutral", () =>
        {
            var info = ErrorCatalog.Resolve(999999);
            Assert.Equal(ErrorCatalog.UnknownI18nKey, info.I18nKey);
            var text = I18n.T(info.I18nKey);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain("999999", text, StringComparison.Ordinal);
        });
        yield return Check("c6_unknown_string_code_is_neutral", () =>
        {
            Assert.Equal(ErrorCatalog.UnknownI18nKey, ErrorCatalog.Resolve("NOT_A_CODE").I18nKey);
            Assert.Equal(ErrorCatalog.UnknownI18nKey, ErrorCatalog.Resolve((string?)null).I18nKey);
            Assert.Equal(ErrorCatalog.UnknownI18nKey, ErrorCatalog.Resolve("   ").I18nKey);
        });
        yield return Check("c6_every_registered_code_has_i18n_text", () =>
        {
            foreach (var info in ErrorCatalog.All)
            {
                var text = I18n.T(info.I18nKey);
                Assert.False(string.IsNullOrWhiteSpace(text), $"{info.Key} has no i18n text ({info.I18nKey}).");
                Assert.NotEqual(info.I18nKey, text);
            }
        });
        yield return Check("c6_i18n_keys_are_unique", () =>
        {
            var keys = I18n.AllKeys.ToArray();
            Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        });
        yield return Check("points_reason_codes_localised", () =>
        {
            string[] reasons =
            [
                "DAILY_FIRST_CHAT", "STREAK_3_DAY", "ANNIVERSARY", "ITEM_SEND", "ITEM_REFUND", "ADMIN_ADJUST",
            ];

            foreach (var reason in reasons)
            {
                var key = "points.reason." + reason;
                Assert.True(I18n.Has(key), $"missing localisation for reason_code {reason} ({key}).");
                Assert.NotEqual(key, I18n.T(key));
            }

            Assert.True(I18n.Has("points.reason.unknown"), "missing neutral fallback for unknown reason_code.");
        });
    }
}
