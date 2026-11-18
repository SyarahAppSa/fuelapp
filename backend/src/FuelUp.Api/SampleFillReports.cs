using System.IO.Compression;
using FuelUp.Api.Controllers;
using FuelUp.Api.Security;
using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FuelUp.Api;

/// <summary>
/// Sample «تقارير التعبئة» for trying the driver app and the review screen (Seed__Sample only, once: marker "sample_fillreports"):
///  • the demo driver (fuelup_driver_demo, from DemoKit) gets a history through the real trip workflow: two approved reports,
///    one «بحاجة إلى مراجعة» (partial unloading), one returned with a note (to fix in the app) and one in progress (2 of 4 stages);
///  • recent sample orders of driver01…driver10 get reports in every status, so the operations list has work to review.
/// Tank photos are drawings marked «صورة تجريبية»; meter photos are drawn with the report's own readings.
/// </summary>
public static class SampleFillReports
{
    private const string Marker = "sample_fillreports";
    private static readonly TimeSpan Ksa = TimeSpan.FromHours(3);

    public static async Task RunAsync(FuelUpDb db, IConfiguration cfg, ILogger log)
    {
        if (await db.Settings.AnyAsync(s => s.Key == Marker)) return;
        var admin = await db.Users.Where(u => u.Role == Role.Admin).OrderBy(u => u.CreatedAt).FirstAsync();
        var reviewer = await db.Users.Where(u => u.Role == Role.OpsSupervisor).OrderBy(u => u.CreatedAt).FirstOrDefaultAsync() ?? admin;
        var boss = new Actor(admin.Id, null, Enum.GetValues<Section>().ToDictionary(s => s, _ => PermissionLevel.Edit), admin.CompanyId, Role.Admin);
        var store = new FileStore(cfg);
        var rnd = new Random(1718);
        var made = 0;

        // ── the demo driver's own history ───────────────────────────────────────────────────────────────────────────
        var demo = await db.Users.FirstOrDefaultAsync(u => u.Username == DemoKit.DriverUser);
        if (demo?.DriverId is { } demoDriver)
        {
            var audit = new AuditWriter(db); var ledger = new MeterLedger(db); var assign = new AssignmentService(db, audit);
            var svc = new TripService(db, ledger, assign, audit);
            var va = await db.VehicleAssignments.FirstAsync(a => a.DriverId == demoDriver && a.To == null);
            var vehicle = await db.Vehicles.FirstAsync(v => v.Id == va.VehicleId);
            var meter = await db.Meters.FirstAsync(m => m.Id == vehicle.MeterId);
            var customers = await db.Customers.OrderBy(c => c.Name).Take(4).ToListAsync();
            var station = await db.Stations.FirstAsync(s => s.Type == StationType.Aramco);
            var last = await db.MeterReadings.Where(r => r.MeterId == meter.Id).OrderByDescending(r => r.Seq).Select(r => (decimal?)r.Reading).FirstOrDefaultAsync();
            if (last is null) { await ledger.SetBaselineAsync(boss, meter.Id, 152300); await db.SaveChangesAsync(); last = 152300; }
            var reading = last.Value;
            var drv = boss with { DriverId = demoDriver };
            var today = DateTime.UtcNow.Add(Ksa).Date;
            // (days ago, quantity, what happens)
            (int Days, decimal Qty, string Plan)[] plans = [(9, 18000, "approved"), (6, 20000, "approved"), (4, 16000, "partial"), (2, 12000, "returned"), (0, 15000, "draft")];
            var n = 0;
            foreach (var (days, qty, plan) in plans)
            {
                n++;
                var cust = customers[n % customers.Count];
                var at = new DateTimeOffset(today.AddDays(-days).AddHours(plan == "draft" ? Math.Max(1, DateTime.UtcNow.Add(Ksa).Hour - 3) : 7 + n) - Ksa, TimeSpan.Zero);
                var t = await svc.CreateAsync(boss, new CreateTrip(cust.Id, demoDriver, vehicle.Id, station.Id, $"DEMO-H{n:00}", "ديزل", qty, null, 0.85m));
                var start = reading; var end = start + qty;
                await svc.LoadAsync(drv, t.Id, new LoadInput(vehicle.Id, qty, $"DOC-D{n:000}", 210000 + n * 180, null, start, end, $"S{rnd.Next(1000, 9999)}", station.Name, station.Lat, station.Lng, at.AddMinutes(35)));
                reading = end;
                if (plan == "draft") { await SampleData.Backdate(db, t.Id, at, 1); }
                else
                {
                    await svc.DepartAsync(drv, t.Id);
                    await svc.ArriveAsync(drv, t.Id, new ArriveInput(cust.Site, cust.Lat, cust.Lng));
                    await svc.UnloadAsync(drv, t.Id, new UnloadInput(true, null));
                    var left = plan == "partial" ? 3000m : 0m;
                    var after = end + qty - left;
                    await svc.DeliverAsync(drv, t.Id, new DeliverInput(end, after, $"TK-D{n:000}", qty - left, left, 0, $"RC-D{n:000}", false, at.AddHours(3)));
                    reading = after;
                    if (plan == "approved") await svc.ApproveAsync(boss, t.Id, "مطابق للسند");
                    await SampleData.Backdate(db, t.Id, at, 3);
                }
                await db.SaveChangesAsync();
                var status = plan switch { "approved" => FillReportStatus.Approved, "partial" => FillReportStatus.NeedsReview, "returned" => FillReportStatus.Returned, _ => FillReportStatus.Draft };
                await AddReport(db, store, rnd, t.Id, demo.Id, reviewer.Id, status, plan == "partial", plan == "draft" ? 2 : 4);
                made++;
            }
            // the order notifications of past trips were read long ago; only the newest stay unread
            var past = await db.Trips.Where(t => t.DriverId == demoDriver && t.SourceRef.StartsWith("DEMO-H") && t.Status != TripStatus.Loaded).ToDictionaryAsync(t => t.No, t => t.CreatedAt);
            foreach (var note in await db.Notifications.Where(x => x.DriverId == demoDriver && x.ReadAt == null && x.Ref != null).ToListAsync())
                if (past.TryGetValue(note.Ref!, out var created)) { note.At = created; note.ReadAt = created.AddMinutes(20); }
            await db.SaveChangesAsync();
        }

        // ── sample drivers: recent finished orders get reports, in every status ─────────────────────────────────────
        var users = await db.Users.Where(u => u.Role == Role.Driver && u.Username.StartsWith("driver")).ToDictionaryAsync(u => u.DriverId!.Value, u => u.Id);
        var trips = await db.Trips.Include(t => t.Deliveries)
            .Where(t => users.Keys.Contains(t.DriverId) && t.Deliveries.Any(d => d.MeterAfter != null) && !db.FillReports.Any(r => r.TripId == t.Id))
            .OrderByDescending(t => t.CreatedAt).Take(16).ToListAsync();
        for (var i = 0; i < trips.Count; i++)
        {
            var t = trips[i];
            var (status, partial) = i switch
            {
                0 or 1 or 2 => (FillReportStatus.Submitted, false),
                3 => (FillReportStatus.NeedsReview, true),
                4 => (FillReportStatus.NeedsReview, false),   // meter difference beyond tolerance (see AddReport)
                5 => (FillReportStatus.Returned, false),
                _ => (FillReportStatus.Approved, false),
            };
            await AddReport(db, store, rnd, t.Id, users[t.DriverId], reviewer.Id, status, partial, 4, shortMeter: i == 4);
            made++;
        }

        db.Settings.Add(new Setting { Key = Marker, Value = DateTimeOffset.UtcNow.ToString("o") });
        await db.SaveChangesAsync();
        log.LogWarning("Sample fill reports loaded: {Count}", made);
    }

    private static async Task AddReport(FuelUpDb db, FileStore store, Random rnd, Guid tripId, Guid driverUser, Guid reviewer, FillReportStatus status, bool partial, int stages, bool shortMeter = false)
    {
        var t = await db.Trips.Include(x => x.Loading).Include(x => x.Deliveries).FirstAsync(x => x.Id == tripId);
        var s = t.Snapshot!;
        var vehicle = t.VehicleId is { } vid ? await db.Vehicles.FirstOrDefaultAsync(v => v.Id == vid) : null;
        var cust = await db.Customers.FirstAsync(c => c.Id == t.CustomerId);
        var d = t.Deliveries.OrderBy(x => x.Seq).FirstOrDefault();
        var load = t.Loading!;
        var startAt = load.At.AddMinutes(-25);
        var before = d?.MeterBefore ?? load.MeterEnd ?? 0;
        var after = d?.MeterAfter;
        if (shortMeter && after is { } a0) after = a0 - 260;   // a reading that does not match the loaded quantity
        var r = new FillReport
        {
            No = "FR-" + (t.No.StartsWith("FU-") ? t.No[3..] : t.No), TripId = t.Id, DriverId = t.DriverId, StartedBy = driverUser, Status = status, StartedAt = startAt,
            TripNo = t.No, Plate = vehicle?.Plate ?? s.Plate, TrailerPlate = vehicle?.TrailerPlate, DriverName = s.DriverName, CustomerName = s.CustomerName,
            DeliverySite = s.Site, LoadingSite = s.StationName, Product = t.Product, QtyRequested = t.QtyRequested, SecretMeterSerial = s.MeterSerial,
            PreFillAt = startAt.AddMinutes(10), PostFillAt = load.At.AddMinutes(8), LoadedQty = load.Qty, LoadedFromTrip = true,
        };
        if (stages == 4)
        {
            var arrived = d?.ArrivedAt ?? load.At.AddHours(2);
            r.ArrivedAt = arrived; r.MeterBefore = before;
            r.UnloadedAt = arrived.AddMinutes(45); r.MeterAfter = after;
            r.Outcome = partial ? UnloadOutcome.Partial : UnloadOutcome.Full;
            r.OutcomeReason = partial ? "خزان العميل امتلأ قبل اكتمال التفريغ" : null;
            r.TankState = partial ? "متبقٍ فيه وقود" : "فارغ";
            r.SubmittedAt = r.UnloadedAt.Value.AddMinutes(4);
            var reasons = new List<string>();
            if (partial) reasons.Add("تفريغ جزئي: " + r.OutcomeReason);
            if (shortMeter && r.DeliveredQty is { } dq) reasons.Add($"الفرق بين المحمّل والمسلّم {dq - load.Qty:0} لتر");
            r.ReviewReasons = reasons.Count > 0 ? string.Join("؛ ", reasons) : null;
            if (status == FillReportStatus.Approved) { r.ReviewedBy = reviewer; r.ReviewedAt = r.SubmittedAt.Value.AddHours(2); r.ReviewNote = "مطابق، الصور والقراءات واضحة"; }
            if (status == FillReportStatus.Returned) { r.ReviewedBy = reviewer; r.ReviewedAt = r.SubmittedAt.Value.AddHours(1); r.ReviewNote = "صورة العداد بعد التفريغ غير واضحة، أعد تصويرها وأرسل التقرير"; }
        }
        db.FillReports.Add(r);

        // photos: stations' and customers' coordinates, one without GPS for variety
        var slots = FillReportsController.Slots.Where(x => stages == 4 || x.Stage is "pre-fill" or "post-fill").ToList();
        foreach (var slot in slots)
        {
            var atStation = slot.Stage is "pre-fill" or "post-fill";
            var when = slot.Stage switch { "pre-fill" => r.PreFillAt!.Value.AddMinutes(-2), "post-fill" => r.PostFillAt!.Value.AddMinutes(-2), "arrival" => r.ArrivedAt!.Value.AddMinutes(-3), _ => r.UnloadedAt!.Value.AddMinutes(-2) };
            byte[] bytes; string ext;
            if (slot.Kind.StartsWith("meter-"))
            {
                var value = slot.Kind == "meter-before" ? before : after ?? before;
                bytes = MeterPicture.Png(value, unclear: status == FillReportStatus.Returned && slot.Kind == "meter-after"); ext = ".png";
            }
            else { bytes = Resource($"{slot.Kind}-{rnd.Next(1, 3)}.jpg"); ext = ".jpg"; }
            var (path, sha) = await store.SaveAsync(bytes, ext, CancellationToken.None);
            double? lat = atStation ? s.StationLat : cust.Lat, lng = atStation ? s.StationLng : cust.Lng;
            var noGps = slot.Kind == "tank-post-unload" && rnd.Next(4) == 0 || lat is null;
            db.Attachments.Add(new Attachment
            {
                OwnerType = "fillreport", OwnerId = r.Id, Kind = slot.Kind, FileName = slot.Label, ContentType = ext == ".png" ? "image/png" : "image/jpeg", Size = bytes.Length,
                Sha256 = sha, StoragePath = path, CapturedAt = when, UploadedAt = when.AddSeconds(20), FromGallery = false, ByUserId = driverUser, ClientId = Guid.NewGuid(),
                Lat = noGps ? null : lat + (rnd.NextDouble() - .5) * .002, Lng = noGps ? null : lng + (rnd.NextDouble() - .5) * .002,
                LocationNote = noGps ? "لا توجد إشارة GPS في المكان" : null,
            });
        }

        // history, with the times it would have had
        void Log(DateTimeOffset at, Guid by, string action, string? detail) =>
            db.Audit.Add(new AuditEntry { CompanyId = t.CompanyId, At = at, UserId = by, Entity = "fillreport", EntityId = r.Id, Action = action, Detail = detail });
        Log(startAt, driverUser, "بدء توثيق تقرير التعبئة", $"{r.No} · {r.TripNo}");
        if (r.SubmittedAt is { } sub) Log(sub, driverUser, "إرسال تقرير التعبئة", r.ReviewReasons ?? "مكتمل ومطابق");
        if (status == FillReportStatus.Approved) Log(r.ReviewedAt!.Value, reviewer, "اعتماد تقرير التعبئة", r.ReviewNote);
        if (status == FillReportStatus.Returned)
        {
            Log(r.ReviewedAt!.Value, reviewer, "إعادة التقرير للسائق", r.ReviewNote);
            db.Notifications.Add(new Notification { DriverId = t.DriverId, Text = $"أُعيد تقرير التعبئة {r.No}: {r.ReviewNote}", Ref = r.No, At = r.ReviewedAt.Value });
        }
        if (status == FillReportStatus.Approved)
            db.Notifications.Add(new Notification { DriverId = t.DriverId, Text = $"اعتُمد تقرير التعبئة {r.No}", Ref = r.No, At = r.ReviewedAt!.Value, ReadAt = r.ReviewedAt.Value.AddHours(1) });
        await db.SaveChangesAsync();
    }

    private static byte[] Resource(string name)
    {
        using var s = typeof(SampleFillReports).Assembly.GetManifestResourceStream($"FuelUp.Api.SampleMedia.{name}") ?? throw new InvalidOperationException(name);
        using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
    }
}

/// <summary>Draws a fuel meter showing a reading (seven-segment digits) as a PNG, without any imaging library.</summary>
public static class MeterPicture
{
    private const int W = 960, H = 720;

    public static byte[] Png(decimal value, bool unclear = false)
    {
        var px = new byte[W * H * 3];
        void Rect(int x0, int y0, int x1, int y1, (byte R, byte G, byte B) c)
        {
            for (var y = Math.Max(0, y0); y < Math.Min(H, y1); y++)
                for (var x = Math.Max(0, x0); x < Math.Min(W, x1); x++) { var i = (y * W + x) * 3; px[i] = c.R; px[i + 1] = c.G; px[i + 2] = c.B; }
        }
        for (var y = 0; y < H; y++) Rect(0, y, W, y + 1, ((byte)(70 + y / 12), (byte)(78 + y / 14), (byte)(90 + y / 16)));   // background
        Rect(120, 110, 840, 610, (60, 64, 72)); Rect(132, 122, 828, 598, (196, 199, 205));                                   // meter body
        Rect(170, 220, 790, 420, (32, 36, 30)); Rect(180, 230, 780, 410, (150, 182, 120));                                  // display
        Rect(200, 470, 380, 520, (40, 44, 52)); Rect(580, 470, 760, 520, (180, 30, 36));                                    // buttons
        var digits = ((long)Math.Round(value)).ToString().PadLeft(8, '0');
        var on = unclear ? ((byte)110, (byte)130, (byte)95) : ((byte)20, (byte)28, (byte)18);
        string[] seg = ["1110111", "0010010", "1011101", "1011011", "0111010", "1101011", "1101111", "1010010", "1111111", "1111011"]; // a,b,c,d,e,f,g (top, upper-left, upper-right, middle, lower-left, lower-right, bottom)
        for (var k = 0; k < digits.Length; k++)
        {
            var s = seg[digits[k] - '0']; int x = 200 + k * 72, y = 250, w = 52, t = 10, h = 140;
            if (s[0] == '1') Rect(x + t, y, x + w - t, y + t, on);
            if (s[1] == '1') Rect(x, y + t, x + t, y + h / 2, on);
            if (s[2] == '1') Rect(x + w - t, y + t, x + w, y + h / 2, on);
            if (s[3] == '1') Rect(x + t, y + h / 2 - t / 2, x + w - t, y + h / 2 + t / 2, on);
            if (s[4] == '1') Rect(x, y + h / 2, x + t, y + h - t, on);
            if (s[5] == '1') Rect(x + w - t, y + h / 2, x + w, y + h - t, on);
            if (s[6] == '1') Rect(x + t, y + h - t, x + w - t, y + h, on);
        }
        if (unclear) for (var y = 200; y < 440; y += 6) Rect(160, y, 800, y + 3, (235, 235, 230));                      // glare: the "unclear" photo
        Rect(0, H - 60, W, H, (0, 0, 0));

        // PNG: 8-bit RGB, no filter, zlib
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            for (var y = 0; y < H; y++) { z.WriteByte(0); z.Write(px, y * W * 3, W * 3); }
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length); if (BitConverter.IsLittleEndian) Array.Reverse(len);
            png.Write(len);
            var td = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            png.Write(td);
            var crc = BitConverter.GetBytes(Crc(td)); if (BitConverter.IsLittleEndian) Array.Reverse(crc);
            png.Write(crc);
        }
        var ihdr = new byte[13];
        ihdr[0] = 0; ihdr[1] = 0; ihdr[2] = W >> 8; ihdr[3] = W & 255; ihdr[4] = 0; ihdr[5] = 0; ihdr[6] = H >> 8; ihdr[7] = H & 255; ihdr[8] = 8; ihdr[9] = 2;
        Chunk("IHDR", ihdr); Chunk("IDAT", raw.ToArray()); Chunk("IEND", []);
        return png.ToArray();
    }

    private static uint Crc(byte[] b)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in b) { c ^= x; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; }
        return c ^ 0xFFFFFFFFu;
    }
}
