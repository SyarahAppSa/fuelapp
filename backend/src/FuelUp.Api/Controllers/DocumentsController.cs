using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;
using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FuelUp.Api.Controllers;

public record DocInput(Guid TypeId, Guid OwnerId, [StringLength(60)] string? Number, [StringLength(40)] string? Category, [StringLength(100)] string? Issuer,
    DateOnly? IssueDate, DateOnly? Expiry, [StringLength(500)] string? Note);
public record DocRenewInput([StringLength(60)] string? Number, [StringLength(40)] string? Category, [StringLength(100)] string? Issuer, DateOnly? IssueDate, DateOnly? Expiry, [StringLength(500)] string? Note);
public record DocCorrectInput([StringLength(60)] string? Number, [StringLength(40)] string? Category, [StringLength(100)] string? Issuer, DateOnly? IssueDate, DateOnly? Expiry,
    [Required, StringLength(500, MinimumLength = 3)] string Reason);
public record DocTypeInput([Required, StringLength(80, MinimumLength = 2)] string Name, [Required, RegularExpression("^(vehicle|driver)$")] string AppliesTo,
    [MaxLength(6)] List<DocField> Fields, bool MandatoryForDispatch, bool IsActive = true, [Range(0, 1000)] int SortOrder = 100);
public record DocSettingsInput([MaxLength(8)] List<int> Offsets, Guid? FleetManagerUserId);

/// <summary>
/// «وثائق المركبات والسائقين»: one screen to register fleet documents and follow their expiry. Each document belongs to a vehicle or a driver;
/// a renewal adds a version (previous versions and their copies are kept, nothing is deleted). See <see cref="DocumentService"/> for permissions.
/// </summary>
[ApiController, Route("api/documents")]
public class DocumentsController(FuelUpDb db, ActorAccessor actor, AuditWriter audit, DocumentService svc) : ControllerBase
{
    private async Task<Actor> Viewer() { var a = await actor.GetAsync(); if (!DocumentService.CanSeeDates(a)) throw new ForbiddenException(); return a; }
    private async Task<Actor> Editor() { var a = await actor.GetAsync(); a.Require(Section.Documents, PermissionLevel.Edit); return a; }

    [HttpGet("meta")]
    public async Task<IActionResult> Meta()
    {
        var a = await Viewer();
        var types = await db.DocumentTypes.OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync();
        var edit = a.Can(Section.Documents, PermissionLevel.Edit);
        return Ok(new
        {
            types, offsets = await svc.OffsetsAsync(), warnDays = await svc.WarnDaysAsync(), fleetManagerUserId = await svc.FleetManagerAsync(),
            users = edit ? await db.Users.Where(u => u.IsActive && u.Role != Role.Driver).OrderBy(u => u.DisplayName).Select(u => new { u.Id, u.DisplayName, u.Username, role = u.Role }).ToListAsync() : null,
            canEdit = edit, canSeeCopies = DocumentService.CanSeeCopies(a), isAdmin = a.Role == Role.Admin,
            statuses = new[] { DocRules.Valid, DocRules.Soon, DocRules.Expired, DocRules.NoDate }.Select(s => new { value = s, label = DocRules.Label(s) }),
        });
    }

    // ── report ────────────────────────────────────────────────────────────────────────────────────────────────────
    private record Row(Guid Id, Guid TypeId, string TypeName, bool Mandatory, string OwnerKind, Guid OwnerId, string OwnerName, string? Number, string? Category, string? Issuer,
        DateOnly? IssueDate, DateOnly? Expiry, int? DaysLeft, string Status, string StatusLabel, int Version, bool HasCopy, DateTimeOffset UpdatedAt);

    private async Task<List<Row>> RowsAsync(string? q, Guid? typeId, string? status, string? ownerKind, Guid? ownerId)
    {
        var today = Ksa.Day(DateTimeOffset.UtcNow);
        var warn = await svc.WarnDaysAsync();
        var query = db.Documents.AsQueryable();
        if (typeId is { } t) query = query.Where(d => d.TypeId == t);
        if (ownerId is { } o) query = query.Where(d => d.OwnerId == o);
        if (ownerKind is "vehicle" or "driver") query = query.Where(d => d.OwnerKind == ownerKind);
        var docs = await query.ToListAsync();
        var types = await db.DocumentTypes.ToDictionaryAsync(x => x.Id);
        var names = await svc.OwnerNamesAsync();
        var vids = docs.Select(d => d.CurrentVersionId).ToList();
        var withCopy = (await db.Attachments.Where(x => x.OwnerType == "docversion" && vids.Contains(x.OwnerId)).Select(x => x.OwnerId).Distinct().ToListAsync()).ToHashSet();
        var rows = docs.Select(d =>
        {
            var st = DocRules.Status(d.Expiry, today, warn);
            var type = types.GetValueOrDefault(d.TypeId);
            return new Row(d.Id, d.TypeId, type?.Name ?? "", type?.MandatoryForDispatch ?? false, d.OwnerKind, d.OwnerId, names.GetValueOrDefault(d.OwnerId) ?? "", d.Number, d.Category, d.Issuer,
                d.IssueDate, d.Expiry, d.Expiry is { } e ? e.DayNumber - today.DayNumber : null, st, DocRules.Label(st), d.Version, withCopy.Contains(d.CurrentVersionId), d.UpdatedAt);
        });
        if (!string.IsNullOrWhiteSpace(q)) { var k = q.Trim(); rows = rows.Where(r => r.OwnerName.Contains(k, StringComparison.OrdinalIgnoreCase) || (r.Number?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false)); }
        if (!string.IsNullOrWhiteSpace(status)) rows = rows.Where(r => r.Status == status);
        return rows.OrderBy(r => r.Expiry is null).ThenBy(r => r.Expiry).ThenBy(r => r.TypeName).ToList();   // nearest expiry first, rows without a date last
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery, StringLength(60)] string? q, [FromQuery] Guid? typeId, [FromQuery, StringLength(10)] string? status, [FromQuery] string? ownerKind,
        [FromQuery] Guid? ownerId, [FromQuery] string? format)
    {
        var a = await Viewer();
        var rows = await RowsAsync(q, typeId, status, ownerKind, ownerId);
        var summary = new { total = rows.Count, expired = rows.Count(r => r.Status == DocRules.Expired), soon = rows.Count(r => r.Status == DocRules.Soon), noDate = rows.Count(r => r.Status == DocRules.NoDate) };
        if (format != "xlsx") return Ok(new { summary, rows });

        a.Require(Section.Export, PermissionLevel.Edit);
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("الوثائق");
        ws.RightToLeft = true;
        string[] head = ["الوثيقة", "اللوحة / السائق", "يخص", "الرقم", "الفئة / الجهة", "تاريخ الإصدار", "تاريخ الانتهاء", "الأيام المتبقية", "الحالة", "إلزامية للتشغيل", "الإصدار"];
        for (var i = 0; i < head.Length; i++) ws.Cell(1, i + 1).Value = head[i];
        var r = 2;
        foreach (var x in rows)
        {
            object?[] v = [x.TypeName, x.OwnerName, x.OwnerKind == "driver" ? "سائق" : "مركبة", x.Number, x.Category ?? x.Issuer, x.IssueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                x.Expiry?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), x.DaysLeft, x.StatusLabel, x.Mandatory ? "نعم" : "لا", x.Version];
            for (var i = 0; i < v.Length; i++) ws.Cell(r, i + 1).Value = v[i] is int n ? n : XLCellValue.FromObject(v[i]?.ToString() ?? "");
            if (x.Status == DocRules.Expired) ws.Row(r).Style.Fill.BackgroundColor = XLColor.FromHtml("#FDE5E5");
            else if (x.Status == DocRules.Soon) ws.Row(r).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF4DB");
            r++;
        }
        ws.Row(1).Style.Font.Bold = true; ws.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#EBECFC");
        ws.Columns().AdjustToContents(); ws.SheetView.FreezeRows(1);
        using var ms = new MemoryStream(); wb.SaveAs(ms);
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"fleet-documents-{DateTime.UtcNow:yyyyMMdd}.xlsx");
    }

    /// <summary>Expired documents and those expiring within <paramref name="days"/> days (default 30), across vehicles and drivers.</summary>
    [HttpGet("expiring")]
    public async Task<IActionResult> Expiring([FromQuery, Range(0, 365)] int days = 30)
    {
        await Viewer();
        var rows = await RowsAsync(null, null, null, null, null);
        return Ok(new { days, expired = rows.Where(r => r.Status == DocRules.Expired), expiring = rows.Where(r => r.DaysLeft is { } d && d >= 0 && d <= days) });
    }

    // ── one document ──────────────────────────────────────────────────────────────────────────────────────────────
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var a = await Viewer();
        var d = await db.Documents.FirstOrDefaultAsync(x => x.Id == id) ?? throw new NotFoundException("الوثيقة");
        var row = (await RowsAsync(null, null, null, null, d.OwnerId)).First(r => r.Id == id);
        var type = await db.DocumentTypes.FirstAsync(t => t.Id == d.TypeId);
        var versions = await db.DocumentVersions.Where(v => v.DocumentId == id).OrderByDescending(v => v.Version).ToListAsync();
        var copies = DocumentService.CanSeeCopies(a);
        var vids = versions.Select(v => v.Id).ToList();
        var files = copies ? (await db.Attachments.Where(x => x.OwnerType == "docversion" && vids.Contains(x.OwnerId)).OrderBy(x => x.UploadedAt)
            .Select(x => new { x.Id, x.OwnerId, x.FileName, x.ContentType, x.Size, x.UploadedAt }).ToListAsync()).ToLookup(x => x.OwnerId) : null;
        var history = await db.Audit.Where(x => x.Entity == "document" && x.EntityId == id).OrderBy(x => x.Id).ToListAsync();
        var uids = history.Where(h => h.UserId != null).Select(h => h.UserId!.Value).Concat(versions.Select(v => v.ByUserId)).Distinct().ToList();
        var names = await db.Users.Where(u => uids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        return Ok(new
        {
            document = row, type, d.CurrentVersionId, canSeeCopies = copies, canEdit = a.Can(Section.Documents, PermissionLevel.Edit),
            versions = versions.Select(v => new
            {
                v.Id, v.Version, v.Kind, v.Number, v.Category, v.Issuer, v.IssueDate, v.Expiry, v.Note, v.At, by = names.GetValueOrDefault(v.ByUserId), current = v.Id == d.CurrentVersionId,
                files = files?[v.Id], fileCount = files?[v.Id].Count(),
            }),
            history = history.Select(h => new { h.At, h.Action, h.Detail, h.OldValue, h.NewValue, by = h.UserId is { } u ? names.GetValueOrDefault(u) : null }),
        });
    }

    private static void Check(DocumentType type, string? number, string? category, string? issuer, DateOnly? issue, DateOnly? expiry)
    {
        var values = new Dictionary<string, bool> { ["number"] = !string.IsNullOrWhiteSpace(number), ["category"] = !string.IsNullOrWhiteSpace(category), ["issuer"] = !string.IsNullOrWhiteSpace(issuer), ["issueDate"] = issue != null, ["expiry"] = expiry != null };
        foreach (var f in type.Fields.Where(f => f.Required && values.ContainsKey(f.Key)))
            if (!values[f.Key]) throw new DomainException($"أدخل «{f.Label}».");
        if (issue is { } i && expiry is { } e && e < i) throw new DomainException("تاريخ الانتهاء يجب أن يكون بعد تاريخ الإصدار.");
    }

    private static string? Keep(DocumentType t, string key, string? v) => t.Fields.Any(f => f.Key == key) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
    private static DateOnly? Keep(DocumentType t, string key, DateOnly? v) => t.Fields.Any(f => f.Key == key) ? v : null;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Values(FleetDocument d) => JsonSerializer.Serialize(new { d.Number, d.Category, d.Issuer, d.IssueDate, d.Expiry }, Json);

    [HttpPost]
    public async Task<IActionResult> Create(DocInput i)
    {
        var a = await Editor();
        var type = await db.DocumentTypes.FirstOrDefaultAsync(t => t.Id == i.TypeId && t.IsActive) ?? throw new NotFoundException("نوع الوثيقة");
        var owner = type.AppliesTo == "driver"
            ? await db.Drivers.Where(x => x.Id == i.OwnerId).Select(x => x.Name).FirstOrDefaultAsync() ?? throw new NotFoundException("السائق")
            : await db.Vehicles.Where(x => x.Id == i.OwnerId).Select(x => x.Plate).FirstOrDefaultAsync() ?? throw new NotFoundException("المركبة");
        if (await db.Documents.AnyAsync(d => d.TypeId == type.Id && d.OwnerId == i.OwnerId))
            throw new DomainException($"وثيقة «{type.Name}» مسجلة لـ{owner}؛ استخدم «تجديد الوثيقة».");
        var (number, category, issuer, issue, expiry) = (Keep(type, "number", i.Number), Keep(type, "category", i.Category), Keep(type, "issuer", i.Issuer), Keep(type, "issueDate", i.IssueDate), Keep(type, "expiry", i.Expiry));
        Check(type, number, category, issuer, issue, expiry);
        var d = new FleetDocument { TypeId = type.Id, OwnerKind = type.AppliesTo, OwnerId = i.OwnerId, Number = number, Category = category, Issuer = issuer, IssueDate = issue, Expiry = expiry, CreatedBy = a.UserId };
        var v = new FleetDocumentVersion { DocumentId = d.Id, Version = 1, Kind = "تسجيل", Number = number, Category = category, Issuer = issuer, IssueDate = issue, Expiry = expiry, Note = i.Note?.Trim(), ByUserId = a.UserId };
        d.CurrentVersionId = v.Id;
        db.Documents.Add(d); db.DocumentVersions.Add(v);
        audit.Write(a, "document", d.Id, "تسجيل وثيقة", $"«{type.Name}» — {DocumentService.OwnerLabel(type.AppliesTo, owner)}", null, Values(d));
        audit.Write(a, type.AppliesTo, i.OwnerId, "تسجيل وثيقة", $"«{type.Name}» {number} تنتهي {expiry:yyyy-MM-dd}");
        await db.SaveChangesAsync();
        return Ok(new { d.Id, versionId = v.Id });
    }

    /// <summary>«تجديد الوثيقة»: a new version with the new number and dates; the previous version and its copy stay in the history. The alert cycle restarts from the new expiry.</summary>
    [HttpPost("{id:guid}/renew")]
    public async Task<IActionResult> Renew(Guid id, DocRenewInput i)
    {
        var a = await Editor();
        var d = await db.Documents.FirstOrDefaultAsync(x => x.Id == id) ?? throw new NotFoundException("الوثيقة");
        var type = await db.DocumentTypes.FirstAsync(t => t.Id == d.TypeId);
        var (number, category, issuer, issue, expiry) = (Keep(type, "number", i.Number), Keep(type, "category", i.Category), Keep(type, "issuer", i.Issuer), Keep(type, "issueDate", i.IssueDate), Keep(type, "expiry", i.Expiry));
        Check(type, number, category, issuer, issue, expiry);
        if (d.Expiry is { } old && expiry is { } nu && nu <= old) throw new DomainException($"تاريخ الانتهاء الجديد يجب أن يكون بعد الحالي ({old:yyyy-MM-dd}).");
        var before = Values(d);
        var v = new FleetDocumentVersion { DocumentId = d.Id, Version = d.Version + 1, Kind = "تجديد", Number = number, Category = category, Issuer = issuer, IssueDate = issue, Expiry = expiry, Note = i.Note?.Trim(), ByUserId = a.UserId };
        db.DocumentVersions.Add(v);
        (d.Number, d.Category, d.Issuer, d.IssueDate, d.Expiry, d.Version, d.CurrentVersionId, d.UpdatedAt) = (number, category, issuer, issue, expiry, v.Version, v.Id, DateTimeOffset.UtcNow);
        audit.Write(a, "document", d.Id, "تجديد الوثيقة", $"الإصدار {v.Version}" + (string.IsNullOrWhiteSpace(i.Note) ? "" : $" · {i.Note.Trim()}"), before, Values(d));
        await db.SaveChangesAsync();
        return Ok(new { d.Id, versionId = v.Id, version = v.Version });
    }

    /// <summary>Correction of a typing mistake in the current values (not a renewal): reason required, old and new values kept in the audit log.</summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Correct(Guid id, DocCorrectInput i)
    {
        var a = await Editor();
        var d = await db.Documents.FirstOrDefaultAsync(x => x.Id == id) ?? throw new NotFoundException("الوثيقة");
        var type = await db.DocumentTypes.FirstAsync(t => t.Id == d.TypeId);
        var (number, category, issuer, issue, expiry) = (Keep(type, "number", i.Number), Keep(type, "category", i.Category), Keep(type, "issuer", i.Issuer), Keep(type, "issueDate", i.IssueDate), Keep(type, "expiry", i.Expiry));
        Check(type, number, category, issuer, issue, expiry);
        var before = Values(d);
        (d.Number, d.Category, d.Issuer, d.IssueDate, d.Expiry, d.UpdatedAt) = (number, category, issuer, issue, expiry, DateTimeOffset.UtcNow);
        if (Values(d) == before) return Ok(new { d.Id });
        audit.Write(a, "document", d.Id, "تصحيح بيانات الوثيقة", i.Reason.Trim(), before, Values(d));
        await db.SaveChangesAsync();
        return Ok(new { d.Id });
    }

    // ── types and alert settings ──────────────────────────────────────────────────────────────────────────────────
    private static List<DocField> CleanFields(List<DocField>? fields)
    {
        var list = (fields ?? []).Where(f => DocumentService.FieldKeys.Contains(f.Key)).GroupBy(f => f.Key).Select(g => g.First())
            .Select(f => new DocField(f.Key, string.IsNullOrWhiteSpace(f.Label) ? f.Key : f.Label.Trim()[..Math.Min(f.Label.Trim().Length, 60)], f.Required && f.Key != "image")).ToList();
        if (list.Count == 0) throw new DomainException("اختر حقلًا واحدًا على الأقل للوثيقة.");
        return list;
    }

    [HttpPost("types")]
    public async Task<IActionResult> AddType(DocTypeInput i)
    {
        var a = await Editor();
        if (await db.DocumentTypes.AnyAsync(t => t.Name == i.Name.Trim())) throw new DomainException("يوجد نوع وثيقة بهذا الاسم.");
        var t = new DocumentType { Code = "custom", Name = i.Name.Trim(), AppliesTo = i.AppliesTo, Fields = CleanFields(i.Fields), MandatoryForDispatch = i.MandatoryForDispatch, IsActive = i.IsActive, SortOrder = i.SortOrder };
        db.DocumentTypes.Add(t);
        audit.Write(a, "documenttype", t.Id, "إضافة نوع وثيقة", t.Name, null, JsonSerializer.Serialize(t, Json));
        await db.SaveChangesAsync();
        return Ok(t);
    }

    [HttpPut("types/{id:guid}")]
    public async Task<IActionResult> UpdateType(Guid id, DocTypeInput i)
    {
        var a = await Editor();
        var t = await db.DocumentTypes.FirstOrDefaultAsync(x => x.Id == id) ?? throw new NotFoundException("نوع الوثيقة");
        if (t.AppliesTo != i.AppliesTo && await db.Documents.AnyAsync(d => d.TypeId == id)) throw new DomainException("لا يمكن تغيير «يخص» لنوع له وثائق مسجلة.");
        var before = JsonSerializer.Serialize(t, Json);
        (t.Name, t.AppliesTo, t.Fields, t.MandatoryForDispatch, t.IsActive, t.SortOrder) = (i.Name.Trim(), i.AppliesTo, CleanFields(i.Fields), i.MandatoryForDispatch, i.IsActive, i.SortOrder);
        audit.Write(a, "documenttype", t.Id, "تعديل نوع وثيقة", t.Name, before, JsonSerializer.Serialize(t, Json));
        await db.SaveChangesAsync();
        return Ok(t);
    }

    [HttpPut("settings")]
    public async Task<IActionResult> Settings(DocSettingsInput i)
    {
        var a = await Editor();
        var offsets = (i.Offsets ?? []).Where(n => n is >= 0 and <= 365).Distinct().OrderByDescending(n => n).ToList();
        if (offsets.Count == 0) throw new DomainException("أدخل موعد تنبيه واحدًا على الأقل (بالأيام قبل الانتهاء).");
        if (i.FleetManagerUserId is { } fm && !await db.Users.AnyAsync(u => u.Id == fm && u.IsActive)) throw new NotFoundException("المستخدم");
        var before = $"{string.Join(',', await svc.OffsetsAsync())} | {await svc.FleetManagerAsync()}";
        await Put(DocumentService.OffsetsKey, string.Join(',', offsets));
        await Put(DocumentService.FleetManagerKey, i.FleetManagerUserId?.ToString() ?? "");
        audit.Write(a, "settings", Guid.Empty, "إعدادات تنبيهات الوثائق", null, before, $"{string.Join(',', offsets)} | {i.FleetManagerUserId}");
        await db.SaveChangesAsync();
        return NoContent();
    }

    private async Task Put(string key, string value)
    {
        var s = await db.Settings.FirstOrDefaultAsync(x => x.Key == key);
        if (s is null) db.Settings.Add(new Setting { Key = key, Value = value }); else s.Value = value;
    }

    /// <summary>Runs the daily expiry check now for this company (administrators; the same check runs every day at 06:00 Saudi time).</summary>
    [HttpPost("run-alerts")]
    public async Task<IActionResult> RunAlerts()
    {
        var a = await actor.GetAsync();
        if (a.Role != Role.Admin) throw new ForbiddenException();
        var made = await svc.RunAlertsAsync(Ksa.Day(DateTimeOffset.UtcNow));
        audit.Write(a, "settings", Guid.Empty, "تشغيل فحص انتهاء الوثائق يدويًا", $"{made.Count} تنبيه");
        await db.SaveChangesAsync();
        return Ok(new { sent = made.Count, alerts = made });
    }
}
