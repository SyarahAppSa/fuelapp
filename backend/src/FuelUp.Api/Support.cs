using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using FuelUp.Api.Security;
using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FuelUp.Api;

/// <summary>
/// Builds the acting user on every request from the database, not from the token alone:
/// the session (sid) must still be open, the user and the company active, and permissions are read fresh.
/// It also binds the request to the user's company (EF filters + PostgreSQL row-level security).
/// </summary>
public class ActorAccessor(IHttpContextAccessor http, FuelUpDb db, TenantContext tenant)
{
    private Actor? _actor;
    public async Task<Actor> GetAsync()
    {
        if (_actor != null) return _actor;
        var p = http.HttpContext?.User ?? throw new UnauthorizedAccessException();
        if (!Guid.TryParse(p.FindFirstValue(JwtRegisteredClaimNames.Sub), out var uid) || !Guid.TryParse(p.FindFirstValue("sid"), out var sid))
            throw new UnauthorizedAccessException();
        var now = DateTimeOffset.UtcNow;
        var session = await db.Sessions.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sid && s.UserId == uid && s.RevokedAt == null && s.ExpiresAt > now) ?? throw new UnauthorizedAccessException();
        var u = await db.Users.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Id == uid && x.IsActive) ?? throw new UnauthorizedAccessException();
        if (u.CompanyId != session.CompanyId || !await db.Companies.IgnoreQueryFilters().AnyAsync(c => c.Id == u.CompanyId && c.IsActive)) throw new UnauthorizedAccessException();
        tenant.Set(u.CompanyId);
        var levels = await db.UserPermissions.Where(x => x.UserId == uid).ToDictionaryAsync(x => x.Section, x => x.Level);
        // A management account that skipped two-step verification gets no management rights.
        if (!session.MfaVerified && MfaService.IsRequired(u.Role, levels)) throw new UnauthorizedAccessException();
        return _actor = new Actor(u.Id, u.DriverId, levels, u.CompanyId, u.Role, sid);
    }
}

/// <summary>Files live outside the database and outside any web root, under random names chosen by the server.</summary>
public class FileStore(IConfiguration cfg)
{
    private readonly string _root = Path.GetFullPath(cfg["Files:Root"] ?? Path.Combine(AppContext.BaseDirectory, "files"));

    public async Task<(string Path, string Sha256)> SaveAsync(byte[] content, string ext, CancellationToken ct)
    {
        var sha = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var rel = Path.Combine(DateTime.UtcNow.ToString("yyyy/MM"), $"{Guid.NewGuid():N}{ext}");
        var full = FullPath(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, content, ct);
        return (rel, sha);
    }

    /// <summary>Resolves a stored path and refuses anything that would escape the files folder.</summary>
    public string FullPath(string rel)
    {
        var full = Path.GetFullPath(Path.Combine(_root, rel));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new UnauthorizedAccessException();
        return full;
    }
}

/// <summary>Maps rule violations to clear HTTP answers with the Arabic message the user sees. Internal details are never returned.</summary>
public class ErrorFilter(SecurityEvents events) : IAsyncExceptionFilter
{
    public async Task OnExceptionAsync(ExceptionContext ctx)
    {
        var tenant = ctx.HttpContext.RequestServices.GetRequiredService<TenantContext>();
        Guid? uid = Guid.TryParse(ctx.HttpContext.User.FindFirstValue(JwtRegisteredClaimNames.Sub), out var u) ? u : null;
        (int code, string msg) = ctx.Exception switch
        {
            DomainException e => (400, e.Message),
            ForbiddenException e => (403, e.Message),
            NotFoundException e => (404, e.Message),
            UnauthorizedAccessException => (401, "سجّل الدخول من جديد."),
            TenantViolationException => (403, "ليست لديك صلاحية لهذا الإجراء."),
            DbUpdateException { InnerException: PostgresException { SqlState: "42501" } } => (403, "ليست لديك صلاحية لهذا الإجراء."),
            DbUpdateException { InnerException: PostgresException { SqlState: "23505" } } => (409, "القيمة مسجلة مسبقًا (رقم مكرر)."),
            _ => (0, ""),
        };
        if (code == 0) return;   // unexpected: the global handler answers with a generic 500 and logs the details server-side
        if (code == 403)
        {
            var path = $"{ctx.HttpContext.Request.Method} {ctx.HttpContext.Request.Path}";
            var kind = ctx.Exception is ForbiddenException ? "access.denied" : "tenant.violation";
            await events.RecordAsync(kind, kind == "access.denied" ? Severity.Medium : Severity.High, $"رُفض طلب غير مصرح به: {path}", tenant.CompanyId, uid);
        }
        ctx.Result = new ObjectResult(new { error = msg }) { StatusCode = code };
        ctx.ExceptionHandled = true;
    }
}

public static class Ksa
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");
    public static DateTime Of(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, Tz).DateTime;
    public static string Text(DateTimeOffset? t) => t is { } x ? Of(x).ToString("yyyy-MM-dd HH:mm") : "";
    public static DateOnly Day(DateTimeOffset t) => DateOnly.FromDateTime(Of(t));
}
