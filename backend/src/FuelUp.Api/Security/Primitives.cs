using System.Security.Cryptography;
using System.Text;
using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using OtpNet;

namespace FuelUp.Api.Security;

/// <summary>
/// Password rules and hashing. Hashing is bcrypt (BCrypt.Net-Next, cost 12, per-password salt). No home-made crypto.
/// Rules follow NIST SP 800-63B: length and a deny-list rather than forced symbol mixes.
/// </summary>
public static class Passwords
{
    public const int WorkFactor = 12;
    public const int MinLength = 10;
    public const int MaxLength = 64;

    // A deny-list of passwords seen first in every credential-stuffing list, plus local variants.
    private static readonly HashSet<string> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "password1", "password123", "passw0rd", "p@ssw0rd", "p@ssword1", "qwerty123", "qwertyuiop", "1234567890", "12345678910",
        "123456789a", "a123456789", "1q2w3e4r5t", "1qaz2wsx3edc", "zaq12wsxcde3", "iloveyou12", "welcome123", "admin12345", "administrator",
        "letmein123", "football123", "abc1234567", "aa123456789", "11111111111", "0000000000", "9876543210", "sunshine123", "princess123",
        "fuelup2026", "fuelup@2026", "fullapp2026", "fuelup12345", "saudi12345", "riyadh12345", "aramco12345", "ksa1234567",
    };

    // Constant cost hash used when the username does not exist, so the response time does not reveal valid usernames.
    public static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword(Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)), WorkFactor);

    /// <summary>Returns the Arabic reason the password is refused, or null when it is acceptable.</summary>
    public static string? Problem(string? password, string? username = null, string? displayName = null)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength) return $"كلمة المرور {MinLength} أحرف على الأقل.";
        if (password.Length > MaxLength || Encoding.UTF8.GetByteCount(password) > 72) return $"كلمة المرور أطول من المسموح ({MaxLength} حرفًا).";
        if (Common.Contains(password)) return "كلمة المرور شائعة ومتوقعة؛ اختر كلمة أخرى.";
        if (password.Distinct().Count() < 4) return "كلمة المرور ضعيفة (أحرف مكررة).";
        if (!string.IsNullOrWhiteSpace(username) && username.Length >= 3 && password.Contains(username, StringComparison.OrdinalIgnoreCase)) return "لا تضع اسم المستخدم داخل كلمة المرور.";
        return null;
    }

    public static string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, WorkFactor);
    public static bool Verify(string password, string hash) { try { return BCrypt.Net.BCrypt.Verify(password, hash); } catch (BCrypt.Net.SaltParseException) { return false; } }
    public static bool NeedsRehash(string hash) => BCrypt.Net.BCrypt.PasswordNeedsRehash(hash, WorkFactor);
}

/// <summary>
/// Two-step verification with time-based one-time codes (RFC 6238, Otp.NET), usable with Google/Microsoft Authenticator.
/// The shared secret is encrypted at rest with ASP.NET Core Data Protection; a code can be used only once (replay check).
/// </summary>
public class MfaService(IDataProtectionProvider dp)
{
    private readonly IDataProtector _protector = dp.CreateProtector("FuelUp.Mfa.Secret.v1");
    public const string Issuer = "FuelUp";

    /// <summary>Management accounts must use two-step verification.</summary>
    public static bool IsRequired(User u) => !IsExempt(u.Username) && IsRequired(u.Role, u.Permissions.ToDictionary(p => p.Section, p => p.Level));

    /// <summary>
    /// Trial environments only (App__Environment other than "production"): accounts listed in Auth__MfaExemptUsers sign in with
    /// username and password alone. Set once at start-up; ignored, with an error in the log, in production.
    /// </summary>
    public static IReadOnlySet<string> Exempt { get; private set; } = new HashSet<string>();
    public static bool IsExempt(string username) => Exempt.Contains(username);
    public static string? ConfigureExemptions(string? list, string? environment)
    {
        var names = (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).ToHashSet();
        if (names.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(environment) || environment.Equals("production", StringComparison.OrdinalIgnoreCase))
            return "Auth__MfaExemptUsers is ignored: two-step verification can only be waived in a trial environment (App__Environment).";
        Exempt = names;
        return null;
    }
    public static bool IsRequired(Role role, IReadOnlyDictionary<Section, PermissionLevel> levels) =>
        role is Role.Admin or Role.OpsSupervisor || levels.GetValueOrDefault(Section.Users) == PermissionLevel.Edit;

    public string NewProtectedSecret() => _protector.Protect(Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20)));
    public string Reveal(string protectedSecret) => _protector.Unprotect(protectedSecret);

    public static string OtpAuthUri(string base32Secret, string username) =>
        $"otpauth://totp/{Uri.EscapeDataString(Issuer)}:{Uri.EscapeDataString(username)}?secret={base32Secret}&issuer={Uri.EscapeDataString(Issuer)}&algorithm=SHA1&digits=6&period=30";

    /// <summary>Checks a 6-digit code within ±1 step and refuses a step already used. Returns the matched step or null.</summary>
    public long? Verify(string protectedSecret, string? code, long lastUsedStep)
    {
        if (code is null) return null;
        code = new string(code.Where(char.IsDigit).ToArray());
        if (code.Length != 6) return null;
        var totp = new Totp(Base32Encoding.ToBytes(Reveal(protectedSecret)));
        if (!totp.VerifyTotp(code, out var step, new VerificationWindow(previous: 1, future: 1))) return null;
        return step > lastUsedStep ? step : null;
    }

    public static (List<string> Plain, List<string> Hashes) NewRecoveryCodes(int n = 8)
    {
        var plain = Enumerable.Range(0, n).Select(_ =>
        {
            var s = Base32Encoding.ToString(RandomNumberGenerator.GetBytes(7))[..10].ToLowerInvariant();
            return $"{s[..5]}-{s[5..]}";
        }).ToList();
        return (plain, plain.Select(HashCode).ToList());
    }

    public static string HashCode(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Trim().ToLowerInvariant().Replace(" ", ""))));

    /// <summary>Consumes a recovery code if it matches one of the stored hashes.</summary>
    public static bool UseRecoveryCode(User u, string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 20) return false;
        var h = HashCode(code);
        var match = u.RecoveryCodeHashes.FirstOrDefault(x => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(x), Encoding.ASCII.GetBytes(h)));
        if (match is null) return false;
        u.RecoveryCodeHashes = u.RecoveryCodeHashes.Where(x => x != match).ToList();
        return true;
    }
}

/// <summary>Identifies uploaded files by their first bytes, never by the name or the Content-Type the client sends.</summary>
public static class FileSignatures
{
    public static (string ContentType, string Ext)? Detect(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ("image/jpeg", ".jpg");
        if (b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return ("image/png", ".png");
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8)) return ("image/webp", ".webp");
        if (b.Length >= 12 && b[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = Encoding.ASCII.GetString(b[8..12]);
            if (brand is "heic" or "heix" or "mif1" or "msf1" or "hevc") return ("image/heic", ".heic");
        }
        if (b.Length >= 5 && b[..5].SequenceEqual("%PDF-"u8)) return ("application/pdf", ".pdf");
        return null;
    }
}

public static class ClientInfo
{
    public static string? Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();
    public static string? Agent(HttpContext ctx) { var ua = ctx.Request.Headers.UserAgent.ToString(); return ua.Length > 200 ? ua[..200] : ua; }
}
