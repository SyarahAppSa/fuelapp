using System.ComponentModel.DataAnnotations;
using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FuelUp.Api.Controllers;

/// <summary>
/// «طلبات العملاء»: the customer order is the hub that links the departments. Opening an order shows the customer, type, price,
/// every truck and driver dispatched on it, loading documents, delivered quantities and fill reports. Ops:View to read, Ops:Edit to change;
/// prices and the revenue estimate only with Costs:View.
/// </summary>
[ApiController, Route("api/orders")]
public class OrdersController(FuelUpDb db, ActorAccessor actor, OrderService svc, TripService trips) : ControllerBase
{
    private static readonly OrderStatus[] OpenSet = [OrderStatus.New, OrderStatus.Planned, OrderStatus.InProgress];

    [HttpGet]
    public async Task<IActionResult> List([FromQuery, StringLength(60)] string? q, [FromQuery] OrderStatus? status, [FromQuery] OrderType? type, [FromQuery] OrderPriority? priority,
        [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] bool late = false, [FromQuery] int page = 1, [FromQuery] int size = 20)
    {
        var a = await actor.GetAsync(); a.Require(Section.Ops, PermissionLevel.View);
        var query = db.Orders.AsQueryable();
        if (status is { } s) query = query.Where(o => o.Status == s);
        if (type is { } t) query = query.Where(o => o.Type == t);
        if (priority is { } p) query = query.Where(o => o.Priority == p);
        if (from is { } f) { var fs = new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3)); query = query.Where(o => o.CreatedAt >= fs); }
        if (to is { } t2) { var te = new DateTimeOffset(t2.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3)).AddDays(1); query = query.Where(o => o.CreatedAt < te); }
        if (late) { var now = DateTimeOffset.UtcNow; query = query.Where(o => OpenSet.Contains(o.Status) && o.RequestedTo != null && o.RequestedTo < now); }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = "%" + q.Trim().Replace("%", "").Replace("_", "") + "%";
            query = query.Where(o => EF.Functions.ILike(o.No, like) || EF.Functions.ILike(o.CustomerName, like) || (o.CustomerRef != null && EF.Functions.ILike(o.CustomerRef, like)));
        }
        size = Math.Clamp(size, 1, 100); page = Math.Max(page, 1);
        var total = await query.CountAsync();
        var rows = await query.OrderByDescending(o => o.CreatedAt).Skip((page - 1) * size).Take(size).ToListAsync();
        var ids = rows.Select(o => o.Id).ToList();
        var tripsBy = (await db.Trips.Include(x => x.Loading).Include(x => x.Deliveries).Where(x => x.OrderId != null && ids.Contains(x.OrderId.Value)).ToListAsync())
            .ToLookup(x => x.OrderId!.Value);
        var tol = await trips.ToleranceAsync();
        var money = a.Can(Section.Costs);
        return Ok(new { total, page, size, items = rows.Select(o => Row(o, OrderService.Totals(o, tripsBy[o.Id], tol), money)) });
    }

    private static object Row(CustomerOrder o, OrderTotals totals, bool money) => new
    {
        o.Id, o.No, o.CustomerId, o.CustomerName, o.Site, o.CustomerRef, type = o.Type, typeLabel = OrderRules.TypeLabel(o.Type), o.Product, o.QtyRequested,
        priority = o.Priority, priorityLabel = OrderRules.PriorityLabel(o.Priority), status = o.Status, statusLabel = OrderRules.StatusLabel(o.Status), o.StatusReason,
        o.RequestedFrom, o.RequestedTo, o.CreatedAt, o.Notes, totals,
        money = money ? new { o.TransportRatePerLiter, o.FuelPricePerLiter } : null,
    };

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var a = await actor.GetAsync(); a.Require(Section.Ops, PermissionLevel.View);
        return Ok(await Hub(a, await svc.GetAsync(id)));
    }

    /// <summary>The order hub: header, totals, every trip with its loading document, delivered quantities and fill report, photos and history.</summary>
    private async Task<object> Hub(Actor a, CustomerOrder o)
    {
        var list = await db.Trips.Include(x => x.Loading).Include(x => x.Deliveries).Where(x => x.OrderId == o.Id).OrderBy(x => x.CreatedAt).ToListAsync();
        var tol = await trips.ToleranceAsync();
        var totals = OrderService.Totals(o, list, tol);
        var ids = list.Select(x => x.Id).ToList();
        var vids = list.Where(x => x.VehicleId != null).Select(x => x.VehicleId!.Value).ToList();
        var trailers = await db.Vehicles.Where(v => vids.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.TrailerPlate);
        var reports = await db.FillReports.Where(r => ids.Contains(r.TripId)).ToDictionaryAsync(r => r.TripId);
        var fr = a.Can(Section.FillReports);
        object? photos = null;
        if (fr && reports.Count > 0)
        {
            var latest = reports.Values.OrderByDescending(r => r.StartedAt).ToList();
            foreach (var r in latest)
            {
                var atts = await db.Attachments.Where(x => x.OwnerType == "fillreport" && x.OwnerId == r.Id).OrderBy(x => x.UploadedAt).ToListAsync();
                if (atts.Count == 0) continue;
                var last = atts.GroupBy(x => x.Kind).ToDictionary(g => g.Key, g => g.Last());
                photos = new { reportId = r.Id, reportNo = r.No, tripNo = r.TripNo, items = FillReportsController.Slots.Where(s => last.ContainsKey(s.Kind)).Select(s => new { id = last[s.Kind].Id, s.Kind, s.Label }) };
                break;
            }
        }
        var history = await db.Audit.Where(x => x.Entity == "order" && x.EntityId == o.Id).OrderBy(x => x.Id).ToListAsync();
        var uids = history.Where(h => h.UserId != null).Select(h => h.UserId!.Value).Append(o.CreatedBy).Distinct().ToList();
        var names = await db.Users.Where(u => uids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        var money = a.Can(Section.Costs);
        var rate = (o.TransportRatePerLiter ?? 0) + (o.Type == OrderType.TransportAndSupply ? o.FuelPricePerLiter ?? 0 : 0);
        return new
        {
            order = Row(o, totals, money),
            createdBy = names.GetValueOrDefault(o.CreatedBy),
            trips = list.Select(t =>
            {
                var rep = reports.GetValueOrDefault(t.Id);
                var done = t.Deliveries.Where(d => d.SubmittedAt != null).OrderBy(d => d.Seq).ToList();
                return new
                {
                    t.Id, t.No, status = t.Status, statusLabel = TripWorkflow.Label(t.Status), driver = t.Snapshot?.DriverName, plate = t.Snapshot?.Plate,
                    trailerPlate = t.VehicleId is { } v ? trailers.GetValueOrDefault(v) : null, station = t.Snapshot?.StationName, t.SourceRef, loadingDocNo = t.Loading?.DocNo,
                    t.QtyRequested, loaded = t.Loading?.Qty, delivered = OrderRules.Delivered(t), receipt = OrderRules.Receipt(t),
                    receiptNo = done.Count == 0 ? null : string.Join("، ", done.Select(d => d.ReceiptNo).Where(x => !string.IsNullOrEmpty(x))),
                    difference = OrderRules.Delivered(t) is { } dq && t.Loading is { } l ? dq - l.Qty : (decimal?)null, tolerance = tol.For(t.Loading?.Qty),
                    t.CreatedAt, loadedAt = t.Loading?.At, deliveredAt = done.LastOrDefault()?.SubmittedAt, t.ApprovedAt,
                    fillReport = rep is null ? null : new { rep.Id, rep.No, status = rep.Status },
                };
            }),
            photos,
            money = money ? new
            {
                o.TransportRatePerLiter, o.FuelPricePerLiter, ratePerLiter = rate,
                revenueEstimate = Math.Round(totals.Delivered * rate, 2), orderValue = Math.Round(o.QtyRequested * rate, 2),
            } : null,
            history = history.Select(h => new { h.At, h.Action, h.Detail, h.OldValue, h.NewValue, by = h.UserId is { } u ? names.GetValueOrDefault(u) : null }),
        };
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateOrderInput i) { var a = await actor.GetAsync(); return Ok(await Hub(a, await svc.CreateAsync(a, i))); }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateOrderInput i) { var a = await actor.GetAsync(); return Ok(await Hub(a, await svc.UpdateAsync(a, id, i))); }

    [HttpPost("{id:guid}/trips")]
    public async Task<IActionResult> Dispatch(Guid id, DispatchInput i)
    {
        var a = await actor.GetAsync();
        var (t, warnings) = await svc.DispatchAsync(a, id, i);
        return Ok(new { tripId = t.Id, tripNo = t.No, warnings });
    }

    [HttpPost("{id:guid}/close")]
    public async Task<IActionResult> Close(Guid id, OrderReason r) { var a = await actor.GetAsync(); return Ok(await Hub(a, await svc.CloseAsync(a, id, r.Reason))); }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, OrderReason r) { var a = await actor.GetAsync(); return Ok(await Hub(a, await svc.CancelAsync(a, id, r.Reason))); }

    [HttpPost("{id:guid}/link-trip")]
    public async Task<IActionResult> Link(Guid id, LinkTripInput r) { var a = await actor.GetAsync(); return Ok(await Hub(a, await svc.LinkTripAsync(a, id, r.TripId))); }

    /// <summary>Trips of the same customer not yet linked to any order (legacy trips), to attach them from the hub.</summary>
    [HttpGet("{id:guid}/linkable")]
    public async Task<IActionResult> Linkable(Guid id)
    {
        var a = await actor.GetAsync(); a.Require(Section.Ops, PermissionLevel.View);
        var o = await svc.GetAsync(id);
        var list = await db.Trips.Where(t => t.CustomerId == o.CustomerId && t.OrderId == null && t.Status != TripStatus.Cancelled).OrderByDescending(t => t.CreatedAt).Take(50).ToListAsync();
        return Ok(list.Select(t => new { t.Id, t.No, t.Status, statusLabel = TripWorkflow.Label(t.Status), t.QtyRequested, t.CreatedAt, driver = t.Snapshot?.DriverName, plate = t.Snapshot?.Plate }));
    }
}

/// <summary>«لوحة التوزيع»: open orders with quantity left to dispatch, trucks and drivers with their readiness, in one call.</summary>
[ApiController, Route("api/dispatch")]
public class DispatchController(FuelUpDb db, ActorAccessor actor, TripService trips, DocumentService docs) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Board()
    {
        var a = await actor.GetAsync(); a.Require(Section.Ops, PermissionLevel.View);
        var open = await db.Orders.Where(o => o.Status == OrderStatus.New || o.Status == OrderStatus.Planned || o.Status == OrderStatus.InProgress).ToListAsync();
        var oids = open.Select(o => o.Id).ToList();
        var tripsBy = (await db.Trips.Include(x => x.Loading).Include(x => x.Deliveries).Where(x => x.OrderId != null && oids.Contains(x.OrderId.Value)).ToListAsync()).ToLookup(x => x.OrderId!.Value);
        var tol = await trips.ToleranceAsync();
        var orders = open.Select(o => (o, t: OrderService.Totals(o, tripsBy[o.Id], tol))).Where(x => x.t.ToDispatch > 0 || x.t.Late)
            .OrderByDescending(x => x.o.Priority == OrderPriority.Urgent).ThenBy(x => x.o.RequestedTo == null).ThenBy(x => x.o.RequestedTo).ThenBy(x => x.o.CreatedAt)
            .Select(x => new
            {
                x.o.Id, x.o.No, x.o.CustomerName, x.o.Site, x.o.CustomerRef, type = x.o.Type, typeLabel = OrderRules.TypeLabel(x.o.Type), priority = x.o.Priority,
                priorityLabel = OrderRules.PriorityLabel(x.o.Priority), status = x.o.Status, statusLabel = OrderRules.StatusLabel(x.o.Status), x.o.Product, x.o.QtyRequested,
                x.o.RequestedFrom, x.o.RequestedTo, toDispatch = x.t.ToDispatch, remaining = x.t.Remaining, late = x.t.Late, trips = x.t.Trips,
            }).ToList();

        var today = Ksa.Day(DateTimeOffset.UtcNow);
        var warn = await docs.WarnDaysAsync();
        var docRows = await db.Documents.Join(db.DocumentTypes.Where(t => t.IsActive), d => d.TypeId, t => t.Id, (d, t) => new { d.OwnerId, d.Expiry, t.Name, t.MandatoryForDispatch }).ToListAsync();
        var docState = docRows.Select(x => (x.OwnerId, x.Name, x.MandatoryForDispatch, st: DocRules.Status(x.Expiry, today, warn), x.Expiry)).ToLookup(x => x.OwnerId);
        var running = await db.Trips.Where(t => t.Status != TripStatus.Cancelled && t.Status != TripStatus.Approved && t.Status != TripStatus.AwaitingApproval && t.Status != TripStatus.UnderReview)
            .Select(t => new { t.Id, t.No, t.VehicleId, t.DriverId, t.Status }).ToListAsync();
        var workshop = (await db.WorkOrders.Where(w => !w.Closed).Select(w => new { w.VehicleId, w.No }).ToListAsync()).ToLookup(w => w.VehicleId);
        var assigned = await db.VehicleAssignments.Where(x => x.To == null && x.DriverId != null).Select(x => new { x.VehicleId, x.DriverId }).ToListAsync();
        var drivers = await db.Drivers.OrderBy(d => d.Name).ToListAsync();
        var dname = drivers.ToDictionary(d => d.Id, d => d.Name);
        var vehicles = await db.Vehicles.OrderBy(v => v.Plate).ToListAsync();
        var present = (await db.Users.Where(u => u.DriverId != null && db.Attendance.Any(r => r.UserId == u.Id && (r.CheckOutAt == null || r.Day == today))).Select(u => u.DriverId!.Value).ToListAsync()).ToHashSet();

        object Docs(Guid owner) => docState[owner].Where(x => x.st is DocRules.Expired or DocRules.Soon)
            .Select(x => new { type = x.Name, mandatory = x.MandatoryForDispatch, status = x.st, label = DocRules.Label(x.st), expiry = x.Expiry }).ToList();
        bool Expired(Guid owner) => docState[owner].Any(x => x.st == DocRules.Expired && x.MandatoryForDispatch);
        bool Soon(Guid owner) => docState[owner].Any(x => x.st == DocRules.Soon || (x.st == DocRules.Expired && !x.MandatoryForDispatch));

        var trucks = vehicles.Select(v =>
        {
            var trip = running.FirstOrDefault(t => t.VehicleId == v.Id);
            var wo = workshop[v.Id].FirstOrDefault();
            var driverId = assigned.FirstOrDefault(x => x.VehicleId == v.Id)?.DriverId;
            var states = new List<string>();
            if (wo != null) states.Add("في الصيانة");
            if (trip != null) states.Add("في رحلة");
            if (Expired(v.Id)) states.Add("وثيقة منتهية");
            else if (Soon(v.Id)) states.Add("وثيقة تقارب الانتهاء");
            if (states.Count == 0) states.Add("متاحة");
            return new
            {
                v.Id, v.Plate, v.TrailerPlate, v.InternalNo, v.Capacity, driverId, driver = driverId is { } d ? dname.GetValueOrDefault(d) : null,
                state = states[0], states, available = wo == null && trip == null && !Expired(v.Id), blocked = Expired(v.Id),
                openTrip = trip?.No, workOrder = wo?.No, documents = Docs(v.Id),
            };
        });
        var people = drivers.Select(d =>
        {
            var trip = running.FirstOrDefault(t => t.DriverId == d.Id);
            var vehicleId = assigned.FirstOrDefault(x => x.DriverId == d.Id)?.VehicleId;
            var states = new List<string>();
            if (trip != null) states.Add("في رحلة");
            if (Expired(d.Id)) states.Add("وثيقة منتهية");
            if (present.Contains(d.Id)) states.Add("حاضر اليوم");
            if (trip == null && !Expired(d.Id)) states.Insert(0, "متاح");
            return new
            {
                d.Id, d.Name, d.EmpNo, d.Phone, vehicleId, plate = vehicleId is { } vid ? vehicles.FirstOrDefault(v => v.Id == vid)?.Plate : null,
                state = states[0], states, available = trip == null && !Expired(d.Id), present = present.Contains(d.Id), blocked = Expired(d.Id),
                openTrip = trip?.No, documents = Docs(d.Id),
            };
        });
        var stations = await db.Stations.OrderBy(s => s.Name).Select(s => new { s.Id, s.Name, s.Type }).ToListAsync();
        return Ok(new { orders, trucks, drivers = people, stations, canDispatch = a.Can(Section.Ops, PermissionLevel.Edit) });
    }
}
