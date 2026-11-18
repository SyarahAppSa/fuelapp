using System.ComponentModel.DataAnnotations;
using ClosedXML.Excel;
using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FuelUp.Api.Controllers;

public record TripView(Trip Trip, Reconciliation Reconciliation, string StatusLabel, string? OrderNo = null, string? CustomerRef = null);
public record NoteRequest([StringLength(1000)] string? Note);
public record AckRequest(bool Confirmed, [StringLength(1000)] string? Note);
public record CorrectionRequest([Required, StringLength(20)] string Field, [Required, StringLength(60)] string NewValue, [Required, StringLength(1000)] string Reason);
public record DecideRequest(bool Approve);
public record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int Size);

[ApiController, Route("api/trips"), Authorize]
public class TripsController(FuelUpDb db, ActorAccessor actor, TripService svc) : ControllerBase
{
    private async Task<TripView> View(Trip t)
    {
        var o = t.OrderId is { } oid ? await db.Orders.Where(x => x.Id == oid).Select(x => new { x.No, x.CustomerRef }).FirstOrDefaultAsync() : null;
        return new(t, Reconciliation.Of(t, await svc.ToleranceAsync()), TripWorkflow.Label(t.Status), o?.No, o?.CustomerRef);
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] TripStatus? status, [FromQuery] Guid? driverId, [FromQuery] int page = 1, [FromQuery] int size = 20)
    {
        var a = await actor.GetAsync();
        var q = db.Trips.Include(t => t.Loading).Include(t => t.Deliveries).Include(t => t.Handovers).AsQueryable();
        if (!a.Can(Section.Ops)) { if (a.DriverId is null) throw new ForbiddenException(); q = q.Where(t => t.DriverId == a.DriverId); }   // drivers see only their own orders
        if (status is { } s) q = q.Where(t => t.Status == s);
        if (driverId is { } d) q = q.Where(t => t.DriverId == d);
        var total = await q.CountAsync();
        var items = await q.OrderByDescending(t => t.CreatedAt).Skip((Math.Max(page, 1) - 1) * size).Take(Math.Clamp(size, 1, 100)).ToListAsync();
        var tol = await svc.ToleranceAsync();
        var oids = items.Where(t => t.OrderId != null).Select(t => t.OrderId!.Value).Distinct().ToList();
        var orders = await db.Orders.Where(o => oids.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => new { o.No, o.CustomerRef });
        return Ok(new PagedResult<TripView>(items.Select(t =>
        {
            var o = t.OrderId is { } oid ? orders.GetValueOrDefault(oid) : null;
            return new TripView(t, Reconciliation.Of(t, tol), TripWorkflow.Label(t.Status), o?.No, o?.CustomerRef);
        }).ToList(), total, page, size));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var a = await actor.GetAsync();
        var t = await svc.FindAsync(id) ?? throw new NotFoundException("الطلب");
        if (!a.Can(Section.Ops) && a.DriverId != t.DriverId) throw new ForbiddenException();
        var audit = await db.Audit.Where(x => x.Entity == "trip" && x.EntityId == id).OrderBy(x => x.At).ToListAsync();
        return Ok(new { view = await View(t), audit });
    }

    [HttpPost] public async Task<IActionResult> Create(CreateTrip r) => Ok(await View(await svc.CreateAsync(await actor.GetAsync(), r)));
    [HttpPost("{id:guid}/load")] public async Task<IActionResult> Load(Guid id, LoadInput r) => Ok(await View(await svc.LoadAsync(await actor.GetAsync(), id, r)));
    [HttpPost("{id:guid}/depart")] public async Task<IActionResult> Depart(Guid id) => Ok(await View(await svc.DepartAsync(await actor.GetAsync(), id)));
    [HttpPost("{id:guid}/arrive")] public async Task<IActionResult> Arrive(Guid id, ArriveInput r) => Ok(await View(await svc.ArriveAsync(await actor.GetAsync(), id, r)));
    [HttpPost("{id:guid}/unload")] public async Task<IActionResult> Unload(Guid id, UnloadInput r) => Ok(await View(await svc.UnloadAsync(await actor.GetAsync(), id, r)));
    [HttpPost("{id:guid}/deliver")] public async Task<IActionResult> Deliver(Guid id, DeliverInput r) => Ok(await View(await svc.DeliverAsync(await actor.GetAsync(), id, r)));
    [HttpPost("{id:guid}/approve")] public async Task<IActionResult> Approve(Guid id, NoteRequest r) => Ok(await View(await svc.ApproveAsync(await actor.GetAsync(), id, r.Note)));
    [HttpPost("{id:guid}/resolve")] public async Task<IActionResult> Resolve(Guid id, NoteRequest r) => Ok(await View(await svc.ResolveAsync(await actor.GetAsync(), id, r.Note ?? "")));
    [HttpPost("{id:guid}/cancel")] public async Task<IActionResult> Cancel(Guid id, NoteRequest r) => Ok(await View(await svc.CancelAsync(await actor.GetAsync(), id, r.Note ?? "")));
    [HttpPost("{id:guid}/handover")] public async Task<IActionResult> Handover(Guid id, HandoverInput r) => Ok(await View(await svc.HandoverAsync(await actor.GetAsync(), id, r)));
    [HttpPost("{id:guid}/handover/ack")] public async Task<IActionResult> Ack(Guid id, AckRequest r) => Ok(await View(await svc.AckHandoverAsync(await actor.GetAsync(), id, r.Confirmed, r.Note)));
    [HttpPost("{id:guid}/corrections")] public async Task<IActionResult> Correction(Guid id, CorrectionRequest r) => Ok(await svc.RequestCorrectionAsync(await actor.GetAsync(), id, r.Field, r.NewValue, r.Reason));
    [HttpPost("{id:guid}/corrections/{cid:guid}/decide")] public async Task<IActionResult> Decide(Guid id, Guid cid, DecideRequest r) { await svc.DecideCorrectionAsync(await actor.GetAsync(), id, cid, r.Approve); return NoContent(); }
}

public record FillRow(Guid Id, string No, string At, string Status, string Driver, string Plate, string Place, string Source, string LoadRef, string Customer, string? Project,
    string? Meter, decimal? MeterBefore, decimal? MeterAfter, decimal? Computed, decimal? Receipt, decimal? Difference, bool DifferenceFlag, string? ApprovedBy, string ApprovedAt, string Notes);

[ApiController, Route("api/reports"), Authorize]
public class ReportsController(FuelUpDb db, ActorAccessor actor, TripService svc) : ControllerBase
{
    /// <summary>Fill operations report. format=xlsx returns an RTL Excel workbook.</summary>
    [HttpGet("fill-operations")]
    public async Task<IActionResult> FillOperations([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] Guid? driverId, [FromQuery] Guid? vehicleId,
        [FromQuery] Guid? customerId, [FromQuery] Guid? stationId, [FromQuery] Guid? meterId, [FromQuery] TripStatus? status, [FromQuery] string? format)
    {
        var a = await actor.GetAsync(); a.Require(Section.Moves, PermissionLevel.View);
        var q = db.Trips.Include(t => t.Loading).Include(t => t.Deliveries).Include(t => t.Corrections).Where(t => t.Loading != null);
        if (driverId != null) q = q.Where(t => t.DriverId == driverId);
        if (vehicleId != null) q = q.Where(t => t.VehicleId == vehicleId);
        if (customerId != null) q = q.Where(t => t.CustomerId == customerId);
        if (stationId != null) q = q.Where(t => t.StationId == stationId);
        if (meterId != null) q = q.Where(t => t.MeterId == meterId);
        if (status != null) q = q.Where(t => t.Status == status);
        var trips = (await q.OrderByDescending(t => t.Loading!.At).ToListAsync())
            .Where(t => (from is null || Ksa.Day(t.Loading!.At) >= from) && (to is null || Ksa.Day(t.Loading!.At) <= to)).ToList();
        var approvers = await db.Users.Where(u => trips.Select(t => t.ApprovedBy).Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        var tol = await svc.ToleranceAsync();
        var rows = trips.Select(t =>
        {
            var c = Reconciliation.Of(t, tol);
            var ds = t.Deliveries.Where(d => d.SubmittedAt != null).OrderBy(d => d.Seq).ToList();
            var s = t.Snapshot;
            var notes = string.Join(" | ", new[] { t.ReviewReason, t.Resolution is null ? null : "القرار: " + t.Resolution, t.ApprovalNote }.Concat(t.Corrections.Select(x => $"تصحيح {x.Field}: {x.Reason}")).Where(x => !string.IsNullOrEmpty(x)));
            return new FillRow(t.Id, t.No, Ksa.Text(t.Loading!.At), TripWorkflow.Label(t.Status), s?.DriverName ?? "", s?.Plate ?? "", s?.StationName ?? "", s?.StationType.ToString() ?? "", t.SourceRef,
                s?.CustomerName ?? "", s?.Project, s?.MeterSerial, ds.FirstOrDefault()?.MeterBefore, ds.LastOrDefault()?.MeterAfter, c.Delivered, c.Receipt, c.MeterVsReceipt,
                c.Exceeds(c.Unsettled) || c.Exceeds(c.MeterVsReceipt), t.ApprovedBy is { } ab ? approvers.GetValueOrDefault(ab) : null, Ksa.Text(t.ApprovedAt), notes);
        }).ToList();
        var summary = new { count = rows.Count, computed = rows.Sum(r => r.Computed ?? 0), receipt = rows.Sum(r => r.Receipt ?? 0), difference = rows.Sum(r => r.Difference ?? 0),
            pending = trips.Count(t => t.Status is not (TripStatus.Approved or TripStatus.Cancelled)), flagged = rows.Count(r => r.DifferenceFlag) };
        if (format != "xlsx") return Ok(new { summary, rows });

        a.Require(Section.Export, PermissionLevel.Edit);
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("العمليات");
        ws.RightToLeft = true;
        string[] head = ["رقم العملية", "التاريخ والوقت (السعودية)", "الحالة", "السائق", "اللوحة", "مكان التعبئة", "رقم طلب التحميل", "العميل", "المشروع", "رقم العداد", "قراءة قبل", "قراءة بعد", "الكمية المحتسبة", "المعتمدة من العميل", "فرق الكمية", "المعتمد", "وقت الاعتماد", "الملاحظات"];
        for (var i = 0; i < head.Length; i++) ws.Cell(1, i + 1).Value = head[i];
        var r = 2;
        foreach (var x in rows)
        {
            object?[] v = [x.No, x.At, x.Status, x.Driver, x.Plate, x.Place, x.LoadRef, x.Customer, x.Project, x.Meter, x.MeterBefore, x.MeterAfter, x.Computed, x.Receipt, x.Difference, x.ApprovedBy, x.ApprovedAt, x.Notes];
            for (var i = 0; i < v.Length; i++) ws.Cell(r, i + 1).Value = v[i] is decimal d ? d : XLCellValue.FromObject(v[i]?.ToString() ?? "");
            if (x.DifferenceFlag) ws.Row(r).Style.Fill.BackgroundColor = XLColor.FromHtml("#FDE5E5");
            r++;
        }
        ws.Row(1).Style.Font.Bold = true; ws.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#EBECFC");
        ws.Columns().AdjustToContents(); ws.SheetView.FreezeRows(1);
        using var ms = new MemoryStream(); wb.SaveAs(ms);
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"fill-operations-{DateTime.UtcNow:yyyyMMdd}.xlsx");
    }
}

[ApiController, Route("api/dashboard"), Authorize]
public class DashboardController(FuelUpDb db, ActorAccessor actor, TripService svc) : ControllerBase
{
    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        (await actor.GetAsync()).Require(Section.Ops, PermissionLevel.View);
        var today = Ksa.Day(DateTimeOffset.UtcNow);
        var f = from ?? new DateOnly(today.Year, today.Month, 1); var e = to ?? today;
        var tol = await svc.ToleranceAsync();
        var all = await db.Trips.Include(t => t.Loading).Include(t => t.Deliveries).Include(t => t.Handovers).Where(t => t.Status != TripStatus.Cancelled).ToListAsync();
        var period = all.Where(t => t.Loading != null && Ksa.Day(t.Loading.At) >= f && Ksa.Day(t.Loading.At) <= e).ToList();
        var recon = period.Select(t => (t, c: Reconciliation.Of(t, tol))).ToList();
        var months = Enumerable.Range(0, 12).Select(i => today.AddMonths(-i)).Select(m => new
        {
            month = $"{m.Year}-{m.Month:00}",
            liters = all.Where(t => t.Loading != null && Ksa.Day(t.Loading.At).Year == m.Year && Ksa.Day(t.Loading.At).Month == m.Month).Sum(t => t.Loading!.Qty),
        }).Reverse();
        return Ok(new
        {
            from = f, to = e,
            loaded = period.Sum(t => t.Loading!.Qty),
            delivered = recon.Sum(x => x.c.Delivered ?? 0),
            netDifference = recon.Sum(x => x.c.Unsettled ?? 0),
            operations = period.Count,
            trucks = period.Select(t => t.VehicleId).Distinct().Count(),
            drivers = period.Select(t => t.DriverId).Distinct().Count(),
            open = all.Count(t => TripWorkflow.IsOpen(t.Status)),
            awaitingApproval = all.Count(t => t.Status == TripStatus.AwaitingApproval),
            underReview = all.Count(t => t.Status == TripStatus.UnderReview),
            byStation = period.GroupBy(t => t.Snapshot?.StationName ?? "").Select(g => new { label = g.Key, value = g.Sum(t => t.Loading!.Qty) }).OrderByDescending(x => x.value),
            byCustomer = recon.GroupBy(x => x.t.Snapshot?.CustomerName ?? "").Select(g => new { label = g.Key, value = g.Sum(x => x.c.Delivered ?? 0) }).OrderByDescending(x => x.value),
            byDriver = period.GroupBy(t => t.Snapshot?.DriverName ?? "").Select(g => new { label = g.Key, value = g.Sum(t => t.Loading!.Qty) }).OrderByDescending(x => x.value).Take(6),
            monthly = months,
            counts = new
            {
                customers = await db.Customers.CountAsync(), stations = await db.Stations.CountAsync(),
                vehicles = await db.Vehicles.CountAsync(), drivers = await db.Drivers.CountAsync(),
            },
            // trips created in the period, by status (for the status donut)
            byStatus = all.Concat(await db.Trips.Where(t => t.Status == TripStatus.Cancelled).ToListAsync())
                .Where(t => Ksa.Day(t.CreatedAt) >= f && Ksa.Day(t.CreatedAt) <= e)
                .GroupBy(t => t.Status).Select(g => new { status = g.Key.ToString(), label = TripWorkflow.Label(g.Key), value = g.Count() }).OrderByDescending(x => x.value),
        });
    }
}
