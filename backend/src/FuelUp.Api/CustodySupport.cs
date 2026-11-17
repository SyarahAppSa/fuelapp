using System.Globalization;
using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FuelUp.Api;

/// <summary>Arabic labels and fixed lists of the custody section (shared by the API, reports and the dashboard via /api/custody/meta).</summary>
public static class CustodyText
{
    public static readonly Dictionary<PartStatus, string> Status = new()
    {
        [PartStatus.Stock] = "بالمستودع", [PartStatus.Issued] = "مصروفة (عهدة)", [PartStatus.Installed] = "مركّبة على شاحنة", [PartStatus.Repair] = "تحت الإصلاح",
        [PartStatus.ReturnedToSupplier] = "مرتجعة للمورد", [PartStatus.Damaged] = "تالفة", [PartStatus.Lost] = "مفقودة", [PartStatus.Scrapped] = "متلفة (إتلاف معتمد)",
    };

    public static readonly Dictionary<string, string> Moves = new()
    {
        ["receive"] = "استلام للمستودع", ["handover"] = "تسليم بانتظار القبول", ["handover-rejected"] = "رفض الاستلام", ["issue"] = "صرف عهدة",
        ["install"] = "تركيب على شاحنة", ["remove"] = "فك من شاحنة", ["replace-out"] = "استبدال: القطعة القديمة", ["replace-in"] = "استبدال: القطعة الجديدة",
        ["transfer"] = "نقل عهدة", ["return"] = "إرجاع للمستودع", ["repair-send"] = "إرسال للإصلاح", ["repair-return"] = "عودة من الإصلاح",
        ["damaged"] = "تسجيل تالف", ["lost"] = "تسجيل مفقود", ["review"] = "نتيجة مراجعة", ["scrap"] = "إتلاف معتمد", ["claim"] = "مطالبة ضمان",
        ["claim-result"] = "نتيجة مطالبة الضمان", ["inspect"] = "فحص دوري", ["edit"] = "تعديل بيانات", ["correction"] = "تصحيح",
        ["stock-receive"] = "استلام كمية", ["stock-issue"] = "صرف كمية", ["stock-return"] = "إرجاع كمية", ["stock-adjust"] = "تسوية مخزون",
        ["stock-move"] = "نقل بين المستودعات", ["count-adjust"] = "تسوية جرد", ["consume"] = "استهلاك على شاحنة",
    };

    public static readonly Dictionary<string, string> Fates = new() { ["stock"] = "إرجاع للمستودع", ["repair"] = "إرسال للإصلاح", ["claim"] = "مطالبة ضمان", ["scrap"] = "إتلاف معتمد" };

    /// <summary>Approved reasons for issuing parts without a work order.</summary>
    public static readonly string[] IssueReasons = ["صيانة دورية", "استهلاك تشغيلي", "بدل قطعة تالفة", "تجهيز شاحنة", "عهدة شخصية للموظف", "طوارئ على الطريق"];

    public static readonly string[] Categories = ["إطار", "بطارية", "عداد وقود", "مضخة تفريغ", "جهاز تتبع", "خرطوم", "معدات سلامة", "أداة", "زيوت", "فلاتر", "مسامير وقطع صغيرة", "مواد استهلاكية", "أخرى"];

    public static readonly string[] TyrePositions =
    [
        "أمامي يمين", "أمامي يسار", "خلفي أول يمين خارجي", "خلفي أول يمين داخلي", "خلفي أول يسار خارجي", "خلفي أول يسار داخلي",
        "خلفي ثاني يمين خارجي", "خلفي ثاني يمين داخلي", "خلفي ثاني يسار خارجي", "خلفي ثاني يسار داخلي", "احتياطي",
    ];

    public static readonly string[] WorkOrderTypes = ["صيانة دورية", "إصلاح عطل", "استبدال إطارات", "استبدال بطارية", "معايرة عداد", "فحص سلامة", "حادث", "أخرى"];

    public static string Holder(string? kind) => kind switch { "driver" => "سائق", "employee" => "موظف", "warehouse" => "مستودع", "vehicle" => "شاحنة", _ => "" };
    public static string Move(string type) => Moves.GetValueOrDefault(type, type);
    public static string N(decimal? v) => v is { } x ? x.ToString("#,0.##", CultureInfo.InvariantCulture) : "";
}

/// <summary>Who holds custody: a driver or an employee (technician, storekeeper...).</summary>
public record Holder(string Kind, Guid Id, string Name);

/// <summary>Lookups and atomic stock operations used by the custody controllers.</summary>
public class CustodyService(FuelUpDb db)
{
    /// <summary>Next number for a document type, unique per company (atomic counter row; shared table with trip numbers, own keys).</summary>
    public async Task<string> NextNoAsync(string key, string prefix, int digits, CancellationToken ct = default)
    {
        var cid = db.CurrentCompanyId ?? throw new TenantViolationException("no company in context");
        var n = (await db.Database.SqlQuery<int>($"""
            INSERT INTO trip_counters (company_id, period, last) VALUES ({cid}, {key}, 1)
            ON CONFLICT (company_id, period) DO UPDATE SET last = trip_counters.last + 1
            RETURNING last AS "Value"
            """).ToListAsync(ct))[0];
        return prefix + n.ToString(new string('0', digits), CultureInfo.InvariantCulture);
    }

    public async Task<Holder> HolderAsync(string? kind, Guid? id, CancellationToken ct = default)
    {
        if (id is not { } hid) throw new DomainException("اختر المستلم (سائق أو موظف).");
        return kind switch
        {
            "driver" => new Holder("driver", hid, await db.Drivers.Where(x => x.Id == hid).Select(x => x.Name).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("السائق")),
            "employee" => new Holder("employee", hid, await db.Employees.Where(x => x.Id == hid && x.IsActive).Select(x => x.Name).FirstOrDefaultAsync(ct) ?? throw new NotFoundException("الموظف")),
            _ => throw new DomainException("نوع المستلم يجب أن يكون سائقًا أو موظفًا."),
        };
    }

    /// <summary>Row lock on the part for the rest of the transaction, then the fresh row.</summary>
    public async Task<Part> LockPartAsync(Guid id, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM parts WHERE id = {id} FOR UPDATE", ct);
        var p = await db.Parts.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("القطعة");
        await db.Entry(p).ReloadAsync(ct);
        return p;
    }

    public async Task<PartCatalog> CatalogAsync(Guid id, CancellationToken ct = default) =>
        await db.PartCatalog.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("الصنف");

    public async Task<Warehouse> WarehouseAsync(Guid? id, CancellationToken ct = default)
    {
        if (id is not { } wid) throw new DomainException("اختر المستودع.");
        return await db.Warehouses.FirstOrDefaultAsync(x => x.Id == wid, ct) ?? throw new NotFoundException("المستودع");
    }

    public async Task<Vehicle> VehicleAsync(Guid id, CancellationToken ct = default) =>
        await db.Vehicles.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("الشاحنة");

    public Task<Guid?> CurrentDriverAsync(Guid vehicleId, CancellationToken ct = default) =>
        db.VehicleAssignments.Where(x => x.VehicleId == vehicleId && x.To == null).Select(x => x.DriverId).FirstOrDefaultAsync(ct);

    /// <summary>Issuing must be linked to an open work order of the company or to an approved reason.</summary>
    public async Task<WorkOrder?> RequireWorkOrderOrReasonAsync(Guid? workOrderId, string? reason, Guid? vehicleId = null, CancellationToken ct = default)
    {
        if (workOrderId is { } wid)
        {
            var wo = await db.WorkOrders.FirstOrDefaultAsync(x => x.Id == wid, ct) ?? throw new NotFoundException("أمر الصيانة");
            if (wo.Closed) throw new DomainException($"أمر الصيانة {wo.No} مغلق. افتح أمرًا جديدًا أو اختر سبب صرف معتمد.");
            if (vehicleId is { } v && wo.VehicleId != v) throw new DomainException($"أمر الصيانة {wo.No} يخص شاحنة أخرى.");
            return wo;
        }
        if (string.IsNullOrWhiteSpace(reason) || !CustodyText.IssueReasons.Contains(reason.Trim()))
            throw new DomainException("الصرف يجب أن يرتبط بأمر صيانة مفتوح أو بسبب صرف معتمد.");
        return null;
    }

    // ── quantity stock: atomic, never negative (also guarded by CHECK constraints in the database) ──────────────────
    public async Task AddStockAsync(Guid catalogId, Guid warehouseId, decimal qty, string? bin, CancellationToken ct = default)
    {
        var cid = db.CurrentCompanyId ?? throw new TenantViolationException("no company in context");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO stock_balances (company_id, catalog_id, warehouse_id, qty, bin) VALUES ({cid}, {catalogId}, {warehouseId}, {qty}, {bin})
            ON CONFLICT (catalog_id, warehouse_id) DO UPDATE SET qty = stock_balances.qty + EXCLUDED.qty, bin = COALESCE(EXCLUDED.bin, stock_balances.bin)
            """, ct);
    }

    public async Task TakeStockAsync(PartCatalog c, Guid warehouseId, decimal qty, CancellationToken ct = default)
    {
        var n = await db.StockBalances.Where(x => x.CatalogId == c.Id && x.WarehouseId == warehouseId && x.Qty >= qty)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Qty, x => x.Qty - qty), ct);
        if (n == 0)
        {
            var have = await db.StockBalances.Where(x => x.CatalogId == c.Id && x.WarehouseId == warehouseId).Select(x => (decimal?)x.Qty).FirstOrDefaultAsync(ct) ?? 0;
            throw new DomainException($"الكمية غير متوفرة: المتاح من «{c.Name}» في هذا المستودع {CustodyText.N(have)} {c.Unit} فقط.");
        }
    }

    public async Task AddHolderAsync(Holder h, Guid catalogId, decimal qty, CancellationToken ct = default)
    {
        var cid = db.CurrentCompanyId ?? throw new TenantViolationException("no company in context");
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO holder_balances (company_id, holder_kind, holder_id, catalog_id, qty, holder_name, since) VALUES ({cid}, {h.Kind}, {h.Id}, {catalogId}, {qty}, {h.Name}, {now})
            ON CONFLICT (holder_kind, holder_id, catalog_id) DO UPDATE SET qty = holder_balances.qty + EXCLUDED.qty, holder_name = EXCLUDED.holder_name,
              since = CASE WHEN holder_balances.qty = 0 THEN EXCLUDED.since ELSE holder_balances.since END
            """, ct);
    }

    public async Task TakeHolderAsync(Holder h, PartCatalog c, decimal qty, CancellationToken ct = default)
    {
        var n = await db.HolderBalances.Where(x => x.HolderKind == h.Kind && x.HolderId == h.Id && x.CatalogId == c.Id && x.Qty >= qty)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Qty, x => x.Qty - qty), ct);
        if (n == 0)
        {
            var have = await db.HolderBalances.Where(x => x.HolderKind == h.Kind && x.HolderId == h.Id && x.CatalogId == c.Id).Select(x => (decimal?)x.Qty).FirstOrDefaultAsync(ct) ?? 0;
            throw new DomainException($"عهدة {h.Name} من «{c.Name}» {CustodyText.N(have)} {c.Unit} فقط.");
        }
    }

    /// <summary>Last known odometer of a truck: highest of loading odometers and custody movements.</summary>
    public async Task<decimal?> LastKmAsync(Guid vehicleId, CancellationToken ct = default)
    {
        var trips = await db.TripLoadings.Where(l => db.Trips.Any(t => t.Id == l.TripId && t.VehicleId == vehicleId)).MaxAsync(l => l.Odometer, ct);
        var moves = await db.PartMovements.Where(m => m.VehicleId == vehicleId).MaxAsync(m => m.Km, ct);
        var wos = await db.WorkOrders.Where(w => w.VehicleId == vehicleId).MaxAsync(w => w.Km, ct);
        return new[] { trips, moves, wos }.Max();
    }
}

/// <summary>Names for ids, loaded once per request (master data of one company is small).</summary>
public class CustodyLookups
{
    public Dictionary<Guid, PartCatalog> Catalog = new();
    public Dictionary<Guid, string> Warehouses = new();
    public Dictionary<Guid, string> Plates = new();
    public Dictionary<Guid, string> Users = new();
    public Dictionary<Guid, string> WorkOrders = new();

    public static async Task<CustodyLookups> LoadAsync(FuelUpDb db, CancellationToken ct = default) => new()
    {
        Catalog = await db.PartCatalog.AsNoTracking().ToDictionaryAsync(x => x.Id, ct),
        Warehouses = await db.Warehouses.ToDictionaryAsync(x => x.Id, x => x.Name, ct),
        Plates = await db.Vehicles.ToDictionaryAsync(x => x.Id, x => x.Plate, ct),
        Users = await db.Users.ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct),
        WorkOrders = await db.WorkOrders.ToDictionaryAsync(x => x.Id, x => x.No, ct),
    };

    public string Item(Guid catalogId) => Catalog.TryGetValue(catalogId, out var c) ? c.Name : "";
    public string Code(Guid catalogId) => Catalog.TryGetValue(catalogId, out var c) ? c.Code : "";
    public string Wh(Guid? id) => id is { } x ? Warehouses.GetValueOrDefault(x, "") : "";
    public string Plate(Guid? id) => id is { } x ? Plates.GetValueOrDefault(x, "") : "";
    public string User(Guid? id) => id is { } x ? Users.GetValueOrDefault(x, "") : "";
    public string Wo(Guid? id) => id is { } x ? WorkOrders.GetValueOrDefault(x, "") : "";

    /// <summary>Where the part is now, in words.</summary>
    public string Location(Part p) => p.Status switch
    {
        PartStatus.Stock => Wh(p.WarehouseId) + (string.IsNullOrEmpty(p.Bin) ? "" : $" · {p.Bin}"),
        PartStatus.Installed => Plate(p.VehicleId) + (string.IsNullOrEmpty(p.Position) ? "" : $" · {p.Position}"),
        PartStatus.Issued => p.HolderRef ?? "",
        PartStatus.Repair => p.RepairVendor ?? "",
        PartStatus.ReturnedToSupplier => p.Supplier ?? "",
        _ => p.LastLocation ?? "",
    };

    public string From(PartMovement m) => m.FromHolderRef ?? (m.FromVehicleId is { } v ? Plate(v) : m.FromWarehouseId is { } w ? Wh(w) : "");
    public string To(PartMovement m) => m.HolderRef ?? (m.VehicleId is { } v ? Plate(v) + (string.IsNullOrEmpty(m.Position) ? "" : $" · {m.Position}") : m.ToWarehouseId is { } w ? Wh(w) : m.Vendor ?? "");
}
