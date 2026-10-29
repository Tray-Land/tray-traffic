using System.Net;
using TrayTraffic.Core;

namespace TrayTraffic.Services;

internal static class TrafficServices
{
    public const string CredentialName = "TomTomApiKey";

    private static readonly HttpClient Http = CreateHttpClient();
    public static TomTomClient Client { get; } = new(Http);

    public static Task<string?> ReadKeyAsync(CancellationToken cancellationToken) =>
        Task.Run(() => CredentialStore.Get(CredentialName) ?? Secrets.TomTomApiKey, cancellationToken);

    public static string ErrorMessage(Exception exception) => exception switch
    {
        TrafficServiceException service => service.Message,
        ArgumentException => "Check your input and try again.",
        OperationCanceledException => "The request took too long. Try again shortly.",
        HttpRequestException => ConnectivityService.IsInternetAvailable
            ? "Can't reach TomTom. Retrying shortly."
            : "No internet connection. This will update when you're back online.",
        _ => "This operation couldn't finish. Try again; if it continues, restart the app.",
    };

    private static HttpClient CreateHttpClient()
    {
        HttpClient http = new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = false,
            UseCookies = false,
            MaxConnectionsPerServer = 4,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TrayTraffic/1.0");
        return http;
    }
}
