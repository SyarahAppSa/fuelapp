using System.Globalization;
using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FuelUp.Api;

/// <summary>
/// Sample «طلبات العملاء» and «وثائق المركبات والسائقين» (Seed__Sample only, once each: markers "sample_orders" and "sample_documents").
/// Works on databases that already hold the sample trips: the recent sample trips are grouped into ~22 customer orders (1–3 trucks each),
/// plus a few orders not dispatched yet; older trips stay without an order (legacy, can be linked from the order page).
/// Mix of «نقل فقط» / «نقل وتوريد», urgent / scheduled, a couple late, one delivered-not-closed, one cancelled; transport 0.08–0.15 SAR/L, diesel 1.66–1.75 SAR/L.
/// Documents: registration, insurance, periodic inspection and operating card for the 8 sample trucks and the demo truck (a few within 30 days,
/// one expired registration on a sample truck — never the demo truck), driving licences for the sample drivers (one expired).
/// </summary>
public static class SampleOrders
{
    private const string OrdersMarker = "sample_orders", DocsMarker = "sample_documents";
    private static readonly TimeSpan Ksa = TimeSpan.FromHours(3);

    public static async Task RunAsync(FuelUpDb db, ILogger log)
    {
        await DocumentsAsync(db, log);
        if (await db.Settings.AnyAsync(s => s.Key == OrdersMarker)) return;
        var admin = await db.Users.Where(u => u.Role == Role.Admin).OrderBy(u => u.CreatedAt).FirstAsync();
        var rnd = new Random(3030);
        var tol = await new TripService(db, new MeterLedger(db), new AssignmentService(db, new AuditWriter(db)), new AuditWriter(db)).ToleranceAsync();
        var customers = await db.Customers.ToDictionaryAsync(c => c.Id);
        var trips = await db.Trips.Include(t => t.Loading).Include(t => t.Deliveries)
            .Where(t => t.OrderId == null && !t.SourceRef.StartsWith("DEMO")).OrderBy(t => t.CreatedAt).ToListAsync();
        var now = DateTimeOffset.UtcNow;

        // group trips of the same customer created within 3 days (max 3 trucks per order)
        var groups = new List<List<Trip>>();
        var open = new Dictionary<Guid, List<Trip>>();
        foreach (var t in trips.Where(t => t.Status != TripStatus.Cancelled))
        {
            if (open.TryGetValue(t.CustomerId, out var g) && g.Count < 3 && t.CreatedAt - g[0].CreatedAt < TimeSpan.FromDays(3) && rnd.Next(10) < 7) { g.Add(t); continue; }
            g = [t]; groups.Add(g); open[t.CustomerId] = g;
        }
        var chosen = groups.Skip(Math.Max(0, groups.Count - 21)).ToList();
        var cancelledTrip = trips.LastOrDefault(t => t.Status == TripStatus.Cancelled);
        if (cancelledTrip != null) chosen.Insert(0, [cancelledTrip]);

        // numbers of past months are given here; this month's come from the live counter, so they never collide with real orders
        var perPeriod = new Dictionary<string, int>();
        async Task<string> No(DateTimeOffset at)
        {
            var p = at.UtcDateTime.ToString("yyMM", CultureInfo.InvariantCulture);
            if (p == now.UtcDateTime.ToString("yyMM", CultureInfo.InvariantCulture)) return await OrderService.NextNoAsync(db, default);
            perPeriod[p] = perPeriod.GetValueOrDefault(p) + 1;
            return $"SO-{p}-{perPeriod[p]:0000}";
        }
        // the open groups (trips still running) are the late candidates
        var running = chosen.Where(g => g.Any(t => t.Status is TripStatus.WaitingLoad or TripStatus.Loaded or TripStatus.EnRoute)).ToList();
        var late = running.Take(2).ToHashSet();
        var finished = chosen.Where(g => g.All(OrderRules.Finished)).ToList();
        var keepDelivered = finished.LastOrDefault(g => g.All(t => t.Status == TripStatus.Approved));
        var orders = new List<CustomerOrder>();
        foreach (var g in chosen)
        {
            var first = g[0];
            var c = customers[first.CustomerId];
            var supply = rnd.Next(10) < 5;
            var created = first.CreatedAt.AddHours(-2 - rnd.Next(0, 20));
            var o = new CustomerOrder
            {
                No = await No(created), CustomerId = c.Id, CustomerName = c.Name, Site = c.Site, Type = supply ? OrderType.TransportAndSupply : OrderType.Transport, Product = first.Product,
                QtyRequested = g.Sum(t => t.QtyRequested), Priority = rnd.Next(10) < 3 ? OrderPriority.Urgent : OrderPriority.Scheduled,
                RequestedFrom = created.AddHours(4), RequestedTo = late.Contains(g) ? now.AddHours(-20) : running.Contains(g) ? now.AddHours(24 + rnd.Next(0, 24)) : created.AddDays(2),
                CustomerRef = rnd.Next(10) < 6 ? $"PO-{rnd.Next(41000, 49999)}" : null, Notes = g.Count > 1 ? $"يحتاج {g.Count} شاحنات" : null,
                TransportRatePerLiter = Math.Round(0.08m + rnd.Next(0, 8) / 100m, 2), FuelPricePerLiter = supply ? Math.Round(1.66m + rnd.Next(0, 10) / 100m, 2) : null,
                CreatedAt = created, CreatedBy = admin.Id,
            };
            foreach (var t in g) t.OrderId = o.Id;
            o.Status = OrderRules.Derive(o, g, tol);
            db.Orders.Add(o);
            Audit(db, admin.Id, o.Id, created, "إنشاء طلب العميل", $"{o.No} · {OrderRules.TypeLabel(o.Type)} · {o.QtyRequested:#,0} لتر {o.Product} · {OrderRules.PriorityLabel(o.Priority)}");
            foreach (var t in g)
            {
                Audit(db, admin.Id, o.Id, t.CreatedAt, "إسناد رحلة", $"{t.No} · {t.Snapshot?.Plate} · {t.Snapshot?.DriverName} · {t.QtyRequested:#,0} لتر");
                db.Audit.Add(new AuditEntry { At = t.CreatedAt, UserId = admin.Id, Entity = "trip", EntityId = t.Id, Action = "ضمن طلب العميل", Detail = o.No });
            }
            if (g.All(t => t.Status == TripStatus.Cancelled))
            {
                (o.Status, o.StatusReason, o.ClosedAt, o.ClosedBy) = (OrderStatus.Cancelled, "ألغى العميل الطلب قبل التحميل", created.AddHours(3), admin.Id);
                Audit(db, admin.Id, o.Id, created.AddHours(3), "إلغاء الطلب", o.StatusReason);
            }
            else if (finished.Contains(g) && g != keepDelivered && g.All(t => t.Status == TripStatus.Approved) && g.Max(t => t.ApprovedAt ?? t.CreatedAt) < now.AddDays(-2))
            {
                var at = g.Max(t => t.ApprovedAt ?? t.CreatedAt).AddHours(2);
                var full = o.Status == OrderStatus.Delivered;
                (o.Status, o.StatusReason, o.ClosedAt, o.ClosedBy) = (OrderStatus.Closed, full ? "اكتمل التسليم وتمت المطابقة" : "أُغلق بعد اعتماد فرق الكمية", at, admin.Id);
                Audit(db, admin.Id, o.Id, at, "إغلاق الطلب", o.StatusReason);
            }
            orders.Add(o);
        }

        // orders not dispatched yet (for «لوحة التوزيع»): urgent today, scheduled in two days, one already late
        var list = customers.Values.OrderBy(c => c.Name).ToList();
        (int Cust, decimal Qty, OrderPriority Pri, double DueHours, OrderType Type)[] fresh =
            [(0, 24000, OrderPriority.Urgent, 6, OrderType.TransportAndSupply), (1, 15000, OrderPriority.Scheduled, 48, OrderType.Transport), (2, 8000, OrderPriority.Scheduled, -6, OrderType.TransportAndSupply)];
        foreach (var (ci, qty, pri, due, type) in fresh)
        {
            var c = list[ci % list.Count];
            var created = now.AddHours(-30 + ci);
            var o = new CustomerOrder
            {
                No = await No(now), CustomerId = c.Id, CustomerName = c.Name, Site = c.Site, Type = type, Product = "ديزل", QtyRequested = qty, Priority = pri,
                RequestedFrom = now.AddHours(-24), RequestedTo = now.AddHours(due), CustomerRef = $"PO-{rnd.Next(41000, 49999)}", CreatedAt = created, CreatedBy = admin.Id,
                TransportRatePerLiter = Math.Round(0.08m + rnd.Next(0, 8) / 100m, 2), FuelPricePerLiter = type == OrderType.TransportAndSupply ? Math.Round(1.66m + rnd.Next(0, 10) / 100m, 2) : null,
                Notes = qty > 20000 ? "يحتاج شاحنتين" : null,
            };
            db.Orders.Add(o); orders.Add(o);
            Audit(db, admin.Id, o.Id, created, "إنشاء طلب العميل", $"{o.No} · {OrderRules.TypeLabel(o.Type)} · {o.QtyRequested:#,0} لتر · {OrderRules.PriorityLabel(o.Priority)}");
        }
        db.Settings.Add(new Setting { Key = OrdersMarker, Value = now.ToString("o") });
        await db.SaveChangesAsync();
        log.LogWarning("Sample orders: {Orders} customer orders, {Trips} trips linked", orders.Count, chosen.Sum(g => g.Count));
    }

    private static void Audit(FuelUpDb db, Guid user, Guid order, DateTimeOffset at, string action, string? detail) =>
        db.Audit.Add(new AuditEntry { At = at, UserId = user, Entity = "order", EntityId = order, Action = action, Detail = detail });

    private static async Task DocumentsAsync(FuelUpDb db, ILogger log)
    {
        if (await db.Settings.AnyAsync(s => s.Key == DocsMarker)) return;
        var admin = await db.Users.Where(u => u.Role == Role.Admin).OrderBy(u => u.CreatedAt).FirstAsync();
        var types = await db.DocumentTypes.ToListAsync();
        DocumentType? T(string code) => types.FirstOrDefault(t => t.Code == code);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Add(Ksa));
        var trucks = await db.Vehicles.Where(v => v.Plate.EndsWith("(ت)")).OrderBy(v => v.InternalNo).ToListAsync();
        var demo = await db.Vehicles.FirstOrDefaultAsync(v => v.Plate == "ت ج ر 2026");
        var have = (await db.Documents.Select(d => new { d.TypeId, d.OwnerId }).ToListAsync()).Select(x => (x.TypeId, x.OwnerId)).ToHashSet();
        var rnd = new Random(4040);
        var made = 0;
        void Add(DocumentType? type, Guid owner, string number, int expiresInDays, string? issuer = null, string? category = null, int validDays = 365)
        {
            if (type is null || have.Contains((type.Id, owner))) return;
            var expiry = today.AddDays(expiresInDays);
            var d = new FleetDocument
            {
                TypeId = type.Id, OwnerKind = type.AppliesTo, OwnerId = owner, Number = number, Issuer = type.Fields.Any(f => f.Key == "issuer") ? issuer : null,
                Category = type.Fields.Any(f => f.Key == "category") ? category : null, IssueDate = expiry.AddDays(-validDays), Expiry = expiry, CreatedBy = admin.Id,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-rnd.Next(20, 60)),
            };
            var v = new FleetDocumentVersion { DocumentId = d.Id, Version = 1, Kind = "تسجيل", Number = d.Number, Issuer = d.Issuer, Category = d.Category, IssueDate = d.IssueDate, Expiry = d.Expiry, At = d.CreatedAt, ByUserId = admin.Id, Note = "بيانات تجريبية" };
            d.CurrentVersionId = v.Id; d.UpdatedAt = d.CreatedAt;
            db.Documents.Add(d); db.DocumentVersions.Add(v);
            db.Audit.Add(new AuditEntry { At = d.CreatedAt, UserId = admin.Id, Entity = "document", EntityId = d.Id, Action = "تسجيل وثيقة", Detail = $"«{type.Name}» (تجريبي)" });
            have.Add((type.Id, owner)); made++;
        }
        string[] insurers = ["التعاونية للتأمين", "ملاذ للتأمين", "بوبا العربية", "الراجحي تكافل"];
        for (var i = 0; i < trucks.Count; i++)
        {
            var v = trucks[i];
            // index 6: registration expired 4 days ago (a mandatory document: dispatch refused unless overridden); 1 and 4: close to expiry
            Add(T("registration"), v.Id, $"REG-{60010 + i}", i == 6 ? -4 : 120 + i * 37, validDays: 1095);
            Add(T("insurance"), v.Id, $"POL-{88200 + i}", i == 1 ? 12 : 60 + i * 29, insurers[i % insurers.Length]);
            Add(T("inspection"), v.Id, $"FAH-{51000 + i}", i == 4 ? 5 : 45 + i * 23);
            Add(T("opcard"), v.Id, $"TGA-{7300 + i}", i == 3 ? 26 : 150 + i * 19);
        }
        if (demo != null)   // the demo truck stays valid (some close to expiry, nothing expired)
        {
            Add(T("registration"), demo.Id, "REG-DEMO-2026", 300, validDays: 1095);
            Add(T("insurance"), demo.Id, "POL-DEMO-2026", 200, insurers[0]);
            Add(T("inspection"), demo.Id, "FAH-DEMO-2026", 20);
            Add(T("opcard"), demo.Id, "TGA-DEMO-2026", 240);
        }
        var drivers = await db.Drivers.Where(d => d.EmpNo != null && (EF.Functions.Like(d.EmpNo, "E2%") || d.EmpNo == "DEMO-D1")).OrderBy(d => d.EmpNo).ToListAsync();
        for (var i = 0; i < drivers.Count; i++)
        {
            var d = drivers[i];
            // the last sample driver (no truck of his own) has an expired licence; one licence expires within 30 days
            var days = d.EmpNo == "DEMO-D1" ? 900 : i == drivers.Count - 1 ? -15 : i == 2 ? 25 : 200 + i * 61;
            Add(T("license"), d.Id, $"{1100000000 + rnd.Next(0, 99999999)}", days, category: i % 3 == 0 ? "نقل ثقيل" : "نقل عام", validDays: 3650);
        }
        db.Settings.Add(new Setting { Key = DocsMarker, Value = DateTimeOffset.UtcNow.ToString("o") });
        await db.SaveChangesAsync();
        log.LogWarning("Sample documents: {Count} fleet documents registered", made);
    }
}
