using System.ComponentModel.DataAnnotations;
using FuelUp.Api.Security;
using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace FuelUp.Api.Controllers;

public record LoginRequest([Required, StringLength(64)] string Username, [Required, StringLength(128)] string Password, [RegularExpression("^(web|mobile)$")] string? Client);
public record MfaRequest([Required, StringLength(2000)] string MfaToken, [Required, StringLength(20)] string Code, [RegularExpression("^(web|mobile)$")] string? Client);
public record RefreshRequest([StringLength(200)] string? RefreshToken);
public record ChangePasswordRequest([Required, StringLength(128)] string CurrentPassword, [Required, StringLength(128)] string NewPassword);
public record CodeRequest([Required, StringLength(20)] string Code);
public record DisableMfaRequest([Required, StringLength(128)] string Password, [Required, StringLength(20)] string Code);
public record AdminPasswordRequest([Required, StringLength(128)] string NewPassword);

/// <summary>
/// Sign-in. Protections: bcrypt, generic error messages, constant-time path for unknown users, per-account lockout,
/// per-IP rate limit, mandatory two-step verification for management accounts, single-use rotating refresh tokens.
/// Web clients get the refresh token only as an HttpOnly + Secure + SameSite=Strict cookie (unreachable from JavaScript).
/// </summary>
[ApiController, Route("api/auth"), EnableRateLimiting("auth")]
public class AuthController(FuelUpDb db, TenantContext tenant, TokenService tokens, SessionService sessions, MfaService mfa, SecurityEvents events, IConfiguration cfg) : ControllerBase
{
    public const string RefreshCookie = "fu_rt";
    private const string Generic = "بيانات الدخول غير صحيحة، أو أن الحساب موقوف مؤقتًا بسبب محاولات متكررة.";
    private int MaxFailures => cfg.GetValue("Lockout:MaxFailures", 5);
    private TimeSpan LockFor => TimeSpan.FromMinutes(cfg.GetValue("Lockout:Minutes", 15));

    [HttpPost("login"), AllowAnonymous]
    public async Task<IActionResult> Login(LoginRequest r)
    {
        var username = r.Username.Trim().ToLowerInvariant();
        var u = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Username == username);
        if (u is null)
        {
            Passwords.Verify(r.Password, Passwords.DummyHash);   // same cost as a real check: response time does not reveal valid usernames
            await events.RecordAsync("login.failed", Severity.Low, "محاولة دخول باسم مستخدم غير موجود.");
            return Unauthorized(new { error = Generic });
        }
        tenant.Set(u.CompanyId);
        await db.Entry(u).Collection(x => x.Permissions).LoadAsync();
        var now = DateTimeOffset.UtcNow;
        if (u.LockoutUntil > now)
        {
            await events.RecordAsync("login.blocked", Severity.Medium, "محاولة دخول أثناء الإيقاف المؤقت للحساب.", u.CompanyId, u.Id);
            return Unauthorized(new { error = Generic });
        }
        if (!Passwords.Verify(r.Password, u.PasswordHash)) return await FailAsync(u, "login.failed", "كلمة مرور خاطئة.");
        if (!u.IsActive || !await db.Companies.AnyAsync(c => c.Id == u.CompanyId && c.IsActive))
        {
            await events.RecordAsync("login.inactive", Severity.Medium, "محاولة دخول بحساب معطّل.", u.CompanyId, u.Id);
            return Unauthorized(new { error = Generic });
        }
        u.FailedLogins = 0;
        if (Passwords.NeedsRehash(u.PasswordHash)) u.PasswordHash = Passwords.Hash(r.Password);

        if (u.MfaEnabled)
        {
            await db.SaveChangesAsync();
            return Ok(new { mfaRequired = true, mfaToken = tokens.MfaTicket(u, "verify") });
        }
        if (MfaService.IsRequired(u))
        {
            u.MfaPendingProtected ??= mfa.NewProtectedSecret();
            await db.SaveChangesAsync();
            var secret = mfa.Reveal(u.MfaPendingProtected);
            return Ok(new { mfaSetupRequired = true, mfaToken = tokens.MfaTicket(u, "enroll"), secret, otpauthUri = MfaService.OtpAuthUri(secret, u.Username) });
        }
        return await CompleteAsync(u, r.Client, mfaUsed: false);
    }

    /// <summary>Second step: 6-digit code from the authenticator app, or a one-time recovery code.</summary>
    [HttpPost("mfa"), AllowAnonymous]
    public async Task<IActionResult> Mfa(MfaRequest r)
    {
        var (u, error) = await TicketUserAsync(r.MfaToken, "verify");
        if (u is null) return error!;
        if (u.MfaSecretProtected is null) return Unauthorized(new { error = Generic });
        var step = mfa.Verify(u.MfaSecretProtected, r.Code, u.MfaLastStep);
        if (step is { } st) u.MfaLastStep = st;
        else if (MfaService.UseRecoveryCode(u, r.Code))
            await events.RecordAsync("mfa.recovery_used", Severity.Medium, $"دخول برمز استرداد. المتبقي: {u.RecoveryCodeHashes.Count}.", u.CompanyId, u.Id);
        else return await FailAsync(u, "mfa.failed", "رمز تحقق خاطئ أو مستخدم سابقًا.", "رمز التحقق غير صحيح.");
        return await CompleteAsync(u, r.Client, mfaUsed: true);
    }

    /// <summary>First sign-in of a management account: confirm the authenticator app, receive recovery codes.</summary>
    [HttpPost("mfa/enroll"), AllowAnonymous]
    public async Task<IActionResult> Enroll(MfaRequest r)
    {
        var (u, error) = await TicketUserAsync(r.MfaToken, "enroll");
        if (u is null) return error!;
        if (u.MfaEnabled || u.MfaPendingProtected is null) return Unauthorized(new { error = Generic });
        if (mfa.Verify(u.MfaPendingProtected, r.Code, 0) is not { } step) return await FailAsync(u, "mfa.failed", "رمز تفعيل خاطئ.", "رمز التحقق غير صحيح.");
        var (plain, hashes) = MfaService.NewRecoveryCodes();
        (u.MfaSecretProtected, u.MfaPendingProtected, u.MfaEnabled, u.MfaLastStep, u.RecoveryCodeHashes) = (u.MfaPendingProtected, null, true, step, hashes);
        await events.RecordAsync("mfa.enrolled", Severity.Medium, "فُعّل التحقق بخطوتين للحساب.", u.CompanyId, u.Id);
        var result = await CompleteAsync(u, r.Client, mfaUsed: true);
        if (result is OkObjectResult { Value: { } v }) return Ok(new { tokens = v, recoveryCodes = plain });
        return result;
    }

    /// <summary>Mobile sends the refresh token in the body; the web sends the HttpOnly cookie plus a custom header (blocks cross-site requests).</summary>
    [HttpPost("refresh"), AllowAnonymous]
    public async Task<IActionResult> Refresh(RefreshRequest? r)
    {
        var raw = r?.RefreshToken;
        var web = raw is null;
        if (web)
        {
            if (Request.Headers["X-FuelUp-Client"] != "web") return Unauthorized(new { error = "انتهت الجلسة. سجّل الدخول من جديد." });
            raw = Request.Cookies[RefreshCookie];
        }
        var issued = raw is null ? null : await sessions.RotateAsync(raw, HttpContext);
        if (issued is null)
        {
            if (web) Response.Cookies.Delete(RefreshCookie, CookieOptions());
            return Unauthorized(new { error = "انتهت الجلسة. سجّل الدخول من جديد." });
        }
        return Ok(Shape(issued, web));
    }

    [HttpPost("logout"), Authorize, DisableRateLimiting]
    public async Task<IActionResult> Logout([FromServices] ActorAccessor actor)
    {
        var a = await actor.GetAsync();
        await sessions.RevokeAsync(a.UserId, "logout", only: a.SessionId);
        Response.Cookies.Delete(RefreshCookie, CookieOptions());
        return NoContent();
    }

    private async Task<(User? User, IActionResult? Error)> TicketUserAsync(string ticket, string purpose)
    {
        var t = tokens.ReadMfaTicket(ticket);
        if (t is null || t.Value.Purpose != purpose) return (null, Unauthorized(new { error = "انتهت مهلة التحقق. سجّل الدخول من جديد." }));
        var u = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == t.Value.UserId && x.IsActive);
        if (u is null || TokenService.Stamp(u) != t.Value.Stamp) return (null, Unauthorized(new { error = "انتهت مهلة التحقق. سجّل الدخول من جديد." }));
        tenant.Set(u.CompanyId);
        if (u.LockoutUntil > DateTimeOffset.UtcNow) return (null, Unauthorized(new { error = Generic }));
        await db.Entry(u).Collection(x => x.Permissions).LoadAsync();
        return (u, null);
    }

    private async Task<IActionResult> FailAsync(User u, string kind, string detail, string? message = null)
    {
        u.FailedLogins++;
        var locked = u.FailedLogins >= MaxFailures;
        if (locked) { u.LockoutUntil = DateTimeOffset.UtcNow.Add(LockFor); u.FailedLogins = 0; }
        await db.SaveChangesAsync();
        await events.RecordAsync(kind, Severity.Low, detail, u.CompanyId, u.Id);
        if (locked) await events.RecordAsync("account.locked", Severity.High, $"أُوقف الحساب «{u.Username}» مؤقتًا {LockFor.TotalMinutes:0} دقيقة بعد {MaxFailures} محاولات فاشلة.", u.CompanyId, u.Id);
        return Unauthorized(new { error = message ?? Generic });
    }

    private async Task<IActionResult> CompleteAsync(User u, string? client, bool mfaUsed)
    {
        client = client == "web" ? "web" : "mobile";
        var ip = ClientInfo.Ip(HttpContext);
        if (MfaService.IsRequired(u) && u.LastLoginIp is { } last && last != ip)
        {
            db.Notifications.Add(new Notification { UserId = u.Id, Text = $"دخول جديد إلى حسابك من عنوان مختلف ({ip}). إن لم تكن أنت فغيّر كلمة المرور وألغِ الجلسات.", At = DateTimeOffset.UtcNow });
            await events.RecordAsync("login.new_ip", Severity.Medium, "دخول حساب إداري من عنوان IP جديد.", u.CompanyId, u.Id);
        }
        u.FailedLogins = 0; u.LockoutUntil = null; u.LastLoginAt = DateTimeOffset.UtcNow; u.LastLoginIp = ip;
        var issued = await sessions.StartAsync(u, client, mfaUsed, HttpContext);
        await events.RecordAsync("login.success", Severity.Low, client == "web" ? "دخول من لوحة الويب." : "دخول من التطبيق.", u.CompanyId, u.Id);
        return Ok(Shape(issued, client == "web"));
    }

    private object Shape(IssuedTokens t, bool web)
    {
        if (!web) return new { accessToken = t.AccessToken, refreshToken = t.RefreshToken, expiresIn = t.ExpiresIn };
        Response.Cookies.Append(RefreshCookie, t.RefreshToken, CookieOptions());
        return new { accessToken = t.AccessToken, expiresIn = t.ExpiresIn };
    }

    private CookieOptions CookieOptions() => new()
    {
        HttpOnly = true, Secure = cfg.GetValue("Auth:SecureCookies", true), SameSite = SameSiteMode.Strict, Path = "/api/auth", IsEssential = true,
        MaxAge = TimeSpan.FromHours(cfg.GetValue("Sessions:WebHours", 12)),
    };
}

/// <summary>The signed-in user's own security: sessions, password, two-step verification.</summary>
[ApiController, Route("api/me"), Authorize]
public class MySecurityController(FuelUpDb db, ActorAccessor actor, SessionService sessions, MfaService mfa, SecurityEvents events) : ControllerBase
{
    [HttpGet("security")]
    public async Task<IActionResult> Security()
    {
        var a = await actor.GetAsync();
        var u = await db.Users.Include(x => x.Permissions).FirstAsync(x => x.Id == a.UserId);
        return Ok(new { u.MfaEnabled, mfaRequired = MfaService.IsRequired(u), recoveryCodesLeft = u.RecoveryCodeHashes.Count, u.LastLoginAt, u.LastLoginIp, u.PasswordChangedAt });
    }

    [HttpGet("sessions")]
    public async Task<IActionResult> Sessions()
    {
        var a = await actor.GetAsync();
        var now = DateTimeOffset.UtcNow;
        return Ok(await db.Sessions.Where(s => s.UserId == a.UserId && s.RevokedAt == null && s.ExpiresAt > now).OrderByDescending(s => s.LastSeenAt)
            .Select(s => new { s.Id, s.Client, s.Ip, s.UserAgent, s.CreatedAt, s.LastSeenAt, current = s.Id == a.SessionId }).ToListAsync());
    }

    [HttpPost("sessions/{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id)
    {
        var a = await actor.GetAsync();
        if (await sessions.RevokeAsync(a.UserId, "user", only: id) == 0) throw new NotFoundException("الجلسة");
        await events.RecordAsync("session.revoked", Severity.Low, "أنهى المستخدم جلسة على جهاز آخر.", a.CompanyId, a.UserId);
        return NoContent();
    }

    [HttpPost("sessions/revoke-others")]
    public async Task<IActionResult> RevokeOthers()
    {
        var a = await actor.GetAsync();
        var n = await sessions.RevokeAsync(a.UserId, "user", except: a.SessionId);
        await events.RecordAsync("session.revoked", Severity.Low, $"أنهى المستخدم {n} جلسة أخرى.", a.CompanyId, a.UserId);
        return Ok(new { revoked = n });
    }

    [HttpPost("password"), EnableRateLimiting("auth")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest r)
    {
        var a = await actor.GetAsync();
        var u = await db.Users.FirstAsync(x => x.Id == a.UserId);
        if (!Passwords.Verify(r.CurrentPassword, u.PasswordHash))
        {
            await events.RecordAsync("password.change_failed", Severity.Medium, "محاولة تغيير كلمة المرور بكلمة حالية خاطئة.", a.CompanyId, a.UserId);
            throw new DomainException("كلمة المرور الحالية غير صحيحة.");
        }
        if (Passwords.Problem(r.NewPassword, u.Username) is { } p) throw new DomainException(p);
        if (Passwords.Verify(r.NewPassword, u.PasswordHash)) throw new DomainException("اختر كلمة مرور مختلفة عن الحالية.");
        u.PasswordHash = Passwords.Hash(r.NewPassword); u.PasswordChangedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        var n = await sessions.RevokeAsync(a.UserId, "password-changed", except: a.SessionId);
        await events.RecordAsync("password.changed", Severity.Medium, $"غيّر المستخدم كلمة المرور وأُنهيت {n} جلسة أخرى.", a.CompanyId, a.UserId);
        return NoContent();
    }

    /// <summary>Optional two-step verification for accounts where it is not mandatory (e.g. drivers).</summary>
    [HttpPost("mfa/setup")]
    public async Task<IActionResult> Setup()
    {
        var a = await actor.GetAsync();
        var u = await db.Users.FirstAsync(x => x.Id == a.UserId);
        if (u.MfaEnabled) throw new DomainException("التحقق بخطوتين مفعّل مسبقًا.");
        u.MfaPendingProtected = mfa.NewProtectedSecret();
        await db.SaveChangesAsync();
        var secret = mfa.Reveal(u.MfaPendingProtected);
        return Ok(new { secret, otpauthUri = MfaService.OtpAuthUri(secret, u.Username) });
    }

    [HttpPost("mfa/confirm"), EnableRateLimiting("auth")]
    public async Task<IActionResult> Confirm(CodeRequest r)
    {
        var a = await actor.GetAsync();
        var u = await db.Users.FirstAsync(x => x.Id == a.UserId);
        if (u.MfaEnabled || u.MfaPendingProtected is null || mfa.Verify(u.MfaPendingProtected, r.Code, 0) is not { } step) throw new DomainException("رمز التحقق غير صحيح.");
        var (plain, hashes) = MfaService.NewRecoveryCodes();
        (u.MfaSecretProtected, u.MfaPendingProtected, u.MfaEnabled, u.MfaLastStep, u.RecoveryCodeHashes) = (u.MfaPendingProtected, null, true, step, hashes);
        await db.SaveChangesAsync();
        await events.RecordAsync("mfa.enrolled", Severity.Medium, "فعّل المستخدم التحقق بخطوتين.", a.CompanyId, a.UserId);
        return Ok(new { recoveryCodes = plain });
    }

    [HttpPost("mfa/recovery-codes"), EnableRateLimiting("auth")]
    public async Task<IActionResult> NewRecoveryCodes(CodeRequest r)
    {
        var a = await actor.GetAsync();
        var u = await db.Users.FirstAsync(x => x.Id == a.UserId);
        if (!u.MfaEnabled || mfa.Verify(u.MfaSecretProtected!, r.Code, u.MfaLastStep) is not { } step) throw new DomainException("رمز التحقق غير صحيح.");
        var (plain, hashes) = MfaService.NewRecoveryCodes();
        (u.MfaLastStep, u.RecoveryCodeHashes) = (step, hashes);
        await db.SaveChangesAsync();
        await events.RecordAsync("mfa.recovery_regenerated", Severity.Medium, "أنشأ المستخدم رموز استرداد جديدة (القديمة لم تعد صالحة).", a.CompanyId, a.UserId);
        return Ok(new { recoveryCodes = plain });
    }

    [HttpPost("mfa/disable"), EnableRateLimiting("auth")]
    public async Task<IActionResult> Disable(DisableMfaRequest r)
    {
        var a = await actor.GetAsync();
        var u = await db.Users.Include(x => x.Permissions).FirstAsync(x => x.Id == a.UserId);
        if (MfaService.IsRequired(u)) throw new DomainException("التحقق بخطوتين إلزامي لحسابات الإدارة ولا يمكن إيقافه.");
        if (!u.MfaEnabled || !Passwords.Verify(r.Password, u.PasswordHash) || mfa.Verify(u.MfaSecretProtected!, r.Code, u.MfaLastStep) is null)
            throw new DomainException("كلمة المرور أو رمز التحقق غير صحيح.");
        (u.MfaEnabled, u.MfaSecretProtected, u.RecoveryCodeHashes) = (false, null, []);
        await db.SaveChangesAsync();
        await events.RecordAsync("mfa.disabled", Severity.High, "أوقف المستخدم التحقق بخطوتين لحسابه.", a.CompanyId, a.UserId);
        return NoContent();
    }
}

public record UserInput(
    [Required, RegularExpression("^[a-zA-Z0-9._-]{3,32}$", ErrorMessage = "اسم المستخدم: 3–32 من الحروف الإنجليزية والأرقام و . _ -")] string Username,
    [Required, StringLength(80, MinimumLength = 2)] string DisplayName,
    [StringLength(128)] string? Password, Role Role, Guid? DriverId, Dictionary<Section, PermissionLevel>? Levels);

/// <summary>
/// User administration. Rules enforced here, not in the UI: nobody grants more than they hold, only an Admin creates or edits Admins,
/// disabling or re-keying an account ends its sessions at once, and every change is recorded.
/// </summary>
[ApiController, Route("api/users"), Authorize]
public class UsersController(FuelUpDb db, ActorAccessor actor, AuditWriter audit, SessionService sessions, SecurityEvents events) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List()
    {
        (await actor.GetAsync()).Require(Section.Users, PermissionLevel.View);
        return Ok(await db.Users.Include(u => u.Permissions).OrderBy(u => u.Username)
            .Select(u => new { u.Id, u.Username, u.DisplayName, u.Role, u.DriverId, u.IsActive, u.MfaEnabled, u.LockoutUntil, u.LastLoginAt, Permissions = u.Permissions.Select(p => new { p.Section, p.Level }) })
            .ToListAsync());
    }

    [HttpPost]
    public async Task<IActionResult> Create(UserInput r)
    {
        var a = await actor.GetAsync(); a.Require(Section.Users, PermissionLevel.Edit);
        if (Passwords.Problem(r.Password, r.Username, r.DisplayName) is { } p) throw new DomainException(p);
        var levels = Grantable(a, r);
        await Refs.EnsureAsync<Driver>(db, r.DriverId, "السائق");
        var u = new User { Username = r.Username.Trim().ToLowerInvariant(), DisplayName = r.DisplayName.Trim(), PasswordHash = Passwords.Hash(r.Password!), Role = r.Role, DriverId = r.DriverId };
        u.Permissions = levels.Where(kv => kv.Value != PermissionLevel.None).Select(kv => new UserPermission { UserId = u.Id, Section = kv.Key, Level = kv.Value }).ToList();
        db.Users.Add(u);
        audit.Write(a, "user", u.Id, "إضافة مستخدم", $"{u.Username} · {u.Role}");
        await db.SaveChangesAsync();
        await events.RecordAsync("user.created", Severity.Medium, $"أُنشئ المستخدم «{u.Username}» بدور {u.Role}.", a.CompanyId, a.UserId);
        return Ok(new { u.Id });
    }

    [HttpPut("{id:guid}/permissions")]
    public async Task<IActionResult> SetPermissions(Guid id, UserInput r)
    {
        var a = await actor.GetAsync(); a.Require(Section.Users, PermissionLevel.Edit);
        var u = await Target(a, id);
        var levels = Grantable(a, r);
        if (id == a.UserId && levels.GetValueOrDefault(Section.Users) != PermissionLevel.Edit) throw new DomainException("لا يمكنك إزالة صلاحية إدارة المستخدمين عن نفسك.");
        await Refs.EnsureAsync<Driver>(db, r.DriverId, "السائق");
        var before = string.Join(",", u.Permissions.Select(p => $"{p.Section}:{p.Level}"));
        db.UserPermissions.RemoveRange(u.Permissions);
        u.Permissions = levels.Where(kv => kv.Value != PermissionLevel.None).Select(kv => new UserPermission { UserId = u.Id, Section = kv.Key, Level = kv.Value }).ToList();
        u.Role = r.Role; u.DriverId = r.DriverId; u.DisplayName = r.DisplayName.Trim();
        var after = string.Join(",", u.Permissions.Select(p => $"{p.Section}:{p.Level}"));
        audit.Write(a, "user", u.Id, "تعديل الصلاحيات", null, before, after);
        await db.SaveChangesAsync();
        await events.RecordAsync("permissions.changed", Severity.High, $"عُدّلت صلاحيات المستخدم «{u.Username}» ({u.Role}).", a.CompanyId, a.UserId);
        return NoContent();
    }

    [HttpPost("{id:guid}/disable")]
    public async Task<IActionResult> Disable(Guid id)
    {
        var a = await actor.GetAsync(); a.Require(Section.Users, PermissionLevel.Edit);
        if (id == a.UserId) throw new DomainException("لا يمكنك تعطيل حسابك.");
        var u = await Target(a, id);
        u.IsActive = false;
        audit.Write(a, "user", u.Id, "تعطيل المستخدم");
        await db.SaveChangesAsync();
        var n = await sessions.RevokeAsync(u.Id, "disabled");
        await events.RecordAsync("user.disabled", Severity.Medium, $"عُطّل المستخدم «{u.Username}» وأُنهيت {n} جلسة.", a.CompanyId, a.UserId);
        return NoContent();
    }

    [HttpPost("{id:guid}/enable")]
    public async Task<IActionResult> Enable(Guid id)
    {
        var a = await actor.GetAsync(); a.Require(Section.Users, PermissionLevel.Edit);
        var u = await Target(a, id);
        (u.IsActive, u.LockoutUntil, u.FailedLogins) = (true, null, 0);
        audit.Write(a, "user", u.Id, "تفعيل المستخدم وفك الإيقاف");
        await db.SaveChangesAsync();
        await events.RecordAsync("user.enabled", Severity.Medium, $"فُعّل المستخدم «{u.Username}».", a.CompanyId, a.UserId);
        return NoContent();
    }

    [HttpPost("{id:guid}/revoke-sessions")]
    public async Task<IActionResult> RevokeSessions(Guid id)
    {
        var a = await actor.GetAsync(); a.Require(Section.Users, PermissionLevel.Edit);
        var u = await Target(a, id);
        var n = await sessions.RevokeAsync(u.Id, "admin");
        await events.RecordAsync("session.revoked", Severity.Medium, $"أنهى المشرف كل جلسات «{u.Username}» ({n}).", a.CompanyId, a.UserId);
        return Ok(new { revoked = n });
    }

    [HttpPost("{id:guid}/password")]
    public async Task<IActionResult> ResetPassword(Guid id, AdminPasswordRequest r)
    {
        var a = await actor.GetAsync(); a.Require(Section.Users, PermissionLevel.Edit);
        var u = await Target(a, id);
        if (Passwords.Problem(r.NewPassword, u.Username) is { } p) throw new DomainException(p);
        (u.PasswordHash, u.PasswordChangedAt, u.LockoutUntil, u.FailedLogins) = (Passwords.Hash(r.NewPassword), DateTimeOffset.UtcNow, null, 0);
        audit.Write(a, "user", u.Id, "إعادة تعيين كلمة المرور");
        await db.SaveChangesAsync();
        await sessions.RevokeAsync(u.Id, "password-reset");
        await events.RecordAsync("password.reset", Severity.High, $"أعاد المشرف تعيين كلمة مرور «{u.Username}».", a.CompanyId, a.UserId);
        return NoContent();
    }

    /// <summary>Lost phone: an Admin clears two-step verification; the user must enrol again at next sign-in.</summary>
    [HttpPost("{id:guid}/reset-mfa")]
    public async Task<IActionResult> ResetMfa(Guid id)
    {
        var a = await actor.GetAsync(); a.Require(Section.Users, PermissionLevel.Edit);
        if (a.Role != Role.Admin) throw new ForbiddenException();
        var u = await Target(a, id);
        (u.MfaEnabled, u.MfaSecretProtected, u.MfaPendingProtected, u.RecoveryCodeHashes) = (false, null, null, []);
        audit.Write(a, "user", u.Id, "إعادة ضبط التحقق بخطوتين");
        await db.SaveChangesAsync();
        await sessions.RevokeAsync(u.Id, "mfa-reset");
        await events.RecordAsync("mfa.reset", Severity.High, $"أعاد المدير ضبط التحقق بخطوتين للمستخدم «{u.Username}».", a.CompanyId, a.UserId);
        return NoContent();
    }

    /// <summary>The target must be in the same company (query filter), and only an Admin touches an Admin.</summary>
    private async Task<User> Target(Actor a, Guid id)
    {
        var u = await db.Users.Include(x => x.Permissions).FirstOrDefaultAsync(x => x.Id == id) ?? throw new NotFoundException("المستخدم");
        if (u.Role == Role.Admin && a.Role != Role.Admin) throw new ForbiddenException();
        return u;
    }

    /// <summary>No privilege escalation: each requested level must be at most the actor's own level, and only an Admin creates an Admin.</summary>
    private static Dictionary<Section, PermissionLevel> Grantable(Actor a, UserInput r)
    {
        if (r.Role == Role.Admin && a.Role != Role.Admin) throw new ForbiddenException();
        if (!Enum.IsDefined(r.Role)) throw new DomainException("دور غير معروف.");
        var levels = r.Levels ?? Permissions.Presets[r.Role].ToDictionary();
        foreach (var (section, level) in levels)
        {
            if (!Enum.IsDefined(section) || !Enum.IsDefined(level)) throw new DomainException("صلاحية غير معروفة.");
            if (level > a.Levels.GetValueOrDefault(section)) throw new ForbiddenException();
        }
        if (levels.GetValueOrDefault(Section.Driver) == PermissionLevel.Edit && r.DriverId is null) throw new DomainException("صلاحية تطبيق السائق تحتاج ربط الحساب بسائق.");
        return levels;
    }
}

/// <summary>Security log for administrators (sign-ins, lockouts, denied access, permission changes...).</summary>
[ApiController, Route("api/security"), Authorize]
public class SecurityLogController(FuelUpDb db, ActorAccessor actor) : ControllerBase
{
    [HttpGet("events")]
    public async Task<IActionResult> Events([FromQuery, Range(1, 500)] int take = 200, [FromQuery] string? severity = null)
    {
        (await actor.GetAsync()).Require(Section.Users, PermissionLevel.View);
        var q = db.Audit.Where(x => x.Entity == "security");
        if (severity is "Low" or "Medium" or "High") q = q.Where(x => x.Severity == severity);
        var users = await db.Users.Select(u => new { u.Id, u.Username }).ToDictionaryAsync(u => u.Id, u => u.Username);
        var rows = await q.OrderByDescending(x => x.Id).Take(take).ToListAsync();
        return Ok(rows.Select(x => new { x.At, x.Action, x.Severity, x.Detail, x.Ip, user = x.UserId is { } id ? users.GetValueOrDefault(id) : null }));
    }
}
