using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TKSDesktop.Contracts.Dtos;
using TKSDesktop.Core.Network;
using TKSDesktop.Core.Platform;
using TKSDesktop.Core.Services;
using TKSDesktop.Core.Services.Adapters;
using TKSDesktop.Core.Data;
using TKSDesktop.Core.Data.Repositories;
using TKSDesktop.ViewModels;
using Xunit;

namespace TKSDesktop.Tests;

public sealed class GamificationTests
{
    [Fact]
    public async Task OfflineRestartRestoresProgressLedgerAndCardsFromSqlite()
    {
        var root = Path.Combine(Path.GetTempPath(), "tks-m4-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var db = await LocalDb.OpenAsync(Path.Combine(root, "cache.db"));
            var cache = new ProgressCacheRepository(db);
            using (var online = new Fixture(cache, new Auth()))
            {
                await online.Service.RefreshOverviewAsync();
                await online.Service.GetPointsHistoryAsync(1, 20);
                await online.Service.GetMakeupHistoryAsync(1, 20);
                await online.Service.GetLevelConfigAsync();
            }
            using var offline = new Fixture(cache, new Auth());
            offline.Handler.Offline = true;
            var restored = await offline.Service.RefreshOverviewAsync();
            Assert.True(restored.IsOfflineCache);
            Assert.Equal(17, restored.Balance);
            Assert.Equal(2, restored.AvailableMakeupCards);
            Assert.Single((await offline.Service.GetPointsHistoryAsync(1, 20)).Items);
            Assert.Single((await offline.Service.GetMakeupHistoryAsync(1, 20)).Items);
            Assert.Equal(9, Assert.Single((await offline.Service.GetLevelConfigAsync()).Levels).ThresholdDays);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(40201, false)]
    [InlineData(40204, true)]
    public async Task BusinessFailurePreservesAuthoritativeBalanceAndRefund(int code, bool refunded)
    {
        using var fixture = new Fixture();
        fixture.Handler.SendCode = code;
        fixture.Handler.Refunded = refunded;
        var result = await fixture.Service.SendInteractionAsync("coffee", "stable-request", "note");
        Assert.False(result.Success);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(17, result.Balance);
        Assert.Equal(refunded, result.Refunded);
        Assert.Equal("fallback", result.FallbackText);
        Assert.Equal(17, fixture.Service.Current?.Balance);
        Assert.Empty(result.RequestIdsToMarkDelivered);
    }

    [Fact]
    public async Task MissingErrorDataDoesNotBecomeSuccess()
    {
        using var handler = new StubHttpMessageHandler((HttpStatusCode.OK,
            """{"code":40201,"data":{"balance":0}}"""));
        using var rest = new RestClient(new HttpClient(handler), new FakeTokenProvider(),
            () => "https://example.test/api/v1/", NullLogger<RestClient>.Instance);
        var result = await rest.GetAsync<InteractionSendDataDto>("interaction/send");
        Assert.False(result.TryGetValue(out _));
        Assert.Null(result.Value);
        Assert.Equal(0, result.FailureValue?.Balance);
    }

    [Fact]
    public async Task OverviewFailureMarksExistingDataOfflineWithoutReplacingWithZero()
    {
        using var fixture = new Fixture();
        var online = await fixture.Service.RefreshOverviewAsync();
        fixture.Handler.Offline = true;
        ProgressSnapshot? published = null;
        fixture.Service.ProgressChanged += (_, value) => published = value;
        var offline = await fixture.Service.RefreshOverviewAsync();
        Assert.True(offline.IsOfflineCache);
        Assert.Equal(online.Balance, offline.Balance);
        Assert.Equal(online.SyncedAt, offline.SyncedAt);
        Assert.Equal(offline, published);
    }

    [Fact]
    public async Task EventsSetBalancesIncludingZeroAndClearWarningOnValidChat()
    {
        using var fixture = new Fixture();
        await fixture.Service.RefreshOverviewAsync();
        var warnings = new List<StreakWarningPayloadDto>();
        fixture.Service.StreakWarning += (_, value) => warnings.Add(value);
        await fixture.Service.HandleFrameAsync(new WsStreakWarningFrame(new() { GapDays = 3, RemainingDays = 2, DeadlineDate = "2026-09-25" }));
        await fixture.Service.HandleFrameAsync(new WsPointsChangedFrame(new() { BalanceAfter = 0, Balance = 0 }));
        Assert.Equal(0, fixture.Service.Current?.Balance);
        await fixture.Service.HandleFrameAsync(new WsPointsChangedFrame(new() { BalanceAfter = 1, ReasonCode = "DAILY_FIRST_CHAT" }));
        Assert.Equal(new[] { 3, 0 }, warnings.Select(w => w.GapDays));
        await fixture.Service.HandleFrameAsync(new WsMakeupCardChangedFrame(new() { Available = 0, Reason = "USED" }));
        Assert.Equal(0, fixture.Service.Current?.AvailableMakeupCards);
    }

    [Theory]
    [InlineData("UPGRADE", true)]
    [InlineData("RESTORE", false)]
    [InlineData("RESET", false)]
    public async Task LevelFeedbackIsTypedAndDuplicateEventsDoNotCelebrateTwice(string type, bool celebration)
    {
        using var fixture = new Fixture();
        var feedback = new List<LevelChangeFeedback>();
        fixture.Service.LevelChanged += (_, value) => feedback.Add(value);
        var frame = new WsLevelChangedFrame(new() { ChangeType = type, LevelCode = "PANDA_LV1", ContinuousDays = 3, Timestamp = 123 });
        await fixture.Service.HandleFrameAsync(frame);
        await fixture.Service.HandleFrameAsync(frame);
        Assert.Equal(celebration, Assert.Single(feedback).PlayCelebration);
    }

    [Fact]
    public async Task MenuUsesServerAffordabilityAndRetainsIdAndTextAfterAmbiguousFailure()
    {
        using var fixture = new Fixture();
        using var menu = new InteractionMenuViewModel(fixture.Service, new ImmediateDispatcher());
        await menu.LoadAsync();
        Assert.Equal(new[] { "coffee", "noodles" }, menu.Items.Select(i => i.ItemId));
        Assert.False(menu.Items[1].CanSend);
        menu.MessageText = "keep me";
        fixture.Handler.FailSend = true;
        await menu.SendCommand.ExecuteAsync(menu.Items[0]);
        Assert.Equal("keep me", menu.MessageText);
        fixture.Handler.FailSend = false;
        var sent = new List<string>();
        menu.Sent += (_, text) => sent.Add(text);
        await menu.SendCommand.ExecuteAsync(menu.Items[0]);
        Assert.Equal(2, fixture.Handler.RequestIds.Count);
        Assert.Equal(fixture.Handler.RequestIds[0], fixture.Handler.RequestIds[1]);
        Assert.Equal("keep me", Assert.Single(sent));
        Assert.Equal(string.Empty, menu.MessageText);
    }

    [Fact]
    public async Task MakeupRequiresConfirmationAndUsesServerCandidateDate()
    {
        using var fixture = new Fixture();
        var prompt = new Prompt();
        using var profile = new ProfileViewModel(fixture.Service, new Notifications(), prompt, new ImmediateDispatcher());
        await profile.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("2024-01-02", Assert.Single(profile.MakeupCandidates));
        prompt.Allow = false;
        await profile.UseMakeupCardCommand.ExecuteAsync("2024-01-02");
        Assert.Empty(fixture.Handler.UsedDates);
        prompt.Allow = true;
        await profile.UseMakeupCardCommand.ExecuteAsync("2024-01-02");
        Assert.Equal("2024-01-02", Assert.Single(fixture.Handler.UsedDates));
        Assert.Contains("2024-01-02", prompt.LastConfirmation);
        Assert.Single(profile.MakeupHistory);
    }

    [Fact]
    public async Task LedgerSupportsReasonFilterAndTotalBasedPagination()
    {
        using var fixture = new Fixture();
        using var profile = new ProfileViewModel(fixture.Service, new Notifications(), new Prompt(), new ImmediateDispatcher());
        profile.SelectedReasonCode = "ITEM_SEND";
        await profile.FilterLedgerCommand.ExecuteAsync(null);
        Assert.True(profile.HasMoreLedger);
        Assert.Contains("reasonCode=ITEM_SEND", fixture.Handler.LastLedgerQuery);
        Assert.Equal("-5", Assert.Single(profile.Ledger).ChangeText);
        Assert.DoesNotContain("{0}", profile.Ledger[0].ReasonText);
    }

    [Fact]
    public async Task NotificationSemanticIdsMatchAndroid()
    {
        using var fixture = new Fixture();
        await fixture.Service.HandleFrameAsync(new WsLevelChangedFrame(new() { ChangeType = "RESTORE", LevelCode = "PANDA_LV1" }));
        await fixture.Service.HandleFrameAsync(new WsStreakWarningFrame(new() { GapDays = 3 }));
        Assert.Equal(new[] { 1006, 1007 }, fixture.Notifications.Ids);
        Assert.Equal(5, Enum.GetValues<NotificationCategory>().Length);
    }

    private sealed class Fixture : IDisposable
    {
        public Handler Handler { get; } = new();
        private readonly RestClient _rest;
        public GamificationService Service { get; }
        public Notifications Notifications { get; } = new();
        public Fixture(ProgressCacheRepository? cache = null, IAuthService? auth = null)
        {
            _rest = new RestClient(new HttpClient(Handler), new FakeTokenProvider(),
                () => "https://example.test/api/v1/", NullLogger<RestClient>.Instance);
            Service = new(new GamificationPortAdapter(_rest), TimeProvider.System, cache: cache, auth: auth, notifications: Notifications);
        }
        public void Dispose() { Service.Dispose(); _rest.Dispose(); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public bool Offline { get; set; }
        public bool FailSend { get; set; }
        public bool Refunded { get; set; }
        public int SendCode { get; set; }
        public List<string> RequestIds { get; } = [];
        public List<string> UsedDates { get; } = [];
        public string LastLedgerQuery { get; private set; } = string.Empty;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("offline");
            var path = request.RequestUri!.AbsolutePath;
            object data = new { };
            var code = 0;
            if (path.EndsWith("/interaction/send", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                RequestIds.Add(body.RootElement.GetProperty("requestId").GetString()!);
                if (FailSend) throw new HttpRequestException("response lost");
                code = SendCode;
                data = new { success = code == 0, balance = 17, refunded = Refunded, fallbackText = "fallback", mergedRequestIds = new[] { "chat-1" } };
            }
            else if (path.EndsWith("/interaction/items", StringComparison.Ordinal))
                data = new
                {
                    balance = 17,
                    items = new[] {
                    new { id = "noodles", name = "noodles", affordable = false, costPoints = 7, sortOrder = 2, icon = "N" },
                    new { id = "coffee", name = "coffee", affordable = true, costPoints = 5, sortOrder = 1, icon = "C" } }
                };
            else if (path.EndsWith("/points/overview", StringComparison.Ordinal))
                data = new { balance = 17, level = new { levelCode = "NONE", levelName = "", isDefaultLevel = true }, makeupCard = new { available = 2 } };
            else if (path.EndsWith("/candidates", StringComparison.Ordinal))
                data = new { available = 2, items = new[] { new { date = "2024-01-02", daysAgo = 994 } } };
            else if (path.EndsWith("/makeup-card", StringComparison.Ordinal))
                data = new { available = 2, monthlyGrant = 1, maxAvailable = 12 };
            else if (path.EndsWith("/use", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                UsedDates.Add(body.RootElement.GetProperty("targetDate").GetString()!);
                data = new { success = true, availableCards = 1, level = new { changeType = "RESTORE", levelCode = "PANDA_LV1", continuousDays = 3 } };
            }
            else if (path.EndsWith("/makeup-card/history", StringComparison.Ordinal))
                data = new { total = 1, page = 1, pageSize = 20, items = new[] { new { id = 1, granted_month = "2024-01", status = "USED", used_for_date = "2024-01-02", created_at = 1L } } };
            else if (path.EndsWith("/points/history", StringComparison.Ordinal))
            {
                LastLedgerQuery = request.RequestUri.Query;
                data = new { total = 21, page = 1, pageSize = 20, items = new[] { new { id = 1, change_amount = -5, balance_after = 17, reason_code = "ITEM_SEND", related_item_id = "coffee" } } };
            }
            else if (path.EndsWith("/level/config", StringComparison.Ordinal))
                data = new { levels = new[] { new { level_code = "PANDA_LV1", level_name = "custom", threshold_days = 9, sort_order = 1 } } };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { code, data }), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool IsOnUiThread => true;
        public void Invoke(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
    }
    private sealed class Notifications : IUserNotificationService
    {
        public List<int> Ids { get; } = [];
        public void NotifyProgress(SemanticNotificationId semanticId, string title, string body) => Ids.Add((int)semanticId);
        public bool IsWindowFocused { get; set; }
        public void Notify(NotificationCategory category, string title, string body, string? messageId = null) { }
        public void ClearMessageNotifications() { }
    }
    private sealed class Prompt : IUserPrompt
    {
        public bool Allow { get; set; }
        public string? LastConfirmation { get; private set; }
        public bool Confirm(string message) { LastConfirmation = message; return Allow; }
        public void Alert(string message) { }
    }

    private sealed class Auth : IAuthService
    {
        public AuthState State => AuthState.Authenticated;
        public string UserId => "test-user";
        public string DeviceId => "test-device";
        public CredentialViewStatus CredentialStatus => new(true, false, string.Empty);
        public Task<LoginOutcome> LoginAsync(string username, string password, CancellationToken ct = default) => throw new NotSupportedException();
        public Core.Auth.CredentialRestoreResult TryRestore() => throw new NotSupportedException();
        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
