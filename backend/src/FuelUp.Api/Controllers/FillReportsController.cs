using System.ComponentModel.DataAnnotations;
using FuelUp.Api.Security;
using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FuelUp.Api.Controllers;

public record StartFillReport(Guid TripId);
public record StageInput(
    [Range(typeof(decimal), "0", "200000")] decimal? LoadedQty, [StringLength(50)] string? MeterSerial,
    [Range(typeof(decimal), "0", "999999999")] decimal? MeterBefore, [Range(typeof(decimal), "0", "999999999")] decimal? MeterAfter,
    [StringLength(60)] string? TankState, UnloadOutcome? Outcome, [StringLength(1000)] string? OutcomeReason, DateTimeOffset? At);
public record ReviewInput([StringLength(1000)] string? Note);
public record FieldEdit([Required, RegularExpression("^(loadedQty|meterBefore|meterAfter|secretMeterSerial|tankState)$")] string Field,
    [Required, StringLength(60)] string Value, [Required, StringLength(500, MinimumLength = 3)] string Reason);

/// <summary>
/// «تقرير التعبئة»: the driver documents a tanker trip in four stages with six photos and the secret meter readings;
/// operations review, approve or return it. All rules are enforced here; the app and the dashboard only guide.
/// </summary>
[ApiController, Route("api/fill-reports")]
public class FillReportsController(FuelUpDb db, ActorAccessor actor, AuditWriter audit, FileStore store, TripService trips, SecurityEvents events) : ControllerBase
{
    /// <summary>Photo slots in report order: (kind, stage, label, message shown when missing).</summary>
    public static readonly (string Kind, string Stage, string Label, string Missing)[] Slots =
    [
        ("tank-pre-fill", "pre-fill", "الصهريج قبل التعبئة", "صوّر الصهريج قبل التعبئة"),
        ("tank-post-fill", "post-fill", "الصهريج بعد التعبئة", "صوّر الصهريج بعد التعبئة"),
        ("tank-arrival", "arrival", "الصهريج ممتلئ عند العميل", "صوّر الصهريج ممتلئًا عند العميل قبل التفريغ"),
        ("meter-before", "arrival", "العداد السري قبل التفريغ", "صوّر قراءة العداد قبل التفريغ"),
        ("tank-post-unload", "post-unload", "الصهريج بعد التفريغ", "صوّر الصهريج بعد التفريغ"),
        ("meter-after", "post-unload", "العداد السري بعد التفريغ", "صوّر قراءة العداد بعد التفريغ"),
    ];
    private static readonly string[] Stages = ["pre-fill", "post-fill", "arrival", "post-unload"];
    private const long MaxBytes = 10 * 1024 * 1024;

    // ── driver side ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Trips assigned to me (open ones and the last 14 days) with the state of their report.</summary>
    [HttpGet("mine")]
    public async Task<IActionResult> Mine()
    {
        var a = await actor.GetAsync();
        if (a.DriverId is not { } did) throw new ForbiddenException();
        var since = DateTimeOffset.UtcNow.AddDays(-14);
        var list = await db.Trips.Where(t => t.DriverId == did && t.Status != TripStatus.Cancelled && (t.CreatedAt > since || (t.Status != TripStatus.Approved)))
            .OrderByDescending(t => t.CreatedAt).Take(50).ToListAsync();
        var ids = list.Select(t => t.Id).ToList();
        var reports = await db.FillReports.Where(r => ids.Contains(r.TripId)).ToDictionaryAsync(r => r.TripId);
        return Ok(list.Select(t =>
        {
            var r = reports.GetValueOrDefault(t.Id);
            return new
            {
                tripId = t.Id, tripNo = t.No, plate = t.Snapshot?.Plate, customer = t.Snapshot?.CustomerName, site = t.Snapshot?.Site, qtyRequested = t.QtyRequested,
                product = t.Product, loadingSite = t.Snapshot?.StationName, tripStatus = t.Status.ToString(),
                report = r is null ? null : new { r.Id, r.No, status = r.Status, stagesDone = StagesDone(r), r.ReviewNote },
            };
        }));
    }

    /// <summary>«ابدأ التوثيق»: creates the report for a trip (or returns it if it already exists — safe to retry offline).</summary>
    [HttpPost("start")]
    public async Task<IActionResult> Start(StartFillReport r)
    {
        var a = await actor.GetAsync();
        var existing = await db.FillReports.FirstOrDefaultAsync(x => x.TripId == r.TripId);
        if (existing != null) { await RequireAccess(a, existing, write: true); return Ok(await Detail(a, existing)); }
        var t = await trips.FindAsync(r.TripId) ?? throw new NotFoundException("الرحلة");
        if (!(a.Can(Section.Driver, PermissionLevel.Edit) && a.DriverId == t.DriverId)) throw new ForbiddenException();
        if (t.Status == TripStatus.Cancelled) throw new DomainException("هذه الرحلة ملغاة.");
        var vehicleId = t.VehicleId ?? await db.VehicleAssignments.Where(x => x.DriverId == t.DriverId && x.To == null).Select(x => (Guid?)x.VehicleId).FirstOrDefaultAsync();
        var vehicle = vehicleId is { } vid ? await db.Vehicles.FirstOrDefaultAsync(v => v.Id == vid) : null;
        var meter = vehicle?.MeterId is { } mid ? await db.Meters.FirstOrDefaultAsync(m => m.Id == mid) : null;
        var s = t.Snapshot;
        var rep = new FillReport
        {
            No = "FR-" + (t.No.StartsWith("FU-") ? t.No[3..] : t.No), TripId = t.Id, DriverId = t.DriverId, StartedBy = a.UserId,
            TripNo = t.No, Plate = vehicle?.Plate ?? s?.Plate ?? "", TrailerPlate = vehicle?.TrailerPlate, DriverName = s?.DriverName ?? "",
            CustomerName = s?.CustomerName ?? "", DeliverySite = s?.Site ?? "", LoadingSite = s?.StationName ?? "", Product = t.Product, QtyRequested = t.QtyRequested,
            SecretMeterSerial = meter?.Serial, LoadedQty = t.Loading?.Qty, LoadedFromTrip = t.Loading != null,
        };
        db.FillReports.Add(rep);
        audit.Write(a, "fillreport", rep.Id, "بدء توثيق تقرير التعبئة", $"{rep.No} · {rep.TripNo}");
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: "23505" })   // two taps / retry at the same instant
        {
            db.ChangeTracker.Clear();
            rep = await db.FillReports.FirstAsync(x => x.TripId == r.TripId);
        }
        return Ok(await Detail(a, rep));
    }

    /// <summary>One photo for a slot. Retrying with the same clientId (after a network loss) returns the stored photo: no duplicates.</summary>
    [HttpPost("{id:guid}/photos"), RequestSizeLimit(MaxBytes + 64 * 1024), RequestFormLimits(MultipartBodyLengthLimit = MaxBytes + 64 * 1024), EnableRateLimiting("upload")]
    public async Task<IActionResult> Photo(Guid id, [FromForm] IFormFile file, [FromForm, Required] string kind, [FromForm] Guid clientId,
        [FromForm] DateTimeOffset? capturedAt, [FromForm, Range(-90, 90)] double? lat, [FromForm, Range(-180, 180)] double? lng, [FromForm, StringLength(200)] string? locationNote)
    {
        var a = await actor.GetAsync();
        var rep = await Get(id);
        await RequireAccess(a, rep, write: true, driverOnly: true);
        if (clientId == Guid.Empty) throw new DomainException("معرّف الصورة مطلوب.");
        var dup = await db.Attachments.FirstOrDefaultAsync(x => x.OwnerId == id && x.ClientId == clientId);
        if (dup != null) return Ok(new { dup.Id, dup.Kind, duplicate = true });
        if (rep.Status is not (FillReportStatus.Draft or FillReportStatus.Returned)) throw new DomainException("أُرسل التقرير؛ لا يمكن تغيير صوره إلا إذا أعاده فريق العمليات.");
        var slot = Slots.FirstOrDefault(s => s.Kind == kind);
        if (slot.Kind is null) throw new DomainException("خانة الصورة غير معروفة.");
        if (file.Length == 0 || file.Length > MaxBytes) throw new DomainException("حجم الصورة يجب ألا يتجاوز 10 ميجابايت.");
        await using var ms = new MemoryStream(); await file.CopyToAsync(ms, HttpContext.RequestAborted);
        var bytes = ms.ToArray();
        if (FileSignatures.Detect(bytes) is not var (contentType, ext) || !contentType.StartsWith("image/"))
        {
            await events.RecordAsync("upload.rejected", Severity.Medium, "رُفض ملف في تقرير التعبئة لأن محتواه ليس صورة.", a.CompanyId, a.UserId);
            throw new DomainException("الملف ليس صورة. صوّر من كاميرا التطبيق.");
        }
        var (path, sha) = await store.SaveAsync(bytes, ext, HttpContext.RequestAborted);
        var att = new Attachment
        {
            OwnerType = "fillreport", OwnerId = id, Kind = kind, FileName = slot.Label, ContentType = contentType, Size = bytes.Length, Sha256 = sha, StoragePath = path,
            CapturedAt = capturedAt, UploadedAt = DateTimeOffset.UtcNow, FromGallery = capturedAt is not { } c || DateTimeOffset.UtcNow - c > TimeSpan.FromHours(48),
            Lat = lat, Lng = lng, LocationNote = lat is null ? (string.IsNullOrWhiteSpace(locationNote) ? "لم يُرسل الموقع" : locationNote.Trim()) : null,
            ByUserId = a.UserId, ClientId = clientId,
        };
        db.Attachments.Add(att);
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: "23505" })
        {
            db.ChangeTracker.Clear();
            var again = await db.Attachments.FirstAsync(x => x.OwnerId == id && x.ClientId == clientId);
            return Ok(new { again.Id, again.Kind, duplicate = true });
        }
        return Ok(new { att.Id, att.Kind, duplicate = false });
    }

    /// <summary>Confirms a stage. Checks the order of stages, the required photos and the readings, with the exact missing item in the answer.</summary>
    [HttpPost("{id:guid}/stages/{stage}")]
    public async Task<IActionResult> Stage(Guid id, string stage, StageInput i)
    {
        var a = await actor.GetAsync();
        var rep = await Get(id);
        await RequireAccess(a, rep, write: true, driverOnly: true);
        if (rep.Status is not (FillReportStatus.Draft or FillReportStatus.Returned)) throw new DomainException("أُرسل التقرير بالفعل.");
        var idx = Array.IndexOf(Stages, stage);
        if (idx < 0) throw new NotFoundException("المرحلة");
        if (idx > 0 && !Done(rep, Stages[idx - 1])) throw new DomainException("أكمل المرحلة السابقة أولًا.");
        var photos = await Kinds(id);
        var at = i.At is { } t && t > rep.StartedAt.AddHours(-1) && t < DateTimeOffset.UtcNow.AddMinutes(5) ? t : DateTimeOffset.UtcNow;   // offline: time of the tap, if plausible
        void Need(string kind) { if (!photos.Contains(kind)) throw new DomainException(Slots.First(s => s.Kind == kind).Missing + "."); }

        switch (stage)
        {
            case "pre-fill":
                Need("tank-pre-fill"); rep.PreFillAt = at; break;
            case "post-fill":
                Need("tank-post-fill");
                var qty = i.LoadedQty ?? rep.LoadedQty;
                if (qty is not > 0) throw new DomainException("أدخل الكمية المحمّلة باللتر.");
                if (i.LoadedQty is { } q && q != rep.LoadedQty) { rep.LoadedQty = q; rep.LoadedFromTrip = false; }
                rep.PostFillAt = at; break;
            case "arrival":
                Need("tank-arrival"); Need("meter-before");
                if (i.MeterBefore is null) throw new DomainException("أدخل قراءة العداد قبل التفريغ.");
                rep.MeterBefore = i.MeterBefore;
                if (!string.IsNullOrWhiteSpace(i.MeterSerial)) rep.SecretMeterSerial = i.MeterSerial.Trim();
                rep.ArrivedAt = at; break;
            case "post-unload":
                var outcome = i.Outcome ?? UnloadOutcome.Full;
                if (outcome != UnloadOutcome.Full && string.IsNullOrWhiteSpace(i.OutcomeReason)) throw new DomainException(outcome == UnloadOutcome.Partial ? "اكتب سبب التفريغ الجزئي." : "اكتب سبب تعذّر التفريغ.");
                if (outcome != UnloadOutcome.Failed)
                {
                    Need("tank-post-unload"); Need("meter-after");
                    if (i.MeterAfter is null) throw new DomainException("أدخل قراءة العداد بعد التفريغ.");
                }
                if (string.IsNullOrWhiteSpace(i.TankState)) throw new DomainException("حدّد حالة الصهريج بعد التفريغ.");
                (rep.Outcome, rep.OutcomeReason, rep.MeterAfter, rep.TankState, rep.UnloadedAt) = (outcome, i.OutcomeReason?.Trim(), i.MeterAfter, i.TankState.Trim(), at);
                break;
        }
        await db.SaveChangesAsync();
        return Ok(await Detail(a, rep));
    }

    /// <summary>Sends the report. Anything that needs a human look sends it to operations as «بحاجة إلى مراجعة». Safe to retry.</summary>
    [HttpPost("{id:guid}/submit")]
    public async Task<IActionResult> Submit(Guid id)
    {
        var a = await actor.GetAsync();
        var rep = await Get(id);
        await RequireAccess(a, rep, write: true, driverOnly: true);
        if (rep.Status is FillReportStatus.Submitted or FillReportStatus.NeedsReview or FillReportStatus.Approved) return Ok(await Detail(a, rep));   // retried after a network loss
        foreach (var s in Stages) if (!Done(rep, s)) throw new DomainException(s switch
        {
            "pre-fill" => "وثّق مرحلة ما قبل التعبئة.", "post-fill" => "وثّق مرحلة ما بعد التعبئة.", "arrival" => "وثّق الوصول وقراءة العداد قبل التفريغ.", _ => "وثّق مرحلة ما بعد التفريغ.",
        });
        var reasons = await ReviewReasons(rep);
        rep.ReviewReasons = reasons.Count == 0 ? null : string.Join("؛ ", reasons);
        rep.Status = reasons.Count == 0 ? FillReportStatus.Submitted : FillReportStatus.NeedsReview;
        rep.SubmittedAt = DateTimeOffset.UtcNow;
        audit.Write(a, "fillreport", rep.Id, "إرسال تقرير التعبئة", rep.ReviewReasons ?? "مكتمل ومطابق");
        var reviewers = await db.Users.Where(u => u.IsActive && u.Permissions.Any(p => p.Section == Section.FillReports && p.Level == PermissionLevel.Edit)).Select(u => u.Id).ToListAsync();
        foreach (var u in reviewers)
            db.Notifications.Add(new Notification { UserId = u, Text = $"تقرير تعبئة {rep.No} من {rep.DriverName}" + (rep.Status == FillReportStatus.NeedsReview ? " — بحاجة إلى مراجعة" : ""), Ref = rep.No, At = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return Ok(await Detail(a, rep));
    }

    // ── operations side ───────────────────────────────────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> List([FromQuery, StringLength(60)] string? q, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] FillReportStatus? status, [FromQuery] int page = 1)
    {
        var a = await actor.GetAsync(); a.Require(Section.FillReports, PermissionLevel.View);
        var query = db.FillReports.AsQueryable();
        if (status is { } st) query = query.Where(x => x.Status == st);
        if (from is { } f) { var fs = new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3)); query = query.Where(x => x.StartedAt >= fs); }
        if (to is { } t2) { var te = new DateTimeOffset(t2.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3)).AddDays(1); query = query.Where(x => x.StartedAt < te); }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = "%" + q.Trim().Replace("%", "").Replace("_", "") + "%";
            query = query.Where(x => EF.Functions.ILike(x.No, like) || EF.Functions.ILike(x.TripNo, like) || EF.Functions.ILike(x.Plate, like) || EF.Functions.ILike(x.DriverName, like) || EF.Functions.ILike(x.CustomerName, like));
        }
        var total = await query.CountAsync();
        var rows = await query.OrderByDescending(x => x.StartedAt).Skip((Math.Max(page, 1) - 1) * 30).Take(30).ToListAsync();
        var tol = await trips.ToleranceAsync();
        return Ok(new
        {
            total, page,
            items = rows.Select(r => new
            {
                r.Id, r.No, r.TripNo, r.Plate, r.DriverName, r.CustomerName, r.DeliverySite, status = r.Status, r.StartedAt, r.SubmittedAt, r.QtyRequested, r.LoadedQty,
                deliveredQty = r.DeliveredQty, difference = Diff(r), flagged = r.Status == FillReportStatus.NeedsReview || (Diff(r) is { } d && Math.Abs(d) > tol.For(r.LoadedQty ?? 0)), stagesDone = StagesDone(r),
            }),
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get_(Guid id)
    {
        var a = await actor.GetAsync();
        var rep = await Get(id);
        await RequireAccess(a, rep, write: false);
        return Ok(await Detail(a, rep));
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, ReviewInput r)
    {
        var a = await actor.GetAsync(); a.Require(Section.FillReports, PermissionLevel.Edit);
        var rep = await Get(id);
        if (rep.Status is not (FillReportStatus.Submitted or FillReportStatus.NeedsReview)) throw new DomainException("لا يُعتمد إلا تقرير مُرسل.");
        if (rep.Status == FillReportStatus.NeedsReview && string.IsNullOrWhiteSpace(r.Note)) throw new DomainException("اكتب قرار المراجعة قبل الاعتماد.");
        (rep.Status, rep.ReviewedBy, rep.ReviewedAt, rep.ReviewNote) = (FillReportStatus.Approved, a.UserId, DateTimeOffset.UtcNow, r.Note?.Trim());
        audit.Write(a, "fillreport", rep.Id, "اعتماد تقرير التعبئة", r.Note);
        await db.SaveChangesAsync();
        return Ok(await Detail(a, rep));
    }

    /// <summary>Back to the driver with a precise note; he can retake photos and correct readings, then send again.</summary>
    [HttpPost("{id:guid}/return")]
    public async Task<IActionResult> Return(Guid id, ReviewInput r)
    {
        var a = await actor.GetAsync(); a.Require(Section.FillReports, PermissionLevel.Edit);
        if (string.IsNullOrWhiteSpace(r.Note)) throw new DomainException("اكتب ما المطلوب من السائق تحديدًا.");
        var rep = await Get(id);
        if (rep.Status is not (FillReportStatus.Submitted or FillReportStatus.NeedsReview)) throw new DomainException("لا يُعاد إلا تقرير مُرسل.");
        (rep.Status, rep.ReviewedBy, rep.ReviewedAt, rep.ReviewNote) = (FillReportStatus.Returned, a.UserId, DateTimeOffset.UtcNow, r.Note.Trim());
        audit.Write(a, "fillreport", rep.Id, "إعادة التقرير للسائق", r.Note);
        audit.Notify(rep.DriverId, $"أُعيد تقرير التعبئة {rep.No}: {r.Note.Trim()}", rep.No);
        await db.SaveChangesAsync();
        return Ok(await Detail(a, rep));
    }

    /// <summary>Correction after sending: field, original value, new value, who, when and why are kept in the audit log.</summary>
    [HttpPut("{id:guid}/fields")]
    public async Task<IActionResult> Edit(Guid id, FieldEdit e)
    {
        var a = await actor.GetAsync(); a.Require(Section.FillReports, PermissionLevel.Edit);
        var rep = await Get(id);
        if (rep.Status is not (FillReportStatus.Submitted or FillReportStatus.NeedsReview)) throw new DomainException("التعديل متاح للتقارير المرسلة قبل اعتمادها.");
        decimal Num() => decimal.TryParse(e.Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0 && v < 1_000_000_000 ? v : throw new DomainException("القيمة يجب أن تكون رقمًا صحيحًا.");
        string? old;
        static string? N(decimal? v) => v?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        switch (e.Field)
        {
            case "loadedQty": old = N(rep.LoadedQty); rep.LoadedQty = Num(); break;
            case "meterBefore": old = N(rep.MeterBefore); rep.MeterBefore = Num(); break;
            case "meterAfter": old = N(rep.MeterAfter); rep.MeterAfter = Num(); break;
            case "secretMeterSerial": old = rep.SecretMeterSerial; rep.SecretMeterSerial = e.Value.Trim(); break;
            default: old = rep.TankState; rep.TankState = e.Value.Trim(); break;
        }
        audit.Write(a, "fillreport", rep.Id, $"تعديل {FieldName(e.Field)}", e.Reason.Trim(), old, e.Field is "secretMeterSerial" or "tankState" ? e.Value.Trim() : N(Num()));
        var reasons = await ReviewReasons(rep);
        rep.ReviewReasons = reasons.Count == 0 ? null : string.Join("؛ ", reasons);
        await db.SaveChangesAsync();
        return Ok(await Detail(a, rep));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<FillReport> Get(Guid id) => await db.FillReports.FirstOrDefaultAsync(x => x.Id == id) ?? throw new NotFoundException("التقرير");

    /// <summary>The driver of the report, or operations staff with the Fill Reports permission (read; edit for staff actions).</summary>
    private Task RequireAccess(Actor a, FillReport r, bool write, bool driverOnly = false)
    {
        var isDriver = a.Can(Section.Driver, PermissionLevel.Edit) && a.DriverId == r.DriverId;
        if (isDriver) return Task.CompletedTask;
        if (!driverOnly && a.Can(Section.FillReports, write ? PermissionLevel.Edit : PermissionLevel.View)) return Task.CompletedTask;
        throw new ForbiddenException();
    }

    private async Task<HashSet<string>> Kinds(Guid id) => (await db.Attachments.Where(x => x.OwnerType == "fillreport" && x.OwnerId == id).Select(x => x.Kind).ToListAsync()).ToHashSet();

    private static bool Done(FillReport r, string stage) => stage switch
    {
        "pre-fill" => r.PreFillAt != null, "post-fill" => r.PostFillAt != null, "arrival" => r.ArrivedAt != null, _ => r.UnloadedAt != null,
    };
    private static int StagesDone(FillReport r) => Stages.Count(s => Done(r, s));
    private static decimal? Diff(FillReport r) => r.DeliveredQty is { } d && r.LoadedQty is { } l ? d - l : null;
    private static string FieldName(string f) => f switch
    {
        "loadedQty" => "الكمية المحمّلة", "meterBefore" => "قراءة العداد قبل التفريغ", "meterAfter" => "قراءة العداد بعد التفريغ", "secretMeterSerial" => "الرقم التسلسلي للعداد", _ => "حالة الصهريج",
    };

    private async Task<List<string>> ReviewReasons(FillReport r)
    {
        var reasons = new List<string>();
        if (r.Outcome == UnloadOutcome.Partial) reasons.Add("تفريغ جزئي: " + r.OutcomeReason);
        if (r.Outcome == UnloadOutcome.Failed) reasons.Add("تعذّر التفريغ: " + r.OutcomeReason);
        if (r.MeterBefore is { } b && r.MeterAfter is { } af && af < b) reasons.Add("قراءة العداد بعد التفريغ أقل من قراءته قبل التفريغ");
        if (r.Outcome == UnloadOutcome.Full && r.TankState != "فارغ") reasons.Add($"حالة الصهريج بعد التفريغ: {r.TankState}");
        var tol = await trips.ToleranceAsync();
        if (r.Outcome == UnloadOutcome.Full && Diff(r) is { } d && Math.Abs(d) > tol.For(r.LoadedQty ?? 0))
            reasons.Add($"الفرق بين المحمّل والمسلّم {d:+#,0;-#,0} لتر (المسموح {tol.For(r.LoadedQty ?? 0):#,0})");
        return reasons;
    }

    private async Task<object> Detail(Actor a, FillReport r)
    {
        var photos = await db.Attachments.Where(x => x.OwnerType == "fillreport" && x.OwnerId == r.Id).OrderBy(x => x.UploadedAt).ToListAsync();
        var latest = photos.GroupBy(p => p.Kind).ToDictionary(g => g.Key, g => g.Last());
        var driver = await db.Drivers.FirstOrDefaultAsync(d => d.Id == r.DriverId);
        // full identity and phone only for the driver himself and for users allowed to manage users or approve reports
        var seeAll = a.DriverId == r.DriverId || a.Can(Section.Users) || a.Can(Section.FillReports, PermissionLevel.Edit);
        static string? Mask(string? v) => string.IsNullOrEmpty(v) ? v : (v.Length <= 4 ? "••••" : new string('•', v.Length - 4) + v[^4..]);
        var tol = await trips.ToleranceAsync();
        var history = a.Can(Section.FillReports) ? await db.Audit.Where(x => x.Entity == "fillreport" && x.EntityId == r.Id).OrderBy(x => x.Id).ToListAsync() : [];
        var names = history.Count > 0 ? await db.Users.Select(u => new { u.Id, u.DisplayName }).ToDictionaryAsync(u => u.Id, u => u.DisplayName) : [];
        return new
        {
            r.Id, r.No, r.TripId, r.TripNo, status = r.Status, r.StartedAt, r.SubmittedAt,
            r.Plate, r.TrailerPlate, r.CustomerName, r.DeliverySite, r.LoadingSite, r.Product,
            driver = new { name = r.DriverName, nationalId = seeAll ? driver?.NationalId : Mask(driver?.NationalId), phone = seeAll ? driver?.Phone : Mask(driver?.Phone), masked = !seeAll },
            stages = new
            {
                preFill = new { done = r.PreFillAt != null, at = r.PreFillAt },
                postFill = new { done = r.PostFillAt != null, at = r.PostFillAt, r.LoadedQty, r.LoadedFromTrip },
                arrival = new { done = r.ArrivedAt != null, at = r.ArrivedAt, r.MeterBefore, r.SecretMeterSerial },
                postUnload = new { done = r.UnloadedAt != null, at = r.UnloadedAt, r.MeterAfter, outcome = r.Outcome, r.OutcomeReason, r.TankState },
            },
            quantities = new { requested = r.QtyRequested, loaded = r.LoadedQty, delivered = r.DeliveredQty, difference = Diff(r), allowed = tol.For(r.LoadedQty ?? 0) },
            photos = Slots.Select(s => latest.TryGetValue(s.Kind, out var p)
                ? new { kind = s.Kind, stage = s.Stage, label = s.Label, id = (Guid?)p.Id, p.CapturedAt, p.UploadedAt, p.Lat, p.Lng, p.LocationNote, p.FromGallery, retakes = photos.Count(x => x.Kind == s.Kind) - 1 }
                : new { kind = s.Kind, stage = s.Stage, label = s.Label, id = (Guid?)null, CapturedAt = (DateTimeOffset?)null, UploadedAt = DateTimeOffset.MinValue, Lat = (double?)null, Lng = (double?)null, LocationNote = (string?)null, FromGallery = false, retakes = 0 }),
            missing = Slots.Where(s => !latest.ContainsKey(s.Kind) && !(s.Stage == "post-unload" && r.Outcome == UnloadOutcome.Failed)).Select(s => s.Missing),
            r.ReviewReasons, r.ReviewNote, r.ReviewedAt, reviewedBy = r.ReviewedBy is { } rb ? names.GetValueOrDefault(rb) : null,
            history = history.Select(h => new { h.At, h.Action, h.Detail, h.OldValue, h.NewValue, by = h.UserId is { } u ? names.GetValueOrDefault(u) : null }),
        };
    }
}
