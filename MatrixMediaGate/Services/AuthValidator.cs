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

    public async Task<bool> UpdateAuth(HttpContext ctx) {
        if (ctx.Connection.RemoteIpAddress is null) return false;
        var remote = GetRemoteAddress(ctx);
        if (string.IsNullOrWhiteSpace(remote)) return false;

        if (_authCache.TryGetValue(remote, out var value)) {
            if (value > DateTime.Now.AddSeconds(30)) {
                return true;
            }

            _authCache.Remove(remote);
        }

        string? token = GetToken(ctx);
        if (string.IsNullOrWhiteSpace(token)) return false;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{cfg.Upstream}/_matrix/client/v3/account/whoami?access_token={token}");
        var response = await _hc.SendAsync(req);

        try {
            var content = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(content);
            if (json.RootElement.TryGetProperty("user_id", out var userId)) {
                _authCache[remote] = DateTime.Now.AddMinutes(5);
                logger.LogInformation("Authenticated {userId} on {remote}, expiring at {time}", userId, remote, _authCache[remote]);
                return true;
            }
        }
        catch (Exception e) {
            logger.LogError(e, "Failed to authenticate {remote}", remote);
            return false;
        }

        return false;
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

    public string? GetToken(HttpContext ctx) {
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

    private string? GetRemoteAddress(HttpContext ctx) {
        if (ctx.Request.Headers.TryGetValue("X-Real-IP", out var xRealIp)) {
            return xRealIp.ToString();
        }

        if (ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var xForwardedFor)) {
            return xForwardedFor.ToString();
        }

        return ctx.Connection.RemoteIpAddress?.ToString();
    }
}