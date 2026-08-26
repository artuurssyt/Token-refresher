using System.Net;

namespace LocaltsAccountManager.Infrastructure.Minecraft;

public sealed class MinecraftApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }

    public MinecraftApiException(string message, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}
