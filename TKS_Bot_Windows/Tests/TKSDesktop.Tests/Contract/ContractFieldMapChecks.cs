using TKSDesktop.Contracts.Dtos;
using Xunit;

namespace TKSDesktop.Tests.Contract;

/// <summary>
/// Per-DTO field-map checks (group "dto"), anchored on PRD section 5.2 / 5.3.
/// Each check pins one DTO's exact [JsonPropertyName] set, so a single renamed field fails with that
/// field's name in the message -- this is the wire surface the mock backend serves.
/// </summary>
internal static partial class ContractChecks
{
    private static ContractCheck Dto(string id, Type type, params string[] fields)
        => new("dto", id, () => ContractEngine.AssertJsonMap(type, fields));

    private static ContractCheck Dto7(string id, Type type, params string[] fields)
        => new("dto7", id, () => ContractEngine.AssertJsonMap(type, fields));

    private static IEnumerable<ContractCheck> EnvelopeDtoChecks()
    {
        // ---- section 5.2 auth / envelope (no envelope on /auth/*) ----
        yield return Dto("AuthTokensDto", typeof(AuthTokensDto),
            "accessToken", "refreshToken", "tokenType", "expiresIn", "userId", "deviceId");
        yield return Dto("LoginRequestDto", typeof(LoginRequestDto), "username", "password", "deviceId");
        yield return Dto("RefreshRequestDto", typeof(RefreshRequestDto), "refreshToken");
        yield return Dto("ApiEnvelope", typeof(ApiEnvelope<object>), "code", "message", "data", "traceId");
        yield return Dto("ApiErrorEnvelope", typeof(ApiErrorEnvelope), "code", "message", "traceId", "detail");
        yield return Dto("ApiErrorDetail", typeof(ApiErrorDetail), "code", "message", "traceId");

        // ---- section 5.2 history / memory / city / degraded chat ----
        yield return Dto("TimelineItemDto", typeof(TimelineItemDto),
            "messageId", "userId", "role", "content", "timestamp",
            "contentType", "itemId", "itemName", "itemIcon", "costPoints",
            "interactionFailed", "messageKind", "greetingScenario", "modelProvider");
        yield return Dto("ChatHistoryDataDto", typeof(ChatHistoryDataDto), "items");
        yield return Dto("UserFactDto", typeof(UserFactDto), "factId", "userId", "fact", "timestamp");
        yield return Dto("MemoryFactsDataDto", typeof(MemoryFactsDataDto), "items");
        yield return Dto("CityDataDto", typeof(CityDataDto), "city");
        yield return Dto("CityUpdateRequestDto", typeof(CityUpdateRequestDto), "city");
        yield return Dto("RestChatRequestDto", typeof(RestChatRequestDto),
            "requestId", "conversationId", "message", "stream", "traceId");
        yield return Dto("RestChatDataDto", typeof(RestChatDataDto),
            "conversationId", "reply", "messageId", "model", "debounceWindowSec", "timerInstruction");
        yield return Dto("TimerInstructionDto", typeof(TimerInstructionDto), "target", "text");

        // ---- section 5.3.1 client to server frames ----
        yield return Dto("ChatImagePayloadDto", typeof(ChatImagePayloadDto), "mimeType", "dataBase64", "localUri");
        yield return Dto("ChatMessagePayloadDto", typeof(ChatMessagePayloadDto),
            "messageType", "content", "images", "timestamp");
        yield return Dto("WsPingFrameDto", typeof(WsPingFrameDto), "type", "payload");
        yield return Dto("WsPingPayloadDto", typeof(WsPingPayloadDto), "timestamp");
        yield return Dto("WsChatMessageFrameDto", typeof(WsChatMessageFrameDto), "type", "requestId", "payload");

        // ---- section 5.3.2 server to client frame payloads ----
        yield return Dto("ChatTypingPayloadDto", typeof(ChatTypingPayloadDto), "typing", "stage");
        yield return Dto("ChatReplyStreamPayloadDto", typeof(ChatReplyStreamPayloadDto),
            "delta", "done", "messageId", "finalContent", "timestamp", "contentType", "modelProvider",
            "timerInstruction", "requestIds", "messageKind", "greetingScenario",
            "interactionItemId", "interactionItemName", "interactionItemIcon", "interactionFailed");
        yield return Dto("ChatEchoPayloadDto", typeof(ChatEchoPayloadDto),
            "content", "imageCount", "timestamp", "originDeviceId");
        yield return Dto("ChatQueuedPayloadDto", typeof(ChatQueuedPayloadDto), "debounceWindowSec");
        yield return Dto("BotErrorPayloadDto", typeof(BotErrorPayloadDto),
            "errorCode", "message", "requestIds", "timestamp");
        yield return Dto("AuthExpiredPayloadDto", typeof(AuthExpiredPayloadDto), "reason", "timestamp");
        yield return Dto("PointsChangedPayloadDto", typeof(PointsChangedPayloadDto),
            "ledgerId", "reasonCode", "changeAmount", "balanceAfter", "balance",
            "relatedItemId", "businessDate", "timestamp");
        yield return Dto("LevelChangedPayloadDto", typeof(LevelChangedPayloadDto),
            "levelCode", "levelName", "prevLevelCode", "continuousDays", "changeType", "changeSource",
            "highestLevelCode", "gapDays", "breakDeadlineDate", "nextLevelCode", "nextLevelName",
            "daysToNextLevel", "timestamp");
        yield return Dto("StreakWarningPayloadDto", typeof(StreakWarningPayloadDto),
            "levelCode", "levelName", "continuousDays", "gapDays", "remainingDays", "deadlineDate", "timestamp");
        yield return Dto("MakeupCardChangedPayloadDto", typeof(MakeupCardChangedPayloadDto),
            "reason", "available", "used", "totalGranted", "maxAvailable", "lastGrantedMonth", "timestamp");
    }

    /// <summary>Per-DTO field maps for the gamification surface (PRD section 7.0 / 7.1), group "dto7".</summary>
    private static IEnumerable<ContractCheck> GamificationDtoChecks()
    {
        // ---- interaction (camelCase) ----
        yield return Dto7("InteractionItemDto", typeof(InteractionItemDto),
            "id", "name", "icon", "iconUrl", "costPoints", "sortOrder", "affordable");
        yield return Dto7("InteractionItemsDataDto", typeof(InteractionItemsDataDto), "balance", "dailyLimit", "items");
        yield return Dto7("InteractionSendRequestDto", typeof(InteractionSendRequestDto), "itemId", "requestId", "text");
        yield return Dto7("InteractionSendDataDto", typeof(InteractionSendDataDto),
            "success", "itemId", "requestId", "balance", "charged", "refunded", "duplicate", "item",
            "reply", "messageId", "timerInstruction", "mergedCount", "mergedRequestIds",
            "fallbackText", "errorCode", "messageText");
        yield return Dto7("InteractionSendItemDto", typeof(InteractionSendItemDto), "id", "name", "icon", "costPoints");

        // ---- points / level / makeup card (camelCase outer) ----
        yield return Dto7("PointsBalanceDataDto", typeof(PointsBalanceDataDto), "balance", "updatedAt");
        yield return Dto7("MakeupCardSummaryDto", typeof(MakeupCardSummaryDto),
            "available", "used", "totalGranted", "maxAvailable", "monthlyGrant",
            "lastGrantedMonth", "currentMonthGranted", "atLimit");
        yield return Dto7("LevelStatusDto", typeof(LevelStatusDto),
            "levelCode", "levelName", "prevLevelCode", "continuousDays", "changeType", "changeSource",
            "highestLevelCode", "lastValidDate", "gapDays", "breakDeadlineDate", "nextLevelCode",
            "nextLevelName", "nextLevelThresholdDays", "daysToNextLevel", "levelUpdatedAt",
            "isDefaultLevel", "availableMakeupCards");
        yield return Dto7("PointsOverviewDataDto", typeof(PointsOverviewDataDto),
            "balance", "balanceUpdatedAt", "level", "makeupCard");

        // ---- the three snake_case exceptions plus their outer envelopes ----
        yield return Dto7("LevelConfigDataDto", typeof(LevelConfigDataDto),
            "levels", "defaultLevelCode", "defaultLevelName", "makeupCardMax", "breakGapDays", "warningGapDays");
        yield return Dto7("PagedDataDto", typeof(PagedDataDto<object>),
            "items", "total", "page", "pageSize", "hasMore");
        yield return Dto7("MakeupCandidateDto", typeof(MakeupCandidateDto), "date", "daysAgo");
        yield return Dto7("MakeupCandidatesDataDto", typeof(MakeupCandidatesDataDto),
            "items", "total", "firstActivityDate", "available");
        yield return Dto7("MakeupCardUseRequestDto", typeof(MakeupCardUseRequestDto), "targetDate");
        yield return Dto7("MakeupCardUseDataDto", typeof(MakeupCardUseDataDto),
            "success", "availableCards", "targetDate", "card", "level");
    }
}
