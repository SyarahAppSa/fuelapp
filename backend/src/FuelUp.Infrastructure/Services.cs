using System.ComponentModel.DataAnnotations;
using System.Globalization;
using FuelUp.Domain;
using Microsoft.EntityFrameworkCore;

namespace FuelUp.Infrastructure;

/// <summary>Who is acting. Built from the JWT in the API, and by hand in tests.</summary>
public record Actor(Guid UserId, Guid? DriverId, IReadOnlyDictionary<Section, PermissionLevel> Levels, Guid CompanyId = default, Role Role = Role.Custom, Guid SessionId = default)
{
    public bool Can(Section s, PermissionLevel min = PermissionLevel.View) => Levels.TryGetValue(s, out var l) && l >= min;
    public void Require(Section s, PermissionLevel min)
    {
        if (!Can(s, min)) throw new ForbiddenException();
    }
}

public sealed class ForbiddenException() : Exception("ليست لديك صلاحية لهذا الإجراء.");
public sealed class NotFoundException(string what) : Exception($"{what} غير موجود.");

/// <summary>Referenced ids must exist inside the caller's company (query filters + RLS hide other companies' rows).</summary>
public static class Refs
{
    public static async Task EnsureAsync<T>(FuelUpDb db, Guid? id, string label, CancellationToken ct = default) where T : Entity
    {
        if (id is { } v && !await db.Set<T>().AnyAsync(x => x.Id == v, ct)) throw new NotFoundException(label);
    }
}

public class AuditWriter(FuelUpDb db)
{
    public void Write(Actor a, string entity, Guid id, string action, string? detail = null, string? oldValue = null, string? newValue = null) =>
        db.Audit.Add(new AuditEntry { At = DateTimeOffset.UtcNow, UserId = a.UserId, Entity = entity, EntityId = id, Action = action, Detail = detail, OldValue = oldValue, NewValue = newValue });

    public void Notify(Guid? driverId, string text, string? reference = null)
    {
        if (driverId is null) return;
        db.Notifications.Add(new Notification { DriverId = driverId, Text = text, Ref = reference, At = DateTimeOffset.UtcNow });
    }
}

public class MeterLedger(FuelUpDb db)
{
    /// <summary>Row lock on the meter so two operations on the same meter can never interleave.</summary>
    public async Task LockAsync(Guid meterId, CancellationToken ct = default)
    {
        if (db.IsPostgres)
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM meters WHERE id = {meterId} FOR UPDATE", ct);
    }

    public async Task<decimal?> LastAsync(Guid meterId, CancellationToken ct = default) =>
        await db.MeterReadings.Where(r => r.MeterId == meterId).OrderByDescending(r => r.Seq).Select(r => (decimal?)r.Reading).FirstOrDefaultAsync(ct);

    public Task<bool> BaselinePendingAsync(Guid? meterId, CancellationToken ct = default) =>
        meterId is null ? Task.FromResult(false) :
        db.MeterBaselines.Where(x => x.MeterId == meterId).OrderByDescending(x => x.At).Select(x => x.State == ApprovalState.Pending).FirstOrDefaultAsync(ct);

    /// <summary>Validates against the latest reading in the database (inside the caller's transaction) and appends.</summary>
    public async Task AppendAsync(Actor a, Guid meterId, IReadOnlyList<(decimal Reading, string Kind)> readings, Guid? tripId, bool requireIncrease, DateTimeOffset? capturedAt, CancellationToken ct = default)
    {
        await LockAsync(meterId, ct);
        var pending = db.MeterReadings.Local.Where(r => r.MeterId == meterId && db.Entry(r).State == EntityState.Added).OrderBy(r => r.Seq).ToList();
        var last = pending.Count > 0 ? pending[^1].Reading : await LastAsync(meterId, ct);
        MeterRules.Validate(last, readings.Select(r => r.Reading).ToList(), requireIncrease);
        var seq = Math.Max(await db.MeterReadings.Where(r => r.MeterId == meterId).MaxAsync(r => (long?)r.Seq, ct) ?? 0, pending.Count > 0 ? pending[^1].Seq : 0);
        foreach (var (reading, kind) in readings)
            db.MeterReadings.Add(new MeterReading { MeterId = meterId, Seq = ++seq, Reading = reading, Kind = kind, TripId = tripId, At = DateTimeOffset.UtcNow, CapturedAt = capturedAt, ByUserId = a.UserId });
    }

    public async Task SetBaselineAsync(Actor a, Guid meterId, decimal reading, CancellationToken ct = default)
    {
        if (!a.Can(Section.Meters, PermissionLevel.Edit) && !a.Can(Section.Driver, PermissionLevel.Edit)) throw new ForbiddenException();
        _ = await db.Meters.FindAsync([meterId], ct) ?? throw new NotFoundException("العداد");
        await LockAsync(meterId, ct);
        if (await db.MeterBaselines.AnyAsync(x => x.MeterId == meterId, ct) || await db.MeterReadings.AnyAsync(r => r.MeterId == meterId, ct))
            throw new DomainException("للعداد قراءة تأسيسية مسجلة. لتغييرها استخدم «استبدال / تصفير».");
        var approved = a.Can(Section.Meters, PermissionLevel.Edit);
        db.MeterBaselines.Add(new MeterBaseline { MeterId = meterId, Reading = reading, State = approved ? ApprovalState.Approved : ApprovalState.Pending, At = DateTimeOffset.UtcNow, ByUserId = a.UserId, ApprovedBy = approved ? a.UserId : null, ApprovedAt = approved ? DateTimeOffset.UtcNow : null });
        await AppendAsync(a, meterId, [(reading, "قراءة تأسيسية")], null, false, null, ct);
    }

    public async Task ApproveBaselineAsync(Actor a, Guid meterId, CancellationToken ct = default)
    {
        a.Require(Section.Meters, PermissionLevel.Edit);
        var b = await db.MeterBaselines.Where(x => x.MeterId == meterId).OrderByDescending(x => x.At).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("القراءة التأسيسية");
        b.State = ApprovalState.Approved; b.ApprovedBy = a.UserId; b.ApprovedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Replacement or zeroing: supervisor only, documented, keeps the full previous history.</summary>
    public async Task ResetAsync(Actor a, Guid meterId, string type, string reason, decimal newBaseline, string? newSerial, CancellationToken ct = default)
    {
        a.Require(Section.Meters, PermissionLevel.Edit);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("سبب الاستبدال أو التصفير مطلوب.");
        await LockAsync(meterId, ct);
        var last = await LastAsync(meterId, ct);
        db.MeterResets.Add(new MeterReset { MeterId = meterId, Type = type, Reason = reason, OldLast = last, NewBaseline = newBaseline, NewSerial = newSerial, At = DateTimeOffset.UtcNow, ByUserId = a.UserId });
        db.MeterBaselines.Add(new MeterBaseline { MeterId = meterId, Reading = newBaseline, State = ApprovalState.Approved, At = DateTimeOffset.UtcNow, ByUserId = a.UserId, ApprovedBy = a.UserId, ApprovedAt = DateTimeOffset.UtcNow });
        var seq = await db.MeterReadings.Where(r => r.MeterId == meterId).MaxAsync(r => (long?)r.Seq, ct) ?? 0;
        db.MeterReadings.Add(new MeterReading { MeterId = meterId, Seq = seq + 1, Reading = newBaseline, Kind = $"{type} (تأسيسية جديدة)", At = DateTimeOffset.UtcNow, ByUserId = a.UserId });
        if (!string.IsNullOrWhiteSpace(newSerial))
        {
            var m = await db.Meters.FindAsync([meterId], ct) ?? throw new NotFoundException("العداد");
            m.Serial = newSerial;
        }
    }
}

public class AssignmentService(FuelUpDb db, AuditWriter audit)
{
    public Task<Guid?> CurrentDriverOfVehicleAsync(Guid vehicleId, CancellationToken ct = default) =>
        db.VehicleAssignments.Where(x => x.VehicleId == vehicleId && x.To == null).Select(x => x.DriverId).FirstOrDefaultAsync(ct);

    public Task<Guid?> DefaultDriverOfCustomerAsync(Guid customerId, CancellationToken ct = default) =>
        db.CustomerAssignments.Where(x => x.CustomerId == customerId && x.To == null).Select(x => x.DriverId).FirstOrDefaultAsync(ct);

    public Task<List<Guid>> VehiclesOfDriverAsync(Guid driverId, CancellationToken ct = default) =>
        db.VehicleAssignments.Where(x => x.DriverId == driverId && x.To == null).Select(x => x.VehicleId).ToListAsync(ct);

    public async Task AssignVehicleAsync(Actor a, Guid vehicleId, Guid? driverId, string? note, CancellationToken ct = default)
    {
        a.Require(Section.Assign, PermissionLevel.Edit);
        await Refs.EnsureAsync<Vehicle>(db, vehicleId, "المركبة", ct);
        await Refs.EnsureAsync<Driver>(db, driverId, "السائق", ct);
        var now = DateTimeOffset.UtcNow;
        var open = await db.VehicleAssignments.FirstOrDefaultAsync(x => x.VehicleId == vehicleId && x.To == null, ct);
        if (open?.DriverId == driverId) return;
        if (open != null) { open.To = now; audit.Notify(open.DriverId, "أُلغي إسناد مركبة منك"); }
        if (driverId != null)
        {
            db.VehicleAssignments.Add(new VehicleAssignment { VehicleId = vehicleId, DriverId = driverId, From = now, ByUserId = a.UserId, Note = note });
            var plate = await db.Vehicles.Where(v => v.Id == vehicleId).Select(v => v.Plate).FirstAsync(ct);
            audit.Notify(driverId, $"أُسندت إليك المركبة {plate}");
        }
        audit.Write(a, "vehicle", vehicleId, "إسناد مركبة", $"{open?.DriverId} ← {driverId}", note);
    }

    public async Task AssignCustomerAsync(Actor a, Guid customerId, Guid? driverId, CancellationToken ct = default)
    {
        a.Require(Section.Assign, PermissionLevel.Edit);
        await Refs.EnsureAsync<Customer>(db, customerId, "العميل", ct);
        await Refs.EnsureAsync<Driver>(db, driverId, "السائق", ct);
        var now = DateTimeOffset.UtcNow;
        var open = await db.CustomerAssignments.FirstOrDefaultAsync(x => x.CustomerId == customerId && x.To == null, ct);
        if (open?.DriverId == driverId) return;
        if (open != null) open.To = now;
        if (driverId != null) db.CustomerAssignments.Add(new CustomerAssignment { CustomerId = customerId, DriverId = driverId, From = now, ByUserId = a.UserId });
        audit.Write(a, "customer", customerId, "السائق الافتراضي للعميل", $"{open?.DriverId} ← {driverId}");
    }

    /// <summary>Who held each vehicle at a moment in time.</summary>
    public Task<List<VehicleAssignment>> WhoAtAsync(DateTimeOffset at, CancellationToken ct = default) =>
        db.VehicleAssignments.Where(x => x.From <= at && (x.To == null || x.To > at)).ToListAsync(ct);
}

public record CreateTrip(Guid CustomerId, Guid? DriverId, Guid? VehicleId, Guid StationId, [Required, StringLength(60)] string SourceRef, [Required, StringLength(40)] string Product, [Range(typeof(decimal), "0", "200000")] decimal QtyRequested, DateTimeOffset? Eta, [Range(typeof(decimal), "0", "100000")] decimal? Rate);
public record LoadInput(Guid VehicleId, [Range(typeof(decimal), "0", "200000")] decimal Qty, [Required, StringLength(60)] string DocNo, [Range(typeof(decimal), "0", "9999999")] decimal Odometer, [Range(typeof(decimal), "0", "999999999")] decimal? MeterBaseline, [Range(typeof(decimal), "0", "999999999")] decimal? MeterStart, [Range(typeof(decimal), "0", "999999999")] decimal? MeterEnd, [StringLength(200)] string? OutletSeals, [StringLength(200)] string? Location, [Range(-90, 90)] double? Lat, [Range(-180, 180)] double? Lng, DateTimeOffset? CapturedAt);
public record ArriveInput([Required, StringLength(200)] string Location, [Range(-90, 90)] double? Lat, [Range(-180, 180)] double? Lng);
public record UnloadInput(bool SealsOk, [StringLength(1000)] string? SealNotes);
public record DeliverInput([Range(typeof(decimal), "0", "999999999")] decimal? MeterBefore, [Range(typeof(decimal), "0", "999999999")] decimal? MeterAfter, [StringLength(60)] string? TicketNo, [Range(typeof(decimal), "0", "200000")] decimal Received, [Range(typeof(decimal), "0", "200000")] decimal LeftInTank, [Range(typeof(decimal), "0", "200000")] decimal Returned, [Required, StringLength(60)] string ReceiptNo, bool HasMoreStops, DateTimeOffset? CapturedAt);
public record HandoverInput(Guid ToDriverId, Guid? VehicleId, [Range(typeof(decimal), "0", "200000")] decimal? Remaining, [Range(typeof(decimal), "0", "999999999")] decimal? MeterReading, [Required, StringLength(1000)] string Note, bool MoveVehicle);

public class TripService(FuelUpDb db, MeterLedger meters, AssignmentService assign, AuditWriter audit)
{
    public async Task<Tolerance> ToleranceAsync(CancellationToken ct = default)
    {
        var s = await db.Settings.Where(x => x.Key == "tolAbs" || x.Key == "tolPct").ToDictionaryAsync(x => x.Key, x => x.Value, ct);
        decimal P(string k, decimal d) => s.TryGetValue(k, out var v) && decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var x) ? x : d;
        return new Tolerance(P("tolAbs", 20m), P("tolPct", 0.3m));
    }

    public Task<Trip?> FindAsync(Guid id, CancellationToken ct = default) =>
        db.Trips.Include(t => t.Loading).Include(t => t.Deliveries).Include(t => t.Handovers).Include(t => t.Corrections).FirstOrDefaultAsync(t => t.Id == id, ct);

    private async Task<Trip> GetAsync(Guid id, CancellationToken ct) => await FindAsync(id, ct) ?? throw new NotFoundException("الطلب");

    private static void RequireDriverOf(Actor a, Trip t)
    {
        a.Require(Section.Driver, PermissionLevel.Edit);
        if (a.DriverId != t.DriverId && !a.Can(Section.Ops, PermissionLevel.Edit)) throw new ForbiddenException();
    }

    private async Task<TripSnapshot> SnapshotAsync(Trip t, CancellationToken ct)
    {
        var d = await db.Drivers.FindAsync([t.DriverId], ct);
        var v = t.VehicleId is { } vid ? await db.Vehicles.FindAsync([vid], ct) : null;
        var c = await db.Customers.FindAsync([t.CustomerId], ct);
        var s = await db.Stations.FindAsync([t.StationId], ct);
        var m = t.MeterId is { } mid ? await db.Meters.FindAsync([mid], ct) : null;
        return new TripSnapshot(d?.Name ?? "", d?.EmpNo, v?.Plate ?? "", c?.Name ?? "", c?.Project, c?.Site ?? "", s?.Name ?? "", s?.Type ?? StationType.Station, s?.Lat, s?.Lng, m?.Serial, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Next order number FU-yyMM-####, unique per company. On PostgreSQL a single atomic UPSERT on a counter row hands out
    /// numbers, so dispatchers creating orders at the same moment never collide (found by the load test).
    /// A number taken by a request that then fails is not reused (gaps are possible, duplicates are not).
    /// </summary>
    private async Task<string> NextNoAsync(CancellationToken ct)
    {
        var period = DateTime.UtcNow.ToString("yyMM", CultureInfo.InvariantCulture);
        var prefix = $"FU-{period}-";
        int n;
        if (db.IsPostgres && db.CurrentCompanyId is { } cid)
        {
            var like = prefix + "%";
            var from = prefix.Length + 1;
            n = (await db.Database.SqlQuery<int>($"""
                INSERT INTO trip_counters (company_id, period, last)
                VALUES ({cid}, {period}, COALESCE((SELECT max(CAST(substring(no from {from}) AS int)) FROM trips WHERE company_id = {cid} AND no LIKE {like}), 0) + 1)
                ON CONFLICT (company_id, period) DO UPDATE SET last = trip_counters.last + 1
                RETURNING last AS "Value"
                """).ToListAsync(ct))[0];
        }
        else
        {
            var last = await db.Trips.Where(t => t.No.StartsWith(prefix)).OrderByDescending(t => t.No).Select(t => t.No).FirstOrDefaultAsync(ct);
            n = (last is null ? 0 : int.Parse(last[prefix.Length..], CultureInfo.InvariantCulture)) + 1;
        }
        return prefix + n.ToString("0000", CultureInfo.InvariantCulture);
    }

    public async Task<Trip> CreateAsync(Actor a, CreateTrip input, CancellationToken ct = default)
    {
        a.Require(Section.Ops, PermissionLevel.Edit);
        if (input.QtyRequested <= 0) throw new DomainException("الكمية المطلوبة يجب أن تكون أكبر من صفر.");
        await Refs.EnsureAsync<Customer>(db, input.CustomerId, "العميل", ct);
        await Refs.EnsureAsync<Station>(db, input.StationId, "موقع التعبئة", ct);
        await Refs.EnsureAsync<Driver>(db, input.DriverId, "السائق", ct);
        await Refs.EnsureAsync<Vehicle>(db, input.VehicleId, "المركبة", ct);
        var defaultDriver = await assign.DefaultDriverOfCustomerAsync(input.CustomerId, ct);
        var driverId = input.DriverId ?? defaultDriver ?? throw new DomainException("حدد السائق؛ لا يوجد سائق افتراضي لهذا العميل.");
        var t = new Trip
        {
            No = await NextNoAsync(ct), CreatedBy = a.UserId, CustomerId = input.CustomerId, DriverId = driverId, VehicleId = input.VehicleId, StationId = input.StationId,
            SourceRef = input.SourceRef, Product = input.Product, QtyRequested = input.QtyRequested, Eta = input.Eta, Rate = input.Rate,
            DriverOverride = defaultDriver != null && defaultDriver != driverId,
        };
        if (t.VehicleId is { } v) t.MeterId = await db.Vehicles.Where(x => x.Id == v).Select(x => x.MeterId).FirstOrDefaultAsync(ct);
        t.Snapshot = await SnapshotAsync(t, ct);
        db.Trips.Add(t);
        audit.Write(a, "trip", t.Id, "إنشاء طلب النقل", $"{t.QtyRequested} لتر {t.Product}");
        audit.Notify(driverId, $"طلب جديد {t.No}: {t.Snapshot.CustomerName} · {t.QtyRequested:#,0} لتر {t.Product}", t.No);
        await db.SaveChangesAsync(ct);
        return t;
    }

    public async Task<Trip> LoadAsync(Actor a, Guid id, LoadInput input, CancellationToken ct = default)
    {
        await using var tx = db.IsPostgres ? await db.Database.BeginTransactionAsync(ct) : null;
        var t = await GetAsync(id, ct);
        RequireDriverOf(a, t);
        var mine = await assign.VehiclesOfDriverAsync(t.DriverId, ct);
        if (!mine.Contains(input.VehicleId)) throw new DomainException("المركبة المختارة ليست مسندة لك.");
        if (input.Qty <= 0) throw new DomainException("أدخل الكمية المحمّلة.");
        var meterId = await db.Vehicles.Where(x => x.Id == input.VehicleId).Select(x => x.MeterId).FirstOrDefaultAsync(ct);
        if (meterId is { } mid)
        {
            if (input.MeterStart is null || input.MeterEnd is null) throw new DomainException("أدخل قراءتي العداد عند بداية التعبئة ونهايتها.");
            if (input.MeterBaseline is { } bl) await meters.SetBaselineAsync(a, mid, bl, ct);
            else if (await meters.LastAsync(mid, ct) is null) throw new DomainException("أول استخدام لهذا العداد: سجّل القراءة التأسيسية مع صورة العداد، ويعتمدها المشرف.");
            await meters.AppendAsync(a, mid, [(input.MeterStart.Value, "بداية تعبئة"), (input.MeterEnd.Value, "نهاية تعبئة")], t.Id, true, input.CapturedAt, ct);
        }
        TripWorkflow.Move(t, TripStatus.Loaded);
        t.VehicleId = input.VehicleId;
        t.MeterId = meterId;
        t.Loading = new TripLoading { TripId = t.Id, Qty = input.Qty, DocNo = input.DocNo, Odometer = input.Odometer, MeterStart = input.MeterStart, MeterEnd = input.MeterEnd, OutletSeals = input.OutletSeals, Location = input.Location, Lat = input.Lat, Lng = input.Lng, At = DateTimeOffset.UtcNow, ByUserId = a.UserId };
        t.Snapshot = await SnapshotAsync(t, ct);
        audit.Write(a, "trip", t.Id, "بدء التنفيذ واعتماد التحميل", $"{input.Qty} لتر · سند {input.DocNo}" + (input.MeterStart is { } s ? $" · عداد {s}←{input.MeterEnd}" : ""));
        await db.SaveChangesAsync(ct);
        if (tx != null) await tx.CommitAsync(ct);
        return t;
    }

    public async Task<Trip> DepartAsync(Actor a, Guid id, CancellationToken ct = default)
    {
        var t = await GetAsync(id, ct); RequireDriverOf(a, t);
        TripWorkflow.Move(t, TripStatus.EnRoute); t.DepartedAt = DateTimeOffset.UtcNow;
        audit.Write(a, "trip", t.Id, "بدء الرحلة"); await db.SaveChangesAsync(ct); return t;
    }

    public async Task<Trip> ArriveAsync(Actor a, Guid id, ArriveInput input, CancellationToken ct = default)
    {
        var t = await GetAsync(id, ct); RequireDriverOf(a, t);
        var site = await db.Customers.Where(c => c.Id == t.CustomerId).Select(c => c.Site).FirstAsync(ct);
        TripWorkflow.Move(t, TripStatus.Arrived);
        t.Deliveries.Add(new TripDelivery { TripId = t.Id, Seq = t.Deliveries.Count + 1, Site = site, ArrivedAt = DateTimeOffset.UtcNow, ArrivalLocation = input.Location, Lat = input.Lat, Lng = input.Lng, SiteMismatch = input.Location != site });
        audit.Write(a, "trip", t.Id, "الوصول للتفريغ", input.Location); await db.SaveChangesAsync(ct); return t;
    }

    public async Task<Trip> UnloadAsync(Actor a, Guid id, UnloadInput input, CancellationToken ct = default)
    {
        var t = await GetAsync(id, ct); RequireDriverOf(a, t);
        var d = t.Deliveries.OrderBy(x => x.Seq).Last();
        TripWorkflow.Move(t, TripStatus.Unloading);
        d.SealsBroken = !input.SealsOk; d.SealNotes = input.SealNotes; d.UnloadStartAt = DateTimeOffset.UtcNow;
        audit.Write(a, "trip", t.Id, "فحص الأختام وبدء التفريغ", input.SealsOk ? "الأختام سليمة" : "أختام مختلفة: " + input.SealNotes); await db.SaveChangesAsync(ct); return t;
    }

    public async Task<Trip> DeliverAsync(Actor a, Guid id, DeliverInput input, CancellationToken ct = default)
    {
        await using var tx = db.IsPostgres ? await db.Database.BeginTransactionAsync(ct) : null;
        var t = await GetAsync(id, ct); RequireDriverOf(a, t);
        if (t.Status != TripStatus.Unloading) throw new DomainException("سجّل فحص الأختام وبدء التفريغ أولًا.");
        if (t.MeterId is { } mid)
        {
            if (input.MeterBefore is null || input.MeterAfter is null) throw new DomainException("أدخل قراءتي العداد قبل التفريغ وبعده.");
            await meters.AppendAsync(a, mid, [(input.MeterBefore.Value, "بداية تفريغ"), (input.MeterAfter.Value, "نهاية تفريغ")], t.Id, true, input.CapturedAt, ct);
        }
        var d = t.Deliveries.OrderBy(x => x.Seq).Last();
        d.MeterBefore = input.MeterBefore; d.MeterAfter = input.MeterAfter; d.TicketNo = input.TicketNo; d.Received = input.Received; d.LeftInTank = input.LeftInTank;
        d.Returned = input.Returned; d.ReceiptNo = input.ReceiptNo; d.HasMoreStops = input.HasMoreStops; d.SubmittedAt = DateTimeOffset.UtcNow;
        var (status, reason) = TripWorkflow.Finalize(t, await ToleranceAsync(ct), await meters.BaselinePendingAsync(t.MeterId, ct));
        TripWorkflow.Move(t, status);
        if (status == TripStatus.UnderReview) t.ReviewReason = reason;
        t.Snapshot = await SnapshotAsync(t, ct);
        audit.Write(a, "trip", t.Id, "تسجيل التسليم", $"عداد {d.MeterQty} لتر · سند {d.Received}");
        if (status != TripStatus.EnRoute) audit.Write(a, "trip", t.Id, status == TripStatus.AwaitingApproval ? "مطابقة سليمة، بانتظار اعتماد المشرف" : "تحويل للمراجعة", reason);
        await db.SaveChangesAsync(ct);
        if (tx != null) await tx.CommitAsync(ct);
        return t;
    }

    public async Task<Trip> ApproveAsync(Actor a, Guid id, string? note, CancellationToken ct = default)
    {
        a.Require(Section.Approve, PermissionLevel.Edit);
        var t = await GetAsync(id, ct);
        if (t.Status != TripStatus.AwaitingApproval) throw new DomainException("تُعتمد فقط العمليات «بانتظار الاعتماد». العمليات تحت المراجعة تحتاج قرارًا مكتوبًا.");
        TripWorkflow.Move(t, TripStatus.Approved);
        t.ApprovedBy = a.UserId; t.ApprovedAt = t.ClosedAt = DateTimeOffset.UtcNow; t.ApprovalNote = note;
        audit.Write(a, "trip", t.Id, "اعتماد العملية", note); await db.SaveChangesAsync(ct); return t;
    }

    public async Task<Trip> ResolveAsync(Actor a, Guid id, string decision, CancellationToken ct = default)
    {
        a.Require(Section.Approve, PermissionLevel.Edit);
        if (string.IsNullOrWhiteSpace(decision)) throw new DomainException("اكتب قرار المراجعة وسببه.");
        var t = await GetAsync(id, ct);
        if (t.Status != TripStatus.UnderReview) throw new DomainException("العملية ليست تحت المراجعة.");
        TripWorkflow.Move(t, TripStatus.Approved);
        t.Resolution = decision; t.ApprovedBy = a.UserId; t.ApprovedAt = t.ClosedAt = DateTimeOffset.UtcNow;
        audit.Write(a, "trip", t.Id, "اعتماد العملية بقرار مراجعة", decision); await db.SaveChangesAsync(ct); return t;
    }

    public async Task<Trip> CancelAsync(Actor a, Guid id, string reason, CancellationToken ct = default)
    {
        a.Require(Section.Ops, PermissionLevel.Edit);
        var t = await GetAsync(id, ct);
        TripWorkflow.Move(t, TripStatus.Cancelled);
        audit.Write(a, "trip", t.Id, "إلغاء الطلب", reason); audit.Notify(t.DriverId, $"أُلغي الطلب {t.No}", t.No);
        await db.SaveChangesAsync(ct); return t;
    }

    /// <summary>Move an order to another driver. Once loading started, hand-over is documented and the receiver confirms.</summary>
    public async Task<Trip> HandoverAsync(Actor a, Guid id, HandoverInput input, CancellationToken ct = default)
    {
        if (!a.Can(Section.Assign, PermissionLevel.Edit) && !a.Can(Section.Ops, PermissionLevel.Edit)) throw new ForbiddenException();
        if (string.IsNullOrWhiteSpace(input.Note)) throw new DomainException("سبب النقل مطلوب.");
        await Refs.EnsureAsync<Driver>(db, input.ToDriverId, "السائق", ct);
        await Refs.EnsureAsync<Vehicle>(db, input.VehicleId, "المركبة", ct);
        await using var tx = db.IsPostgres ? await db.Database.BeginTransactionAsync(ct) : null;
        var t = await GetAsync(id, ct);
        if (!TripWorkflow.IsOpen(t.Status)) throw new DomainException("لا يمكن نقل طلب مغلق.");
        var from = t.DriverId;
        if (t.Loading is null)
        {
            t.DriverId = input.ToDriverId; t.DriverOverride = true;
            audit.Write(a, "trip", t.Id, "تغيير سائق الطلب", input.Note);
        }
        else
        {
            if (t.MeterId is { } mid)
            {
                if (input.MeterReading is null) throw new DomainException("قراءة العداد وقت النقل مطلوبة.");
                await meters.AppendAsync(a, mid, [(input.MeterReading.Value, "نقل مسؤولية")], t.Id, false, null, ct);
            }
            var vehicleTo = input.VehicleId ?? t.VehicleId;
            t.Handovers.Add(new TripHandover { TripId = t.Id, At = DateTimeOffset.UtcNow, FromDriverId = from, ToDriverId = input.ToDriverId, VehicleFrom = t.VehicleId, VehicleTo = vehicleTo, Remaining = input.Remaining, MeterReading = input.MeterReading, Note = input.Note, ByUserId = a.UserId });
            if (vehicleTo != t.VehicleId) { t.VehicleId = vehicleTo; t.MeterId = await db.Vehicles.Where(v => v.Id == vehicleTo).Select(v => v.MeterId).FirstOrDefaultAsync(ct); }
            t.DriverId = input.ToDriverId;
            if (input.MoveVehicle && t.VehicleId is { } vid) await assign.AssignVehicleAsync(a with { Levels = new Dictionary<Section, PermissionLevel> { [Section.Assign] = PermissionLevel.Edit } }, vid, input.ToDriverId, "نقل الطلب " + t.No, ct);
            audit.Write(a, "trip", t.Id, "نقل الطلب الجاري", $"متبقٍ {input.Remaining} لتر · عداد {input.MeterReading} · {input.Note}");
        }
        audit.Notify(input.ToDriverId, $"نُقل إليك الطلب {t.No}" + (t.Loading is null ? "" : ". أكّد الاستلام من التطبيق."), t.No);
        audit.Notify(from, $"نُقل الطلب {t.No} إلى سائق آخر", t.No);
        t.Snapshot = await SnapshotAsync(t, ct);
        await db.SaveChangesAsync(ct);
        if (tx != null) await tx.CommitAsync(ct);
        return t;
    }

    public async Task<Trip> AckHandoverAsync(Actor a, Guid id, bool confirmed, string? note, CancellationToken ct = default)
    {
        var t = await GetAsync(id, ct);
        var h = t.Handovers.OrderBy(x => x.At).LastOrDefault() ?? throw new DomainException("لا يوجد نقل لهذا الطلب.");
        if (a.DriverId != h.ToDriverId) throw new ForbiddenException();
        if (h.Ack != AckState.Pending) throw new DomainException("تم الرد على هذا النقل مسبقًا.");
        if (!confirmed && string.IsNullOrWhiteSpace(note)) throw new DomainException("اكتب سبب عدم المطابقة.");
        h.Ack = confirmed ? AckState.Confirmed : AckState.Disputed; h.AckAt = DateTimeOffset.UtcNow; h.AckNote = note;
        audit.Write(a, "trip", t.Id, confirmed ? "تأكيد استلام الحمولة بعد النقل" : "اعتراض على النقل", note);
        await db.SaveChangesAsync(ct); return t;
    }

    private static readonly string[] CorrectableFields = ["qty", "docNo", "odometer"];

    public async Task<TripCorrection> RequestCorrectionAsync(Actor a, Guid id, string field, string newValue, string reason, CancellationToken ct = default)
    {
        if (!a.Can(Section.Ops, PermissionLevel.Edit) && !a.Can(Section.Driver, PermissionLevel.Edit)) throw new ForbiddenException();
        if (!CorrectableFields.Contains(field)) throw new DomainException("هذا البيان لا يُصحح.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("سبب التصحيح مطلوب.");
        newValue = newValue.Trim();
        if (field is "qty" or "odometer" && (!decimal.TryParse(newValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var num) || num < 0 || num > 9_999_999))
            throw new DomainException("القيمة الجديدة يجب أن تكون رقمًا صحيحًا موجبًا.");
        var t = await GetAsync(id, ct);
        if (!a.Can(Section.Ops, PermissionLevel.Edit) && a.DriverId != t.DriverId) throw new ForbiddenException();   // a driver corrects only his own trips
        var l = t.Loading ?? throw new DomainException("لا يوجد تحميل لتصحيحه.");
        var old = field switch { "qty" => l.Qty.ToString(CultureInfo.InvariantCulture), "docNo" => l.DocNo, _ => l.Odometer?.ToString(CultureInfo.InvariantCulture) };
        var c = new TripCorrection { TripId = t.Id, Field = field, OldValue = old, NewValue = newValue, Reason = reason, RequestedBy = a.UserId, At = DateTimeOffset.UtcNow, State = ApprovalState.Pending };
        t.Corrections.Add(c);
        audit.Write(a, "trip", t.Id, "طلب تصحيح", reason, old, newValue);
        await db.SaveChangesAsync(ct); return c;
    }

    public async Task DecideCorrectionAsync(Actor a, Guid id, Guid correctionId, bool approve, CancellationToken ct = default)
    {
        a.Require(Section.Approve, PermissionLevel.Edit);
        var t = await GetAsync(id, ct);
        var c = t.Corrections.FirstOrDefault(x => x.Id == correctionId) ?? throw new NotFoundException("طلب التصحيح");
        if (c.State != ApprovalState.Pending) throw new DomainException("تم البت في هذا الطلب.");
        c.State = approve ? ApprovalState.Approved : ApprovalState.Rejected; c.DecidedBy = a.UserId; c.DecidedAt = DateTimeOffset.UtcNow;
        if (approve && t.Loading is { } l)
        {
            switch (c.Field)
            {
                case "qty": l.Qty = decimal.Parse(c.NewValue, CultureInfo.InvariantCulture); break;
                case "docNo": l.DocNo = c.NewValue; break;
                case "odometer": l.Odometer = decimal.Parse(c.NewValue, CultureInfo.InvariantCulture); break;
            }
        }
        audit.Write(a, "trip", t.Id, approve ? "موافقة على تصحيح" : "رفض تصحيح", c.Reason, c.OldValue, c.NewValue);
        await db.SaveChangesAsync(ct);
    }
}
