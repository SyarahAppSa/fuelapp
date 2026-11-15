using System.Text;
using FuelUp.Api.Security;
using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OtpNet;
using Xunit;

namespace FuelUp.Tests;

public class PasswordTests
{
    [Theory]
    [InlineData("short1!", "10 أحرف")]
    [InlineData("Password123", "شائعة")]
    [InlineData("aaaaaaaaaaaa", "مكررة")]
    [InlineData("driver1-truck-2026", "اسم المستخدم")]
    public void Weak_passwords_are_refused(string pw, string reason) => Assert.Contains(reason, Passwords.Problem(pw, "driver1"));

    [Fact]
    public void Too_long_for_bcrypt_is_refused() => Assert.NotNull(Passwords.Problem(new string('ض', 40)));   // 80 bytes in UTF-8 > bcrypt's 72

    [Fact]
    public void Good_password_accepted_and_hashed_with_bcrypt_cost_12()
    {
        Assert.Null(Passwords.Problem("Dammam-Truck-Route-77", "driver1"));
        var h = Passwords.Hash("Dammam-Truck-Route-77");
        Assert.StartsWith("$2", h); Assert.Contains("$12$", h);
        Assert.True(Passwords.Verify("Dammam-Truck-Route-77", h));
        Assert.False(Passwords.Verify("dammam-truck-route-77", h));
        Assert.NotEqual(h, Passwords.Hash("Dammam-Truck-Route-77"));   // per-password salt
    }
}

public class MfaTests
{
    private static readonly MfaService Mfa = new(new EphemeralDataProtectionProvider());

    [Fact]
    public void Secret_is_encrypted_at_rest_and_code_cannot_be_replayed()
    {
        var stored = Mfa.NewProtectedSecret();
        var secret = Mfa.Reveal(stored);
        Assert.DoesNotContain(secret, stored);
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        var code = totp.ComputeTotp();
        var step = Mfa.Verify(stored, code, 0);
        Assert.NotNull(step);
        Assert.Null(Mfa.Verify(stored, code, step!.Value));      // same code again → refused
        var window = new[] { -30, 0, 30 }.Select(d => totp.ComputeTotp(DateTime.UtcNow.AddSeconds(d))).ToHashSet();
        var wrong = Enumerable.Range(0, 10).Select(i => new string((char)('0' + i), 6)).First(c => !window.Contains(c));
        Assert.Null(Mfa.Verify(stored, wrong, 0));               // wrong code → refused
        Assert.Null(Mfa.Verify(stored, "12345", 0));             // wrong length
    }

    [Fact]
    public void Recovery_code_works_once_and_only_hashes_are_kept()
    {
        var (plain, hashes) = MfaService.NewRecoveryCodes();
        Assert.Equal(8, plain.Count); Assert.Equal(8, plain.Distinct().Count());
        var u = new User { RecoveryCodeHashes = hashes };
        Assert.DoesNotContain(plain[0], string.Join("", u.RecoveryCodeHashes));
        Assert.True(MfaService.UseRecoveryCode(u, plain[0].ToUpperInvariant()));
        Assert.False(MfaService.UseRecoveryCode(u, plain[0]));
        Assert.Equal(7, u.RecoveryCodeHashes.Count);
    }

    [Fact]
    public void Management_roles_require_two_step_verification()
    {
        Assert.True(MfaService.IsRequired(Role.Admin, new Dictionary<Section, PermissionLevel>()));
        Assert.True(MfaService.IsRequired(Role.OpsSupervisor, new Dictionary<Section, PermissionLevel>()));
        Assert.True(MfaService.IsRequired(Role.Custom, new Dictionary<Section, PermissionLevel> { [Section.Users] = PermissionLevel.Edit }));
        Assert.False(MfaService.IsRequired(Role.Driver, new Dictionary<Section, PermissionLevel> { [Section.Driver] = PermissionLevel.Edit }));
    }
}

public class FileSignatureTests
{
    [Fact]
    public void Real_types_are_detected_from_content_not_name()
    {
        Assert.Equal("image/jpeg", FileSignatures.Detect([0xFF, 0xD8, 0xFF, 0xE0, 0, 0])?.ContentType);
        Assert.Equal("image/png", FileSignatures.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0])?.ContentType);
        Assert.Equal("application/pdf", FileSignatures.Detect(Encoding.ASCII.GetBytes("%PDF-1.7 ..."))?.ContentType);
        Assert.Null(FileSignatures.Detect(Encoding.UTF8.GetBytes("<svg onload=alert(1)>")));
        Assert.Null(FileSignatures.Detect(Encoding.UTF8.GetBytes("<html><script>")));
        Assert.Null(FileSignatures.Detect(Encoding.ASCII.GetBytes("MZ\u0090\0 executable")));
        Assert.Null(FileSignatures.Detect([]));
    }
}

public class TenantIsolationTests
{
    private static (FuelUpDb Db, TenantContext Tenant) Db(string name)
    {
        var t = new TenantContext();
        return (new FuelUpDb(new DbContextOptionsBuilder<FuelUpDb>().UseInMemoryDatabase(name).Options, t), t);
    }

    [Fact]
    public async Task Queries_see_only_the_current_company_and_new_rows_are_stamped()
    {
        var name = Guid.NewGuid().ToString();
        var (db, t) = Db(name);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        t.Set(a); db.Stations.Add(new Station { Name = "A" }); await db.SaveChangesAsync();
        t.Set(b); db.Stations.Add(new Station { Name = "B" }); await db.SaveChangesAsync();

        var (db2, t2) = Db(name);
        t2.Set(a);
        var rows = await db2.Stations.ToListAsync();
        Assert.Single(rows); Assert.Equal("A", rows[0].Name); Assert.Equal(a, rows[0].CompanyId);
        t2.Clear();
        Assert.Empty(await db2.Stations.ToListAsync());          // signed-out context sees nothing
    }

    [Fact]
    public async Task Writing_another_companys_row_is_refused()
    {
        var name = Guid.NewGuid().ToString();
        var (db, t) = Db(name);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        t.Set(a);
        db.Stations.Add(new Station { Name = "X", CompanyId = b });   // client tried to choose the company
        await Assert.ThrowsAsync<TenantViolationException>(() => db.SaveChangesAsync());

        var (db2, t2) = Db(name);
        t2.Set(a); var s = new Station { Name = "Mine" }; db2.Stations.Add(s); await db2.SaveChangesAsync();
        s.CompanyId = b;                                               // tries to move it to another company
        await Assert.ThrowsAsync<TenantViolationException>(() => db2.SaveChangesAsync());
    }
}
