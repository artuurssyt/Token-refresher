namespace LocaltsAccountManager.Core.Models;

public sealed class LocaltsUserInfo
{
    public string Username { get; init; } = string.Empty;
    public decimal Balance { get; init; }
}

public sealed class LocaltsOrderSummary
{
    public string Id { get; init; } = string.Empty;
    public string ProductId { get; init; } = string.Empty;
    public string ProductType { get; init; } = string.Empty;
    public long Timestamp { get; init; }
}

public sealed class LocaltsOrderItem
{
    public string Id { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
}

public sealed class LocaltsOrderDetail
{
    public string OrderId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string? ProductName { get; init; }
    public IReadOnlyList<LocaltsOrderItem> Items { get; init; } = Array.Empty<LocaltsOrderItem>();
}
