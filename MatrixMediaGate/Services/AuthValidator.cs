using System.Net;
using System.Text.Json;

namespace MatrixMediaGate.Services;

public class AuthValidator(ILogger<AuthValidator> logger, ProxyConfiguration cfg) {
    private readonly Dictionary<string, DateTime> _authCache = new();
    private readonly HttpClient _hc = new() {
        BaseAddress = new Uri(cfg.Upstream),
        DefaultRequestHeaders = {
            Host = cfg.Host
        }
    };

    public async Task UpdateAuth(HttpContext ctx) {
        if (ctx.Connection.RemoteIpAddress is null) return;
        var remote = ctx.Connection.RemoteIpAddress.ToString();
        
        if (_authCache.TryGetValue(remote, out var value)) {
            if (value > DateTime.Now.AddSeconds(30)) {
                return;
            }

            _authCache.Remove(remote);
        }

        string? token = getToken(ctx);
        if (token is null) return;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{cfg.Upstream}/_matrix/client/v3/account/whoami?access_token={token}");
        var response = await _hc.SendAsync(req);

        if (response.Content.Headers.ContentType?.MediaType != "application/json") return;
        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);
        if (json.RootElement.TryGetProperty("user_id", out var userId)) {
            _authCache[remote] = DateTime.Now.AddMinutes(5);
            logger.LogInformation("Authenticated {userId} on {remote}, expiring at {time}", userId, remote, _authCache[remote]);
        }
    }

    public bool ValidateAuth(HttpContext ctx) {
        if (ctx.Connection.RemoteIpAddress is null) return false;
        var remote = ctx.Connection.RemoteIpAddress.ToString();
        
        if (_authCache.ContainsKey(remote)) {
            if (_authCache[remote] > DateTime.Now) {
                return true;
            }

            _authCache.Remove(remote);
        }

        return false;
    }

    private string? getToken(HttpContext ctx) {
        if (ctx.Request.Headers.TryGetValue("Authorization", out var header)) {
            return header.ToString().Split(' ', 2)[1];
        }
        else if (ctx.Request.Query.ContainsKey("access_token")) {
            return ctx.Request.Query["access_token"]!;
        }
        else {
            return null;
        }
    }
}