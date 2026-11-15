using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FuelUp.Api;

/// <summary>
/// Demo data for development and tests: two separate companies, so company isolation can be tested.
/// Refused outside Development unless explicitly allowed (Program.cs), because the demo password is public.
/// </summary>
public static class DemoSeeder
{
    public const string DemoPassword = "FuelUp@2026";

    public static async Task RunAsync(FuelUpDb db, TenantContext tenant)
    {
        if (await db.Users.IgnoreQueryFilters().AnyAsync()) return;
        var company = new Company { Name = "شركة فيول آب (تجريبية)" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        tenant.Set(company.Id);

        var stations = new[]
        {
            new Station { Name = "محطة الظهران", Type = StationType.Station, City = "الظهران", Lat = 26.2361, Lng = 50.0393 },
            new Station { Name = "حوش ميسان", Type = StationType.MaysanYard, Lat = 26.42, Lng = 50.08 },
            new Station { Name = "أرامكو رأس تنورة", Type = StationType.Aramco, City = "رأس تنورة", Lat = 26.64, Lng = 50.16 },
        };
        var meters = new[] { new Meter { Serial = "LC-M7-40112", Kind = "إلكتروني بطابعة تذاكر" }, new Meter { Serial = "LC-M7-40588", Kind = "إلكتروني بطابعة تذاكر" } };
        var vehicles = new[]
        {
            new Vehicle { Plate = "ر ط أ 4821", InternalNo = "T-07", Make = "مرسيدس-بنز", Model = "أكتروس 3340", Year = 2021, Capacity = 32000, Compartments = [new(1, 16000), new(2, 16000)], MeterId = meters[0].Id },
            new Vehicle { Plate = "ب ح د 1307", InternalNo = "T-11", Make = "فولفو", Model = "FH 460", Year = 2019, Capacity = 20000, Compartments = [new(1, 10000), new(2, 10000)], MeterId = meters[1].Id },
        };
        meters[0].VehicleId = vehicles[0].Id; meters[1].VehicleId = vehicles[1].Id;
        var drivers = new[] { new Driver { Name = "سالم العتيبي", EmpNo = "E100", Type = DriverType.Aramco }, new Driver { Name = "خالد الدوسري", EmpNo = "E101", Type = DriverType.SmallTruck } };
        var customers = new[]
        {
            new Customer { Name = "مقاولات الجبيل", Project = "مشروع الطريق الدائري", Site = "موقع الكسارة — الجبيل", Recipient = "مشرف الموقع", Lat = 27.0, Lng = 49.6 },
            new Customer { Name = "مصنع الدمام", Project = "مولدات المصنع", Site = "المدينة الصناعية الثانية", Lat = 26.37, Lng = 50.15 },
        };
        db.AddRange(stations); db.AddRange(meters); db.AddRange(vehicles); db.AddRange(drivers); db.AddRange(customers);
        db.Settings.AddRange(new Setting { Key = "tolAbs", Value = "20" }, new Setting { Key = "tolPct", Value = "0.3" });

        User U(string username, string name, Role role, Guid? driverId = null)
        {
            var u = new User { Username = username, DisplayName = name, Role = role, DriverId = driverId, PasswordHash = Security.Passwords.Hash(DemoPassword) };
            u.Permissions = Permissions.Presets[role].Select(kv => new UserPermission { UserId = u.Id, Section = kv.Key, Level = kv.Value }).ToList();
            return u;
        }
        var admin = U("admin", "مدير المنظومة", Role.Admin);
        db.Users.AddRange(admin, U("supervisor", "مشرف التشغيل", Role.OpsSupervisor), U("driver1", "سالم العتيبي", Role.Driver, drivers[0].Id), U("driver2", "خالد الدوسري", Role.Driver, drivers[1].Id));

        var now = DateTimeOffset.UtcNow;
        db.VehicleAssignments.AddRange(
            new VehicleAssignment { VehicleId = vehicles[0].Id, DriverId = drivers[0].Id, From = now, ByUserId = admin.Id },
            new VehicleAssignment { VehicleId = vehicles[1].Id, DriverId = drivers[1].Id, From = now, ByUserId = admin.Id });
        db.CustomerAssignments.AddRange(
            new CustomerAssignment { CustomerId = customers[0].Id, DriverId = drivers[0].Id, From = now, ByUserId = admin.Id },
            new CustomerAssignment { CustomerId = customers[1].Id, DriverId = drivers[1].Id, From = now, ByUserId = admin.Id });

        // approved baseline for the first meter, matching the documented example (last approved reading 125,000)
        db.MeterBaselines.Add(new MeterBaseline { MeterId = meters[0].Id, Reading = 125000, State = ApprovalState.Approved, At = now, ByUserId = admin.Id, ApprovedBy = admin.Id, ApprovedAt = now });
        db.MeterReadings.Add(new MeterReading { MeterId = meters[0].Id, Seq = 1, Reading = 125000, Kind = "قراءة تأسيسية", At = now, ByUserId = admin.Id });

        db.Trips.Add(new Trip
        {
            No = $"FU-{DateTime.UtcNow:yyMM}-0001", CreatedBy = admin.Id, StationId = stations[2].Id, SourceRef = "SA-7782210", CustomerId = customers[0].Id, DriverId = drivers[0].Id,
            Product = "ديزل", QtyRequested = 3000,
            Snapshot = new TripSnapshot(drivers[0].Name, drivers[0].EmpNo, "", customers[0].Name, customers[0].Project, customers[0].Site, stations[2].Name, stations[2].Type, stations[2].Lat, stations[2].Lng, null, now),
        });
        await db.SaveChangesAsync();
        await SecondCompanyAsync(db, tenant);
    }

    /// <summary>A second, unrelated company. Its users must never see the first company's data, and vice versa.</summary>
    private static async Task SecondCompanyAsync(FuelUpDb db, TenantContext tenant)
    {
        var other = new Company { Name = "شركة الخليج للنقل (تجريبية)" };
        db.Companies.Add(other);
        await db.SaveChangesAsync();
        tenant.Set(other.Id);
        var station = new Station { Name = "محطة الجبيل", Type = StationType.Station, City = "الجبيل" };
        var driver = new Driver { Name = "فهد القحطاني", EmpNo = "G200", Type = DriverType.SmallTruck };
        var customer = new Customer { Name = "عميل شركة الخليج", Site = "الخبر" };
        var vehicle = new Vehicle { Plate = "ر ط أ 4821", Make = "إيسوزو", Capacity = 8000 };   // same plate as company 1: allowed, plates are unique per company
        db.AddRange(station, driver, customer, vehicle);
        db.Settings.AddRange(new Setting { Key = "tolAbs", Value = "20" }, new Setting { Key = "tolPct", Value = "0.3" });
        User U(string username, string name, Role role, Guid? driverId = null)
        {
            var u = new User { Username = username, DisplayName = name, Role = role, DriverId = driverId, PasswordHash = Security.Passwords.Hash(DemoPassword) };
            u.Permissions = Permissions.Presets[role].Select(kv => new UserPermission { UserId = u.Id, Section = kv.Key, Level = kv.Value }).ToList();
            return u;
        }
        var admin = U("gulf.admin", "مدير شركة الخليج", Role.Admin);
        db.Users.AddRange(admin, U("gulf.driver", "فهد القحطاني", Role.Driver, driver.Id));
        db.VehicleAssignments.Add(new VehicleAssignment { VehicleId = vehicle.Id, DriverId = driver.Id, From = DateTimeOffset.UtcNow, ByUserId = admin.Id });
        db.Trips.Add(new Trip
        {
            No = $"FU-{DateTime.UtcNow:yyMM}-0001", CreatedBy = admin.Id, StationId = station.Id, SourceRef = "GULF-1", CustomerId = customer.Id, DriverId = driver.Id, Product = "ديزل", QtyRequested = 5000,
            Snapshot = new TripSnapshot(driver.Name, driver.EmpNo, "", customer.Name, null, customer.Site, station.Name, station.Type, null, null, null, DateTimeOffset.UtcNow),
        });
        await db.SaveChangesAsync();
        tenant.Clear();
    }
}

/// <summary>
/// Production first run: creates the first company and its administrator from one-time environment variables
/// (Bootstrap__CompanyName, Bootstrap__AdminUsername, Bootstrap__AdminPassword). No default password ships with the system;
/// the administrator must enrol two-step verification at first sign-in. Remove the variables after the first start.
/// </summary>
public static class Bootstrap
{
    public static async Task RunAsync(FuelUpDb db, TenantContext tenant, IConfiguration cfg, ILogger log)
    {
        if (await db.Users.IgnoreQueryFilters().AnyAsync()) return;
        var (name, username, password) = (cfg["Bootstrap:CompanyName"], cfg["Bootstrap:AdminUsername"], cfg["Bootstrap:AdminPassword"]);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            log.LogWarning("No users yet. Set Bootstrap__CompanyName, Bootstrap__AdminUsername and Bootstrap__AdminPassword for the first start.");
            return;
        }
        if (Security.Passwords.Problem(password, username) is { } p) throw new InvalidOperationException("Bootstrap admin password refused: " + p);
        var company = new Company { Name = name };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        tenant.Set(company.Id);
        var u = new User { Username = username.Trim().ToLowerInvariant(), DisplayName = "مدير المنظومة", Role = Role.Admin, PasswordHash = Security.Passwords.Hash(password) };
        u.Permissions = Permissions.Presets[Role.Admin].Select(kv => new UserPermission { UserId = u.Id, Section = kv.Key, Level = kv.Value }).ToList();
        db.Users.Add(u);
        db.Settings.AddRange(new Setting { Key = "tolAbs", Value = "20" }, new Setting { Key = "tolPct", Value = "0.3" });
        await db.SaveChangesAsync();
        tenant.Clear();
        log.LogWarning("Bootstrap: company and administrator created. Remove the Bootstrap__* variables now.");
    }
}
