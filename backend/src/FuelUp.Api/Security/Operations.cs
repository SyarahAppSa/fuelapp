using System.Collections.Concurrent;
using FuelUp.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FuelUp.Api.Security;

/// <summary>
/// Reports unexpected server errors: full details to the server log (with a trace id), and a short notice —
/// exception type, endpoint and trace id only, never request data — to the alert webhook, at most once a minute per error kind.
/// </summary>
public class ErrorReporter(ILogger<ErrorReporter> log, IConfiguration cfg, IHttpClientFactory http)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _last = new();
    public static long Count;

    public async Task ReportAsync(Exception e, HttpContext ctx)
    {
        Interlocked.Increment(ref Count);
        var trace = ctx.TraceIdentifier;
        var where = $"{ctx.Request.Method} {ctx.Request.Path}";
        log.LogError(e, "UNHANDLED_ERROR trace={Trace} at {Where}", trace, where);
        var key = e.GetType().FullName + "|" + ctx.GetEndpoint()?.DisplayName;
        var now = DateTimeOffset.UtcNow;
        if (_last.TryGetValue(key, out var t) && now - t < TimeSpan.FromMinutes(1)) return;
        _last[key] = now;
        if (cfg["Alerts:WebhookUrl"] is not { Length: > 0 } url) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await http.CreateClient("alerts").PostAsJsonAsync(url, new
            {
                text = $"[FuelUp {cfg["App:Environment"] ?? "production"}] خطأ غير متوقع في الخادم: {e.GetType().Name} عند {where} (trace {trace})",
                kind = "server.error", type = e.GetType().Name, where, trace, at = now,
            }, cts.Token);
        }
        catch (Exception ex) { log.LogError("Error webhook failed: {Error}", ex.GetType().Name); }
    }
}

/// <summary>Readiness: the database answers with the restricted role, migrations are applied, and the files volume is writable.</summary>
public static class Readiness
{
    public static async Task<IResult> CheckAsync(FuelUpDb db, IConfiguration cfg)
    {
        var checks = new Dictionary<string, string>();
        var ok = true;
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1");
            var pending = (await db.Database.GetPendingMigrationsAsync()).Count();
            checks["database"] = "ok";
            checks["migrations"] = pending == 0 ? "ok" : $"{pending} pending";
            ok &= pending == 0;
        }
        catch (Exception e) { checks["database"] = "unreachable: " + e.GetType().Name; ok = false; }
        try
        {
            var root = cfg["Files:Root"] ?? Path.Combine(AppContext.BaseDirectory, "files");
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, ".probe");
            await File.WriteAllTextAsync(probe, "ok"); File.Delete(probe);
            checks["files"] = "ok";
        }
        catch (Exception e) { checks["files"] = "not writable: " + e.GetType().Name; ok = false; }
        checks["unhandledErrorsSinceStart"] = ErrorReporter.Count.ToString();
        var body = new { status = ok ? "ready" : "not-ready", version = cfg["App:Version"] ?? "dev", checks };
        return ok ? Results.Ok(body) : Results.Json(body, statusCode: 503);
    }
}
