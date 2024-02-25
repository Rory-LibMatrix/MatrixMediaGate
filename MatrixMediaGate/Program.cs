using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MatrixMediaGate;
using MatrixMediaGate.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ProxyConfiguration>();
builder.Services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
builder.Services.AddScoped<AuthValidator>();

var app = builder.Build();

async Task Proxy(ProxyConfiguration cfg, AuthValidator auth, HttpContext ctx, ILogger<Program> logger) {
    if (ctx is null) return;
    var path = ctx.Request.Path.Value;
    if(path is null) return;
    if (path.StartsWith('/'))
        path = path[1..];
    path += ctx.Request.QueryString.Value;
    
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
    auth.UpdateAuth();
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed

    using var handler = new HttpClientHandler() { AutomaticDecompression = DecompressionMethods.None };

    using var hc = new HttpClient(handler) { BaseAddress = new Uri(cfg.Upstream) };
    var method = new HttpMethod(ctx.Request.Method);
    using var req = new HttpRequestMessage(method, path);
    foreach (var header in ctx.Request.Headers) {
        if (header.Key != "Accept-Encoding" && header.Key != "Content-Type" && header.Key != "Content-Length") req.Headers.Add(header.Key, header.Value.ToArray());
    }

    if (ctx.Request.ContentLength > 0) {
        req.Content = new StreamContent(ctx.Request.Body);
        if (ctx.Request.ContentType != null) req.Content.Headers.ContentType = new MediaTypeHeaderValue(ctx.Request.ContentType);
        if (ctx.Request.ContentLength != null) req.Content.Headers.ContentLength = ctx.Request.ContentLength;
    }

    logger.LogInformation($"Proxying {method} {path} to {hc.BaseAddress}{path}");

    using var response = await hc.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    ctx.Response.Headers.Clear();
    foreach (var header in response.Headers) {
        if (header.Key != "Transfer-Encoding") ctx.Response.Headers[header.Key] = header.Value.ToArray();
    }

    ctx.Response.StatusCode = (int)response.StatusCode;
    ctx.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
    await ctx.Response.StartAsync();
    await using var content = await response.Content.ReadAsStreamAsync();
    await content.CopyToAsync(ctx.Response.Body);
    await ctx.Response.Body.FlushAsync();
    await ctx.Response.CompleteAsync();
}

async Task ProxyMedia(string serverName, ProxyConfiguration cfg, AuthValidator auth, HttpContext ctx, ILogger<Program> logger) {
    if (cfg.TrustedServers.Contains(serverName) || auth.ValidateAuth()) {
        await Proxy(cfg, auth, ctx, logger);
    }
    else {
        ctx.Response.StatusCode = 403;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.StartAsync();
        var json = JsonSerializer.Serialize(new { errcode = "M_FORBIDDEN", error = "Unauthenticated access to remote media has been disabled on this server." });
        await ctx.Response.WriteAsync(json);
        await ctx.Response.Body.FlushAsync();
        await ctx.Response.CompleteAsync();
    }
}

app.Map("{*_}", Proxy);

foreach (var route in (string[]) [
             "/_matrix/media/{version}/download/{serverName}/{mediaId}",
             "/_matrix/media/{version}/download/{serverName}/{mediaId}/{fileName}",
             "/_matrix/media/{version}/thumbnail/{serverName}/{mediaId}",
         ])
    app.Map(route, ProxyMedia);

app.Run();