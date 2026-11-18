using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FuelUp.Api;

/// <summary>
/// Sample data for «توثيق التعبئة» (Seed__Sample only, once: marker "sample_loadingdocs"; also runs on databases that already have the
/// sample fill reports): a few reports get the number the driver typed from the Aramco document / station invoice, and one sent report of
/// the demo driver gets an open «طلب إعادة التصوير» on the tank after filling, so the app shows the request and the report shows «يحتاج مراجعة».
/// </summary>
public static class SampleLoadingDocs
{
    private const string Marker = "sample_loadingdocs";

    public static async Task RunAsync(FuelUpDb db, IConfiguration cfg, ILogger log)
    {
        if (await db.Settings.AnyAsync(s => s.Key == Marker) || !await db.Settings.AnyAsync(s => s.Key == "sample_fillreports")) return;
        var reviewer = await db.Users.Where(u => u.Role == Role.OpsSupervisor).OrderBy(u => u.CreatedAt).FirstOrDefaultAsync()
            ?? await db.Users.Where(u => u.Role == Role.Admin).OrderBy(u => u.CreatedAt).FirstAsync();
        var reports = await db.FillReports.OrderByDescending(r => r.StartedAt).Take(12).ToListAsync();
        var tripIds = reports.Select(r => r.TripId).ToList();
        var types = (await db.Trips.Where(t => tripIds.Contains(t.Id)).ToListAsync()).ToDictionary(t => t.Id, t => t.Snapshot?.StationType ?? StationType.Station);
        var n = 0;
        foreach (var r in reports.Where(r => r.PostFillAt != null).Take(5))
            r.LoadingDocNo = (types.GetValueOrDefault(r.TripId) == StationType.Aramco ? "ARM-" : "INV-") + (48210 + ++n * 37);

        // the demo driver's report under review: the supervisor asks for the tank after filling again
        var demo = await db.Users.FirstOrDefaultAsync(u => u.Username == DemoKit.DriverUser(cfg));
        var target = demo?.DriverId is { } did
            ? await db.FillReports.Where(r => r.DriverId == did && r.Status == FillReportStatus.NeedsReview).OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync()
            : null;
        target ??= reports.FirstOrDefault(r => r.Status is FillReportStatus.Submitted or FillReportStatus.NeedsReview);
        if (target != null && !await db.RetakeRequests.AnyAsync(x => x.ReportId == target.Id && x.ResolvedAt == null))
        {
            const string note = "الصورة مظلمة ولا يظهر مستوى الوقود في الفتحة؛ صوّر الصهريج بعد التعبئة مرة أخرى في الضوء";
            var at = DateTimeOffset.UtcNow.AddHours(-2);
            db.RetakeRequests.Add(new RetakeRequest { ReportId = target.Id, Kind = LoadingDocRules.PostFill, Note = note, RequestedBy = reviewer.Id, RequestedAt = at });
            db.Audit.Add(new AuditEntry { CompanyId = target.CompanyId, At = at, UserId = reviewer.Id, Entity = "fillreport", EntityId = target.Id, Action = "طلب إعادة التصوير", Detail = $"الصهريج بعد التعبئة — {note}" });
            db.Notifications.Add(new Notification { DriverId = target.DriverId, Text = $"طلب إعادة تصوير «الصهريج بعد التعبئة» في تقرير التعبئة {target.No} (الرحلة {target.TripNo}): {note}", Ref = target.No, At = at });
        }
        db.Settings.Add(new Setting { Key = Marker, Value = DateTimeOffset.UtcNow.ToString("o") });
        await db.SaveChangesAsync();
        log.LogWarning("Sample loading documentation: {Count} document numbers, retake request on {Report}", n, target?.No ?? "—");
    }
}
