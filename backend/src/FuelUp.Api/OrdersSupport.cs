using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FuelUp.Api;

public record CreateOrderInput(Guid CustomerId, OrderType Type, [Required, StringLength(40)] string Product, [Range(typeof(decimal), "1", "10000000")] decimal QtyRequested,
    OrderPriority Priority, DateTimeOffset? RequestedFrom, DateTimeOffset? RequestedTo, [StringLength(60)] string? CustomerRef, [StringLength(1000)] string? Notes,
    [Range(typeof(decimal), "0", "100")] decimal? TransportRatePerLiter, [Range(typeof(decimal), "0", "100")] decimal? FuelPricePerLiter);
public record UpdateOrderInput(Guid CustomerId, OrderType Type, [Required, StringLength(40)] string Product, [Range(typeof(decimal), "1", "10000000")] decimal QtyRequested,
    OrderPriority Priority, DateTimeOffset? RequestedFrom, DateTimeOffset? RequestedTo, [StringLength(60)] string? CustomerRef, [StringLength(1000)] string? Notes,
    [Range(typeof(decimal), "0", "100")] decimal? TransportRatePerLiter, [Range(typeof(decimal), "0", "100")] decimal? FuelPricePerLiter,
    [Required, StringLength(500, MinimumLength = 3)] string Reason);
public record DispatchInput(Guid VehicleId, Guid? DriverId, Guid StationId, [Required, StringLength(60)] string SourceRef, [Range(typeof(decimal), "1", "200000")] decimal Qty,
    DateTimeOffset? Eta, [StringLength(500)] string? OverrideReason);
public record OrderReason([Required, StringLength(500, MinimumLength = 3)] string Reason);
public record LinkTripInput(Guid TripId);

/// <summary>Execution totals of an order, computed from its trips (cancelled trips excluded).</summary>
public record OrderTotals(decimal Requested, decimal Planned, decimal Loaded, decimal Delivered, decimal Receipt, decimal Remaining, decimal ToDispatch,
    decimal Difference, decimal Tolerance, bool DifferenceFlag, int Trips, int OpenTrips, bool Late);

/// <summary>
/// «طلبات العملاء»: creation, dispatch of trucks (trips created through <see cref="TripService"/> with all its rules),
/// edits with a reason, close / cancel, and linking of legacy trips. Every change is written to the audit log (entity "order").
/// </summary>
public class OrderService(FuelUpDb db, TripService trips, AuditWriter audit, DocumentService docs)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Next order number SO-yyMM-####, per company, from one atomic UPSERT (same approach as trip numbers).</summary>
    public static async Task<string> NextNoAsync(FuelUpDb db, CancellationToken ct)
    {
        var period = DateTime.UtcNow.ToString("yyMM", CultureInfo.InvariantCulture);
        var prefix = $"SO-{period}-";
        int n;
        if (db.IsPostgres && db.CurrentCompanyId is { } cid)
        {
            var like = prefix + "%";
            var from = prefix.Length + 1;
            n = (await db.Database.SqlQuery<int>($"""
                INSERT INTO order_counters (company_id, period, last)
                VALUES ({cid}, {period}, COALESCE((SELECT max(CAST(substring(no from {from}) AS int)) FROM customer_orders WHERE company_id = {cid} AND no LIKE {like}), 0) + 1)
                ON CONFLICT (company_id, period) DO UPDATE SET last = order_counters.last + 1
                RETURNING last AS "Value"
                """).ToListAsync(ct))[0];
        }
        else
        {
            var last = await db.Orders.Where(t => t.No.StartsWith(prefix)).OrderByDescending(t => t.No).Select(t => t.No).FirstOrDefaultAsync(ct);
            n = (last is null ? 0 : int.Parse(last[prefix.Length..], CultureInfo.InvariantCulture)) + 1;
        }
        return prefix + n.ToString("0000", CultureInfo.InvariantCulture);
    }

    private static void Validate(OrderType type, decimal qty, DateTimeOffset? from, DateTimeOffset? to, ref decimal? fuelPrice)
    {
        if (qty <= 0) throw new DomainException("الكمية المطلوبة يجب أن تكون أكبر من صفر.");
        if (from is { } f && to is { } t && t < f) throw new DomainException("نهاية فترة التسليم يجب أن تكون بعد بدايتها.");
        if (type == OrderType.Transport) fuelPrice = null;   // transport only: the customer owns the fuel
    }

    public async Task<CustomerOrder> GetAsync(Guid id, CancellationToken ct = default) =>
        await db.Orders.FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw new NotFoundException("طلب العميل");

    public async Task<CustomerOrder> CreateAsync(Actor a, CreateOrderInput i, CancellationToken ct = default)
    {
        a.Require(Section.Ops, PermissionLevel.Edit);
        var fuel = i.FuelPricePerLiter;
        Validate(i.Type, i.QtyRequested, i.RequestedFrom, i.RequestedTo, ref fuel);
        var c = await db.Customers.FirstOrDefaultAsync(x => x.Id == i.CustomerId, ct) ?? throw new NotFoundException("العميل");
        var o = new CustomerOrder
        {
            No = await NextNoAsync(db, ct), CustomerId = c.Id, CustomerName = c.Name, Site = c.Site, Type = i.Type, Product = i.Product.Trim(), QtyRequested = i.QtyRequested,
            Priority = i.Priority, RequestedFrom = i.RequestedFrom, RequestedTo = i.RequestedTo, CustomerRef = Clean(i.CustomerRef), Notes = Clean(i.Notes),
            TransportRatePerLiter = i.TransportRatePerLiter, FuelPricePerLiter = fuel, CreatedBy = a.UserId,
        };
        db.Orders.Add(o);
        audit.Write(a, "order", o.Id, "إنشاء طلب العميل", $"{o.No} · {OrderRules.TypeLabel(o.Type)} · {o.QtyRequested:#,0} لتر {o.Product} · {OrderRules.PriorityLabel(o.Priority)}");
        await db.SaveChangesAsync(ct);
        return o;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public async Task<CustomerOrder> UpdateAsync(Actor a, Guid id, UpdateOrderInput i, CancellationToken ct = default)
    {
        a.Require(Section.Ops, PermissionLevel.Edit);
        var o = await GetAsync(id, ct);
        if (!OrderRules.IsOpen(o.Status)) throw new DomainException("لا يمكن تعديل طلب مغلق أو ملغى.");
        var fuel = i.FuelPricePerLiter;
        Validate(i.Type, i.QtyRequested, i.RequestedFrom, i.RequestedTo, ref fuel);
        if (i.CustomerId != o.CustomerId)
        {
            if (await db.Trips.AnyAsync(t => t.OrderId == id && t.Status != TripStatus.Cancelled, ct)) throw new DomainException("لا يمكن تغيير العميل بعد إسناد رحلات للطلب.");
            var c = await db.Customers.FirstOrDefaultAsync(x => x.Id == i.CustomerId, ct) ?? throw new NotFoundException("العميل");
            (o.CustomerId, o.CustomerName, o.Site) = (c.Id, c.Name, c.Site);
        }
        var before = Snapshot(o);
        (o.Type, o.Product, o.QtyRequested, o.Priority, o.RequestedFrom, o.RequestedTo, o.CustomerRef, o.Notes, o.TransportRatePerLiter, o.FuelPricePerLiter) =
            (i.Type, i.Product.Trim(), i.QtyRequested, i.Priority, i.RequestedFrom, i.RequestedTo, Clean(i.CustomerRef), Clean(i.Notes), i.TransportRatePerLiter, fuel);
        var after = Snapshot(o);
        if (before == after) return o;
        await ResyncAsync(a, o, ct);
        audit.Write(a, "order", o.Id, "تعديل طلب العميل", i.Reason.Trim(), before, after);
        await db.SaveChangesAsync(ct);
        return o;
    }

    private static string Snapshot(CustomerOrder o) => JsonSerializer.Serialize(new
    {
        o.CustomerName, type = OrderRules.TypeLabel(o.Type), o.Product, o.QtyRequested, priority = OrderRules.PriorityLabel(o.Priority), o.RequestedFrom, o.RequestedTo,
        o.CustomerRef, o.Notes, o.TransportRatePerLiter, o.FuelPricePerLiter,
    }, Json);

    /// <summary>Re-derives the status from all trips (after a quantity change or a newly linked trip).</summary>
    private async Task ResyncAsync(Actor a, CustomerOrder o, CancellationToken ct)
    {
        var any = await db.Trips.Where(t => t.OrderId == o.Id).Include(t => t.Loading).Include(t => t.Deliveries).FirstOrDefaultAsync(ct);
        if (any != null) { await trips.SyncOrderAsync(a, any, ct); return; }
        if (OrderRules.IsOpen(o.Status) && o.Status != OrderStatus.New) o.Status = OrderStatus.New;
    }

    /// <summary>
    /// Dispatch one truck on the order: creates the trip through <see cref="TripService.CreateAsync(Actor, CreateTrip, Guid?, CancellationToken)"/>.
    /// Expired mandatory documents of the truck or the driver refuse it with the exact document; an operations editor can override with a written
    /// reason (audited on the order and the trip). A quantity above what is left, a truck in the workshop or documents close to expiry only warn.
    /// </summary>
    public async Task<(Trip Trip, List<string> Warnings)> DispatchAsync(Actor a, Guid id, DispatchInput i, CancellationToken ct = default)
    {
        a.Require(Section.Ops, PermissionLevel.Edit);
        var o = await GetAsync(id, ct);
        if (!OrderRules.IsOpen(o.Status)) throw new DomainException($"لا يمكن إسناد رحلة لطلب {OrderRules.StatusLabel(o.Status)}.");
        var vehicle = await db.Vehicles.FirstOrDefaultAsync(v => v.Id == i.VehicleId, ct) ?? throw new NotFoundException("المركبة");
        var driverId = i.DriverId ?? await db.VehicleAssignments.Where(x => x.VehicleId == vehicle.Id && x.To == null).Select(x => x.DriverId).FirstOrDefaultAsync(ct)
                       ?? throw new DomainException($"لا يوجد سائق مسند للمركبة {vehicle.Plate}. اختر السائق.");
        var driver = await db.Drivers.FirstOrDefaultAsync(d => d.Id == driverId, ct) ?? throw new NotFoundException("السائق");

        var ready = await docs.ReadinessAsync(vehicle.Id, driver.Id, ct);
        var overrideReason = Clean(i.OverrideReason);
        if (ready.Expired.Count > 0 && overrideReason is null)
            throw new DomainException($"لا يمكن إسناد الرحلة: {string.Join("، ", ready.Expired.Select(x => x.Text))}. للإسناد رغم ذلك اكتب سبب التجاوز.");

        var warnings = new List<string>();
        var existing = await db.Trips.Include(t => t.Loading).Where(t => t.OrderId == id && t.Status != TripStatus.Cancelled).ToListAsync(ct);
        var planned = existing.Sum(OrderRules.Planned);
        if (planned + i.Qty > o.QtyRequested)
            warnings.Add($"الكمية المخططة ({planned + i.Qty:#,0} لتر) تتجاوز كمية الطلب ({o.QtyRequested:#,0} لتر).");
        if (await db.WorkOrders.AnyAsync(w => w.VehicleId == vehicle.Id && !w.Closed, ct)) warnings.Add($"المركبة {vehicle.Plate} في الصيانة (أمر صيانة مفتوح).");
        var busy = await db.Trips.Where(t => t.VehicleId == vehicle.Id && t.Status != TripStatus.Approved && t.Status != TripStatus.Cancelled && t.Status != TripStatus.AwaitingApproval && t.Status != TripStatus.UnderReview)
            .Select(t => t.No).FirstOrDefaultAsync(ct);
        if (busy != null) warnings.Add($"المركبة {vehicle.Plate} لديها رحلة مفتوحة {busy}.");
        warnings.AddRange(ready.Soon.Select(x => x.Text));
        if (ready.Expired.Count > 0) warnings.AddRange(ready.Expired.Select(x => "تجاوز: " + x.Text));

        await using var tx = db.IsPostgres ? await db.Database.BeginTransactionAsync(ct) : null;
        var t = await trips.CreateAsync(a, new CreateTrip(o.CustomerId, driver.Id, vehicle.Id, i.StationId, i.SourceRef.Trim(), o.Product, i.Qty, i.Eta, o.TransportRatePerLiter), o.Id, ct);
        audit.Write(a, "order", o.Id, "إسناد رحلة", $"{t.No} · {vehicle.Plate} · {driver.Name} · {i.Qty:#,0} لتر" + (warnings.Count > 0 ? " · تنبيهات: " + string.Join(" | ", warnings) : ""));
        audit.Write(a, "trip", t.Id, "ضمن طلب العميل", o.No);
        if (ready.Expired.Count > 0)
        {
            var detail = $"{overrideReason} — {string.Join("، ", ready.Expired.Select(x => x.Text))}";
            audit.Write(a, "order", o.Id, "تجاوز جاهزية الوثائق", detail);
            audit.Write(a, "trip", t.Id, "تجاوز جاهزية الوثائق", detail);
        }
        await db.SaveChangesAsync(ct);
        if (tx != null) await tx.CommitAsync(ct);
        return (t, warnings);
    }

    public async Task<CustomerOrder> LinkTripAsync(Actor a, Guid id, Guid tripId, CancellationToken ct = default)
    {
        a.Require(Section.Ops, PermissionLevel.Edit);
        var o = await GetAsync(id, ct);
        if (!OrderRules.IsOpen(o.Status)) throw new DomainException($"لا يمكن ربط رحلة بطلب {OrderRules.StatusLabel(o.Status)}.");
        var t = await trips.FindAsync(tripId, ct) ?? throw new NotFoundException("الرحلة");
        if (t.OrderId == o.Id) return o;
        if (t.OrderId != null) throw new DomainException("الرحلة مرتبطة بطلب عميل آخر.");
        if (t.CustomerId != o.CustomerId) throw new DomainException("الرحلة لعميل آخر غير عميل هذا الطلب.");
        if (t.Status == TripStatus.Cancelled) throw new DomainException("لا يمكن ربط رحلة ملغاة.");
        t.OrderId = o.Id;
        audit.Write(a, "order", o.Id, "ربط رحلة سابقة بالطلب", t.No);
        audit.Write(a, "trip", t.Id, "ضمن طلب العميل", o.No);
        await trips.SyncOrderAsync(a, t, ct);
        await db.SaveChangesAsync(ct);
        return o;
    }

    public async Task<CustomerOrder> CloseAsync(Actor a, Guid id, string reason, CancellationToken ct = default)
    {
        a.Require(Section.Ops, PermissionLevel.Edit);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("سبب الإغلاق مطلوب.");
        var o = await GetAsync(id, ct);
        if (!OrderRules.IsOpen(o.Status)) throw new DomainException($"الطلب {OrderRules.StatusLabel(o.Status)} بالفعل.");
        var open = await db.Trips.Where(t => t.OrderId == id && t.Status != TripStatus.Cancelled && !(t.Status == TripStatus.AwaitingApproval || t.Status == TripStatus.Approved || t.Status == TripStatus.UnderReview))
            .Select(t => t.No).ToListAsync(ct);
        if (open.Count > 0) throw new DomainException($"لا يمكن إغلاق الطلب وفيه رحلات لم يكتمل تفريغها: {string.Join("، ", open)}.");
        var old = o.Status;
        (o.Status, o.StatusReason, o.ClosedAt, o.ClosedBy) = (OrderStatus.Closed, reason.Trim(), DateTimeOffset.UtcNow, a.UserId);
        audit.Write(a, "order", o.Id, "إغلاق الطلب", reason.Trim(), old.ToString(), o.Status.ToString());
        await db.SaveChangesAsync(ct);
        return o;
    }

    /// <summary>Cancel: only while no trip has started loading. Trips still waiting to load are cancelled with the order.</summary>
    public async Task<CustomerOrder> CancelAsync(Actor a, Guid id, string reason, CancellationToken ct = default)
    {
        a.Require(Section.Ops, PermissionLevel.Edit);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("سبب الإلغاء مطلوب.");
        var o = await GetAsync(id, ct);
        if (!OrderRules.IsOpen(o.Status)) throw new DomainException($"الطلب {OrderRules.StatusLabel(o.Status)} بالفعل.");
        var list = await db.Trips.Where(t => t.OrderId == id && t.Status != TripStatus.Cancelled).Select(t => new { t.Id, t.No, t.Status }).ToListAsync(ct);
        var started = list.Where(t => t.Status != TripStatus.WaitingLoad).Select(t => t.No).ToList();
        if (started.Count > 0) throw new DomainException($"لا يمكن إلغاء الطلب بعد بدء التنفيذ؛ رحلات بدأت: {string.Join("، ", started)}. استخدم «إغلاق الطلب».");
        await using var tx = db.IsPostgres ? await db.Database.BeginTransactionAsync(ct) : null;
        var old = o.Status;
        (o.Status, o.StatusReason, o.ClosedAt, o.ClosedBy) = (OrderStatus.Cancelled, reason.Trim(), DateTimeOffset.UtcNow, a.UserId);
        audit.Write(a, "order", o.Id, "إلغاء الطلب", reason.Trim(), old.ToString(), o.Status.ToString());
        await db.SaveChangesAsync(ct);
        foreach (var t in list) await trips.CancelAsync(a, t.Id, $"إلغاء طلب العميل {o.No}: {reason.Trim()}", ct);
        if (tx != null) await tx.CommitAsync(ct);
        return o;
    }

    /// <summary>Totals from the trips. Late = the delivery window has ended and quantity is still missing.</summary>
    public static OrderTotals Totals(CustomerOrder o, IEnumerable<Trip> all, Tolerance tol)
    {
        var list = all.Where(t => t.Status != TripStatus.Cancelled).ToList();
        var planned = list.Sum(OrderRules.Planned);
        var loaded = list.Sum(t => t.Loading?.Qty ?? 0);
        var delivered = list.Sum(t => OrderRules.Delivered(t) ?? 0);
        var receipt = list.Sum(t => OrderRules.Receipt(t) ?? 0);
        var finished = list.Where(t => OrderRules.Delivered(t) != null && t.Loading != null).ToList();
        var diff = finished.Sum(t => (OrderRules.Delivered(t) ?? 0) - t.Loading!.Qty);
        var allowed = finished.Sum(t => tol.For(t.Loading!.Qty));
        var remaining = Math.Max(0, o.QtyRequested - delivered);
        var late = OrderRules.IsOpen(o.Status) && o.Status != OrderStatus.Delivered && o.RequestedTo is { } rt && DateTimeOffset.UtcNow > rt && remaining > 0;
        return new OrderTotals(o.QtyRequested, planned, loaded, delivered, receipt, remaining, Math.Max(0, o.QtyRequested - planned), diff,
            finished.Count == 0 ? 0 : allowed, finished.Count > 0 && Math.Abs(diff) > allowed, list.Count, list.Count(t => !OrderRules.Finished(t)), late);
    }
}
