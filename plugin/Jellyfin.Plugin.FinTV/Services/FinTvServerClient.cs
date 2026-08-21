using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.FinTV.Services;

public sealed class FinTvServerClient
{
    private readonly IHttpClientFactory _http;

    public FinTvServerClient(IHttpClientFactory http)
    {
        _http = http;
    }

    public async Task PostJsonAsync(string path, object body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task PatchJsonAsync(string path, object body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Patch, path, body, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<T?> GetJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        var baseUrl = (config?.ServerUrl ?? "http://127.0.0.1:8097").TrimEnd('/');
        var client = _http.CreateClient("fintv");
        using var request = new HttpRequestMessage(method, baseUrl + path);
        if (!string.IsNullOrWhiteSpace(config?.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", config.ApiKey);
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
}
