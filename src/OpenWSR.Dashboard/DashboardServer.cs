using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace OpenWSR.Dashboard;

/// <summary>
/// Serves the LAN dashboard: one page, its assets, and the current <see cref="DashboardSnapshot"/>
/// as JSON.
///
/// It is on the watching side of the tray line — it draws nothing itself and polls nothing, it
/// only hands out what the warnings and storm polls already produced — so it keeps running
/// while the window is hidden, which is the whole point of it.
///
/// Open on the network by design, like the LAN services a Home Assistant webpage card embeds:
/// no login, no token, and framing allowed from anywhere. The page carries the saved places,
/// so the shell leaves it off until someone switches it on.
/// </summary>
public sealed class DashboardServer : IAsyncDisposable
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<DashboardServer>();

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyDictionary<string, byte[]> Assets = LoadAssets();

    private Published _published = Serialise(DashboardSnapshot.Empty);
    private WebApplication? _app;

    /// <summary>The port actually bound, which differs from the one asked for when that was 0.</summary>
    public int? Port { get; private set; }

    public bool IsRunning => _app is not null;

    /// <summary>
    /// Replace what the page is served. Serialised here, once, rather than per request: a wall
    /// of dashboards polling every thirty seconds then costs a reference read and an ETag
    /// compare, and every request sees a complete snapshot or the previous one, never a mix.
    /// </summary>
    public void Publish(DashboardSnapshot snapshot) => Volatile.Write(ref _published, Serialise(snapshot));

    /// <summary>The snapshot currently being served.</summary>
    public DashboardSnapshot Current => Volatile.Read(ref _published).Snapshot;

    /// <summary>
    /// Listen on every interface at <paramref name="port"/>. Throws <see cref="IOException"/>
    /// when the port is taken — the caller turns that into something a person can act on.
    /// </summary>
    public async Task StartAsync(int port, CancellationToken ct = default)
    {
        if (_app is not null) throw new InvalidOperationException("The dashboard server is already running.");

        // Empty rather than the default builder: no appsettings.json read from whatever the
        // working directory happens to be, no console logger, no environment-variable config.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseKestrelCore().ConfigureKestrel(k =>
        {
            k.ListenAnyIP(port);
            k.AddServerHeader = false;
        });
        builder.Services.AddRoutingCore();
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.Use(AddCommonHeaders);
        MapRoutes(app);

        try
        {
            await app.StartAsync(ct);
        }
        catch (Exception ex)
        {
            await app.DisposeAsync();
            throw new IOException(DescribeBindFailure(port, ex), ex);
        }

        _app = app;
        Port = BoundPort(app) ?? port;
        Log.Information("Dashboard server listening on port {Port}", Port);
    }

    public async Task StopAsync()
    {
        var app = _app;
        if (app is null) return;
        _app = null;
        Port = null;
        try
        {
            // A dashboard poll in flight is a few kilobytes; it is not worth holding an exit for.
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await app.StopAsync(grace.Token);
        }
        finally
        {
            await app.DisposeAsync();
            Log.Information("Dashboard server stopped");
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private void MapRoutes(WebApplication app)
    {
        app.MapGet("/", () => Asset("index.html"));
        app.MapGet("/health", () => Results.Json(new
        {
            ok = true,
            updatedUtc = Volatile.Read(ref _published).Snapshot.UpdatedUtc,
        }, Json));

        app.MapGet("/api/state", (HttpContext context) =>
        {
            var published = Volatile.Read(ref _published);
            context.Response.Headers.ETag = published.ETag;
            if (context.Request.Headers.IfNoneMatch.Contains(published.ETag))
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Bytes(published.Body, "application/json");
        });
        app.MapGet("/api/threats", () => Results.Json(Current.Threats, Json));
        app.MapGet("/api/alerts", () => Results.Json(Current.Alerts, Json));
        app.MapGet("/api/storms", () => Results.Json(Current.Storms, Json));

        app.MapGet("/{**path}", (string path) => Asset(path));
    }

    private static IResult Asset(string path)
    {
        if (!Assets.TryGetValue(path, out var bytes)) return Results.NotFound();
        return Results.Bytes(bytes, ContentTypeFor(path));
    }

    /// <summary>
    /// Framing is allowed from anywhere, because embedding is what this is for: a Home
    /// Assistant card is an iframe on whatever origin HA is served from, and nobody should
    /// have to tell OpenWSR that origin. CORS is open on the JSON for the same reason — a
    /// dashboard elsewhere on the network reading the threat list is the intended use.
    /// </summary>
    private static Task AddCommonHeaders(HttpContext context, Func<Task> next)
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = "frame-ancestors *";
        headers.XContentTypeOptions = "nosniff";
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            headers.CacheControl = "no-cache";
            headers.AccessControlAllowOrigin = "*";
        }
        return next();
    }

    private static Published Serialise(DashboardSnapshot snapshot)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json);
        var etag = $"\"{Convert.ToHexString(SHA256.HashData(body), 0, 8)}\"";
        return new Published(snapshot, body, etag);
    }

    private static int? BoundPort(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        var first = addresses?.FirstOrDefault();
        return first is not null && Uri.TryCreate(first.Replace("[::]", "localhost"), UriKind.Absolute, out var uri)
            ? uri.Port
            : null;
    }

    private static string DescribeBindFailure(int port, Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse })
                return $"The dashboard could not start: port {port} is already in use by another program. "
                     + "Pick a different port in Settings.";
            if (e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AccessDenied })
                return $"The dashboard could not start: Windows refused port {port}. "
                     + "Pick a port above 1024 in Settings.";
        }
        return $"The dashboard could not start on port {port}: {ex.Message}";
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path) switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".png" => "image/png",
        ".svg" => "image/svg+xml",
        _ => "text/plain; charset=utf-8",
    };

    private static Dictionary<string, byte[]> LoadAssets()
    {
        const string Prefix = "wwwroot/";
        var assembly = typeof(DashboardServer).Assembly;
        var assets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            // RecursiveDir keeps the build machine's separator, so a Windows build names the
            // vendor files "wwwroot/vendor\leaflet.js".
            var normalised = name.Replace('\\', '/');
            if (!normalised.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            assets[normalised[Prefix.Length..]] = buffer.ToArray();
        }
        return assets;
    }

    private sealed record Published(DashboardSnapshot Snapshot, byte[] Body, string ETag);
}
