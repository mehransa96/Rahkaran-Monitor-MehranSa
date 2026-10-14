using System.Net.Http.Headers;
using System.Text;

namespace RSM.Safety.Http;

public sealed class HttpRequestRejectedException(string message) : Exception(message);

/// <summary>
/// The only HTTP client in RSM: GET requests to three RabbitMQ management endpoints (spec §2.3), off by default.
/// No other method or path can be expressed through this API.
/// </summary>
public sealed class RabbitGetClient : IDisposable
{
    public static readonly IReadOnlyList<string> AllowedPaths = ["api/overview", "api/queues", "api/nodes"];

    private readonly HttpClient _http;

    public RabbitGetClient(Uri managementBaseUrl, string? userName, string? password, TimeSpan timeout)
        : this(managementBaseUrl, userName, password, timeout, new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
    {
    }

    internal RabbitGetClient(Uri managementBaseUrl, string? userName, string? password, TimeSpan timeout, HttpMessageHandler handler)
    {
        if (managementBaseUrl.Scheme is not ("http" or "https"))
        {
            throw new HttpRequestRejectedException("RabbitMQ management URL must be http or https");
        }

        var baseUrl = managementBaseUrl.AbsoluteUri.EndsWith('/') ? managementBaseUrl : new Uri(managementBaseUrl.AbsoluteUri + "/");
        _http = new HttpClient(handler, disposeHandler: true) { BaseAddress = baseUrl, Timeout = timeout };
        if (!string.IsNullOrEmpty(userName))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
    }

    /// <summary>GETs one of <see cref="AllowedPaths"/> and returns the JSON body.</summary>
    public async Task<string> GetAsync(string path, CancellationToken cancellationToken)
    {
        var normalised = path.Trim().TrimStart('/');
        if (!AllowedPaths.Contains(normalised, StringComparer.Ordinal))
        {
            throw new HttpRequestRejectedException($"RabbitMQ path '{path}' is not on the GET whitelist");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, normalised);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _http.Dispose();
}
