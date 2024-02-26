using System.Net;
using System.Text.Json;

namespace MatrixMediaGate.Services;

public class AuthValidator(ILogger<AuthValidator> logger, ProxyConfiguration cfg, IHttpContextAccessor ctx) {
    private static Dictionary<string, DateTime> _authCache = new();

    public async Task UpdateAuth() {
        if (ctx.HttpContext?.Connection.RemoteIpAddress is null) return;
        var remote = ctx.HttpContext.Connection.RemoteIpAddress.ToString();
        
        if (_authCache.TryGetValue(remote, out var value)) {
            if (value > DateTime.Now.AddSeconds(30)) {
                return;
            }

            _authCache.Remove(remote);
        }

        string? token = getToken();
        if (token is null) return;
        
        using var hc = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{cfg.Upstream}/_matrix/client/v3/account/whoami?access_token={token}");
        var response = await hc.SendAsync(req);

        if (response.Content.Headers.ContentType?.MediaType != "application/json") return;
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        if (json.RootElement.TryGetProperty("user_id", out var userId)) {
            _authCache[remote] = DateTime.Now.AddMinutes(5);
            logger.LogInformation("Authenticated {userId} on {remote}, expiring at {time}", userId, remote, _authCache[remote]);
        }
    }

    public bool ValidateAuth() {
        if (ctx.HttpContext?.Connection.RemoteIpAddress is null) return false;
        var remote = ctx.HttpContext.Connection.RemoteIpAddress.ToString();
        
        if (_authCache.ContainsKey(remote)) {
            if (_authCache[remote] > DateTime.Now) {
                return true;
            }

            _authCache.Remove(remote);
        }

        return false;
    }

    private string? getToken() {
        if (ctx.HttpContext is null) return null;
        if (ctx.HttpContext.Request.Headers.TryGetValue("Authorization", out var header)) {
            return header.ToString().Split(' ', 2)[1];
        }
        else if (ctx.HttpContext.Request.Query.ContainsKey("access_token")) {
            return ctx.HttpContext.Request.Query["access_token"]!;
        }
        else {
            return null;
        }
    }
}