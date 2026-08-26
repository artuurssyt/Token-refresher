using LocaltsAccountManager.Core.Models;

namespace LocaltsAccountManager.Core.Interfaces;

public interface ILocaltsService
{
    bool IsApiVerified { get; }
    Task<bool> HasApiKeyAsync(CancellationToken cancellationToken = default);
    Task SaveApiKeyAsync(string apiKey, CancellationToken cancellationToken = default);
    Task ClearApiKeyAsync(CancellationToken cancellationToken = default);
    Task<LocaltsUserInfo> ValidateApiKeyAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocaltsOrderSummary>> FetchAllOrdersAsync(CancellationToken cancellationToken = default);
    Task<LocaltsOrderDetail> FetchOrderAsync(string orderId, CancellationToken cancellationToken = default);
    Task<LocaltsConnectivityResult> CheckConnectivityAsync(CancellationToken cancellationToken = default);
}

public sealed class LocaltsConnectivityResult
{
    public bool IsReachable { get; init; }
    public string DiagnosticSummary { get; init; } = string.Empty;
    public string? FailureCategory { get; init; }
}
