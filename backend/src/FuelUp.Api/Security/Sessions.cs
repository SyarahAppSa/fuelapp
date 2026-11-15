using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace FuelUp.Api.Security;

/// <summary>Signs and checks JWTs (HMAC-SHA256 only). The key comes from configuration/secrets, never from the code.</summary>
public class TokenService(IConfiguration cfg)
{
    public const string MfaAudienceSuffix = "-mfa";
    private SymmetricSecurityKey Key => new(Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!));
    private string Issuer => cfg["Jwt:Issuer"] ?? "fuelup";
    private string Audience => cfg["Jwt:Audience"] ?? "fuelup-clients";
    public int AccessMinutes => cfg.GetValue("Jwt:AccessMinutes", 10);

    public string Access(User u, UserSession s)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, u.Id.ToString()), new("sid", s.Id.ToString()), new("cid", u.CompanyId.ToString()),
            new("role", u.Role.ToString()), new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        return Write(claims, Audience, TimeSpan.FromMinutes(AccessMinutes));
    }

    /// <summary>Short-lived ticket between "password correct" and "code correct". Different audience, so it is useless as an access token.</summary>
    public string MfaTicket(User u, string purpose) => Write(
        [new(JwtRegisteredClaimNames.Sub, u.Id.ToString()), new("purpose", purpose), new("stamp", Stamp(u)), new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))],
        Audience + MfaAudienceSuffix, TimeSpan.FromMinutes(5));

    public (Guid UserId, string Purpose, string Stamp)? ReadMfaTicket(string? ticket)
    {
        if (string.IsNullOrEmpty(ticket) || ticket.Length > 2000) return null;
        try
        {
            var p = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(ticket, Parameters(Audience + MfaAudienceSuffix), out _);
            return (Guid.Parse(p.FindFirstValue(JwtRegisteredClaimNames.Sub)!), p.FindFirstValue("purpose")!, p.FindFirstValue("stamp")!);
        }
        catch (Exception e) when (e is SecurityTokenException or ArgumentException or FormatException) { return null; }
    }

    /// <summary>Changes when the password changes, so pending tickets die with the old password.</summary>
    public static string Stamp(User u) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(u.Id + "|" + u.PasswordChangedAt.UtcTicks)))[..16];

    public TokenValidationParameters Parameters(string? audience = null) => new()
    {
        ValidateIssuer = true, ValidIssuer = Issuer, ValidateAudience = true, ValidAudience = audience ?? Audience,
        ValidateIssuerSigningKey = true, IssuerSigningKey = Key, ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true, ClockSkew = TimeSpan.FromSeconds(30),
    };

    private string Write(IEnumerable<Claim> claims, string audience, TimeSpan life) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, audience, claims, DateTime.UtcNow, DateTime.UtcNow.Add(life),
            new SigningCredentials(Key, SecurityAlgorithms.HmacSha256)));

    public static string NewRefresh() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public record IssuedTokens(string AccessToken, string RefreshToken, int ExpiresIn, Guid SessionId);

/// <summary>Sessions: one per signed-in device, listed and revocable by the user and by administrators.</summary>
public class SessionService(FuelUpDb db, TenantContext tenant, TokenService tokens, IConfiguration cfg, SecurityEvents events)
{
    private TimeSpan Absolute(string client) => client == "web" ? TimeSpan.FromHours(cfg.GetValue("Sessions:WebHours", 12)) : TimeSpan.FromDays(cfg.GetValue("Sessions:MobileDays", 30));
    private TimeSpan Idle(string client) => client == "web" ? TimeSpan.FromHours(cfg.GetValue("Sessions:WebIdleHours", 2)) : TimeSpan.FromDays(cfg.GetValue("Sessions:MobileIdleDays", 14));

    public async Task<IssuedTokens> StartAsync(User u, string client, bool mfa, HttpContext ctx)
    {
        tenant.Set(u.CompanyId);
        var s = new UserSession
        {
            CompanyId = u.CompanyId, UserId = u.Id, Client = client, MfaVerified = mfa, Ip = ClientInfo.Ip(ctx), UserAgent = ClientInfo.Agent(ctx),
            ExpiresAt = DateTimeOffset.UtcNow.Add(Absolute(client)),
        };
        db.Sessions.Add(s);
        var refresh = NewRefreshToken(u, s);
        await db.SaveChangesAsync();
        return new IssuedTokens(tokens.Access(u, s), refresh, tokens.AccessMinutes * 60, s.Id);
    }

    private string NewRefreshToken(User u, UserSession s)
    {
        var raw = TokenService.NewRefresh();
        var exp = DateTimeOffset.UtcNow.Add(Idle(s.Client));
        db.RefreshTokens.Add(new RefreshToken { CompanyId = u.CompanyId, UserId = u.Id, SessionId = s.Id, TokenHash = TokenService.Hash(raw), ExpiresAt = exp < s.ExpiresAt ? exp : s.ExpiresAt });
        return raw;
    }

    /// <summary>Rotates a refresh token. A token presented twice is treated as stolen: the session is revoked and an alert raised.</summary>
    public async Task<IssuedTokens?> RotateAsync(string raw, HttpContext ctx)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > 200) return null;
        var hash = TokenService.Hash(raw);
        var t = await db.RefreshTokens.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.TokenHash == hash);
        if (t is null) return null;
        tenant.Set(t.CompanyId);
        var s = await db.Sessions.IgnoreQueryFilters().FirstAsync(x => x.Id == t.SessionId);
        var now = DateTimeOffset.UtcNow;
        if (t.UsedAt is { } used)
        {
            // Two tabs refreshing at the same instant are not theft: a few seconds of grace, without a new token.
            if (now - used < TimeSpan.FromSeconds(10)) return null;
            if (s.RevokedAt is null)
            {
                s.RevokedAt = now; s.RevokeReason = "token-reuse";
                await db.SaveChangesAsync();
                await events.RecordAsync("refresh.reuse", Severity.High, "استُخدم رمز تجديد سبق استخدامه؛ أُلغيت الجلسة احتياطًا (احتمال سرقة الرمز).", s.CompanyId, s.UserId);
            }
            return null;
        }
        if (t.RevokedAt != null || t.ExpiresAt <= now || s.RevokedAt != null || s.ExpiresAt <= now) return null;
        var u = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == t.UserId && x.IsActive);
        var company = await db.Companies.IgnoreQueryFilters().AnyAsync(c => c.Id == s.CompanyId && c.IsActive);
        if (u is null || !company) return null;
        t.UsedAt = now; s.LastSeenAt = now; s.Ip = ClientInfo.Ip(ctx);
        var next = NewRefreshToken(u, s);
        await db.SaveChangesAsync();
        return new IssuedTokens(tokens.Access(u, s), next, tokens.AccessMinutes * 60, s.Id);
    }

    public async Task<int> RevokeAsync(Guid userId, string reason, Guid? only = null, Guid? except = null)
    {
        var now = DateTimeOffset.UtcNow;
        var list = await db.Sessions.IgnoreQueryFilters()
            .Where(x => x.UserId == userId && x.RevokedAt == null && (only == null || x.Id == only) && (except == null || x.Id != except)).ToListAsync();
        foreach (var s in list) { s.RevokedAt = now; s.RevokeReason = reason; }
        var ids = list.Select(s => s.Id).ToList();
        await db.RefreshTokens.IgnoreQueryFilters().Where(t => ids.Contains(t.SessionId) && t.RevokedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(t => t.RevokedAt, now));
        await db.SaveChangesAsync();
        return list.Count;
    }
}
