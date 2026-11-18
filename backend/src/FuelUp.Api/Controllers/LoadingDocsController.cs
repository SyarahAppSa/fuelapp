using System.ComponentModel.DataAnnotations;
using System.Globalization;
using ClosedXML.Excel;
using FuelUp.Api.Security;
using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FuelUp.Api.Controllers;

/// <summary>
/// «توثيق التعبئة»: the four mandatory tank photos of every loading-and-delivery operation (at the start of filling, after filling,
/// before unloading, after unloading) plus the loading document, linked to the customer order and the trip. A record appears as soon
/// as its first photo reaches the server; the next ones join the same record; it is «مكتمل» when the four have arrived. Viewing (and downloading photos) needs Fill Reports: View; «طلب إعادة التصوير» needs Fill Reports: Edit
/// (see FillReportsController.RequestRetake); Excel needs Export as in the other reports.
/// </summary>
[ApiController, Route("api/fill-reports")]
public class LoadingDocsController(FuelUpDb db, ActorAccessor actor) : ControllerBase
{
    private static readonly TimeSpan Ksa = TimeSpan.FromHours(3);
    private const int PageSize = 25;

    public record StagePhoto(int Order, string Kind, string Stage, PhotoInfo? Photo);
    public record PhotoInfo(Guid Id, DateTimeOffset? CapturedAt, DateTimeOffset UploadedAt, double? Lat, double? Lng, string? LocationNote, bool FromGallery, int? LateMinutes, int Versions);
    public record Row(Guid Id, string No, Guid TripId, string TripNo, Guid? OrderId, string? OrderNo, string DriverName, string Plate, string? TrailerPlate,
        string Source, string SourceLabel, string Facility, string? City, string Product, decimal QtyRequested, decimal? LoadedQty,
        string? LoadingDocNo, string? LoadingDocNoFrom, string LoadingDocLabel, PhotoInfo? PreFill, PhotoInfo? PostFill, PhotoInfo? LoadingDoc,
        DateTimeOffset FirstPhotoAt, string Status, string StatusLabel, List<string> Reasons, int OpenRetakes, FillReportStatus ReportStatus,
        List<StagePhoto> Stages, List<string> Missing);

    [HttpGet("loading")]
    public async Task<IActionResult> List([FromQuery, StringLength(60)] string? q, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery, StringLength(20)] string? source, [FromQuery, StringLength(20)] string? status, [FromQuery] int page = 1, [FromQuery] string? format = null)
    {
        var a = await actor.GetAsync(); a.Require(Section.FillReports, PermissionLevel.View);
        var all = await RowsAsync(QueryFor(q, from, to), source);
        var totals = new
        {
            all = all.Count, incomplete = all.Count(r => r.Status == LoadingDocRules.Incomplete),
            awaitingPostFill = all.Count(r => r.Status == LoadingDocRules.Incomplete),   // older name, kept for existing clients
            needsReview = all.Count(r => r.Status == LoadingDocRules.NeedsReview), complete = all.Count(r => r.Status == LoadingDocRules.Complete),
        };
        var wanted = LoadingDocRules.Normalize(status);
        var rows = wanted == "" ? all : all.Where(r => r.Status == wanted).ToList();
        if (format == "xlsx") { a.Require(Section.Export, PermissionLevel.Edit); return Excel(rows); }
        page = Math.Max(page, 1);
        return Ok(new { totals, total = rows.Count, page, size = PageSize, items = rows.Skip((page - 1) * PageSize).Take(PageSize) });
    }

    /// <summary>One record: the loading facts, every version of every photo (oldest → newest, who and when) and the retake requests.</summary>
    [HttpGet("{id:guid}/loading")]
    public async Task<IActionResult> Detail(Guid id)
    {
        var a = await actor.GetAsync(); a.Require(Section.FillReports, PermissionLevel.View);
        var rep = await db.FillReports.FirstOrDefaultAsync(x => x.Id == id) ?? throw new NotFoundException("التقرير");
        var row = (await RowsAsync(db.FillReports.Where(x => x.Id == id), null, includeNotStarted: true)).Single();
        var photos = await db.Attachments.Where(x => x.OwnerType == "fillreport" && x.OwnerId == id).OrderBy(x => x.UploadedAt).ToListAsync();
        var requests = await db.RetakeRequests.Where(x => x.ReportId == id).OrderBy(x => x.RequestedAt).ToListAsync();
        var userIds = photos.Select(p => p.ByUserId).Concat(requests.Select(r => r.RequestedBy)).Distinct().ToList();
        var names = await db.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        var byAttachment = requests.Where(r => r.ResolvedAttachmentId != null).ToDictionary(r => r.ResolvedAttachmentId!.Value);
        return Ok(new
        {
            record = row,
            report = new { rep.CustomerName, rep.DeliverySite, rep.StartedAt, rep.SubmittedAt },
            canRequestRetake = a.Can(Section.FillReports, PermissionLevel.Edit),
            slots = FillReportsController.AllSlots.Select(s =>
            {
                var versions = photos.Where(p => p.Kind == s.Kind).ToList();
                return new
                {
                    kind = s.Kind, stage = s.Stage, label = s.Label, loading = LoadingDocRules.Kinds.Contains(s.Kind),
                    openRetake = requests.Where(r => r.Kind == s.Kind && r.ResolvedAt == null).Select(r => new { r.Id, r.Note, r.RequestedAt, by = names.GetValueOrDefault(r.RequestedBy) }).FirstOrDefault(),
                    versions = versions.Select((p, i) => new
                    {
                        p.Id, version = i + 1, current = i == versions.Count - 1, p.CapturedAt, p.UploadedAt, p.Lat, p.Lng, p.LocationNote, p.FromGallery,
                        lateMinutes = LoadingDocRules.LateMinutes(p.CapturedAt, p.UploadedAt), by = names.GetValueOrDefault(p.ByUserId),
                        retakeNote = byAttachment.TryGetValue(p.Id, out var rq) ? rq.Note : null,
                    }),
                };
            }),
            retakeRequests = requests.Select(r => new
            {
                r.Id, r.Kind, label = FillReportsController.SlotLabel(r.Kind), r.Note, r.RequestedAt, by = names.GetValueOrDefault(r.RequestedBy), r.ResolvedAt, r.ResolvedAttachmentId,
            }),
        });
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Reports with at least one loading photo, narrowed in SQL by search text and by the KSA day of the first loading photo.</summary>
    private IQueryable<FillReport> QueryFor(string? q, DateOnly? from, DateOnly? to)
    {
        var kinds = LoadingDocRules.Kinds;
        var loading = db.Attachments.Where(x => x.OwnerType == "fillreport" && kinds.Contains(x.Kind));
        var query = db.FillReports.Where(r => loading.Any(x => x.OwnerId == r.Id));
        if (from is { } f)
        {
            var fs = new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue), Ksa).ToUniversalTime();
            query = query.Where(r => !loading.Any(x => x.OwnerId == r.Id && (x.CapturedAt ?? x.UploadedAt) < fs));
        }
        if (to is { } t)
        {
            var te = new DateTimeOffset(t.ToDateTime(TimeOnly.MinValue), Ksa).AddDays(1).ToUniversalTime();
            query = query.Where(r => loading.Any(x => x.OwnerId == r.Id && (x.CapturedAt ?? x.UploadedAt) < te));
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = "%" + q.Trim().Replace("%", "").Replace("_", "") + "%";
            query = query.Where(x => EF.Functions.ILike(x.No, like) || EF.Functions.ILike(x.TripNo, like) || EF.Functions.ILike(x.Plate, like) || EF.Functions.ILike(x.DriverName, like)
                || (x.LoadingDocNo != null && EF.Functions.ILike(x.LoadingDocNo, like))
                || db.Trips.Any(t => t.Id == x.TripId && (EF.Functions.ILike(t.SourceRef, like) || db.Orders.Any(o => o.Id == t.OrderId && EF.Functions.ILike(o.No, like))))
                || db.TripLoadings.Any(l => l.TripId == x.TripId && EF.Functions.ILike(l.DocNo, like)));
        }
        return query;
    }

    private async Task<List<Row>> RowsAsync(IQueryable<FillReport> query, string? source, bool includeNotStarted = false)
    {
        var reports = await query.ToListAsync();
        var ids = reports.Select(r => r.Id).ToList();
        var tripIds = reports.Select(r => r.TripId).ToList();
        var photos = (await db.Attachments.Where(x => x.OwnerType == "fillreport" && ids.Contains(x.OwnerId))
            .Select(x => new { x.Id, x.OwnerId, x.Kind, x.CapturedAt, x.UploadedAt, x.Lat, x.Lng, x.LocationNote, x.FromGallery }).ToListAsync()).ToLookup(x => x.OwnerId);
        var open = (await db.RetakeRequests.Where(x => ids.Contains(x.ReportId) && x.ResolvedAt == null).ToListAsync()).ToLookup(x => x.ReportId);
        var trips = await db.Trips.Include(t => t.Loading).Where(t => tripIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id);
        var orderIds = trips.Values.Where(t => t.OrderId != null).Select(t => t.OrderId!.Value).Distinct().ToList();
        var orders = await db.Orders.Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.No);
        var stationIds = trips.Values.Select(t => t.StationId).Distinct().ToList();
        var stations = await db.Stations.Where(s => stationIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
        var now = DateTimeOffset.UtcNow;

        var rows = new List<Row>();
        foreach (var r in reports)
        {
            var t = trips.GetValueOrDefault(r.TripId);
            var station = t is null ? null : stations.GetValueOrDefault(t.StationId);
            var type = t?.Snapshot?.StationType ?? station?.Type ?? StationType.Station;
            var key = LoadingDocRules.SourceKey(type);
            if (!string.IsNullOrWhiteSpace(source) && key != source) continue;

            var mine = photos[r.Id].ToList();
            PhotoInfo? Latest(string kind)
            {
                var v = mine.Where(p => p.Kind == kind).OrderBy(p => p.UploadedAt).ToList();
                if (v.Count == 0) return null;
                var p = v[^1];
                return new PhotoInfo(p.Id, p.CapturedAt, p.UploadedAt, p.Lat, p.Lng, p.LocationNote, p.FromGallery, LoadingDocRules.LateMinutes(p.CapturedAt, p.UploadedAt), v.Count);
            }
            var (pre, post, doc) = (Latest(LoadingDocRules.PreFill), Latest(LoadingDocRules.PostFill), Latest(LoadingDocRules.LoadingDoc));
            var stages = LoadingDocRules.Required.Select((x, i) => new StagePhoto(i + 1, x.Kind, x.Stage, Latest(x.Kind))).ToList();
            var facts = LoadingDocRules.Kinds.Select(k => (k, p: Latest(k))).Where(x => x.p != null)
                .Select(x => new LoadingDocRules.Photo(x.k, LoadingDocRules.StageLabel(x.k), x.p!.CapturedAt, x.p.UploadedAt, x.p.Lat, x.p.LocationNote, x.p.FromGallery))
                .ToList();
            var loadedQty = r.LoadedQty ?? t?.Loading?.Qty;
            var (status, reasons, missing) = LoadingDocRules.Evaluate(facts, open[r.Id].OrderBy(x => x.RequestedAt).Select(x => new LoadingDocRules.OpenRetake(FillReportsController.SlotLabel(x.Kind), x.Note)).ToList(),
                loadedQty, r.PostFillAt != null, now);
            if (status == LoadingDocRules.NotStarted && !includeNotStarted) continue;

            // the driver's number first; otherwise the loading record of the trip; otherwise the loading-order reference
            var (docNo, docFrom) = !string.IsNullOrWhiteSpace(r.LoadingDocNo) ? (r.LoadingDocNo, "driver")
                : !string.IsNullOrWhiteSpace(t?.Loading?.DocNo) ? (t!.Loading!.DocNo, "loading")
                : !string.IsNullOrWhiteSpace(t?.SourceRef) ? (t!.SourceRef, "sourceRef") : ((string?)null, (string?)null);
            var docLabel = type switch { StationType.Aramco => "رقم سند أرامكو", StationType.Station => "رقم فاتورة المحطة", _ => "رقم سند التحميل" };
            var first = mine.Where(p => LoadingDocRules.Kinds.Contains(p.Kind)).Select(p => p.CapturedAt ?? p.UploadedAt).DefaultIfEmpty(r.StartedAt).Min();
            rows.Add(new Row(r.Id, r.No, r.TripId, r.TripNo, t?.OrderId, t?.OrderId is { } oid ? orders.GetValueOrDefault(oid) : null, r.DriverName, r.Plate, r.TrailerPlate,
                key, LoadingDocRules.SourceLabel(type), t?.Snapshot?.StationName ?? (string.IsNullOrEmpty(r.LoadingSite) ? station?.Name ?? "" : r.LoadingSite), station?.City,
                r.Product, r.QtyRequested, loadedQty, docNo, docFrom, docLabel, pre, post, doc, first, status, LoadingDocRules.Label(status, missing), reasons, open[r.Id].Count(), r.Status,
                stages, missing));
        }
        return rows.OrderByDescending(x => x.FirstPhotoAt).ToList();   // newest first
    }

    private FileContentResult Excel(List<Row> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("توثيق التعبئة");
        ws.RightToLeft = true;
        string[] head = ["تاريخ ووقت أول صورة (السعودية)", "رقم الطلب", "رقم الرحلة", "رقم التقرير", "السائق", "اللوحة", "مصدر التحميل", "المنشأة / الفرع", "المنتج",
            "الكمية المطلوبة (لتر)", "الكمية المحمّلة (لتر)", "رقم سند التحميل / الفاتورة", "صورة قبل التعبئة: وقت الالتقاط", "صورة قبل التعبئة: الموقع",
            "صورة بعد التعبئة: وقت الالتقاط", "صورة بعد التعبئة: الموقع", "صورة قبل التفريغ: وقت الالتقاط", "صورة قبل التفريغ: الموقع",
            "صورة بعد التفريغ: وقت الالتقاط", "صورة بعد التفريغ: الموقع", "صورة السند", "حالة التوثيق", "الصور المطلوبة", "الأسباب"];
        for (var i = 0; i < head.Length; i++) ws.Cell(1, i + 1).Value = head[i];
        static string When(DateTimeOffset? d) => d is { } x ? x.ToOffset(Ksa).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";
        static string Where(PhotoInfo? p) => p is null ? "" : p.Lat is { } la ? $"{la.ToString("0.#####", CultureInfo.InvariantCulture)}, {p.Lng?.ToString("0.#####", CultureInfo.InvariantCulture)}" : p.LocationNote ?? "بدون موقع";
        var n = 2;
        foreach (var x in rows)
        {
            object?[] v = [When(x.FirstPhotoAt), x.OrderNo, x.TripNo, x.No, x.DriverName, x.Plate, x.SourceLabel, x.Facility + (x.City is null ? "" : " — " + x.City), x.Product,
                x.QtyRequested, x.LoadedQty, x.LoadingDocNo, When(x.PreFill?.CapturedAt ?? x.PreFill?.UploadedAt), Where(x.PreFill),
                When(x.PostFill?.CapturedAt ?? x.PostFill?.UploadedAt), Where(x.PostFill),
                When(x.Stages[2].Photo?.CapturedAt ?? x.Stages[2].Photo?.UploadedAt), Where(x.Stages[2].Photo),
                When(x.Stages[3].Photo?.CapturedAt ?? x.Stages[3].Photo?.UploadedAt), Where(x.Stages[3].Photo),
                x.LoadingDoc is null ? "لا" : "نعم", x.StatusLabel, string.Join("، ", x.Missing), string.Join("؛ ", x.Reasons)];
            for (var i = 0; i < v.Length; i++) ws.Cell(n, i + 1).Value = v[i] is decimal d ? d : XLCellValue.FromObject(v[i]?.ToString() ?? "");
            if (x.Status == LoadingDocRules.NeedsReview) ws.Row(n).Style.Fill.BackgroundColor = XLColor.FromHtml("#FDE5E5");
            else if (x.Status == LoadingDocRules.Incomplete) ws.Row(n).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF4DB");
            n++;
        }
        ws.Row(1).Style.Font.Bold = true; ws.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#EBECFC");
        ws.Columns().AdjustToContents(); ws.SheetView.FreezeRows(1);
        using var ms = new MemoryStream(); wb.SaveAs(ms);
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"loading-docs-{DateTime.UtcNow:yyyyMMdd}.xlsx");
    }
}
