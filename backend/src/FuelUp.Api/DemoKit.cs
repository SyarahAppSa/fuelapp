using FuelUp.Api.Security;
using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FuelUp.Api;

/// <summary>
/// A ready-to-try kit for the driver app (only when Demo__DriverPassword is set by the host, never in the code):
///  • driver account «fuelup_driver_demo» linked to a demo driver and a demo truck (with trailer and secret meter);
///  • operations account «fuelup_ops_demo» (Demo__OpsPassword) to review the report on the web;
///  • at every start, one assigned trip waiting for the driver, so «ابدأ التوثيق» is ready right after signing in.
/// Passwords are set when the account is created only (Demo__ResetPasswords=true sets them again). Loaded into the first company.
/// </summary>
public static class DemoKit
{
    public static string DriverUser(IConfiguration cfg) => (cfg["Demo:DriverUsername"] ?? "fuelup_driver_demo").Trim().ToLowerInvariant();
    public static string OpsUser(IConfiguration cfg) => (cfg["Demo:OpsUsername"] ?? "fuelup_ops_demo").Trim().ToLowerInvariant();
    private const string Plate = "ت ج ر 2026", MeterSerial = "DEMO-SM-2026";

    public static async Task RunAsync(FuelUpDb db, TenantContext tenant, IConfiguration cfg, ILogger log)
    {
        var driverPass = cfg["Demo:DriverPassword"];
        string DriverUser = DemoKit.DriverUser(cfg), OpsUser = DemoKit.OpsUser(cfg);
        if (string.IsNullOrEmpty(driverPass)) return;
        var opsPass = cfg["Demo:OpsPassword"];
        var reset = cfg.GetValue<bool>("Demo:ResetPasswords");
        var company = await db.Companies.IgnoreQueryFilters().OrderBy(c => c.CreatedAt).FirstOrDefaultAsync();
        if (company is null) return;
        tenant.Set(company.Id);
        if (Passwords.Problem(driverPass, DriverUser) is { } p1) { log.LogWarning("Demo kit skipped: driver password refused ({Problem})", p1); return; }
        if (opsPass is not null && Passwords.Problem(opsPass, OpsUser) is { } p2) { log.LogWarning("Demo kit: ops password refused ({Problem})", p2); opsPass = null; }

        var admin = await db.Users.Where(u => u.Role == Role.Admin).OrderBy(u => u.CreatedAt).FirstAsync();
        var boss = new Actor(admin.Id, null, Enum.GetValues<Section>().ToDictionary(s => s, _ => PermissionLevel.Edit), company.Id, Role.Admin);
        var audit = new AuditWriter(db); var assign = new AssignmentService(db, audit);
        var trips = new TripService(db, new MeterLedger(db), assign, audit);

        // demo truck + secret meter
        var meter = await db.Meters.FirstOrDefaultAsync(m => m.Serial == MeterSerial);
        var vehicle = await db.Vehicles.FirstOrDefaultAsync(v => v.Plate == Plate);
        if (vehicle is null)
        {
            meter ??= new Meter { Serial = MeterSerial, Kind = "عداد سري (تجريبي)" };
            vehicle = new Vehicle { Plate = Plate, TrailerPlate = "م ق ط 2026", InternalNo = "DEMO-1", Make = "مرسيدس-بنز", Model = "أكتروس (تجريبي)", Year = 2022, Capacity = 32000, MeterId = meter.Id };
            meter.VehicleId = vehicle.Id;
            if (db.Entry(meter).State == EntityState.Detached) db.Meters.Add(meter);
            db.Vehicles.Add(vehicle);
            await db.SaveChangesAsync();
        }

        // demo driver + account
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == DriverUser);
        Driver driver;
        if (user is null)
        {
            driver = new Driver { Name = "سائق تجريبي — فيول آب", EmpNo = "DEMO-D1", Phone = "0500000000", NationalId = "1000000001", Type = DriverType.Aramco };
            db.Drivers.Add(driver);
            user = new User { Username = DriverUser, DisplayName = driver.Name, Role = Role.Driver, DriverId = driver.Id, PasswordHash = Passwords.Hash(driverPass) };
            user.Permissions = Permissions.Presets[Role.Driver].Select(kv => new UserPermission { UserId = user.Id, Section = kv.Key, Level = kv.Value }).ToList();
            db.Users.Add(user);
            await db.SaveChangesAsync();
            log.LogWarning("Demo kit: driver account {User} created", DriverUser);
        }
        else
        {
            driver = await db.Drivers.FirstAsync(d => d.Id == user.DriverId);
            if (reset) { user.PasswordHash = Passwords.Hash(driverPass); user.IsActive = true; }
        }
        if (!await db.VehicleAssignments.AnyAsync(a => a.VehicleId == vehicle.Id && a.DriverId == driver.Id && a.To == null))
        {
            await assign.AssignVehicleAsync(boss, vehicle.Id, driver.Id, "عهدة الشاحنة التجريبية");
            await db.SaveChangesAsync();
        }

        // operations reviewer
        if (opsPass is not null)
        {
            var ops = await db.Users.FirstOrDefaultAsync(u => u.Username == OpsUser);
            if (ops is null)
            {
                ops = new User { Username = OpsUser, DisplayName = "مراجع الطلبات والتقارير (تجريبي)", Role = Role.OpsSupervisor, PasswordHash = Passwords.Hash(opsPass) };
                ops.Permissions = Permissions.Presets[Role.OpsSupervisor].Select(kv => new UserPermission { UserId = ops.Id, Section = kv.Key, Level = kv.Value }).ToList();
                db.Users.Add(ops);
                log.LogWarning("Demo kit: operations account {User} created", OpsUser);
            }
            else if (reset) { ops.PasswordHash = Passwords.Hash(opsPass); ops.IsActive = true; }
            await db.SaveChangesAsync();
        }

        // one trip always waiting for the demo driver (not started, no report yet)
        var waiting = await db.Trips.Where(t => t.DriverId == driver.Id && t.Status == TripStatus.WaitingLoad).Select(t => t.Id).ToListAsync();
        var ready = waiting.Count > 0 && waiting.Any(id => !db.FillReports.Any(r => r.TripId == id));
        if (!ready)
        {
            var customer = await db.Customers.FirstOrDefaultAsync(c => c.Name == "عميل تجريبي — شركة الاختبار")
                           ?? db.Customers.Add(new Customer { Name = "عميل تجريبي — شركة الاختبار", Project = "مولدات الموقع", Site = "الدمام — المدينة الصناعية الثانية، بوابة 3", Recipient = "مشرف الموقع", Lat = 26.37, Lng = 50.15 }).Entity;
            var station = await db.Stations.FirstOrDefaultAsync(s => s.Type == StationType.Aramco)
                          ?? db.Stations.Add(new Station { Name = "أرامكو رأس تنورة (تجريبي)", Type = StationType.Aramco, City = "رأس تنورة", Lat = 26.64, Lng = 50.16 }).Entity;
            await db.SaveChangesAsync();
            var n = await db.Trips.CountAsync(t => t.DriverId == driver.Id) + 1;
            var t = await trips.CreateAsync(boss, new CreateTrip(customer.Id, driver.Id, vehicle.Id, station.Id, $"DEMO-{n:000}", "ديزل", 20000, null, 0.85m));
            await db.SaveChangesAsync();
            log.LogWarning("Demo kit: trip {Trip} assigned to {User}, ready to start", t.No, DriverUser);
        }
        tenant.Clear();
    }
}
