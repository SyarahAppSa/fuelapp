using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FuelUp.Api.Security;

public enum Severity { Low, Medium, High }

/// <summary>
/// Records security-relevant events in the append-only audit table and raises alerts for suspicious activity.
/// Writes go through a separate database scope, so an event is kept even when the request itself fails,
/// and a failed request's half-done changes are never saved by accident.
/// Details never contain passwords, codes, tokens or secrets.
/// </summary>
public class SecurityEvents(IServiceScopeFactory scopes, IHttpContextAccessor http, ILogger<SecurityEvents> log, IConfiguration cfg, IHttpClientFactory httpClients)
{
    public async Task RecordAsync(string kind, Severity severity, string detail, Guid? companyId = null, Guid? userId = null, CancellationToken ct = default)
    {
        var ip = http.HttpContext is { } c ? ClientInfo.Ip(c) : null;
        using var scope = scopes.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<TenantContext>();
        if (companyId is { } cid) tenant.Set(cid);
        var db = scope.ServiceProvider.GetRequiredService<FuelUpDb>();
        void Add(string k, Severity s, string d) =>
            db.Audit.Add(new AuditEntry { At = DateTimeOffset.UtcNow, CompanyId = companyId, UserId = userId, Entity = "security", EntityId = userId ?? Guid.Empty, Action = k, Detail = d, Severity = s.ToString(), Ip = ip });
        Add(kind, severity, detail);

        // Bursts turn individually harmless events into one alert.
        var since = DateTimeOffset.UtcNow.AddMinutes(-10);
        string? burst = null;
        if (kind == "access.denied" && userId is { } u && await db.Audit.CountAsync(a => a.Entity == "security" && a.Action == kind && a.UserId == u && a.At > since, ct) >= 9)
            burst = "access.denied.burst";
        if (kind == "login.failed" && companyId is null && ip != null && await db.Audit.CountAsync(a => a.Entity == "security" && a.Action == kind && a.Ip == ip && a.At > since, ct) >= 19)
            burst = "credential.stuffing";
        if (burst != null)
        {
            (kind, severity, detail) = (burst, Severity.High, burst == "credential.stuffing"
                ? "محاولات دخول كثيرة بأسماء مستخدمين غير موجودة من العنوان نفسه."
                : "محاولات متكررة للوصول إلى أقسام غير مصرح بها خلال 10 دقائق.");
            Add(kind, severity, detail);
        }

        // One alert per kind and user every 10 minutes, so an attack does not flood the administrators.
        if (severity == Severity.High && !await db.Audit.AnyAsync(a => a.Entity == "security" && a.Action == kind && a.UserId == userId && a.Severity == "High" && a.At > since, ct))
            await AlertAsync(db, kind, detail, companyId, ip, ct);
        await db.SaveChangesAsync(ct);
        log.Log(severity == Severity.High ? LogLevel.Warning : LogLevel.Information, "SECURITY {Kind} severity={Severity} company={Company} user={User} ip={Ip}", kind, severity, companyId, userId, ip);
    }

    private async Task AlertAsync(FuelUpDb db, string kind, string detail, Guid? companyId, string? ip, CancellationToken ct)
    {
        log.LogWarning("SECURITY_ALERT {Kind} company={Company} ip={Ip}: {Detail}", kind, companyId, ip, detail);
        if (companyId is { } cid)
        {
            // In-app notice to everyone who manages users in that company.
            var admins = await db.Users.Where(x => x.CompanyId == cid && x.IsActive && x.Permissions.Any(p => p.Section == Section.Users && p.Level == PermissionLevel.Edit)).Select(x => x.Id).ToListAsync(ct);
            foreach (var a in admins) db.Notifications.Add(new Notification { UserId = a, Text = "تنبيه أمني: " + detail, Ref = kind, At = DateTimeOffset.UtcNow });
        }
        // Optional external channel (Slack / Teams / SIEM webhook). Configured outside the code: Alerts:WebhookUrl.
        if (cfg["Alerts:WebhookUrl"] is { Length: > 0 } url)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(TimeSpan.FromSeconds(5));
                await httpClients.CreateClient("alerts").PostAsJsonAsync(url, new { text = $"[FuelUp security] {kind}: {detail}", kind, companyId, at = DateTimeOffset.UtcNow }, cts.Token);
            }
            catch (Exception e) { log.LogError("Alert webhook failed: {Error}", e.GetType().Name); }
        }
    }
}
