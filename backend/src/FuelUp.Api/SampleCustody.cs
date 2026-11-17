using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FuelUp.Api;

/// <summary>
/// Sample custody & spare-parts data (part of Seed__Sample), for the company whose tenant is already set: warehouses, ~20 catalog items,
/// parts installed on the trucks with their purchase and installation history, replacements, parts under repair, damaged and lost
/// parts, warranties expiring soon, quantity stock with items below their minimum, work orders with labor, and custody held by drivers.
/// Runs once (marker setting "sample_custody"), also on a database that already had the earlier sample orders ("sample_data").
/// </summary>
public static class SampleCustody
{
    public static async Task RunAsync(FuelUpDb db, ILogger log)
    {
        if (await db.Settings.AnyAsync(s => s.Key == "sample_custody")) { log.LogInformation("Sample custody data already loaded"); return; }
        // the sample trucks (plates marked "(ت)"); a database without them (development demo) uses its own trucks
        var vehicles = await db.Vehicles.Where(v => v.Plate.Contains("(ت)")).OrderBy(v => v.InternalNo).Take(8).ToListAsync();
        if (vehicles.Count == 0) vehicles = await db.Vehicles.OrderBy(v => v.InternalNo).ThenBy(v => v.Plate).Take(8).ToListAsync();
        var admin = await db.Users.Where(u => u.Role == Role.Admin).OrderBy(u => u.CreatedAt).FirstOrDefaultAsync();
        if (vehicles.Count == 0 || admin is null) { log.LogWarning("Sample custody skipped: no vehicles or administrator"); return; }
        log.LogWarning("Loading sample custody data for {Vehicles} trucks…", vehicles.Count);
        await using var tx = await db.Database.BeginTransactionAsync();   // all or nothing: a failure leaves no half-loaded sample

        var svc = new CustodyService(db);
        var rnd = new Random(2027);
        var now = DateTimeOffset.UtcNow;
        var today = Ksa.Day(now);
        DateTimeOffset Ago(double days, int hour = 8) => new(now.UtcDateTime.AddDays(-days).Date.AddHours(hour - 3), TimeSpan.Zero);   // hour in Saudi time
        DateOnly Day(DateTimeOffset t) => Ksa.Day(t);
        var uid = admin.Id;

        // ── warehouses, employees, catalog ─────────────────────────────────────────────────────────────────────────
        var main = new Warehouse { Name = "المستودع الرئيسي — الدمام (تجريبي)", Location = "الدمام، المنطقة الصناعية الثانية", Keeper = "أمين المستودع" };
        var site = new Warehouse { Name = "مستودع موقع الجبيل (تجريبي)", Location = "الجبيل الصناعية", Keeper = "مشرف الموقع" };
        var techTyre = new Employee { Name = "ياسر الفني (تجريبي)", Role = "فني إطارات", EmpNo = "T301" };
        var techElec = new Employee { Name = "حسن الكهربائي (تجريبي)", Role = "فني كهرباء", EmpNo = "T302" };
        var keeper = new Employee { Name = "منصور أمين المستودع (تجريبي)", Role = "أمين مستودع", EmpNo = "S101" };
        db.AddRange(main, site, techTyre, techElec, keeper);

        PartCatalog C(string code, string name, PartTracking tr, string cat, string? brand, string? model, string unit, decimal? min, decimal? reorder, decimal cost, int? inspect = null, string? specs = null, string? mfr = null) =>
            new() { Code = code, Name = name, Tracking = tr, Category = cat, Brand = brand, Model = model, Unit = unit, MinQty = min, ReorderQty = reorder, UnitCost = cost, InspectEveryDays = inspect, Specs = specs, MfrPartNo = mfr };
        const PartTracking I = PartTracking.Individual, Q = PartTracking.Quantity;
        var tyre = C("TYR-315", "إطار 315/80R22.5", I, "إطار", "Michelin", "X Multi Z", "إطار", 6, 12, 1450, specs: "حمولة 156/150L", mfr: "MIC-315XMZ");
        var tyreT = C("TYR-385", "إطار مقطورة 385/65R22.5", I, "إطار", "Bridgestone", "R168", "إطار", 4, 8, 1650);
        var batt = C("BAT-200", "بطارية 12V 200Ah", I, "بطارية", "Varta", "Promotive Black", "بطارية", 3, 6, 900, specs: "12V · 200Ah · 1050A");
        var meterC = C("MTR-LCM7", "عداد وقود إلكتروني LC M7", I, "عداد وقود", "Liquid Controls", "M7", "عداد", null, null, 8500, 365);
        var pump = C("PMP-BLK", "مضخة تفريغ Blackmer", I, "مضخة تفريغ", "Blackmer", "TXD2.5", "مضخة", null, null, 12000, 180);
        var gps = C("GPS-FMB", "جهاز تتبع Teltonika FMB920", I, "جهاز تتبع", "Teltonika", "FMB920", "جهاز", 2, 4, 650);
        var hose = C("HOS-3IN", "خرطوم تفريغ 3 إنش", I, "خرطوم", "Continental", "Fuel Master", "خرطوم", 2, 4, 750, 180);
        var ext = C("EXT-6KG", "طفاية حريق بودرة 6 كجم", I, "معدات سلامة", "NAFFCO", "DCP-6", "طفاية", 4, 8, 180, 90);
        var kit = C("SFT-KIT", "حقيبة أدوات وسلامة السائق", I, "أداة", null, null, "حقيبة", null, null, 350);
        var alt = C("ALT-24V", "دينمو شحن 24V", I, "أخرى", "Bosch", "HD 28V 80A", "قطعة", 1, 2, 2800);
        var oil = C("OIL-1540", "زيت محرك 15W40", Q, "زيوت", "Petromin", "SuperDiesel", "لتر", 60, 200, 14);
        var hyd = C("OIL-HYD", "زيت هيدروليك 68", Q, "زيوت", "Petromin", null, "لتر", 40, 100, 12);
        var fOil = C("FLT-OIL", "فلتر زيت محرك", Q, "فلاتر", "Fleetguard", "LF9009", "حبة", 10, 30, 45);
        var fFuel = C("FLT-FUEL", "فلتر ديزل", Q, "فلاتر", "Fleetguard", "FS1040", "حبة", 10, 30, 55);
        var fAir = C("FLT-AIR", "فلتر هواء", Q, "فلاتر", "Donaldson", "P532503", "حبة", 6, 12, 120);
        var nuts = C("BLT-WHL", "صامولة عجلة M22", Q, "مسامير وقطع صغيرة", null, null, "حبة", 50, 200, 6);
        var grease = C("GRS-EP2", "شحم EP2", Q, "مواد استهلاكية", "Total", null, "كجم", 10, 30, 18);
        var adblue = C("ADB-32", "سائل AdBlue", Q, "مواد استهلاكية", null, null, "لتر", 100, 400, 3);
        var bulbs = C("BLB-24V", "لمبة 24V H7", Q, "مواد استهلاكية", "Philips", null, "حبة", 20, 50, 8);
        var catalog = new[] { tyre, tyreT, batt, meterC, pump, gps, hose, ext, kit, alt, oil, hyd, fOil, fFuel, fAir, nuts, grease, adblue, bulbs };
        foreach (var c in catalog) if (await db.PartCatalog.AnyAsync(x => x.Code == c.Code)) c.Code += "-S";
        db.PartCatalog.AddRange(catalog);
        await db.SaveChangesAsync();

        // ── helpers writing the same rows the API writes (status + append-only movement with its real date) ───────────
        PartMovement Mv(string type, Part p, PartStatus? before, DateTimeOffset at, Action<PartMovement>? fill = null)
        {
            var m = new PartMovement { Type = type, PartId = p.Id, CatalogId = p.CatalogId, Qty = 1, StatusBefore = before?.ToString(), StatusAfter = p.Status.ToString(), At = at, ByUserId = uid };
            fill?.Invoke(m);
            db.PartMovements.Add(m);
            return m;
        }
        var serialNo = 0;
        async Task<Part> Buy(PartCatalog c, DateTimeOffset at, string supplier, int warrantyMonths, bool serial = true, Warehouse? w = null)
        {
            var p = new Part
            {
                CatalogId = c.Id, InternalNo = await svc.NextNoAsync("PART", "P-", 6), Brand = c.Brand, Model = c.Model, Supplier = supplier, InvoiceNo = $"INV-{Day(at):yyMM}-{rnd.Next(100, 999)}",
                PurchaseDate = Day(at), Cost = c.UnitCost, WarrantyStart = warrantyMonths > 0 ? Day(at) : null, WarrantyEnd = warrantyMonths > 0 ? Day(at).AddMonths(warrantyMonths) : null,
                WarrantyTerms = warrantyMonths > 0 ? $"{warrantyMonths} شهرًا ضد عيوب الصناعة" : null, Specs = c.Specs, Status = PartStatus.Stock, WarehouseId = (w ?? main).Id, Bin = $"رف {(char)('A' + rnd.Next(0, 5))}-{rnd.Next(1, 9)}",
                Condition = "جديد", CreatedAt = at,
                NextInspection = c.InspectEveryDays is { } d ? Day(at).AddDays(d) : null, ExpiryDate = c.Category == "معدات سلامة" || c.Category == "خرطوم" ? Day(at).AddYears(c.Category == "خرطوم" ? 3 : 1) : null,
            };
            if (serial) { p.Serial = $"{(c.Brand ?? "GEN")[..3].ToUpperInvariant()}{Day(at):yy}{++serialNo + 4000:00000}"; p.SerialKey = $"{(p.Brand ?? "").ToUpperInvariant()}|{p.Serial}"; }
            db.Parts.Add(p);
            Mv("receive", p, null, at, m => { m.ToWarehouseId = p.WarehouseId; m.Vendor = supplier; m.DocRef = p.InvoiceNo; m.Cost = p.Cost; m.UnitCost = p.Cost; });
            return p;
        }
        var installedOnce = new HashSet<Guid>();
        void Install(Part p, Vehicle v, string? pos, DateTimeOffset at, decimal km, decimal? hours, WorkOrder? wo, string? reason = null)
        {
            var before = p.Status; var fw = p.WarehouseId;
            (p.Status, p.VehicleId, p.Position, p.InstalledAt, p.InstallKm, p.InstallHours, p.WarehouseId, p.Bin) = (PartStatus.Installed, v.Id, pos, at, km, hours, null, null);
            Mv("install", p, before, at, m => { m.VehicleId = v.Id; m.Position = pos; m.Km = km; m.Hours = hours; m.WorkOrderId = wo?.Id; m.Reason = wo is null ? reason ?? "تجهيز شاحنة" : null; m.FromWarehouseId = fw; m.Cost = installedOnce.Add(p.Id) ? p.Cost : null; m.ApprovedBy = uid; });
        }
        async Task<WorkOrder> Wo(Vehicle v, string type, string desc, DateTimeOffset at, decimal km, decimal labor, bool closed, string workshop = "ورشة الشركة")
        {
            var w = new WorkOrder { No = await svc.NextNoAsync("WO", "WO-", 5), VehicleId = v.Id, Type = type, Description = desc, Workshop = workshop, Km = km, LaborCost = labor, OpenedAt = at, OpenedBy = uid,
                DriverId = await svc.CurrentDriverAsync(v.Id), Closed = closed, ClosedAt = closed ? at.AddHours(6) : null, ClosedBy = closed ? uid : null, CloseNote = closed ? "تم التنفيذ (تجريبي)" : null };
            db.WorkOrders.Add(w);
            return w;
        }

        // ── the fleet as it is today: every truck equipped ─────────────────────────────────────────────────────────
        var kmNow = new Dictionary<Guid, decimal>();
        foreach (var v in vehicles) kmNow[v.Id] = await svc.LastKmAsync(v.Id) ?? 150000 + rnd.Next(0, 200000);
        string[] positions = CustodyText.TyrePositions;
        var tyresOnTruck = new Dictionary<Guid, List<Part>>();
        var batteries = new Dictionary<Guid, Part>();
        var pumps = new Dictionary<Guid, Part>();
        var gpsUnits = new Dictionary<Guid, Part>();
        for (var i = 0; i < vehicles.Count; i++)
        {
            var v = vehicles[i];
            var setupAt = Ago(240 + i * 9);
            var kmSetup = Math.Max(1000, kmNow[v.Id] - 60000 - rnd.Next(0, 20000));
            var wo = await Wo(v, "صيانة دورية", "تجهيز الشاحنة بالإطارات والبطاريات وأجهزة السلامة", setupAt, kmSetup, 600, true);
            tyresOnTruck[v.Id] = [];
            for (var k = 0; k < 6; k++)
            {
                var t = await Buy(tyre, setupAt.AddDays(-12), "مؤسسة الإطارات المتحدة", 24);
                Install(t, v, positions[k], setupAt, kmSetup, null, wo);
                tyresOnTruck[v.Id].Add(t);
            }
            var b = await Buy(batt, setupAt.AddDays(-10), "الوكيل المعتمد للبطاريات", 18);
            Install(b, v, "يمين", setupAt, kmSetup, null, wo); batteries[v.Id] = b;
            var g = await Buy(gps, setupAt.AddDays(-30), "شركة آفاقي للتتبع", 12);
            Install(g, v, "لوحة القيادة", setupAt, kmSetup, null, wo); gpsUnits[v.Id] = g;
            var h = await Buy(hose, setupAt.AddDays(-20), "مؤسسة معدات الوقود", 12);
            Install(h, v, "صندوق الخراطيم", setupAt, kmSetup, null, wo);
            h.LastInspection = Day(now.AddDays(-150 + i * 12)); h.NextInspection = h.LastInspection.Value.AddDays(180);
            var e = await Buy(ext, setupAt.AddDays(-20), "نافكو", 12);
            Install(e, v, "خلف مقعد السائق", setupAt, kmSetup, null, wo);
            e.LastInspection = Day(now.AddDays(-80 + i * 3)); e.NextInspection = e.LastInspection.Value.AddDays(90); e.ExpiryDate = Day(now.AddDays(i < 2 ? 20 : 200));
            if (v.MeterId is { } mid)
            {
                var ms = await db.Meters.Where(m => m.Id == mid).Select(m => m.Serial).FirstAsync();
                var mp = await Buy(meterC, setupAt.AddDays(-40), "Liquid Controls — الموزع المحلي", 24, serial: false);
                (mp.Serial, mp.MeterId) = (ms, mid);
                mp.SerialKey = $"{(mp.Brand ?? "").ToUpperInvariant()}|{ms}";
                Install(mp, v, "خط التفريغ", setupAt, kmSetup, 0, wo);
            }
            if (i < 5)
            {
                var pp = await Buy(pump, setupAt.AddDays(-45), "الشركة الصناعية للمضخات", 12);
                Install(pp, v, "مضخة التفريغ", setupAt, kmSetup, 0, wo); pumps[v.Id] = pp;
            }
            kmNow[v.Id] = Math.Max(kmNow[v.Id], kmSetup + 60000);
        }
        await db.SaveChangesAsync();

        // ── history: replacements with each fate ───────────────────────────────────────────────────────────────────
        async Task Replace(Part old, PartCatalog cat, Vehicle v, DateTimeOffset at, decimal km, decimal? hours, string reason, string fate, WorkOrder wo, Action<Part>? fateFill = null)
        {
            var neu = await Buy(cat, at.AddDays(-5), cat == batt ? "الوكيل المعتمد للبطاريات" : cat == pump ? "الشركة الصناعية للمضخات" : "مؤسسة الإطارات المتحدة", cat == tyre ? 24 : 12);
            var pos = old.Position;
            (old.Status, old.VehicleId, old.Position) = (PartStatus.Stock, null, null);
            var ro = Mv("replace-out", old, PartStatus.Installed, at, m => { m.FromVehicleId = v.Id; m.Position = pos; m.Km = km; m.Hours = hours; m.Reason = reason; m.OldPartFate = fate; m.RelatedPartId = neu.Id; m.WorkOrderId = wo.Id; });
            var t2 = at.AddMinutes(1);
            switch (fate)
            {
                case "stock": (old.Status, old.WarehouseId, old.Condition) = (PartStatus.Stock, main.Id, "مستعمل"); Mv("return", old, PartStatus.Stock, t2, m => { m.ToWarehouseId = main.Id; m.FromVehicleId = v.Id; }); break;
                case "repair": old.Status = PartStatus.Repair; fateFill?.Invoke(old); Mv("repair-send", old, PartStatus.Stock, t2, m => { m.Vendor = old.RepairVendor; m.ExpectedReturn = old.RepairExpectedAt; m.FromVehicleId = v.Id; m.WorkOrderId = wo.Id; m.Reason = reason; }); break;
                case "claim": (old.Status, old.ClaimRef, old.ClaimOpenedAt) = (PartStatus.ReturnedToSupplier, $"CLM-{rnd.Next(1000, 9999)}", t2); Mv("claim", old, PartStatus.Stock, t2, m => { m.Vendor = old.Supplier; m.DocRef = old.ClaimRef; m.FromVehicleId = v.Id; m.Reason = reason; }); break;
                case "scrap": (old.Status, old.ScrapApprovedBy, old.LastLocation) = (PartStatus.Scrapped, uid, $"فُكّت من الشاحنة {v.Plate}"); Mv("scrap", old, PartStatus.Stock, t2, m => { m.ApprovedBy = uid; m.DocRef = "اعتماد مدير الصيانة"; m.FromVehicleId = v.Id; m.Reason = reason; }); break;
            }
            old.StatusReason = CustodyText.Fates[fate];
            ro.StatusAfter = old.Status.ToString();
            await db.SaveChangesAsync();
            var before = neu.Status;
            (neu.Status, neu.VehicleId, neu.Position, neu.InstalledAt, neu.InstallKm, neu.InstallHours, neu.WarehouseId, neu.Bin) = (PartStatus.Installed, v.Id, pos, t2, km, hours, null, null);
            installedOnce.Add(neu.Id);
            Mv("replace-in", neu, before, t2.AddMinutes(1), m => { m.VehicleId = v.Id; m.Position = pos; m.Km = km; m.Hours = hours; m.Reason = reason; m.RelatedPartId = old.Id; m.WorkOrderId = wo.Id; m.FromWarehouseId = main.Id; m.Cost = neu.Cost; m.ApprovedBy = uid; });
            await db.SaveChangesAsync();
        }
        for (var i = 0; i < vehicles.Count; i++)
        {
            var v = vehicles[i];
            var at = Ago(100 - i * 9, 9);
            var km = kmNow[v.Id] - 25000 + rnd.Next(0, 4000);
            var wo = await Wo(v, "استبدال إطارات", "استبدال الإطارين الأماميين بسبب التآكل", at, km, 220, true, i % 2 == 0 ? "ورشة الشركة" : "مركز الخليج للإطارات");
            var fate = (i % 4) switch { 0 => "repair", 1 => "scrap", 2 => "stock", _ => "repair" };
            await Replace(tyresOnTruck[v.Id][0], tyre, v, at, km, null, i % 2 == 0 ? "تآكل غير منتظم" : "قطع جانبي", fate, wo, p =>
            {
                (p.RepairVendor, p.RepairSentAt, p.RepairExpectedAt, p.RepairCost) = ("ورشة الخليج للإطارات (تجريبي)", at, Day(at).AddDays(i == 0 ? 20 : 120), 180 + i * 10);
            });
            await Replace(tyresOnTruck[v.Id][1], tyre, v, at.AddMinutes(20), km, null, "تآكل", i % 3 == 0 ? "scrap" : "stock", wo);
        }
        // battery failure under warranty → claim; pump fault → repair (overdue); alternator with a stock-issue
        var bv = vehicles[Math.Min(2, vehicles.Count - 1)];
        var bwo = await Wo(bv, "استبدال بطارية", "البطارية لا تشحن", Ago(18), kmNow[bv.Id] - 3000, 120, true);
        await Replace(batteries[bv.Id], batt, bv, Ago(18, 10), kmNow[bv.Id] - 3000, null, "خلية تالفة — ضمن الضمان", "claim", bwo);
        if (pumps.Count > 1)
        {
            var pv = vehicles[1];
            var pwo = await Wo(pv, "إصلاح عطل", "تسريب من حشوة المضخة وانخفاض الضغط", Ago(35), kmNow[pv.Id] - 6000, 450, false, "الشركة الصناعية للمضخات");
            await Replace(pumps[pv.Id], pump, pv, Ago(35, 11), kmNow[pv.Id] - 6000, 2150, "تسريب وانخفاض ضغط", "repair", pwo, p =>
                (p.RepairVendor, p.RepairSentAt, p.RepairExpectedAt, p.RepairCost) = ("الشركة الصناعية للمضخات (تجريبي)", Ago(35), Day(Ago(5)), 3200));
        }

        // ── damaged and lost ───────────────────────────────────────────────────────────────────────────────────────
        var dv = vehicles[Math.Min(3, vehicles.Count - 1)];
        var dt = tyresOnTruck[dv.Id][3];
        var dWhere = $"على الشاحنة {dv.Plate} · {dt.Position}";
        var dvId = dt.VehicleId;
        (dt.Status, dt.VehicleId, dt.Position, dt.LastLocation, dt.StatusReason) = (PartStatus.Damaged, null, null, dWhere, "انفجار إطار على طريق الجبيل السريع");
        Mv("damaged", dt, PartStatus.Installed, Ago(6, 14), m => { m.FromVehicleId = dvId; m.Reason = dt.StatusReason; m.Km = kmNow[dv.Id] - 900; m.Note = $"آخر موقع/حامل: {dWhere}"; });
        var lv = vehicles[Math.Min(5, vehicles.Count - 1)];
        var lg = gpsUnits[lv.Id];
        var lvId = lg.VehicleId;
        (lg.Status, lg.VehicleId, lg.Position, lg.LastLocation, lg.StatusReason) = (PartStatus.Lost, null, null, $"على الشاحنة {lv.Plate} · لوحة القيادة", "فُقد الجهاز بعد مبيت الشاحنة في موقع العميل");
        Mv("lost", lg, PartStatus.Installed, Ago(12, 9), m => { m.FromVehicleId = lvId; m.Reason = lg.StatusReason; });
        await db.SaveChangesAsync();

        // ── stock in the warehouses (individual) ───────────────────────────────────────────────────────────────────
        for (var k = 0; k < 4; k++) await Buy(tyre, Ago(20), "مؤسسة الإطارات المتحدة", 24);
        for (var k = 0; k < 2; k++) await Buy(tyreT, Ago(60), "بريدجستون — الموزع", 24, w: site);
        await Buy(batt, Ago(350), "الوكيل المعتمد للبطاريات", 12);   // warranty ends in ~2 weeks
        await Buy(batt, Ago(30), "الوكيل المعتمد للبطاريات", 18);
        await Buy(hose, Ago(45), "مؤسسة معدات الوقود", 12);
        for (var k = 0; k < 3; k++) await Buy(ext, Ago(15), "نافكو", 12);
        await Buy(alt, Ago(75), "بوش — الموزع", 12);
        await Buy(gps, Ago(25), "شركة آفاقي للتتبع", 12, w: site);
        await db.SaveChangesAsync();

        // ── custody held by drivers (accepted hand-overs) and one waiting for acceptance ───────────────────────────
        var assignments = await db.VehicleAssignments.Where(x => x.To == null && x.DriverId != null).ToListAsync();
        var driverIds = vehicles.Select(v => assignments.FirstOrDefault(a => a.VehicleId == v.Id)?.DriverId).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
        var names = await db.Drivers.Where(d => driverIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name);
        for (var k = 0; k < driverIds.Count; k++)
        {
            var did = driverIds[k]; var at = Ago(60 - k * 5, 7);
            var kitP = await Buy(kit, at.AddDays(-3), "مؤسسة أدوات السلامة", 0, serial: false);
            var tr = new CustodyTransfer
            {
                No = await svc.NextNoAsync("TR", "TR-", 5), Kind = "issue", PartId = kitP.Id, CatalogId = kit.Id, FromKind = "warehouse", FromId = main.Id, FromName = main.Name, ToKind = "driver", ToId = did, ToName = names[did],
                Reason = "عهدة شخصية للموظف", HandoverBy = keeper.Name, Condition = "جديد", RequestedAt = at, RequestedBy = uid, Status = k == driverIds.Count - 1 ? "Pending" : "Accepted",
                DecidedAt = k == driverIds.Count - 1 ? null : at.AddHours(1), DecidedBy = k == driverIds.Count - 1 ? null : uid, DecisionNote = k == driverIds.Count - 1 ? null : "استلم بتوقيعه (تجريبي)",
            };
            db.CustodyTransfers.Add(tr);
            Mv("handover", kitP, PartStatus.Stock, at, m => { m.TransferId = tr.Id; m.FromWarehouseId = main.Id; m.HolderKind = "driver"; m.HolderId = did; m.HolderRef = names[did]; m.Reason = tr.Reason; });
            if (tr.Status == "Accepted")
            {
                (kitP.Status, kitP.HolderKind, kitP.HolderId, kitP.HolderRef, kitP.HolderSince, kitP.WarehouseId, kitP.Bin) = (PartStatus.Issued, "driver", did, names[did], at.AddHours(1), null, null);
                Mv("issue", kitP, PartStatus.Stock, at.AddHours(1), m => { m.TransferId = tr.Id; m.FromWarehouseId = main.Id; m.HolderKind = "driver"; m.HolderId = did; m.HolderRef = names[did]; m.Reason = tr.Reason; m.ApprovedBy = uid; });
                db.HolderBalances.Add(new HolderBalance { HolderKind = "driver", HolderId = did, CatalogId = bulbs.Id, Qty = 2, HolderName = names[did], Since = at.AddHours(1) });
                db.PartMovements.Add(new PartMovement { Type = "stock-issue", CatalogId = bulbs.Id, Qty = 2, FromWarehouseId = main.Id, HolderKind = "driver", HolderId = did, HolderRef = names[did], Reason = "عهدة شخصية للموظف", UnitCost = bulbs.UnitCost, Cost = bulbs.UnitCost * 2, At = at.AddHours(1), ByUserId = uid, ApprovedBy = uid });
            }
        }
        // a battery in the technician's custody, and a spare hose with a driver
        var tb = await Buy(batt, Ago(40), "الوكيل المعتمد للبطاريات", 18);
        (tb.Status, tb.HolderKind, tb.HolderId, tb.HolderRef, tb.HolderSince, tb.WarehouseId, tb.Bin) = (PartStatus.Issued, "employee", techElec.Id, techElec.Name, Ago(9), null, null);
        Mv("issue", tb, PartStatus.Stock, Ago(9), m => { m.FromWarehouseId = main.Id; m.HolderKind = "employee"; m.HolderId = techElec.Id; m.HolderRef = techElec.Name; m.Reason = "طوارئ على الطريق"; m.ApprovedBy = uid; });
        db.HolderBalances.Add(new HolderBalance { HolderKind = "employee", HolderId = techTyre.Id, CatalogId = nuts.Id, Qty = 24, HolderName = techTyre.Name, Since = Ago(14) });
        db.PartMovements.Add(new PartMovement { Type = "stock-issue", CatalogId = nuts.Id, Qty = 24, FromWarehouseId = main.Id, HolderKind = "employee", HolderId = techTyre.Id, HolderRef = techTyre.Name, Reason = "صيانة دورية", UnitCost = nuts.UnitCost, Cost = nuts.UnitCost * 24, At = Ago(14), ByUserId = uid, ApprovedBy = uid });
        await db.SaveChangesAsync();

        // ── quantity stock (some below minimum) and consumption on trucks ──────────────────────────────────────────
        (PartCatalog C, decimal Qty, Warehouse W, string Bin)[] stock =
        [
            (oil, 45, main, "منطقة الزيوت 1"), (hyd, 80, main, "منطقة الزيوت 2"), (fOil, 26, main, "رف F-1"), (fFuel, 6, main, "رف F-2"), (fAir, 9, main, "رف F-3"),
            (nuts, 180, main, "درج N-4"), (grease, 14, main, "منطقة الزيوت 3"), (adblue, 320, site, "خزان 1"), (bulbs, 12, main, "درج E-1"), (oil, 30, site, "رف 1"),
        ];
        // what left the main warehouse (oil changes below, drivers' bulbs, technician's nuts, the count difference) was received too:
        // receipts = what remains + what was issued, so the ledger adds up to today's balances
        var n = vehicles.Count; var accepted = Math.Max(0, driverIds.Count - 1);
        var issued = new Dictionary<Guid, decimal> { [oil.Id] = 38 * n, [fOil.Id] = 2 * n, [fFuel.Id] = 2 * n + 1, [bulbs.Id] = 2 * accepted, [nuts.Id] = 24 };
        foreach (var (c, q, w, bin) in stock)
        {
            var at = Ago(90);
            await svc.AddStockAsync(c.Id, w.Id, q, bin);
            var received = q + (w == main ? issued.GetValueOrDefault(c.Id) : 0);
            db.PartMovements.Add(new PartMovement { Type = "stock-receive", CatalogId = c.Id, Qty = received, ToWarehouseId = w.Id, UnitCost = c.UnitCost, Cost = c.UnitCost * received, Vendor = c.Brand ?? "مورد محلي", DocRef = $"INV-Q-{rnd.Next(100, 999)}", At = at, ByUserId = uid });
        }
        // oil changes on each truck
        for (var i = 0; i < vehicles.Count; i++)
        {
            var v = vehicles[i]; var at = Ago(55 - i * 5, 10);
            var wo = await Wo(v, "صيانة دورية", "تغيير زيت المحرك والفلاتر", at, kmNow[v.Id] - 12000 + i * 300, 150, i != 0);
            foreach (var (c, q) in new[] { (oil, 38m), (fOil, 2m), (fFuel, 2m) })
                db.PartMovements.Add(new PartMovement { Type = "stock-issue", CatalogId = c.Id, Qty = q, FromWarehouseId = main.Id, VehicleId = v.Id, WorkOrderId = wo.Id, Km = wo.Km, UnitCost = c.UnitCost, Cost = c.UnitCost * q, At = at.AddMinutes(30), ByUserId = uid, ApprovedBy = uid });
        }
        // last count of the main warehouse found a difference on the fuel filters
        db.StockCounts.Add(new StockCount
        {
            No = await svc.NextNoAsync("CNT", "CNT-", 4), WarehouseId = main.Id, At = Ago(10, 13), ByUserId = uid, Note = "جرد شهري (تجريبي)",
            Lines = [new(fFuel.Id, fFuel.Code, fFuel.Name, "رف F-2", 7, 6), new(fOil.Id, fOil.Code, fOil.Name, "رف F-1", 26, 26), new(oil.Id, oil.Code, oil.Name, "منطقة الزيوت 1", 45, 45)],
        });
        db.PartMovements.Add(new PartMovement { Type = "count-adjust", CatalogId = fFuel.Id, Qty = -1, ToWarehouseId = main.Id, Reason = "جرد شهري", Note = "المسجل 7 · الفعلي 6", At = Ago(10, 13), ByUserId = uid, ApprovedBy = uid });

        db.Settings.Add(new Setting { Key = "sample_custody", Value = now.ToString("o") });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        log.LogWarning("Sample custody data loaded: {Parts} parts, {Items} catalog items, {Wo} work orders.",
            await db.Parts.CountAsync(), catalog.Length, await db.WorkOrders.CountAsync());
    }
}
