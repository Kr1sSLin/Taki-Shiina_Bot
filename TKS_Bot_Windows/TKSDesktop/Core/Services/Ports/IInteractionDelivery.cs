using TKSDesktop.Contracts.Dtos;

namespace TKSDesktop.Core.Services.Ports;

public interface IInteractionDelivery
{
    Task BeginAsync(string requestId, string itemId, string? text);
    Task CompleteAsync(string requestId, InteractionSendDataDto? response, bool success, string? errorCode);
}
