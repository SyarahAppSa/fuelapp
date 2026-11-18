using System.Globalization;
using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FuelUp.Api;

public record ReadinessItem(string DocType, string Owner, DateOnly? Expiry, string Text);
public record DocReadiness(List<ReadinessItem> Expired, List<ReadinessItem> Soon);

/// <summary>
/// «وثائق المركبات والسائقين»: document types, expiry status, dispatch readiness and the daily expiry alerts.
/// Permissions (decided for phase 1): dates and statuses are visible to Documents:View, Ops:View, Master:View and VehicleFile:View (office roles);
/// the copies (images / PDF) only to Documents:View; registering, renewing, types and alert settings need Documents:Edit; running the alert check by hand is for admins.
/// </summary>
public class DocumentService(FuelUpDb db)
{
    public const string OffsetsKey = "docAlertOffsets", FleetManagerKey = "docFleetManager", SeededKey = "doc_types_seeded";
    public static readonly string[] FieldKeys = ["number", "category", "issuer", "issueDate", "expiry", "image"];

    public static bool CanSeeDates(Actor a) => a.Can(Section.Documents) || a.Can(Section.Ops) || a.Can(Section.Master) || (a.Can(Section.VehicleFile) && a.Role != Role.Driver);
    public static bool CanSeeCopies(Actor a) => a.Can(Section.Documents);

    public static List<DocumentType> Defaults()
    {
        static DocField F(string k, string l, bool r = false) => new(k, l, r);
        return
        [
            new() { Code = "inspection", Name = "الفحص الدوري", AppliesTo = "vehicle", SortOrder = 1, Fields = [F("number", "رقم الشهادة", true), F("issueDate", "تاريخ الفحص"), F("expiry", "تاريخ الانتهاء", true), F("image", "صورة الشهادة")] },
            new() { Code = "opcard", Name = "كرت التشغيل", AppliesTo = "vehicle", SortOrder = 2, Fields = [F("number", "رقم الكرت", true), F("issueDate", "تاريخ الإصدار"), F("expiry", "تاريخ الانتهاء", true), F("image", "صورة الكرت")] },
            new() { Code = "insurance", Name = "التأمين", AppliesTo = "vehicle", SortOrder = 3, MandatoryForDispatch = true, Fields = [F("number", "رقم البوليصة", true), F("issuer", "شركة التأمين", true), F("issueDate", "بداية التغطية"), F("expiry", "نهاية التغطية", true), F("image", "نسخة البوليصة")] },
            new() { Code = "registration", Name = "استمارة المركبة", AppliesTo = "vehicle", SortOrder = 4, MandatoryForDispatch = true, Fields = [F("number", "رقم الاستمارة", true), F("issueDate", "تاريخ الإصدار"), F("expiry", "تاريخ الانتهاء", true), F("image", "صورة الاستمارة")] },
            new() { Code = "license", Name = "رخصة القيادة", AppliesTo = "driver", SortOrder = 5, MandatoryForDispatch = true, Fields = [F("number", "رقم الرخصة", true), F("category", "الفئة"), F("issueDate", "تاريخ الإصدار"), F("expiry", "تاريخ الانتهاء", true), F("image", "صورة الرخصة")] },
        ];
    }

    /// <summary>Default document types for every company, once (marker setting), so a type an administrator later disables stays disabled.</summary>
    public static async Task SeedAsync(FuelUpDb db, TenantContext tenant)
    {
        foreach (var company in await db.Companies.IgnoreQueryFilters().Select(c => c.Id).ToListAsync())
        {
            tenant.Set(company);
            if (await db.Settings.AnyAsync(s => s.Key == SeededKey)) continue;
            var have = await db.DocumentTypes.Select(t => t.Name).ToListAsync();
            db.DocumentTypes.AddRange(Defaults().Where(t => !have.Contains(t.Name)));
            db.Settings.Add(new Setting { Key = SeededKey, Value = DateTimeOffset.UtcNow.ToString("o") });
            await db.SaveChangesAsync();
        }
        tenant.Clear();
    }

    public async Task<int[]> OffsetsAsync(CancellationToken ct = default)
    {
        var v = await db.Settings.Where(s => s.Key == OffsetsKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        var list = (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1).Where(n => n is >= 0 and <= 365).Distinct().OrderByDescending(n => n).ToArray();
        return list.Length > 0 ? list : DocRules.DefaultOffsets;
    }

    /// <summary>The first alert window: a document is «قريبة من الانتهاء» within the largest offset (30 days by default).</summary>
    public async Task<int> WarnDaysAsync(CancellationToken ct = default) => (await OffsetsAsync(ct)).Max();

    public async Task<Guid?> FleetManagerAsync(CancellationToken ct = default) =>
        Guid.TryParse(await db.Settings.Where(s => s.Key == FleetManagerKey).Select(s => s.Value).FirstOrDefaultAsync(ct), out var g) ? g : null;

    public async Task<Dictionary<Guid, string>> OwnerNamesAsync(CancellationToken ct = default)
    {
        var plates = await db.Vehicles.Select(v => new { v.Id, v.Plate }).ToListAsync(ct);
        var names = await db.Drivers.Select(d => new { d.Id, d.Name }).ToListAsync(ct);
        return plates.Select(x => (x.Id, x.Plate)).Concat(names.Select(x => (x.Id, x.Name))).ToDictionary(x => x.Id, x => x.Item2);
    }

    public static string OwnerLabel(string kind, string name) => kind == "driver" ? $"السائق {name}" : $"المركبة {name}";

    /// <summary>Documents of the truck and the driver that are expired (mandatory types only) or close to expiry (any type).</summary>
    public async Task<DocReadiness> ReadinessAsync(Guid vehicleId, Guid driverId, CancellationToken ct = default)
    {
        var today = Ksa.Day(DateTimeOffset.UtcNow);
        var warn = await WarnDaysAsync(ct);
        var rows = await db.Documents.Where(d => d.OwnerId == vehicleId || d.OwnerId == driverId)
            .Join(db.DocumentTypes.Where(t => t.IsActive), d => d.TypeId, t => t.Id, (d, t) => new { d, t }).ToListAsync(ct);
        var plate = await db.Vehicles.Where(v => v.Id == vehicleId).Select(v => v.Plate).FirstOrDefaultAsync(ct) ?? "";
        var driver = await db.Drivers.Where(v => v.Id == driverId).Select(v => v.Name).FirstOrDefaultAsync(ct) ?? "";
        var expired = new List<ReadinessItem>(); var soon = new List<ReadinessItem>();
        foreach (var x in rows.OrderBy(x => x.t.SortOrder))
        {
            var owner = OwnerLabel(x.d.OwnerKind, x.d.OwnerKind == "driver" ? driver : plate);
            var st = DocRules.Status(x.d.Expiry, today, warn);
            var date = x.d.Expiry?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (st == DocRules.Expired && x.t.MandatoryForDispatch) expired.Add(new(x.t.Name, owner, x.d.Expiry, $"وثيقة «{x.t.Name}» {(x.d.OwnerKind == "driver" ? "للسائق" : "للمركبة")} {(x.d.OwnerKind == "driver" ? driver : plate)} منتهية بتاريخ {date}"));
            else if (st == DocRules.Soon || st == DocRules.Expired)
                soon.Add(new(x.t.Name, owner, x.d.Expiry, st == DocRules.Expired ? $"وثيقة «{x.t.Name}» {owner} منتهية بتاريخ {date}" : $"وثيقة «{x.t.Name}» {owner} تنتهي بتاريخ {date}"));
        }
        return new(expired, soon);
    }

    public static string Days(int n) => n switch { 1 => "يوم واحد", 2 => "يومين", >= 3 and <= 10 => $"{n} أيام", _ => $"{n} يومًا" };

    /// <summary>
    /// The daily expiry check for the current company. Recipients: active administrators and the «مسؤول الأسطول» chosen in settings.
    /// One alert per (document, expiry date, offset), enforced by a unique index; the notification links to /documents/{id}.
    /// </summary>
    public async Task<List<object>> RunAlertsAsync(DateOnly today, CancellationToken ct = default)
    {
        var offsets = await OffsetsAsync(ct);
        var docs = await db.Documents.Where(d => d.Expiry != null)
            .Join(db.DocumentTypes.Where(t => t.IsActive), d => d.TypeId, t => t.Id, (d, t) => new { d, t.Name }).ToListAsync(ct);
        if (docs.Count == 0) return [];
        var sent = (await db.DocumentAlerts.Select(x => new { x.DocumentId, x.Expiry, x.OffsetDays }).ToListAsync(ct)).Select(x => (x.DocumentId, x.Expiry, x.OffsetDays)).ToHashSet();
        var recipients = await db.Users.Where(u => u.IsActive && u.Role == Role.Admin).Select(u => u.Id).ToListAsync(ct);
        if (await FleetManagerAsync(ct) is { } fm && await db.Users.AnyAsync(u => u.Id == fm && u.IsActive, ct) && !recipients.Contains(fm)) recipients.Add(fm);
        var names = await OwnerNamesAsync(ct);
        var made = new List<object>();
        foreach (var x in docs)
        {
            if (DocRules.DueOffset(x.d.Expiry, today, offsets) is not { } off || sent.Contains((x.d.Id, x.d.Expiry!.Value, off))) continue;
            var left = x.d.Expiry!.Value.DayNumber - today.DayNumber;
            var date = x.d.Expiry.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var owner = OwnerLabel(x.d.OwnerKind, names.GetValueOrDefault(x.d.OwnerId) ?? "");
            var when = left > 1 ? $"تنتهي بتاريخ {date} (بعد {Days(left)})" : left == 1 ? $"تنتهي غدًا {date}" : left == 0 ? $"تنتهي اليوم {date}" : $"انتهت بتاريخ {date}";
            var text = $"تنبيه وثيقة: «{x.Name}» — {owner} {when}";
            var link = $"/documents/{x.d.Id}";
            foreach (var u in recipients) db.Notifications.Add(new Notification { UserId = u, Text = text, Ref = link, At = DateTimeOffset.UtcNow });
            db.DocumentAlerts.Add(new DocumentAlert { DocumentId = x.d.Id, Expiry = x.d.Expiry.Value, OffsetDays = off, SentAt = DateTimeOffset.UtcNow, Recipients = recipients.Count });
            made.Add(new { documentId = x.d.Id, offset = off, text, link, recipients = recipients.Count });
        }
        if (made.Count == 0) return made;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: "23505" })   // another instance sent them at the same moment
        { db.ChangeTracker.Clear(); return []; }
        return made;
    }
}

/// <summary>
/// Runs the document expiry check once at start-up and then every day at 06:00 Saudi time (Asia/Riyadh), for every company.
/// Restarts never resend: alerts already sent are recorded in document_alerts.
/// </summary>
public class DocumentAlertWorker(OwnerDb owner, ILogger<DocumentAlertWorker> log, IConfiguration cfg) : BackgroundService
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!cfg.GetValue("Documents:AlertsEnabled", true)) return;
        while (!stop.IsCancellationRequested)
        {
            try { await RunAllAsync(stop); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogError(e, "Document expiry check failed"); }
            var nowKsa = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Tz);
            var next = nowKsa.Date.AddHours(6);
            if (next <= nowKsa.DateTime) next = next.AddDays(1);
            try { await Task.Delay(next - nowKsa.DateTime, stop); } catch (OperationCanceledException) { return; }
        }
    }

    public async Task RunAllAsync(CancellationToken ct)
    {
        var tenant = new TenantContext();
        await using var db = owner.Create(tenant);
        var today = Ksa.Day(DateTimeOffset.UtcNow);
        var total = 0;
        foreach (var company in await db.Companies.IgnoreQueryFilters().Where(c => c.IsActive).Select(c => c.Id).ToListAsync(ct))
        {
            tenant.Set(company);
            total += (await new DocumentService(db).RunAlertsAsync(today, ct)).Count;
            db.ChangeTracker.Clear();
        }
        if (total > 0) log.LogInformation("Document expiry check: {Count} alert(s) sent", total);
    }
}

/// <summary>Builds a database context with the owner (migration) connection for background work across companies.</summary>
public class OwnerDb(DbContextOptions<FuelUpDb> options)
{
    public FuelUpDb Create(TenantContext tenant) => new(options, tenant);
}
