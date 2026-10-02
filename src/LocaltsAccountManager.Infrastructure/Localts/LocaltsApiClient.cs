using System.Net;
using System.Text.Json;
using LocaltsAccountManager.Core.Configuration;
using LocaltsAccountManager.Core.Interfaces;
using LocaltsAccountManager.Core.Models;

namespace LocaltsAccountManager.Infrastructure.Localts;

public sealed class LocaltsApiClient
{
    public const string ApiBaseUrl = "https://localts.store/v1";

    private readonly HttpClient _httpClient;
    private readonly IAppSettingsStore _settingsStore;

    public LocaltsApiClient(HttpClient httpClient, IAppSettingsStore settingsStore)
    {
        _httpClient = httpClient;
        _settingsStore = settingsStore;
        if (_httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new Uri(ApiBaseUrl.TrimEnd('/') + "/");
        }

        if (_httpClient.Timeout == TimeSpan.FromSeconds(100))
        {
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }
    }

    public void SetApiKey(string apiKey)
    {
        _httpClient.DefaultRequestHeaders.Remove("X-API-Key");
        _httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKey);
    }

    public void ClearApiKey()
    {
        _httpClient.DefaultRequestHeaders.Remove("X-API-Key");
    }

    public async Task<LocaltsUserInfo> GetMeAsync(CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(() => _httpClient.GetAsync("me", cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessOrUnauthorizedAsync(response, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureSuccessFlag(json);
        var root = json.RootElement;
        return new LocaltsUserInfo
        {
            Username = root.GetProperty("username").GetString() ?? string.Empty,
            Balance = root.GetProperty("balance").GetDecimal()
        };
    }

    public async Task<(IReadOnlyList<LocaltsOrderSummary> Orders, int Page, int TotalPages, int TotalElements)> GetOrdersAsync(
        int page,
        int size,
        CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(
            () => _httpClient.GetAsync($"orders?page={page}&size={size}", cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessOrUnauthorizedAsync(response, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureSuccessFlag(json);
        var root = json.RootElement;

        var orders = new List<LocaltsOrderSummary>();
        if (root.TryGetProperty("orders", out var ordersElement) && ordersElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var order in ordersElement.EnumerateArray())
            {
                orders.Add(new LocaltsOrderSummary
                {
                    Id = order.GetProperty("id").GetString() ?? string.Empty,
                    ProductId = order.GetProperty("productId").GetString() ?? string.Empty,
                    ProductType = order.GetProperty("productType").GetString() ?? string.Empty,
                    Timestamp = order.GetProperty("timestamp").GetInt64()
                });
            }
        }

        return (
            orders,
            root.TryGetProperty("page", out var pageEl) ? pageEl.GetInt32() : page,
            root.TryGetProperty("totalPages", out var totalPagesEl) ? totalPagesEl.GetInt32() : 1,
            root.TryGetProperty("totalElements", out var totalElementsEl) ? totalElementsEl.GetInt32() : orders.Count);
    }

    public async Task<LocaltsOrderDetail> GetOrderAsync(string orderId, CancellationToken cancellationToken)
    {
        var encodedId = Uri.EscapeDataString(orderId);
        using var response = await SendWithRetryAsync(
            () => _httpClient.GetAsync($"orders/get-order?id={encodedId}", cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new LocaltsApiException("Order was not found.", (int)response.StatusCode);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new LocaltsApiException("Order belongs to another user.", (int)response.StatusCode);
        }

        await EnsureSuccessOrUnauthorizedAsync(response, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureSuccessFlag(json);
        var root = json.RootElement;

        var status = root.TryGetProperty("status", out var statusEl) ? statusEl.GetString() ?? string.Empty : string.Empty;
        string? productName = null;
        if (root.TryGetProperty("product-name", out var productNameEl) && productNameEl.ValueKind == JsonValueKind.String)
        {
            productName = productNameEl.GetString();
        }

        var items = new List<LocaltsOrderItem>();
        if (root.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsEl.EnumerateArray())
            {
                items.Add(new LocaltsOrderItem
                {
                    Id = item.GetProperty("id").GetString() ?? string.Empty,
                    Content = item.GetProperty("content").GetString() ?? string.Empty
                });
            }
        }

        var orderKey = root.TryGetProperty("order-id", out var orderIdEl)
            ? orderIdEl.GetString() ?? orderId
            : orderId;

        return new LocaltsOrderDetail
        {
            OrderId = orderKey,
            Status = status,
            ProductName = productName,
            Items = items
        };
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        var settings = _settingsStore.Load();
        var rateLimitDelay = TimeSpan.FromSeconds(Math.Max(5, settings.LocaltsRateLimitRetrySeconds));
        const int maxServerErrorAttempts = 5;

        for (var serverErrorAttempt = 1; ; serverErrorAttempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HttpResponseMessage response;
            try
            {
                response = await send().ConfigureAwait(false);
            }
            catch (HttpRequestException) when (serverErrorAttempt < maxServerErrorAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * serverErrorAttempt), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && serverErrorAttempt < maxServerErrorAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * serverErrorAttempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = ParseRetryAfter(response) ?? rateLimitDelay;
                response.Dispose();
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                serverErrorAttempt = 1;
                continue;
            }

            if ((int)response.StatusCode >= 500)
            {
                var statusCode = (int)response.StatusCode;
                response.Dispose();
                if (serverErrorAttempt >= maxServerErrorAttempts)
                {
                    throw new LocaltsApiException($"Localts API returned HTTP {statusCode}.", statusCode);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500 * serverErrorAttempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            return response;
        }
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
        {
            return null;
        }

        var raw = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (int.TryParse(raw, out var seconds) && seconds > 0)
        {
            return TimeSpan.FromSeconds(seconds);
        }

        if (DateTimeOffset.TryParse(raw, out var retryAt))
        {
            var wait = retryAt - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : null;
        }

        return null;
    }

    private static async Task EnsureSuccessOrUnauthorizedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new LocaltsApiException("Localts API key is missing or invalid.", 401);
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new LocaltsApiException($"Localts rejected the request: {TrimForDisplay(body)}", 400);
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new LocaltsApiException("Localts API returned HTTP 429.", 429);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new LocaltsApiException($"Localts API returned HTTP {(int)response.StatusCode}.", (int)response.StatusCode);
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new LocaltsApiException("Localts returned malformed JSON.", null, ex);
        }
    }

    private static void EnsureSuccessFlag(JsonDocument json)
    {
        if (!json.RootElement.TryGetProperty("success", out var successEl) || successEl.ValueKind != JsonValueKind.True)
        {
            var error = json.RootElement.TryGetProperty("error", out var errorEl)
                ? errorEl.ToString()
                : "unknown error";
            throw new LocaltsApiException($"Localts API reported failure: {TrimForDisplay(error)}", null);
        }
    }

    private static string TrimForDisplay(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 160 ? trimmed : trimmed[..160] + "...";
    }
}

public sealed class LocaltsApiException : Exception
{
    public int? StatusCode { get; }

    public LocaltsApiException(string message, int? statusCode, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}
