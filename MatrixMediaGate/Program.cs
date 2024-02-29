using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using MatrixMediaGate;
using MatrixMediaGate.Services;
using Microsoft.AspNetCore.Http.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSystemd();
builder.Services.AddSingleton<ProxyConfiguration>();
builder.Services.AddSingleton<AuthValidator>();
builder.Services.AddSingleton<HttpClient>(services => {
    var cfg = services.GetRequiredService<ProxyConfiguration>();
    return new HttpClient() {
        BaseAddress = new Uri(cfg.Upstream),
        MaxResponseContentBufferSize = 1 * 1024 * 1024 // 1MB
    };
});

var app = builder.Build();

var jsonOptions = new JsonSerializerOptions {
    WriteIndented = true
};

app.Map("{*_}", ProxyMaybeAuth);
app.Map("/_matrix/federation/{*_}", Proxy); // Don't bother with auth for federation

foreach (var route in (string[]) [ // Require recent auth for these routes
             "/_matrix/media/{version}/download/{serverName}/{mediaId}",
             "/_matrix/media/{version}/download/{serverName}/{mediaId}/{fileName}",
             "/_matrix/media/{version}/thumbnail/{serverName}/{mediaId}",
         ])
    app.Map(route, ProxyMedia);

app.Run();

// Proxy a request
async Task Proxy(HttpClient hc, ProxyConfiguration cfg, HttpContext ctx, ILogger<Program> logger) {
    HttpRequestMessage? upstreamRequest = null;
    HttpResponseMessage? upstreamResponse = null;
    Exception? exception = null;
    try {
        var path = ctx.Request.GetEncodedPathAndQuery();
        if (path.StartsWith('/'))
            path = path[1..];

        var method = new HttpMethod(ctx.Request.Method);
        upstreamRequest = new HttpRequestMessage(method, path);
        hc.DefaultRequestHeaders.Clear();
        upstreamRequest.Headers.Clear();
        foreach (var header in ctx.Request.Headers) {
            if (header.Key != "Accept-Encoding" && header.Key != "Content-Type" && header.Key != "Content-Length") {
                // req.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                upstreamRequest.Headers.Remove(header.Key);
                upstreamRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        upstreamRequest.Headers.Host = cfg.Host;

        if (ctx.Request.ContentLength > 0) {
            upstreamRequest.Content = new StreamContent(ctx.Request.Body);
            if (ctx.Request.ContentType != null) upstreamRequest.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ctx.Request.ContentType);

            if (ctx.Request.ContentLength != null) upstreamRequest.Content.Headers.ContentLength = ctx.Request.ContentLength;
        }

        logger.LogInformation("Proxying {method} {path} to {target}", method, path, hc.BaseAddress + path);

        upstreamResponse = await hc.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead);
        ctx.Response.Headers.Clear();
        foreach (var header in upstreamResponse.Headers) {
            if (header.Key != "Transfer-Encoding")
                ctx.Response.Headers[header.Key] = header.Value.ToArray();
        }

        ctx.Response.StatusCode = (int)upstreamResponse.StatusCode;
        ctx.Response.ContentType = upstreamResponse.Content.Headers.ContentType?.ToString() ?? "application/json";
        if (upstreamResponse.Content.Headers.ContentLength != null) ctx.Response.ContentLength = upstreamResponse.Content.Headers.ContentLength;
        await ctx.Response.StartAsync();
        await using var content = await upstreamResponse.Content.ReadAsStreamAsync();
        await content.CopyToAsync(ctx.Response.Body);
    }
    catch (HttpRequestException e) {
        exception = e;
        logger.LogError(e, "Failed to proxy request");
        ctx.Response.StatusCode = 502;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.StartAsync();
        await JsonSerializer.SerializeAsync(ctx.Response.Body, new { errcode = "M_UNAVAILABLE", error = "Failed to proxy request" });
    }
    finally {
        await ctx.Response.Body.FlushAsync();
        await ctx.Response.CompleteAsync();
        if (ctx.Response.StatusCode >= 400) {
            await ProxyDump(cfg, ctx, upstreamRequest, upstreamResponse, exception);
        }

        upstreamRequest?.Dispose();
        upstreamResponse?.Dispose();
    }
}

// We attempt to update auth, but we don't require it
async Task ProxyMaybeAuth(HttpClient hc, ProxyConfiguration cfg, AuthValidator auth, HttpContext ctx, ILogger<Program> logger) {
    await auth.UpdateAuth(ctx);
    await Proxy(hc, cfg, ctx, logger);
}

// We know this is a media path, we require recent auth here to prevent abuse
async Task ProxyMedia(string serverName, ProxyConfiguration cfg, HttpClient hc, AuthValidator auth, HttpContext ctx, ILogger<Program> logger) {
    // Some clients may send Authorization header, so we handle this last...
    if (cfg.TrustedServers.Contains(serverName) || auth.ValidateAuth(ctx) || await auth.UpdateAuth(ctx)) {
        await Proxy(hc, cfg, ctx, logger);
    }
    else {
        ctx.Response.StatusCode = 403;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.StartAsync();
        await JsonSerializer.SerializeAsync(ctx.Response.Body, new { errcode = "M_FORBIDDEN", error = "Unauthenticated access to remote media has been disabled on this server." });
        await ctx.Response.Body.FlushAsync();
        await ctx.Response.CompleteAsync();
    }
}


// We dump failed requests to disk
async Task ProxyDump(ProxyConfiguration cfg, HttpContext ctx, HttpRequestMessage? req, HttpResponseMessage? resp, Exception? e) {
    if (ctx.Response.StatusCode >= 400 && cfg.DumpFailedRequests) {
        var dir = Path.Combine(cfg.DumpPath, "failed_requests");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{(int)resp?.StatusCode}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{ctx.Request.GetEncodedPathAndQuery().Replace('/', '_')}.json");
        await using var file = File.Create(path);
        
        //collect data
        JsonObject? requestJsonContent = null;
        try {
            requestJsonContent = await ctx.Request.ReadFromJsonAsync<JsonObject>();
        }
        catch (Exception) {
            // ignored, we might not have a request body...
        }
        
        JsonObject? responseJsonContent = null;
        string? responseRawContent = null;
        try {
            responseJsonContent = await resp.Content.ReadFromJsonAsync<JsonObject>();
        }
        catch (Exception) {
            try {
                responseRawContent = await resp.Content.ReadAsStringAsync();
            }
            catch (Exception) {
                // ignored, we might not have a response body...
            }
        }
        
        await JsonSerializer.SerializeAsync(file, new {
            Self = new {
                Request = new {
                    ctx.Request.Method,
                    Url = ctx.Request.GetEncodedUrl(),
                    ctx.Request.Headers,
                    JsonContent = requestJsonContent
                },
                Response = new {
                    ctx.Response.StatusCode,
                    ctx.Response.Headers,
                    ctx.Response.ContentType,
                    ctx.Response.ContentLength
                }
            },
            Upstream = new {
                Request = new {
                    req?.Method,
                    Url = req?.RequestUri,
                    req?.Headers
                },
                Response = new {
                    resp.StatusCode,
                    resp.Headers,
                    resp.Content.Headers.ContentType,
                    resp.Content.Headers.ContentLength,
                    JsonContent = responseJsonContent,
                    TextContent = responseRawContent
                }
            },
            Exception = new {
                Type = e?.GetType().ToString(),
                Message = e?.Message.ReplaceLineEndings().Split(Environment.NewLine),
                StackTrace = e?.StackTrace?.ReplaceLineEndings().Split(Environment.NewLine)
            }
            // ReSharper disable once AccessToModifiedClosure
        }, jsonOptions);
    }
}