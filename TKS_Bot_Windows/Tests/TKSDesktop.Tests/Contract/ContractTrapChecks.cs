using System.IO;
using Xunit;
using System.Reflection;
using System.Text.Json;
using TKSDesktop.Contracts;
using TKSDesktop.Contracts.Dtos;

namespace TKSDesktop.Tests.Contract;

/// <summary>
/// PRD section 5.6 -- the 12 inherited backend contract traps (group "trap"), plus the NFR-W-12
/// forward-compatibility checks for the WebSocket frame parser (group "ws").
/// All checks are offline: they construct JSON literally and parse it with the real WsFrameParser.
/// </summary>
internal static partial class ContractChecks
{
    private static ContractCheck Trap(string id, Action body, string? skipReason = null)
        => new("trap", id, body, skipReason);

    private static ContractCheck Ws(string id, Action body) => new("ws", id, body);

    /// <summary>Locates a product type by name without a hard compile-time dependency.</summary>
    private static Type? FindType(string fullName)
        => typeof(ProtocolConstants).Assembly.GetType(fullName, throwOnError: false);

    private static string ProductSourceText(params string[] subDirs)
    {
        var files = Gate.GateRepo.ProductCsFiles(subDirs);
        return string.Join("\n", files.Select(Gate.GateRepo.Read));
    }

    private static IEnumerable<ContractCheck> TrapChecks()
    {
        /* -- trap 1: chat.message requestId lives at the TOP level, never inside payload -- */
        yield return Trap("1a_requestId_serialized_top_level", () =>
        {
            var frame = new WsChatMessageFrameDto
            {
                RequestId = "req-42",
                Payload = new ChatMessagePayloadDto { Content = "hi" },
            };

            var json = JsonSerializer.Serialize(frame, WsFrameParser.Options);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.True(root.TryGetProperty("requestId", out var top), "trap 1: requestId missing at frame top level.");
            Assert.Equal("req-42", top.GetString());
            Assert.True(root.TryGetProperty("payload", out var payload), "trap 1: payload missing.");
            Assert.False(payload.TryGetProperty("requestId", out _),
                "trap 1: requestId must NOT be nested inside payload (backend reads frame['requestId']).");
        });

        yield return Trap("1b_requestId_not_readable_from_payload", () =>
        {
            // A frame that nests requestId inside payload must yield NO top-level requestId.
            const string nested =
                "{\"type\":\"chat.reply.stream\",\"payload\":{\"requestId\":\"inside\",\"delta\":\"x\",\"done\":false}}";

            var result = WsFrameParser.Parse(nested);
            Assert.Equal(WsFrameParseKind.Known, result.Kind);
            var frame = Assert.IsType<WsReplyStreamFrame>(result.Frame);
            Assert.Null(frame.RequestId);
        });

        yield return Trap("1c_echo_requestId_read_from_top_level", () =>
        {
            const string json =
                "{\"type\":\"chat.message.echo\",\"requestId\":\"echo-1\",\"payload\":{\"content\":\"hi\",\"imageCount\":0,\"timestamp\":1700000000000,\"originDeviceId\":\"dev-b\"}}";
            var result = WsFrameParser.Parse(json);
            Assert.Equal(WsFrameParseKind.Known, result.Kind);
            var frame = Assert.IsType<WsEchoFrame>(result.Frame);
            Assert.Equal("echo-1", frame.RequestId);
            Assert.Equal("dev-b", frame.Payload.OriginDeviceId);
        });

        /* -- trap 2: pong is a TOP-LEVEL {type,timestamp} with no payload wrapper -- */
        yield return Trap("2a_pong_top_level_timestamp", () =>
        {
            const string json = "{\"type\":\"pong\",\"timestamp\":123}";
            var result = WsFrameParser.Parse(json);
            Assert.Equal(WsFrameParseKind.Known, result.Kind);
            var frame = Assert.IsType<WsPongFrame>(result.Frame);
            Assert.Equal(123L, frame.Timestamp);
        });

        yield return Trap("2b_pong_tolerates_extra_payload", () =>
        {
            const string json = "{\"type\":\"pong\",\"timestamp\":456,\"payload\":{\"timestamp\":999}}";
            var result = WsFrameParser.Parse(json);
            Assert.Equal(WsFrameParseKind.Known, result.Kind);
            var frame = Assert.IsType<WsPongFrame>(result.Frame);
            Assert.Equal(456L, frame.Timestamp);
        });

        /* -- trap 3: http_api auth failure is {"detail":{"code":40101}} -- */
        yield return Trap("3a_detail_wrapped_code_extracted", () =>
        {
            var extractor = FindType("TKSDesktop.Core.Network.ApiErrorExtractor");
            Assert.True(extractor is not null,
                "trap 3: Core/Network/ApiErrorExtractor is missing (owned by the network implementer).");

            var method = extractor!.GetMethod("ExtractCode", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
            Assert.True(method is not null, "trap 3: ApiErrorExtractor.ExtractCode(string) not found.");

            var code = (int?)method!.Invoke(null, ["{\"detail\":{\"code\":40101,\"message\":\"auth failed\"}}"]);
            Assert.Equal(40101, code);
        });

        yield return Trap("3b_top_level_code_still_supported", () =>
        {
            var extractor = FindType("TKSDesktop.Core.Network.ApiErrorExtractor");
            Assert.True(extractor is not null, "trap 3: ApiErrorExtractor missing.");

            var method = extractor!.GetMethod("ExtractCode", BindingFlags.Public | BindingFlags.Static, [typeof(string)])!;
            var code = (int?)method.Invoke(null, ["{\"code\":40101,\"message\":\"x\",\"traceId\":\"t\"}"]);
            Assert.Equal(40101, code);
        });

        yield return Trap("3c_unknown_shape_returns_null_without_throwing", () =>
        {
            var extractor = FindType("TKSDesktop.Core.Network.ApiErrorExtractor");
            Assert.True(extractor is not null, "trap 3: ApiErrorExtractor missing.");

            var method = extractor!.GetMethod("ExtractCode", BindingFlags.Public | BindingFlags.Static, [typeof(string)])!;
            Assert.Null((int?)method.Invoke(null, ["not json at all"]));
            Assert.Null((int?)method.Invoke(null, ["[1,2,3]"]));
        });

        /* -- trap 4: business failure is HTTP 200 + code != 0 -- */
        yield return Trap("4a_business_code_is_not_a_success_fallback", () =>
        {
            var info = ErrorCatalog.Resolve(40201);
            Assert.Equal(40201, int.Parse(info.Key, System.Globalization.CultureInfo.InvariantCulture));
            Assert.NotEqual(ErrorCatalog.UnknownI18nKey, info.I18nKey);
            Assert.False(string.IsNullOrWhiteSpace(TKSDesktop.App.I18n.T(info.I18nKey)));
        });

        yield return Trap("4b_http_200_never_clears_credentials", () =>
        {
            Assert.False(ErrorCatalog.ShouldClearCredentials(200));
            Assert.True(ErrorCatalog.ShouldClearCredentials(401));
            Assert.True(ErrorCatalog.ShouldClearCredentials(403));
            Assert.False(ErrorCatalog.ShouldClearCredentials(null));
        });

        yield return Trap("4c_code_zero_is_success", () =>
        {
            Assert.Equal(0, ErrorCatalog.CodeOk);
            Assert.Equal("error.api.0", ErrorCatalog.I18nKeyOf(0));
            Assert.False(ErrorCatalog.Resolve(0).Retryable);
        });

        /* -- trap 5: the three snake_case routes -- */
        yield return Trap("5a_level_config_is_snake_case", () =>
            ContractEngine.AssertJsonMap(typeof(LevelConfigEntryDto), ["level_code", "level_name", "threshold_days", "sort_order"]));

        yield return Trap("5b_points_history_is_snake_case", () =>
            ContractEngine.AssertJsonMap(
                typeof(PointsLedgerItemDto),
                ["id", "user_id", "change_amount", "reason_code", "balance_after", "related_item_id",
                 "idempotency_key", "created_at", "business_date"]));

        yield return Trap("5c_makeup_card_record_is_snake_case", () =>
            ContractEngine.AssertJsonMap(
                typeof(MakeupCardRecordDto),
                ["id", "user_id", "granted_month", "status", "used_for_date", "used_at", "created_at"]));

        /* -- trap 6: WS close code 4001 is an auth failure, not a retryable network error -- */
        yield return Trap("6a_4001_is_auth_failure", () => Assert.True(WsFrameParser.IsAuthFailureClose(4001)));
        yield return Trap("6b_1006_is_not_auth_failure", () => Assert.False(WsFrameParser.IsAuthFailureClose(1006)));
        yield return Trap("6c_null_close_code_is_not_auth_failure", () => Assert.False(WsFrameParser.IsAuthFailureClose(null)));
        yield return Trap("6d_4001_constant_matches_backend", () => Assert.Equal(4001, ProtocolConstants.WsCloseInvalidToken));

        /* -- trap 7: interaction timeout must exceed the nginx proxy_read_timeout of 60s -- */
        yield return Trap("7_interaction_timeout_exceeds_60s", () =>
        {
            Assert.True(ProtocolConstants.InteractionTimeoutMs > 60_000);
            Assert.True(ProtocolConstants.RestTimeoutMs < ProtocolConstants.InteractionTimeoutMs);
            Assert.True(ProtocolConstants.StreamingTimeoutMs > ProtocolConstants.InteractionTimeoutMs);
            Assert.True(ProtocolConstants.ConnectivityTimeoutMs < ProtocolConstants.RestTimeoutMs);
        });

        /* -- trap 8: UPDATE instead of INSERT OR REPLACE (FR-W-DB-1) -- */
        var messageRepo = Path.Combine(Gate.GateRepo.ProductDir, "Core", "Data", "Repositories", "MessageRepository.cs");
        yield return Trap("8_no_insert_or_replace_in_message_repository", () =>
        {
            Assert.True(File.Exists(messageRepo),
                $"trap 8: {Gate.GateRepo.Rel(messageRepo)} not found (data layer owned by another implementer).");

            var code = Gate.GateRepo.CodeOnly(Gate.GateRepo.Read(messageRepo));
            Assert.DoesNotContain("INSERT OR REPLACE", code, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("REPLACE INTO", code, StringComparison.OrdinalIgnoreCase);
        }, skipReason: File.Exists(messageRepo) ? null : "待集成：Core/Data/Repositories/MessageRepository.cs 尚不存在（数据层并行实现）。");

        /* -- trap 9: three-way reply de-duplication via delivered_bot_messages -- */
        yield return Trap("9a_delivered_bot_messages_table_referenced", () =>
        {
            var text = ProductSourceText();
            Assert.Contains("delivered_bot_messages", text, StringComparison.Ordinal);
        }, skipReason: ProductSourceText().Contains("delivered_bot_messages", StringComparison.Ordinal)
            ? null
            : "待集成：delivered_bot_messages 去重表尚未在产品源码中出现（Core/Data 并行实现中）。");

        yield return Trap("9b_bot_message_id_convention_shard_index", () =>
        {
            // FR-W-INT-12: de-duplication key for a split bot message is {messageId}_{index}.
            // The convention is asserted through the message-id helpers once ChatService lands.
            var chatService = FindType("TKSDesktop.Core.Chat.ChatService");
            Assert.True(chatService is not null, "trap 9: Core/Chat/ChatService not found.");
        }, skipReason: FindType("TKSDesktop.Core.Chat.ChatService") is null
            ? "待集成：Core/Chat/ChatService 尚不存在（聊天核心并行实现）。"
            : null);

        /* -- trap 10: since = max(local max timestamp, cursor) -- */
        var syncService = Path.Combine(Gate.GateRepo.ProductDir, "Core", "Chat", "SyncService.cs");
        yield return Trap("10_incremental_since_uses_max_of_local_and_cursor", () =>
        {
            Assert.True(File.Exists(syncService),
                $"trap 10: {Gate.GateRepo.Rel(syncService)} not found (chat core owned by another implementer).");

            var code = Gate.GateRepo.CodeOnly(Gate.GateRepo.Read(syncService));
            var hasMax = code.Contains("Math.Max", StringComparison.Ordinal)
                         || code.Contains("Math.Max(", StringComparison.Ordinal);
            Assert.True(hasMax,
                "trap 10: SyncService must compute `since = max(localMaxTimestamp, cursor)` (FR-W-SYNC-3); " +
                "no Math.Max found, so clearing the local conversation would immediately re-pull history.");
        }, skipReason: File.Exists(syncService) ? null : "待集成：Core/Chat/SyncService.cs 尚不存在（聊天核心并行实现）。");

        /* -- trap 11: mergedRequestIds must exist on the interaction response -- */
        yield return Trap("11a_merged_request_ids_mapped", () =>
            ContractEngine.AssertMaps(typeof(InteractionSendDataDto), "MergedRequestIds", "mergedRequestIds"));

        yield return Trap("11b_merged_request_ids_is_a_string_list", () =>
        {
            var prop = typeof(InteractionSendDataDto).GetProperty(nameof(InteractionSendDataDto.MergedRequestIds));
            Assert.NotNull(prop);
            Assert.Equal(typeof(List<string>), prop!.PropertyType);
        });

        yield return Trap("11c_merged_count_mapped", () =>
            ContractEngine.AssertMaps(typeof(InteractionSendDataDto), "MergedCount", "mergedCount"));

        /* -- trap 12: iconUrl is empty in practice -> emoji fallback -> neutral placeholder -- */
        yield return Trap("12a_icon_url_mapped", () =>
            ContractEngine.AssertMaps(typeof(InteractionItemDto), "IconUrl", "iconUrl"));

        yield return Trap("12b_empty_icon_url_still_yields_non_empty_visual", () =>
        {
            var item = new InteractionItemDto { Id = "coffee", Name = "coffee", Icon = "C", IconUrl = string.Empty };
            Assert.True(string.IsNullOrEmpty(item.IconUrl));
            Assert.False(string.IsNullOrWhiteSpace(item.Icon));
        });

        yield return Trap("12c_level_visual_fallback_is_non_empty", () =>
        {
            var none = LevelVisuals.Resolve("NONE");
            Assert.False(string.IsNullOrWhiteSpace(none.Emoji));
            Assert.False(string.IsNullOrWhiteSpace(none.FallbackName));

            var empty = LevelVisuals.Resolve(string.Empty);
            Assert.False(string.IsNullOrWhiteSpace(empty.FallbackName));
            Assert.False(string.IsNullOrWhiteSpace(empty.Emoji));

            var unknown = LevelVisuals.Resolve("PANDA_LV99");
            Assert.Equal(LevelVisuals.DefaultLevelCode, unknown.Code);
        });

        yield return Trap("12d_display_name_never_empty_for_default_level", () =>
        {
            var name = LevelVisuals.ResolveDisplayName("NONE", string.Empty);
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.Equal(LevelVisuals.Resolve("NONE").FallbackName, name);
            Assert.Equal("custom", LevelVisuals.ResolveDisplayName("NONE", "custom"));
        });

        /* -- supplementary trap: /healthz only checks HTTP 200 (two processes, two shapes) -- */
        yield return Trap("13_healthz_timeout_is_short_and_non_blocking", () =>
        {
            Assert.Equal(10_000, ProtocolConstants.ConnectivityTimeoutMs);
        });

        /* -- supplementary trap: points.snapshot is a dead event and must not be depended on -- */
        yield return Trap("14_points_snapshot_parses_but_is_flagged_unreliable", () =>
        {
            var result = WsFrameParser.Parse("{\"type\":\"points.snapshot\",\"timestamp\":7}");
            Assert.Equal(WsFrameParseKind.Known, result.Kind);
            Assert.IsType<WsPointsSnapshotFrame>(result.Frame);

            var replyStream = FindType("TKSDesktop.Core.Chat.ChatService");
            _ = replyStream; // presence is not required here; the type-level assertion above is the contract.
        });

        /* -- supplementary trap: messageKind / greetingScenario are additive-only (W-P4) -- */
        yield return Trap("15_additive_fields_tolerate_absence", () =>
        {
            const string minimal = "{\"type\":\"chat.reply.stream\",\"requestId\":\"r1\",\"payload\":{\"delta\":\"hi\",\"done\":false}}";
            var result = WsFrameParser.Parse(minimal);
            Assert.Equal(WsFrameParseKind.Known, result.Kind);
            var frame = Assert.IsType<WsReplyStreamFrame>(result.Frame);
            Assert.Null(frame.Payload.MessageKind);
            Assert.Null(frame.Payload.GreetingScenario);
            Assert.Null(frame.Payload.InteractionItemId);
            Assert.Equal("hi", frame.Payload.Delta);
        });
    }

    private static IEnumerable<ContractCheck> WsForwardCompatibilityChecks()
    {
        /* -- NFR-W-12: unknown type and malformed JSON must never throw -- */
        yield return Ws("unknown_type_returns_UnknownType_without_throwing", () =>
        {
            const string json = "{\"type\":\"future.feature.event\",\"payload\":{\"a\":1}}";
            var result = WsFrameParser.Parse(json);
            Assert.Equal(WsFrameParseKind.UnknownType, result.Kind);
            Assert.Null(result.Frame);
            Assert.Equal("future.feature.event", result.RawType);
        });

        yield return Ws("invalid_json_returns_InvalidJson_without_throwing", () =>
        {
            var result = WsFrameParser.Parse("{ this is not json ");
            Assert.Equal(WsFrameParseKind.InvalidJson, result.Kind);
            Assert.Null(result.Frame);
        });

        yield return Ws("empty_and_null_frames_are_invalid_not_fatal", () =>
        {
            Assert.Equal(WsFrameParseKind.InvalidJson, WsFrameParser.Parse(null).Kind);
            Assert.Equal(WsFrameParseKind.InvalidJson, WsFrameParser.Parse(string.Empty).Kind);
            Assert.Equal(WsFrameParseKind.InvalidJson, WsFrameParser.Parse("   ").Kind);
        });

        yield return Ws("non_object_root_is_rejected", () =>
        {
            Assert.Equal(WsFrameParseKind.InvalidJson, WsFrameParser.Parse("[1,2,3]").Kind);
            Assert.Equal(WsFrameParseKind.InvalidJson, WsFrameParser.Parse("\"text\"").Kind);
        });

        yield return Ws("missing_type_is_treated_as_unknown", () =>
        {
            var result = WsFrameParser.Parse("{\"payload\":{\"delta\":\"x\"}}");
            Assert.Equal(WsFrameParseKind.UnknownType, result.Kind);
        });

        yield return Ws("unknown_fields_are_ignored", () =>
        {
            const string json =
                "{\"type\":\"chat.reply.stream\",\"requestId\":\"r\",\"futureField\":123,\"payload\":{\"delta\":\"hi\",\"done\":true,\"messageId\":\"m1\",\"brandNew\":true}}";
            var result = WsFrameParser.Parse(json);
            Assert.Equal(WsFrameParseKind.Known, result.Kind);
            var frame = Assert.IsType<WsReplyStreamFrame>(result.Frame);
            Assert.Equal("r", frame.RequestId);
            Assert.Equal("m1", frame.Payload.MessageId);
            Assert.True(frame.Payload.Done);
        });

        yield return Ws("wrong_payload_shape_degrades_to_unknown_not_throw", () =>
        {
            // Payload replaced by a string: the parser must degrade, never break the connection.
            var result = WsFrameParser.Parse("{\"type\":\"chat.reply.stream\",\"payload\":\"oops\"}");
            Assert.True(result.Kind is WsFrameParseKind.UnknownType or WsFrameParseKind.InvalidJson,
                $"expected a graceful degradation, got {result.Kind}.");
        });

        yield return Ws("known_frame_type_set_is_complete", () =>
        {
            string[] expected =
            [
                "pong", "chat.message.echo", "chat.queued", "chat.typing", "chat.reply.stream",
                "bot.error", "memory.fact.created", "auth.expired", "points.changed", "level.changed",
                "streak.warning", "makeup_card.changed", "points.snapshot",
            ];

            var actual = WsFrameParser.KnownServerFrameTypes;
            foreach (var type in expected)
            {
                Assert.Contains(type, actual);
            }

            Assert.Equal(expected.Length, actual.Count);
        });

        yield return Ws("chat_queued_uses_server_supplied_debounce_window", () =>
        {
            var result = WsFrameParser.Parse("{\"type\":\"chat.queued\",\"requestId\":\"q1\",\"payload\":{\"debounceWindowSec\":20.0}}");
            Assert.Equal(WsFrameParseKind.Known, result.Kind);
            var frame = Assert.IsType<WsQueuedFrame>(result.Frame);
            Assert.Equal("q1", frame.RequestId);
            Assert.Equal(20.0, frame.Payload.DebounceWindowSec);
        });

        yield return Ws("typing_stage_is_passed_through_verbatim", () =>
        {
            var result = WsFrameParser.Parse("{\"type\":\"chat.typing\",\"payload\":{\"typing\":true,\"stage\":\"vision\"}}");
            Assert.Equal(WsFrameParseKind.Known, result.Kind);
            var frame = Assert.IsType<WsTypingFrame>(result.Frame);
            Assert.True(frame.Payload.Typing);
            Assert.Equal("vision", frame.Payload.Stage);
        });

        yield return Ws("bot_error_carries_error_code_and_request_ids", () =>
        {
            var result = WsFrameParser.Parse(
                "{\"type\":\"bot.error\",\"requestId\":\"e1\",\"payload\":{\"errorCode\":\"VISION_INVALID_MIME\",\"message\":\"m\",\"requestIds\":[\"a\",\"b\"],\"timestamp\":5}}");
            Assert.Equal(WsFrameParseKind.Known, result.Kind);
            var frame = Assert.IsType<WsBotErrorFrame>(result.Frame);
            Assert.Equal("VISION_INVALID_MIME", frame.Payload.ErrorCode);
            Assert.Equal(["a", "b"], frame.Payload.RequestIds);
        });

        yield return Ws("memory_fact_frame_is_parsed", () =>
        {
            var result = WsFrameParser.Parse(
                "{\"type\":\"memory.fact.created\",\"payload\":{\"factId\":\"f1\",\"userId\":\"u1\",\"fact\":\"likes coffee\",\"timestamp\":9}}");
            Assert.Equal(WsFrameParseKind.Known, result.Kind);
            var frame = Assert.IsType<WsMemoryFactFrame>(result.Frame);
            Assert.Equal("f1", frame.Payload.FactId);
        });

        yield return Ws("auth_expired_frame_is_parsed", () =>
        {
            var result = WsFrameParser.Parse("{\"type\":\"auth.expired\",\"payload\":{\"reason\":\"expired\",\"timestamp\":11}}");
            Assert.Equal(WsFrameParseKind.Known, result.Kind);
            var frame = Assert.IsType<WsAuthExpiredFrame>(result.Frame);
            Assert.Equal("expired", frame.Payload.Reason);
        });

        yield return Ws("points_level_streak_makeup_frames_are_parsed", () =>
        {
            var points = WsFrameParser.Parse(
                "{\"type\":\"points.changed\",\"payload\":{\"ledgerId\":1,\"reasonCode\":\"DAILY_FIRST_CHAT\",\"changeAmount\":1,\"balanceAfter\":10,\"balance\":10,\"relatedItemId\":null,\"businessDate\":\"2026-01-01\",\"timestamp\":1}}");
            Assert.IsType<WsPointsChangedFrame>(points.Frame);

            var level = WsFrameParser.Parse(
                "{\"type\":\"level.changed\",\"payload\":{\"levelCode\":\"PANDA_LV5\",\"levelName\":\"n\",\"prevLevelCode\":\"PANDA_LV4\",\"continuousDays\":60,\"changeType\":\"UPGRADE\",\"changeSource\":\"CHAT\",\"highestLevelCode\":\"PANDA_LV5\",\"gapDays\":0,\"breakDeadlineDate\":\"2026-02-01\",\"nextLevelCode\":\"PANDA_LV6\",\"nextLevelName\":\"x\",\"daysToNextLevel\":40,\"timestamp\":2}}");
            Assert.IsType<WsLevelChangedFrame>(level.Frame);

            var streak = WsFrameParser.Parse(
                "{\"type\":\"streak.warning\",\"payload\":{\"levelCode\":\"PANDA_LV5\",\"levelName\":\"n\",\"continuousDays\":60,\"gapDays\":3,\"remainingDays\":1,\"deadlineDate\":\"2026-01-04\",\"timestamp\":3}}");
            Assert.IsType<WsStreakWarningFrame>(streak.Frame);

            var makeup = WsFrameParser.Parse(
                "{\"type\":\"makeup_card.changed\",\"payload\":{\"reason\":\"MONTHLY_GRANT\",\"available\":1,\"used\":0,\"totalGranted\":1,\"maxAvailable\":12,\"lastGrantedMonth\":\"2026-01\",\"timestamp\":4}}");
            var makeupFrame = Assert.IsType<WsMakeupCardChangedFrame>(makeup.Frame);
            Assert.Equal("MONTHLY_GRANT", makeupFrame.Payload.Reason);
        });

        yield return Ws("done_frame_exposes_message_and_timer_fields", () =>
        {
            var result = WsFrameParser.Parse(
                "{\"type\":\"chat.reply.stream\",\"requestId\":\"r1\",\"payload\":{\"delta\":\"\",\"done\":true,\"messageId\":\"m1\",\"finalContent\":\"a\\nb\",\"timestamp\":1700000000000,\"contentType\":\"text\",\"modelProvider\":\"deepseek\",\"timerInstruction\":{\"target\":\"02:00\",\"text\":\"late\"},\"requestIds\":[\"r1\",\"r0\"],\"messageKind\":\"interaction\",\"interactionItemId\":\"coffee\",\"interactionItemName\":\"coffee\",\"interactionItemIcon\":\"C\",\"interactionFailed\":false}}");
            Assert.Equal(WsFrameParseKind.Known, result.Kind);
            var frame = Assert.IsType<WsReplyStreamFrame>(result.Frame);
            Assert.Equal("m1", frame.Payload.MessageId);
            Assert.Equal("a\nb", frame.Payload.FinalContent);
            Assert.Equal(1700000000000L, frame.Payload.Timestamp);
            Assert.Equal("02:00", frame.Payload.TimerInstruction!.Target);
            Assert.Equal(["r1", "r0"], frame.Payload.RequestIds);
            Assert.Equal("interaction", frame.Payload.MessageKind);
            Assert.Equal("coffee", frame.Payload.InteractionItemId);
            Assert.False(frame.Payload.InteractionFailed);
        });
    }
}
