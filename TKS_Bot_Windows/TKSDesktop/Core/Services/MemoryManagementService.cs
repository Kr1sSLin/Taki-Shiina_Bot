using System.Net.Http;
using System.Text.Json;
using TKSDesktop.Contracts;
using TKSDesktop.Contracts.Dtos;
using TKSDesktop.Core.Auth;
using TKSDesktop.Core.Data.Repositories;
using TKSDesktop.Core.Network;

namespace TKSDesktop.Core.Services;

/// <summary>Server-authoritative memory permissions and proactive switch.</summary>
public sealed class MemoryManagementService
{
    private readonly FactRepository _facts;
    private readonly RestClient _rest;
    private readonly AuthService _auth;
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    public MemoryManagementService(FactRepository facts, RestClient rest, AuthService auth)
    {
        _facts = facts;
        _rest = rest;
        _auth = auth;
    }

    public Task<ApiResult<MemoryFactDetailDto>> GetDetailAsync(string factId, CancellationToken ct = default) =>
        _rest.GetAsync<MemoryFactDetailDto>($"/memory/facts/{Uri.EscapeDataString(factId)}",
            cancellationToken: ct);

    public Task<ApiResult<JsonElement>> CorrectFactAsync(string factId, string content, long expectedRevision,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(factId) || string.IsNullOrWhiteSpace(content) ||
            content.Trim().Length > 2000 || expectedRevision < 1)
            throw new ArgumentException("Invalid memory correction request");
        return _rest.SendAsync<JsonElement>(HttpMethod.Patch,
            $"/memory/facts/{Uri.EscapeDataString(factId)}/correction",
            new { content = content.Trim(), expectedRevision, operationId = Guid.NewGuid().ToString("N") },
            envelope: true, authenticated: true, allowRefresh: true, query: null,
            ProtocolConstants.RestTimeoutMs, ct);
    }

    public Task<string?> LatestOperationStateAsync(string factId, CancellationToken ct = default) =>
        _facts.LatestPermissionStateAsync(factId, ct);

    public Task<bool> QueuePermissionAsync(string factId, string userId, bool enabled, long expectedRevision,
        CancellationToken ct = default) =>
        string.Equals(_auth.Describe().UserId, userId, StringComparison.Ordinal)
            ? _facts.QueuePermissionAsync(new MemoryPermissionOperation(Guid.NewGuid().ToString("N"),
                userId, factId, enabled, expectedRevision, "pending"), ct)
            : Task.FromResult(false);

    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var operation in await _facts.PendingPermissionsAsync(ct).ConfigureAwait(false))
            {
                if (!string.Equals(_auth.Describe().UserId, operation.UserId, StringComparison.Ordinal))
                    continue;
                var result = await _rest.SendAsync<JsonElement>(HttpMethod.Patch,
                    $"/memory/facts/{Uri.EscapeDataString(operation.FactId)}/permission",
                    new { enabled = operation.Enabled, expectedRevision = operation.ExpectedRevision,
                        operationId = operation.OperationId }, envelope: true, authenticated: true,
                    allowRefresh: true, query: null, ProtocolConstants.RestTimeoutMs, ct).ConfigureAwait(false);
                if (result.IsSuccess)
                    await _facts.MarkPermissionAsync(operation.OperationId, "applied", ct).ConfigureAwait(false);
                else if (result.Error?.HttpStatus is 409 or 403)
                    await _facts.MarkPermissionAsync(operation.OperationId, "conflict", ct).ConfigureAwait(false);
                else
                    return; // Retry with the same operation ID after reconnect.
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    public Task<ApiResult<ProactiveSettingsDto>> GetProactiveAsync(CancellationToken ct = default) =>
        _rest.GetAsync<ProactiveSettingsDto>("/settings/proactive", cancellationToken: ct);

    public Task<ApiResult<ProactiveSettingsDto>> SetProactiveAsync(bool enabled, CancellationToken ct = default) =>
        _rest.PutAsync<ProactiveSettingsDto>("/settings/proactive", new { enabled }, cancellationToken: ct);
}
