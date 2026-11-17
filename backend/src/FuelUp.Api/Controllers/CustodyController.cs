using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.RegularExpressions;
using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FuelUp.Api.Controllers;

// ── inputs (explicit DTOs: ids, status, location and history are server-owned) ─────────────────────────────────────
public record WarehouseInput([Required, StringLength(100, MinimumLength = 2)] string Name, [StringLength(150)] string? Location, [StringLength(100)] string? Keeper, bool IsActive = true);
public record EmployeeInput([Required, StringLength(100, MinimumLength = 2)] string Name, [StringLength(60)] string? Role, [StringLength(30)] string? EmpNo, [StringLength(20)] string? Phone, bool IsActive = true);
public record CatalogInput([Required, StringLength(40, MinimumLength = 1)] string Code, [Required, StringLength(150, MinimumLength = 2)] string Name, PartTracking Tracking,
    [Required, StringLength(60)] string Category, [StringLength(60)] string? Brand, [StringLength(60)] string? Model, [StringLength(60)] string? MfrPartNo, [StringLength(20)] string? Unit,
    [Range(typeof(decimal), "0", "1000000")] decimal? MinQty, [Range(typeof(decimal), "0", "1000000")] decimal? ReorderQty, [Range(1, 3650)] int? InspectEveryDays,
    [Range(typeof(decimal), "0", "10000000")] decimal? UnitCost, [StringLength(300)] string? Specs, bool IsActive = true);
public record PartInput(Guid CatalogId, Guid? WarehouseId, [StringLength(60)] string? Serial, [StringLength(60)] string? Brand, [StringLength(60)] string? Model,
    [StringLength(100)] string? Supplier, [StringLength(60)] string? InvoiceNo, DateOnly? PurchaseDate, [Range(typeof(decimal), "0", "10000000")] decimal? Cost,
    DateOnly? WarrantyStart, DateOnly? WarrantyEnd, [StringLength(300)] string? WarrantyTerms, [StringLength(60)] string? BatchNo, [StringLength(300)] string? Specs,
    DateOnly? ExpiryDate, DateOnly? NextInspection, [StringLength(40)] string? Condition, [StringLength(500)] string? Notes, Guid? MeterId, [StringLength(40)] string? Bin,
    [StringLength(500)] string? Reason = null);
public record IssueInput([Required] string HolderKind, Guid HolderId, Guid? WorkOrderId, [StringLength(100)] string? Reason, [StringLength(40)] string? Condition,
    [StringLength(100)] string? HandoverBy, [StringLength(500)] string? Note, bool ReceiverSigned = false);
public record InstallInput(Guid VehicleId, [StringLength(60)] string? Position, [Range(typeof(decimal), "0", "9999999")] decimal? Km, [Range(typeof(decimal), "0", "999999")] decimal? Hours,
    Guid? WorkOrderId, [StringLength(100)] string? Reason, [StringLength(500)] string? Note);
public record FateInput([StringLength(20)] string? Fate, Guid? WarehouseId, [StringLength(40)] string? Bin, [StringLength(100)] string? Vendor, DateOnly? ExpectedReturn,
    [Range(typeof(decimal), "0", "10000000")] decimal? EstimatedCost, [StringLength(60)] string? ClaimRef, [StringLength(300)] string? ScrapApproval);
public record RemoveInput([Range(typeof(decimal), "0", "9999999")] decimal? Km, [Range(typeof(decimal), "0", "999999")] decimal? Hours, [Required, StringLength(300, MinimumLength = 3)] string Reason,
    FateInput? Fate, Guid? WorkOrderId, [StringLength(500)] string? Note);
public record ReplaceInput(Guid OldPartId, Guid NewPartId, [Range(typeof(decimal), "0", "9999999")] decimal? Km, [Range(typeof(decimal), "0", "999999")] decimal? Hours,
    [Required, StringLength(300, MinimumLength = 3)] string Reason, FateInput? OldPartFate, Guid? WorkOrderId, [StringLength(100)] string? IssueReason, [StringLength(500)] string? Note);
public record TransferInput([Required] string ToKind, Guid ToId, [StringLength(60)] string? Position, [Range(typeof(decimal), "0", "9999999")] decimal? Km, [StringLength(40)] string? Condition,
    [StringLength(100)] string? HandoverBy, [StringLength(500)] string? Note);
public record DecideInput([StringLength(500)] string? Note, [Range(typeof(decimal), "0", "9999999")] decimal? Km, [Range(typeof(decimal), "0", "999999")] decimal? Hours, [StringLength(60)] string? Position, [StringLength(40)] string? Bin);
public record RepairSendInput([Required, StringLength(100, MinimumLength = 2)] string Vendor, DateOnly? ExpectedReturn, [Range(typeof(decimal), "0", "10000000")] decimal? EstimatedCost,
    [Required, StringLength(300, MinimumLength = 3)] string Reason, Guid? WorkOrderId, [StringLength(60)] string? DocRef);
public record RepairReturnInput(bool Fixed, [Range(typeof(decimal), "0", "10000000")] decimal? Cost, Guid? WarehouseId, [StringLength(40)] string? Bin, [StringLength(500)] string? Note, [StringLength(60)] string? DocRef);
public record ReasonInput([Required, StringLength(300, MinimumLength = 3)] string Reason, [StringLength(500)] string? Note);
public record ReviewOutcomeInput([Required, StringLength(300, MinimumLength = 3)] string Outcome);
public record ScrapInput([Required, StringLength(300, MinimumLength = 3)] string Reason, [Required, StringLength(300, MinimumLength = 2)] string Approval);
public record ClaimInput([Required, StringLength(60, MinimumLength = 1)] string ClaimRef, [Required, StringLength(300, MinimumLength = 3)] string Reason);
public record ClaimResultInput(bool Accepted, [Required, StringLength(300, MinimumLength = 2)] string Result, Guid? WarehouseId, [StringLength(40)] string? Bin);
public record ReturnInput(Guid WarehouseId, [StringLength(40)] string? Bin, [StringLength(40)] string? Condition, [StringLength(500)] string? Note);
public record InspectInput(bool Passed, DateOnly? NextInspection, [StringLength(500)] string? Note);
public record CorrectInput([Required, StringLength(300, MinimumLength = 5)] string Reason, Guid WarehouseId, Guid? CorrectsMovementId);
public record StockReceiveInput(Guid CatalogId, Guid WarehouseId, [Range(typeof(decimal), "0.01", "1000000")] decimal Qty, [Range(typeof(decimal), "0", "10000000")] decimal? UnitCost,
    [StringLength(100)] string? Supplier, [StringLength(60)] string? InvoiceNo, [StringLength(60)] string? BatchNo, [StringLength(40)] string? Bin, [StringLength(500)] string? Note);
public record StockIssueInput(Guid CatalogId, Guid WarehouseId, [Range(typeof(decimal), "0.01", "1000000")] decimal Qty, Guid? VehicleId, string? HolderKind, Guid? HolderId,
    Guid? WorkOrderId, [StringLength(100)] string? Reason, [Range(typeof(decimal), "0", "9999999")] decimal? Km, [StringLength(100)] string? HandoverBy, [StringLength(500)] string? Note, bool ReceiverSigned = false);
public record StockReturnInput(Guid CatalogId, Guid WarehouseId, [Range(typeof(decimal), "0.01", "1000000")] decimal Qty, [Required] string HolderKind, Guid HolderId, [StringLength(500)] string? Note);
public record StockAdjustInput(Guid CatalogId, Guid WarehouseId, [Range(typeof(decimal), "0", "1000000")] decimal NewQty, [Required, StringLength(300, MinimumLength = 3)] string Reason);
public record StockMoveInput(Guid CatalogId, Guid FromWarehouseId, Guid ToWarehouseId, [Range(typeof(decimal), "0.01", "1000000")] decimal Qty, [StringLength(500)] string? Note);
public record ConsumeInput(Guid CatalogId, [Range(typeof(decimal), "0.01", "1000000")] decimal Qty, [Required] string HolderKind, Guid HolderId, Guid VehicleId, Guid? WorkOrderId,
    [StringLength(100)] string? Reason, [Range(typeof(decimal), "0", "9999999")] decimal? Km);
public record HolderTransferInput(Guid CatalogId, [Range(typeof(decimal), "0.01", "1000000")] decimal Qty, [Required] string FromKind, Guid FromId, [Required] string ToKind, Guid ToId,
    [StringLength(100)] string? HandoverBy, [StringLength(500)] string? Note);
public record CountLineInput(Guid CatalogId, [Range(typeof(decimal), "0", "1000000")] decimal Actual, [StringLength(40)] string? Bin);
public record CountInput(Guid WarehouseId, [MaxLength(2000)] List<CountLineInput> Lines, [MaxLength(5000)] List<string>? FoundParts, [StringLength(500)] string? Note);
public record ClearanceInput([Required] string HolderKind, Guid HolderId, Guid WarehouseId, [StringLength(500)] string? Note);
public record WorkOrderInput(Guid VehicleId, [Required, StringLength(60)] string Type, [Required, StringLength(1000, MinimumLength = 3)] string Description, [StringLength(100)] string? Workshop,
    [Range(typeof(decimal), "0", "9999999")] decimal? Km, [Range(typeof(decimal), "0", "10000000")] decimal? LaborCost);
public record WorkOrderUpdate([Required, StringLength(1000, MinimumLength = 3)] string Description, [StringLength(100)] string? Workshop, [Range(typeof(decimal), "0", "9999999")] decimal? Km,
    [Range(typeof(decimal), "0", "10000000")] decimal? LaborCost, [Required, StringLength(300, MinimumLength = 3)] string Reason);
public record WorkOrderClose([Range(typeof(decimal), "0", "10000000")] decimal? LaborCost, [StringLength(500)] string? Note);

/// <summary>
/// «العُهد وقطع غيار الشاحنات»: where every part is, on which truck, who holds it, and what happened to it.
/// Rules are enforced here (and by database constraints), not in the dashboard:
///  • no duplicate serial from the same maker; a part is on one truck at a time; one part per truck position;
///  • issuing needs an open work order or an approved reason; replacing needs the old part's fate;
///  • the truck a part is installed on is separate from the person holding custody (driver changes never move parts);
///  • custody hand-over is completed only when the receiver accepts; movements are append-only (corrections are new rows);
///  • quantity stock and personal custody never go negative (atomic updates + CHECK constraints).
/// </summary>
[ApiController, Route("api/custody"), Authorize]
public class CustodyController(FuelUpDb db, ActorAccessor actor, AuditWriter audit, CustodyService svc) : ControllerBase
{
    // ── permissions ───────────────────────────────────────────────────────────────────────────────────────────────
    private async Task<Actor> ViewAsync() { var a = await actor.GetAsync(); a.Require(Section.Custody, PermissionLevel.View); return a; }
    private async Task<Actor> EditAsync() { var a = await actor.GetAsync(); a.Require(Section.Custody, PermissionLevel.Edit); return a; }
    private static bool SeesCost(Actor a) => a.Can(Section.Costs) || a.Can(Section.Custody, PermissionLevel.Edit);

    private async Task<IActionResult> Tx(Func<Task<IActionResult>> work)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var r = await work();
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return r;
    }

    private PartMovement Log(Actor a, string type, Part p, PartStatus before, Action<PartMovement>? fill = null)
    {
        var m = new PartMovement { Type = type, PartId = p.Id, CatalogId = p.CatalogId, Qty = 1, StatusBefore = before.ToString(), StatusAfter = p.Status.ToString(), At = DateTimeOffset.UtcNow, ByUserId = a.UserId };
        fill?.Invoke(m);
        db.PartMovements.Add(m);
        return m;
    }

    private PartMovement LogQty(Actor a, string type, Guid catalogId, decimal qty, Action<PartMovement>? fill = null)
    {
        var m = new PartMovement { Type = type, CatalogId = catalogId, Qty = qty, At = DateTimeOffset.UtcNow, ByUserId = a.UserId };
        fill?.Invoke(m);
        db.PartMovements.Add(m);
        return m;
    }

    private static void Need(Part p, string action, params PartStatus[] allowed)
    {
        if (!allowed.Contains(p.Status))
            throw new DomainException($"لا يمكن {action} للقطعة {p.InternalNo} وهي «{CustodyText.Status[p.Status]}».");
    }

    private async Task NoPendingAsync(Part p)
    {
        var t = await db.CustodyTransfers.Where(x => x.PartId == p.Id && x.Status == "Pending").Select(x => x.No).FirstOrDefaultAsync();
        if (t != null) throw new DomainException($"القطعة {p.InternalNo} في تسليم معلّق ({t}) بانتظار قبول المستلم. أكمل التسليم أو ارفضه أولًا.");
    }

    private static string Who(Part p, CustodyLookups L) => p.Status switch
    {
        PartStatus.Issued => $"عهدة {p.HolderRef}",
        PartStatus.Installed => $"على الشاحنة {L.Plate(p.VehicleId)}{(string.IsNullOrEmpty(p.Position) ? "" : " · " + p.Position)}",
        PartStatus.Stock => $"المستودع {L.Wh(p.WarehouseId)}",
        PartStatus.Repair => $"الإصلاح لدى {p.RepairVendor}",
        _ => L.Location(p),
    };

    private static string WarrantyState(DateOnly? end)
    {
        if (end is not { } e) return "بدون ضمان";
        var today = Ksa.Day(DateTimeOffset.UtcNow);
        return e < today ? "منتهي" : e <= today.AddDays(30) ? "ينتهي قريبًا" : "ساري";
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string? NormSerial(string? s) => string.IsNullOrWhiteSpace(s) ? null : Regex.Replace(s.Trim().ToUpperInvariant(), @"\s+", "");

    private object PartRow(Part p, CustodyLookups L, bool cost, string? pending = null)
    {
        var c = L.Catalog.GetValueOrDefault(p.CatalogId);
        return new
        {
            p.Id, p.InternalNo, p.Serial, p.CatalogId, code = c?.Code, name = c?.Name, category = c?.Category, tracking = c?.Tracking, p.Brand, p.Model, mfrPartNo = c?.MfrPartNo,
            status = p.Status, statusLabel = CustodyText.Status[p.Status], p.StatusReason, location = L.Location(p),
            p.WarehouseId, warehouse = L.Wh(p.WarehouseId), p.Bin, p.VehicleId, plate = L.Plate(p.VehicleId), p.Position, p.InstalledAt, p.InstallKm, p.InstallHours,
            p.HolderKind, p.HolderId, holder = p.HolderRef, holderType = CustodyText.Holder(p.HolderKind), p.HolderSince,
            p.Supplier, p.InvoiceNo, p.PurchaseDate, cost = cost ? p.Cost : null, p.WarrantyStart, p.WarrantyEnd, p.WarrantyTerms, warranty = WarrantyState(p.WarrantyEnd),
            p.BatchNo, p.Specs, p.ExpiryDate, p.LastInspection, p.NextInspection, p.Condition, p.Notes, p.MeterId,
            p.RepairVendor, p.RepairSentAt, p.RepairExpectedAt, repairCost = cost ? p.RepairCost : null, p.ClaimRef, p.ClaimOpenedAt, p.ClaimResult, p.LastLocation, p.ReviewOutcome, p.CreatedAt,
            pendingTransfer = pending,
        };
    }

    private object MoveRow(PartMovement m, CustodyLookups L, bool cost, Dictionary<Guid, string>? partNos = null) => new
    {
        m.Id, m.Type, label = CustodyText.Move(m.Type), m.PartId, partNo = m.PartId is { } pid ? partNos?.GetValueOrDefault(pid) : null, m.CatalogId, item = L.Item(m.CatalogId), code = L.Code(m.CatalogId),
        m.Qty, m.StatusBefore, m.StatusAfter, from = L.From(m), to = L.To(m), plate = L.Plate(m.VehicleId ?? m.FromVehicleId), m.Position, m.WorkOrderId, workOrder = L.Wo(m.WorkOrderId),
        m.Reason, m.OldPartFate, fate = m.OldPartFate is { } f ? CustodyText.Fates.GetValueOrDefault(f, f) : null, m.RelatedPartId, relatedNo = m.RelatedPartId is { } r ? partNos?.GetValueOrDefault(r) : null,
        m.TransferId, m.Km, m.Hours, cost = cost ? m.Cost : null, m.Vendor, m.DocRef, m.BatchNo, m.ExpectedReturn, m.Note, m.CorrectsId, m.At, by = L.User(m.ByUserId), approvedBy = L.User(m.ApprovedBy),
    };

    // ── meta and master data ──────────────────────────────────────────────────────────────────────────────────────
    [HttpGet("meta")]
    public async Task<IActionResult> Meta()
    {
        var a = await actor.GetAsync();
        if (!a.Can(Section.Custody) && !a.Can(Section.VehicleFile) && a.DriverId is null) throw new ForbiddenException();
        return Ok(new
        {
            statuses = CustodyText.Status.Select(kv => new { value = kv.Key, label = kv.Value }), moves = CustodyText.Moves, fates = CustodyText.Fates,
            issueReasons = CustodyText.IssueReasons, categories = CustodyText.Categories, tyrePositions = CustodyText.TyrePositions, workOrderTypes = CustodyText.WorkOrderTypes,
            canEdit = a.Can(Section.Custody, PermissionLevel.Edit), canSeeCost = SeesCost(a), canExport = a.Can(Section.Export, PermissionLevel.Edit),
        });
    }

    [HttpGet("warehouses")]
    public async Task<IActionResult> Warehouses() { await ViewAsync(); return Ok(await db.Warehouses.OrderBy(x => x.Name).ToListAsync()); }

    [HttpPost("warehouses")]
    public async Task<IActionResult> AddWarehouse(WarehouseInput i)
    {
        var a = await EditAsync();
        var w = new Warehouse { Name = i.Name.Trim(), Location = Clean(i.Location), Keeper = Clean(i.Keeper), IsActive = i.IsActive };
        db.Warehouses.Add(w);
        audit.Write(a, "warehouse", w.Id, "إضافة مستودع", w.Name);
        await db.SaveChangesAsync();
        return Ok(w);
    }

    [HttpPut("warehouses/{id:guid}")]
    public async Task<IActionResult> EditWarehouse(Guid id, WarehouseInput i)
    {
        var a = await EditAsync();
        var w = await db.Warehouses.FirstOrDefaultAsync(x => x.Id == id) ?? throw new NotFoundException("المستودع");
        var before = JsonSerializer.Serialize(w);
        (w.Name, w.Location, w.Keeper, w.IsActive) = (i.Name.Trim(), Clean(i.Location), Clean(i.Keeper), i.IsActive);
        audit.Write(a, "warehouse", w.Id, "تعديل مستودع", w.Name, before, JsonSerializer.Serialize(w));
        await db.SaveChangesAsync();
        return Ok(w);
    }

    [HttpGet("employees")]
    public async Task<IActionResult> Employees() { await ViewAsync(); return Ok(await db.Employees.OrderBy(x => x.Name).ToListAsync()); }

    [HttpPost("employees")]
    public async Task<IActionResult> AddEmployee(EmployeeInput i)
    {
        var a = await EditAsync();
        var e = new Employee { Name = i.Name.Trim(), Role = Clean(i.Role), EmpNo = Clean(i.EmpNo), Phone = Clean(i.Phone), IsActive = i.IsActive };
        db.Employees.Add(e);
        audit.Write(a, "employee", e.Id, "إضافة موظف عهدة", e.Name);
        await db.SaveChangesAsync();
        return Ok(e);
    }

    [HttpPut("employees/{id:guid}")]
    public async Task<IActionResult> EditEmployee(Guid id, EmployeeInput i)
    {
        var a = await EditAsync();
        var e = await db.Employees.FirstOrDefaultAsync(x => x.Id == id) ?? throw new NotFoundException("الموظف");
        var before = JsonSerializer.Serialize(e);
        (e.Name, e.Role, e.EmpNo, e.Phone, e.IsActive) = (i.Name.Trim(), Clean(i.Role), Clean(i.EmpNo), Clean(i.Phone), i.IsActive);
        audit.Write(a, "employee", e.Id, "تعديل موظف عهدة", e.Name, before, JsonSerializer.Serialize(e));
        await db.SaveChangesAsync();
        return Ok(e);
    }

    /// <summary>Everyone who can hold custody (drivers and employees) with what each holds now.</summary>
    [HttpGet("holders")]
    public async Task<IActionResult> Holders()
    {
        await ViewAsync();
        var parts = await db.Parts.Where(p => p.Status == PartStatus.Issued).GroupBy(p => new { p.HolderKind, p.HolderId }).Select(g => new { g.Key.HolderKind, g.Key.HolderId, n = g.Count() }).ToListAsync();
        var qty = await db.HolderBalances.Where(x => x.Qty > 0).GroupBy(x => new { x.HolderKind, x.HolderId }).Select(g => new { g.Key.HolderKind, g.Key.HolderId, n = g.Count() }).ToListAsync();
        int P(string k, Guid id) => parts.FirstOrDefault(x => x.HolderKind == k && x.HolderId == id)?.n ?? 0;
        int Q(string k, Guid id) => qty.FirstOrDefault(x => x.HolderKind == k && x.HolderId == id)?.n ?? 0;
        var drivers = (await db.Drivers.OrderBy(x => x.Name).ToListAsync()).Select(d => new { kind = "driver", d.Id, d.Name, role = "سائق", d.EmpNo, parts = P("driver", d.Id), items = Q("driver", d.Id) });
        var emps = (await db.Employees.Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync()).Select(e => new { kind = "employee", e.Id, e.Name, role = e.Role ?? "موظف", e.EmpNo, parts = P("employee", e.Id), items = Q("employee", e.Id) });
        return Ok(drivers.Concat(emps));
    }

    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog()
    {
        var a = await ViewAsync();
        var stock = await db.StockBalances.GroupBy(x => x.CatalogId).Select(g => new { g.Key, q = g.Sum(x => x.Qty) }).ToDictionaryAsync(x => x.Key, x => x.q);
        var parts = await db.Parts.Where(p => p.Status == PartStatus.Stock).GroupBy(p => p.CatalogId).Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n);
        var list = await db.PartCatalog.OrderBy(x => x.Category).ThenBy(x => x.Name).ToListAsync();
        return Ok(list.Select(c =>
        {
            var have = c.Tracking == PartTracking.Quantity ? stock.GetValueOrDefault(c.Id) : parts.GetValueOrDefault(c.Id);
            return new
            {
                c.Id, c.Code, c.Name, c.Tracking, c.Category, c.Brand, c.Model, c.MfrPartNo, c.Unit, c.MinQty, c.ReorderQty, c.InspectEveryDays, unitCost = SeesCost(a) ? c.UnitCost : null,
                c.Specs, c.IsActive, inStock = have, belowMin = c.MinQty is { } m && have <= m,
            };
        }));
    }

    [HttpPost("catalog")]
    public async Task<IActionResult> AddCatalog(CatalogInput i)
    {
        var a = await EditAsync();
        var code = i.Code.Trim().ToUpperInvariant();
        if (await db.PartCatalog.AnyAsync(x => x.Code == code)) throw new DomainException($"رمز الصنف «{code}» مستخدم لصنف آخر.");
        var c = new PartCatalog { Code = code };
        ApplyCatalog(c, i);
        db.PartCatalog.Add(c);
        audit.Write(a, "partcatalog", c.Id, "إضافة صنف", $"{c.Code} · {c.Name}");
        await db.SaveChangesAsync();
        return Ok(c);
    }

    [HttpPut("catalog/{id:guid}")]
    public async Task<IActionResult> EditCatalog(Guid id, CatalogInput i)
    {
        var a = await EditAsync();
        var c = await svc.CatalogAsync(id);
        var code = i.Code.Trim().ToUpperInvariant();
        if (code != c.Code && await db.PartCatalog.AnyAsync(x => x.Code == code)) throw new DomainException($"رمز الصنف «{code}» مستخدم لصنف آخر.");
        if (i.Tracking != c.Tracking && (await db.Parts.AnyAsync(p => p.CatalogId == id) || await db.StockBalances.AnyAsync(s => s.CatalogId == id && s.Qty > 0)))
            throw new DomainException("لا يمكن تغيير طريقة التتبع لصنف له قطع أو رصيد.");
        var before = JsonSerializer.Serialize(c);
        c.Code = code;
        ApplyCatalog(c, i);
        audit.Write(a, "partcatalog", c.Id, "تعديل صنف", c.Name, before, JsonSerializer.Serialize(c));
        await db.SaveChangesAsync();
        return Ok(c);
    }

    private static void ApplyCatalog(PartCatalog c, CatalogInput i) =>
        (c.Name, c.Tracking, c.Category, c.Brand, c.Model, c.MfrPartNo, c.Unit, c.MinQty, c.ReorderQty, c.InspectEveryDays, c.UnitCost, c.Specs, c.IsActive) =
        (i.Name.Trim(), i.Tracking, i.Category.Trim(), Clean(i.Brand), Clean(i.Model), Clean(i.MfrPartNo), Clean(i.Unit) ?? (i.Tracking == PartTracking.Individual ? "قطعة" : "وحدة"),
         i.MinQty, i.ReorderQty, i.InspectEveryDays, i.UnitCost, Clean(i.Specs), i.IsActive);

    // ── parts ─────────────────────────────────────────────────────────────────────────────────────────────────────
    [HttpGet("parts")]
    public async Task<IActionResult> Parts([FromQuery] string? q, [FromQuery] PartStatus? status, [FromQuery] Guid? catalogId, [FromQuery] string? category, [FromQuery] Guid? vehicleId,
        [FromQuery] string? holderKind, [FromQuery] Guid? holderId, [FromQuery] Guid? warehouseId, [FromQuery] int take = 500)
    {
        var a = await ViewAsync();
        var L = await CustodyLookups.LoadAsync(db);
        var x = db.Parts.AsQueryable();
        if (status is { } s) x = x.Where(p => p.Status == s);
        if (catalogId is { } c) x = x.Where(p => p.CatalogId == c);
        if (!string.IsNullOrWhiteSpace(category)) x = x.Where(p => db.PartCatalog.Any(k => k.Id == p.CatalogId && k.Category == category));
        if (vehicleId is { } v) x = x.Where(p => p.VehicleId == v);
        if (holderId is { } h) x = x.Where(p => p.HolderId == h && (holderKind == null || p.HolderKind == holderKind));
        if (warehouseId is { } w) x = x.Where(p => p.WarehouseId == w);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim(); var up = NormSerial(t)!;
            x = x.Where(p => p.InternalNo == up || (p.Serial != null && p.Serial.Contains(up)) || (p.HolderRef != null && p.HolderRef.Contains(t)) || (p.Brand != null && p.Brand.Contains(t))
                || db.PartCatalog.Any(k => k.Id == p.CatalogId && (k.Name.Contains(t) || k.Code.Contains(up))) || db.Vehicles.Any(vv => vv.Id == p.VehicleId && vv.Plate.Contains(t)));
        }
        var list = await x.OrderByDescending(p => p.CreatedAt).Take(Math.Clamp(take, 1, 2000)).ToListAsync();
        var ids = list.Select(p => p.Id).ToList();
        var pending = await db.CustodyTransfers.Where(t => t.Status == "Pending" && t.PartId != null && ids.Contains(t.PartId!.Value)).ToDictionaryAsync(t => t.PartId!.Value, t => t.No);
        return Ok(list.Select(p => PartRow(p, L, SeesCost(a), pending.GetValueOrDefault(p.Id))));
    }

    [HttpGet("parts/{id:guid}")]
    public async Task<IActionResult> Part(Guid id)
    {
        var a = await ViewAsync();
        var p = await db.Parts.FirstOrDefaultAsync(x => x.Id == id) ?? throw new NotFoundException("القطعة");
        var L = await CustodyLookups.LoadAsync(db);
        var moves = await db.PartMovements.Where(m => m.PartId == id).OrderByDescending(m => m.At).ToListAsync();
        var related = moves.Where(m => m.RelatedPartId != null).Select(m => m.RelatedPartId!.Value).Append(id).Distinct().ToList();
        var nos = await db.Parts.Where(x => related.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.InternalNo);
        var transfers = await db.CustodyTransfers.Where(t => t.PartId == id).OrderByDescending(t => t.RequestedAt).ToListAsync();
        var files = await db.Attachments.Where(x => x.OwnerType == "part" && x.OwnerId == id).OrderBy(x => x.UploadedAt)
            .Select(x => new { x.Id, x.Kind, x.FileName, x.ContentType, x.Size, x.UploadedAt }).ToListAsync();
        var meter = p.MeterId is { } mid ? await db.Meters.Where(m => m.Id == mid).Select(m => new { m.Id, m.Serial, m.Kind, m.VehicleId, m.LastCalibration, m.NextCalibration }).FirstOrDefaultAsync() : null;
        var meterResets = p.MeterId is { } mid2 ? await db.MeterResets.Where(r => r.MeterId == mid2).OrderByDescending(r => r.At).Select(r => new { r.At, r.Type, r.Reason, r.NewBaseline }).ToListAsync() : [];
        // usage: install periods (km and hours travelled while installed)
        var life = LifePeriods(moves.OrderBy(m => m.At).ToList(), p, L);
        return Ok(new
        {
            part = PartRow(p, L, SeesCost(a), transfers.FirstOrDefault(t => t.Status == "Pending")?.No),
            movements = moves.Select(m => MoveRow(m, L, SeesCost(a), nos)),
            transfers = transfers.Select(t => TransferRow(t, L)), attachments = files, meter, meterResets, life,
            totalKm = life.Sum(x => x.Km ?? 0), totalDays = life.Sum(x => x.Days),
        });
    }

    public record LifePeriod(string Plate, string? Position, DateTimeOffset From, DateTimeOffset? To, int Days, decimal? Km, decimal? Hours, string? EndReason);

    /// <summary>Install → remove periods of one part, from its movements (oldest first).</summary>
    public static List<LifePeriod> LifePeriods(List<PartMovement> moves, Part p, CustodyLookups L)
    {
        var res = new List<LifePeriod>();
        PartMovement? open = null;
        foreach (var m in moves)
        {
            if (m.Type is "install" or "replace-in") open = m;
            else if (m.Type is "remove" or "replace-out" || (m.Type is "damaged" or "lost" && m.FromVehicleId != null))
            {
                if (open is null) continue;
                res.Add(new LifePeriod(L.Plate(open.VehicleId), open.Position, open.At, m.At, (int)(m.At - open.At).TotalDays,
                    m.Km is { } k2 && open.Km is { } k1 ? k2 - k1 : null, m.Hours is { } h2 && open.Hours is { } h1 ? h2 - h1 : null, m.Reason));
                open = null;
            }
        }
        if (open != null && p.Status == PartStatus.Installed)
            res.Add(new LifePeriod(L.Plate(open.VehicleId), open.Position, open.At, null, (int)(DateTimeOffset.UtcNow - open.At).TotalDays, null, null, null));
        return res;
    }

    /// <summary>Receive one individually tracked part into a warehouse. The server issues the internal number (P-000123) printed as QR.</summary>
    [HttpPost("parts")]
    public async Task<IActionResult> CreatePart(PartInput i)
    {
        var a = await EditAsync();
        var c = await svc.CatalogAsync(i.CatalogId);
        if (c.Tracking != PartTracking.Individual) throw new DomainException($"«{c.Name}» صنف يُتابع بالكمية؛ استلمه من «المخزون» بالكمية.");
        var wh = await svc.WarehouseAsync(i.WarehouseId);
        await Refs.EnsureAsync<Meter>(db, i.MeterId, "العداد");
        return await Tx(async () =>
        {
            var p = new Part { CatalogId = c.Id, Status = PartStatus.Stock, WarehouseId = wh.Id, InternalNo = await svc.NextNoAsync("PART", "P-", 6) };
            await ApplyPartAsync(p, i, c);
            p.Condition ??= "جديد";
            db.Parts.Add(p);
            Log(a, "receive", p, PartStatus.Stock, m => { m.StatusBefore = null; m.ToWarehouseId = wh.Id; m.Vendor = p.Supplier; m.DocRef = p.InvoiceNo; m.Cost = p.Cost; m.UnitCost = p.Cost; m.BatchNo = p.BatchNo; m.Note = Clean(i.Notes); });
            audit.Write(a, "part", p.Id, "استلام قطعة", $"{p.InternalNo} · {c.Name}{(p.Serial is null ? "" : " · " + p.Serial)}");
            await db.SaveChangesAsync();
            var L = await CustodyLookups.LoadAsync(db);
            return Ok(PartRow(p, L, SeesCost(a)));
        });
    }

    /// <summary>Corrects a part's data (not its place or status). The old values stay in the audit log and an "edit" movement keeps the reason.</summary>
    [HttpPut("parts/{id:guid}")]
    public async Task<IActionResult> EditPart(Guid id, PartInput i)
    {
        var a = await EditAsync();
        if (string.IsNullOrWhiteSpace(i.Reason) || i.Reason.Trim().Length < 3) throw new DomainException("سبب التعديل مطلوب.");
        await Refs.EnsureAsync<Meter>(db, i.MeterId, "العداد");
        return await Tx(async () =>
        {
            var p = await svc.LockPartAsync(id);
            var c = await svc.CatalogAsync(p.CatalogId);
            if (i.CatalogId != p.CatalogId) throw new DomainException("لا يمكن تغيير صنف القطعة؛ سجّل قطعة جديدة.");
            var before = JsonSerializer.Serialize(p);
            await ApplyPartAsync(p, i, c);
            var after = JsonSerializer.Serialize(p);
            Log(a, "edit", p, p.Status, m => { m.Reason = i.Reason!.Trim(); m.Note = "تعديل بيانات القطعة (القيم السابقة في سجل التعديلات)"; });
            audit.Write(a, "part", p.Id, "تعديل بيانات قطعة", i.Reason!.Trim(), before, after);
            return Ok(new { p.Id });
        });
    }

    private async Task ApplyPartAsync(Part p, PartInput i, PartCatalog c)
    {
        if (i.WarrantyStart is { } ws && i.WarrantyEnd is { } we && we < ws) throw new DomainException("نهاية الضمان قبل بدايته.");
        if (i.PurchaseDate is { } pd && pd > Ksa.Day(DateTimeOffset.UtcNow)) throw new DomainException("تاريخ الشراء في المستقبل.");
        p.Serial = NormSerial(i.Serial);
        p.Brand = Clean(i.Brand) ?? c.Brand;
        p.Model = Clean(i.Model) ?? c.Model;
        p.SerialKey = p.Serial is null ? null : $"{(p.Brand ?? "").Trim().ToUpperInvariant()}|{p.Serial}";
        if (p.SerialKey != null)
        {
            var dup = await db.Parts.Where(x => x.SerialKey == p.SerialKey && x.Id != p.Id).Select(x => x.InternalNo).FirstOrDefaultAsync();
            if (dup != null) throw new DomainException($"الرقم التسلسلي «{p.Serial}» مسجل مسبقًا لقطعة من الصانع نفسه ({dup}).");
        }
        (p.Supplier, p.InvoiceNo, p.PurchaseDate, p.Cost, p.WarrantyStart, p.WarrantyEnd, p.WarrantyTerms, p.BatchNo, p.Specs, p.ExpiryDate, p.Condition, p.Notes, p.MeterId) =
            (Clean(i.Supplier), Clean(i.InvoiceNo), i.PurchaseDate, i.Cost ?? p.Cost ?? c.UnitCost, i.WarrantyStart ?? (i.WarrantyEnd != null ? i.PurchaseDate : null), i.WarrantyEnd, Clean(i.WarrantyTerms),
             Clean(i.BatchNo), Clean(i.Specs) ?? c.Specs, i.ExpiryDate, Clean(i.Condition) ?? p.Condition, Clean(i.Notes), i.MeterId);
        p.NextInspection = i.NextInspection ?? p.NextInspection ?? (c.InspectEveryDays is { } d ? Ksa.Day(DateTimeOffset.UtcNow).AddDays(d) : null);
        if (p.Status == PartStatus.Stock && Clean(i.Bin) is { } bin) p.Bin = bin;
    }

    // ── custody hand-over: issue / transfer → accept ───────────────────────────────────────────────────────────────
    /// <summary>Issue a part from the warehouse to a person. Linked to an open work order or an approved reason.
    /// The part becomes the receiver's custody when he accepts (or at once when he signed at the counter: ReceiverSigned).</summary>
    [HttpPost("parts/{id:guid}/issue")]
    public async Task<IActionResult> Issue(Guid id, IssueInput i)
    {
        var a = await EditAsync();
        var wo = await svc.RequireWorkOrderOrReasonAsync(i.WorkOrderId, i.Reason);
        var h = await svc.HolderAsync(i.HolderKind, i.HolderId);
        return await Tx(async () =>
        {
            var p = await svc.LockPartAsync(id);
            Need(p, "صرف", PartStatus.Stock);
            await NoPendingAsync(p);
            var L = await CustodyLookups.LoadAsync(db);
            var t = new CustodyTransfer
            {
                No = await svc.NextNoAsync("TR", "TR-", 5), Kind = "issue", PartId = p.Id, CatalogId = p.CatalogId, Qty = 1, FromKind = "warehouse", FromId = p.WarehouseId!.Value, FromName = L.Wh(p.WarehouseId),
                ToKind = h.Kind, ToId = h.Id, ToName = h.Name, WorkOrderId = wo?.Id, Reason = wo is null ? i.Reason!.Trim() : Clean(i.Reason), Condition = Clean(i.Condition) ?? p.Condition,
                HandoverBy = Clean(i.HandoverBy), HandoverNote = Clean(i.Note), RequestedAt = DateTimeOffset.UtcNow, RequestedBy = a.UserId,
            };
            db.CustodyTransfers.Add(t);
            Log(a, "handover", p, p.Status, m => { m.TransferId = t.Id; m.FromWarehouseId = p.WarehouseId; m.HolderKind = h.Kind; m.HolderId = h.Id; m.HolderRef = h.Name; m.WorkOrderId = t.WorkOrderId; m.Reason = t.Reason; m.Note = t.HandoverNote; });
            audit.Write(a, "part", p.Id, "صرف قطعة (تسليم)", $"{p.InternalNo} → {h.Name} · {t.No}");
            if (h.Kind == "driver") audit.Notify(h.Id, $"لديك قطعة للاستلام كعهدة: {L.Item(p.CatalogId)} {p.InternalNo} ({t.No}). أكّد الاستلام.", t.No);
            await db.SaveChangesAsync();
            if (i.ReceiverSigned) await AcceptAsync(a, t, new DecideInput("استلم بتوقيعه عند أمين المستودع", null, null, null, null));
            return Ok(TransferRow(t, L));
        });
    }

    /// <summary>Hand-over between people, trucks and warehouses. Completed only when the receiver accepts.</summary>
    [HttpPost("parts/{id:guid}/transfer")]
    public async Task<IActionResult> Transfer(Guid id, TransferInput i)
    {
        var a = await EditAsync();
        return await Tx(async () =>
        {
            var p = await svc.LockPartAsync(id);
            await NoPendingAsync(p);
            var L = await CustodyLookups.LoadAsync(db);
            var (fromKind, fromId, fromName) = p.Status switch
            {
                PartStatus.Issued => (p.HolderKind!, p.HolderId!.Value, p.HolderRef ?? ""),
                PartStatus.Stock => ("warehouse", p.WarehouseId!.Value, L.Wh(p.WarehouseId)),
                PartStatus.Installed => ("vehicle", p.VehicleId!.Value, L.Plate(p.VehicleId)),
                _ => throw new DomainException($"لا يمكن نقل القطعة {p.InternalNo} وهي «{CustodyText.Status[p.Status]}»."),
            };
            string toName;
            switch (i.ToKind)
            {
                case "driver" or "employee":
                    if (fromKind == "vehicle") throw new DomainException("القطعة مركّبة على شاحنة: فكّها أولًا (مع تحديد مصيرها) ثم سلّمها.");
                    if (fromKind == "warehouse") throw new DomainException("الصرف من المستودع لشخص يتم من «صرف عهدة» مع أمر صيانة أو سبب معتمد.");
                    toName = (await svc.HolderAsync(i.ToKind, i.ToId)).Name;
                    if (fromKind == i.ToKind && fromId == i.ToId) throw new DomainException("المستلم هو حامل العهدة الحالي.");
                    break;
                case "warehouse":
                    if (fromKind == "vehicle") throw new DomainException("القطعة مركّبة على شاحنة: استخدم «فك» مع تحديد المصير.");
                    toName = (await svc.WarehouseAsync(i.ToId)).Name;
                    if (fromKind == "warehouse" && fromId == i.ToId) throw new DomainException("القطعة في هذا المستودع أصلًا.");
                    break;
                case "vehicle":
                    if (fromKind != "vehicle") throw new DomainException("التركيب على شاحنة يتم من «تركيب» مع أمر صيانة أو سبب معتمد.");
                    toName = (await svc.VehicleAsync(i.ToId)).Plate;
                    if (fromId == i.ToId) throw new DomainException("القطعة على هذه الشاحنة أصلًا.");
                    if (i.Km is { } km && p.InstallKm is { } ik && km < ik) throw new DomainException($"قراءة العداد عند الفك ({CustodyText.N(km)}) أقل من قراءته عند التركيب ({CustodyText.N(ik)}).");
                    break;
                default: throw new DomainException("حدد المستلم: سائق أو موظف أو مستودع أو شاحنة.");
            }
            var t = new CustodyTransfer
            {
                No = await svc.NextNoAsync("TR", "TR-", 5), Kind = "transfer", PartId = p.Id, CatalogId = p.CatalogId, Qty = 1, FromKind = fromKind, FromId = fromId, FromName = fromName,
                ToKind = i.ToKind, ToId = i.ToId, ToName = toName, Position = Clean(i.Position), Km = i.Km, Condition = Clean(i.Condition) ?? p.Condition, HandoverBy = Clean(i.HandoverBy) ?? (fromKind is "driver" or "employee" ? fromName : null),
                HandoverNote = Clean(i.Note), RequestedAt = DateTimeOffset.UtcNow, RequestedBy = a.UserId,
            };
            db.CustodyTransfers.Add(t);
            Log(a, "handover", p, p.Status, m => { m.TransferId = t.Id; m.Note = $"{CustodyText.Holder(fromKind)} {fromName} ← {CustodyText.Holder(t.ToKind)} {toName}" + (t.HandoverNote is null ? "" : " · " + t.HandoverNote); });
            audit.Write(a, "part", p.Id, "طلب نقل عهدة", $"{p.InternalNo}: {fromName} → {toName} · {t.No}");
            if (t.ToKind == "driver") audit.Notify(t.ToId, $"لديك عهدة بانتظار تأكيد الاستلام: {L.Item(p.CatalogId)} {p.InternalNo} ({t.No}).", t.No);
            return Ok(TransferRow(t, L));
        });
    }

    [HttpGet("transfers")]
    public async Task<IActionResult> Transfers([FromQuery] string? status, [FromQuery] int take = 300)
    {
        await ViewAsync();
        var L = await CustodyLookups.LoadAsync(db);
        var q = db.CustodyTransfers.AsQueryable();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(t => t.Status == status);
        var list = await q.OrderByDescending(t => t.RequestedAt).Take(Math.Clamp(take, 1, 2000)).ToListAsync();
        var nos = await PartNosAsync(list.Where(t => t.PartId != null).Select(t => t.PartId!.Value));
        return Ok(list.Select(t => TransferRow(t, L, nos)));
    }

    private async Task<Dictionary<Guid, string>> PartNosAsync(IEnumerable<Guid> ids)
    {
        var l = ids.Distinct().ToList();
        return await db.Parts.Where(p => l.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.InternalNo);
    }

    private static object TransferRow(CustodyTransfer t, CustodyLookups L, Dictionary<Guid, string>? nos = null) => new
    {
        t.Id, t.No, t.Kind, kindLabel = t.Kind switch { "issue" => "صرف عهدة", "qty-issue" => "صرف كمية", "qty-transfer" => "نقل كمية", _ => "نقل عهدة" },
        t.PartId, partNo = t.PartId is { } p ? nos?.GetValueOrDefault(p) : null, t.CatalogId, item = L.Item(t.CatalogId), code = L.Code(t.CatalogId), t.Qty,
        t.FromKind, fromType = CustodyText.Holder(t.FromKind), t.FromId, t.FromName, t.ToKind, toType = CustodyText.Holder(t.ToKind), t.ToId, t.ToName, t.Position, t.Km,
        t.WorkOrderId, workOrder = L.Wo(t.WorkOrderId), t.Reason, t.Status, statusLabel = t.Status switch { "Pending" => "بانتظار قبول المستلم", "Accepted" => "مقبول", "Rejected" => "مرفوض", _ => t.Status },
        t.Condition, t.HandoverBy, t.HandoverNote, t.RequestedAt, requestedBy = L.User(t.RequestedBy), t.DecidedAt, decidedBy = L.User(t.DecidedBy), t.DecisionNote,
    };

    private static bool MayDecide(Actor a, CustodyTransfer t) =>
        a.Can(Section.Custody, PermissionLevel.Edit) || (t.ToKind == "driver" && a.DriverId == t.ToId && a.Can(Section.Driver, PermissionLevel.Edit));

    [HttpPost("transfers/{id:guid}/accept")]
    public async Task<IActionResult> Accept(Guid id, DecideInput i)
    {
        var a = await actor.GetAsync();
        return await Tx(async () =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM custody_transfers WHERE id = {id} FOR UPDATE");
            var t = await db.CustodyTransfers.FirstOrDefaultAsync(x => x.Id == id) ?? throw new NotFoundException("التسليم");
            if (!MayDecide(a, t)) throw new ForbiddenException();
            await AcceptAsync(a, t, i);
            return Ok(TransferRow(t, await CustodyLookups.LoadAsync(db)));
        });
    }

    /// <summary>Receiver accepts: the custody / location changes now, with movements recorded for the item's history.</summary>
    private async Task AcceptAsync(Actor a, CustodyTransfer t, DecideInput i)
    {
        if (t.Status != "Pending") throw new DomainException($"التسليم {t.No} {(t.Status == "Accepted" ? "مقبول" : "مرفوض")} مسبقًا.");
        t.Status = "Accepted"; t.DecidedAt = DateTimeOffset.UtcNow; t.DecidedBy = a.UserId; t.DecisionNote = Clean(i.Note);
        var c = await svc.CatalogAsync(t.CatalogId);
        if (t.PartId is null)   // quantity items: they left the source when handed over
        {
            var to = t.ToKind == "warehouse" ? null : await svc.HolderAsync(t.ToKind, t.ToId);
            if (to is null) await svc.AddStockAsync(t.CatalogId, t.ToId, t.Qty, Clean(i.Bin));
            else await svc.AddHolderAsync(to, t.CatalogId, t.Qty);
            LogQty(a, t.Kind == "qty-issue" ? "stock-issue" : to is null ? "stock-return" : "transfer", t.CatalogId, t.Qty, m =>
            {
                m.TransferId = t.Id; m.WorkOrderId = t.WorkOrderId; m.Reason = t.Reason; m.Note = t.DecisionNote; m.ApprovedBy = t.RequestedBy;
                if (t.FromKind == "warehouse") m.FromWarehouseId = t.FromId; else (m.FromHolderKind, m.FromHolderId, m.FromHolderRef) = (t.FromKind, t.FromId, t.FromName);
                if (to is null) m.ToWarehouseId = t.ToId; else (m.HolderKind, m.HolderId, m.HolderRef) = (to.Kind, to.Id, to.Name);
                if (t.Kind == "qty-issue") { m.UnitCost = c.UnitCost; m.Cost = c.UnitCost * t.Qty; }
            });
            audit.Write(a, "custodytransfer", t.Id, "قبول استلام", $"{t.No} · {c.Name} × {CustodyText.N(t.Qty)} → {t.ToName}");
            return;
        }
        var p = await svc.LockPartAsync(t.PartId.Value);
        var before = p.Status;
        var now = DateTimeOffset.UtcNow;
        switch (t.ToKind)
        {
            case "driver" or "employee":
                var h = await svc.HolderAsync(t.ToKind, t.ToId);
                var fromWh = p.WarehouseId;
                (p.Status, p.HolderKind, p.HolderId, p.HolderRef, p.HolderSince, p.WarehouseId, p.Bin) = (PartStatus.Issued, h.Kind, h.Id, h.Name, now, null, null);
                if (t.Condition != null) p.Condition = t.Condition;
                Log(a, t.Kind == "issue" ? "issue" : "transfer", p, before, m =>
                {
                    m.TransferId = t.Id; m.FromWarehouseId = fromWh; m.FromHolderKind = before == PartStatus.Issued ? t.FromKind : null; m.FromHolderId = before == PartStatus.Issued ? t.FromId : null;
                    m.FromHolderRef = before == PartStatus.Issued ? t.FromName : null; m.HolderKind = h.Kind; m.HolderId = h.Id; m.HolderRef = h.Name; m.WorkOrderId = t.WorkOrderId; m.Reason = t.Reason;
                    m.Note = t.DecisionNote; m.ApprovedBy = t.RequestedBy;
                });
                break;
            case "warehouse":
                var w = await svc.WarehouseAsync(t.ToId);
                var (hk, hid, hn) = (p.HolderKind, p.HolderId, p.HolderRef);
                var fromW = p.WarehouseId;
                (p.Status, p.WarehouseId, p.Bin, p.HolderKind, p.HolderId, p.HolderRef, p.HolderSince) = (PartStatus.Stock, w.Id, Clean(i.Bin), null, null, null, null);
                if (t.Condition != null) p.Condition = t.Condition;
                Log(a, before == PartStatus.Issued ? "return" : "transfer", p, before, m =>
                {
                    m.TransferId = t.Id; m.FromHolderKind = hk; m.FromHolderId = hid; m.FromHolderRef = hn; m.FromWarehouseId = fromW; m.ToWarehouseId = w.Id; m.Note = t.DecisionNote; m.ApprovedBy = t.RequestedBy;
                });
                break;
            case "vehicle":   // truck → truck: removed from the first, installed on the second
                var v = await svc.VehicleAsync(t.ToId);
                var pos = Clean(i.Position) ?? t.Position ?? p.Position;
                await PositionFreeAsync(v.Id, pos, p.Id);
                var fromV = p.VehicleId;
                (p.VehicleId, p.Position, p.Status) = (null, null, PartStatus.Stock);
                Log(a, "remove", p, before, m => { m.TransferId = t.Id; m.FromVehicleId = fromV; m.Km = t.Km; m.Reason = $"نقل إلى الشاحنة {v.Plate} ({t.No})"; });
                await db.SaveChangesAsync();
                (p.Status, p.VehicleId, p.Position, p.InstalledAt, p.InstallKm, p.InstallHours) = (PartStatus.Installed, v.Id, pos, now, i.Km, i.Hours);
                Log(a, "install", p, PartStatus.Stock, m => { m.TransferId = t.Id; m.FromVehicleId = fromV; m.VehicleId = v.Id; m.Position = pos; m.Km = i.Km; m.Hours = i.Hours; m.Reason = $"منقولة من الشاحنة {t.FromName} ({t.No})"; m.ApprovedBy = t.RequestedBy; });
                break;
        }
        audit.Write(a, "custodytransfer", t.Id, "قبول استلام", $"{t.No} · {p.InternalNo} → {t.ToName}");
    }

    [HttpPost("transfers/{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, ReasonInput i)
    {
        var a = await actor.GetAsync();
        return await Tx(async () =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM custody_transfers WHERE id = {id} FOR UPDATE");
            var t = await db.CustodyTransfers.FirstOrDefaultAsync(x => x.Id == id) ?? throw new NotFoundException("التسليم");
            if (!MayDecide(a, t)) throw new ForbiddenException();
            if (t.Status != "Pending") throw new DomainException($"التسليم {t.No} ليس معلّقًا.");
            (t.Status, t.DecidedAt, t.DecidedBy, t.DecisionNote) = ("Rejected", DateTimeOffset.UtcNow, a.UserId, i.Reason.Trim());
            if (t.PartId is { } pid)
            {
                var p = await svc.LockPartAsync(pid);
                Log(a, "handover-rejected", p, p.Status, m => { m.TransferId = t.Id; m.Reason = i.Reason.Trim(); m.Note = $"رفض {t.ToName} استلام القطعة"; });
            }
            else   // quantity returns to where it came from
            {
                if (t.FromKind == "warehouse") await svc.AddStockAsync(t.CatalogId, t.FromId, t.Qty, null);
                else await svc.AddHolderAsync(new Holder(t.FromKind, t.FromId, t.FromName), t.CatalogId, t.Qty);
                LogQty(a, "handover-rejected", t.CatalogId, t.Qty, m => { m.TransferId = t.Id; m.Reason = i.Reason.Trim(); m.Note = $"أعيدت الكمية إلى {t.FromName}"; });
            }
            audit.Write(a, "custodytransfer", t.Id, "رفض استلام", $"{t.No}: {i.Reason.Trim()}");
            return Ok(TransferRow(t, await CustodyLookups.LoadAsync(db)));
        });
    }

    // ── truck: install / remove / replace ─────────────────────────────────────────────────────────────────────────
    private async Task PositionFreeAsync(Guid vehicleId, string? position, Guid partId)
    {
        if (position is null) return;
        var busy = await db.Parts.Where(x => x.VehicleId == vehicleId && x.Position == position && x.Status == PartStatus.Installed && x.Id != partId).Select(x => x.InternalNo).FirstOrDefaultAsync();
        if (busy != null) throw new DomainException($"الموضع «{position}» على الشاحنة مشغول بالقطعة {busy}. استخدم «استبدال».");
    }

    private async Task<bool> FirstInstallAsync(Guid partId) => !await db.PartMovements.AnyAsync(m => m.PartId == partId && (m.Type == "install" || m.Type == "replace-in"));

    [HttpPost("parts/{id:guid}/install")]
    public async Task<IActionResult> Install(Guid id, InstallInput i)
    {
        var a = await EditAsync();
        var v = await svc.VehicleAsync(i.VehicleId);
        return await Tx(async () =>
        {
            var p = await svc.LockPartAsync(id);
            if (p.Status == PartStatus.Installed) throw new DomainException($"القطعة {p.InternalNo} مركّبة على الشاحنة {(await svc.VehicleAsync(p.VehicleId!.Value)).Plate}. لا يمكن تركيبها على شاحنتين؛ فكّها أو انقلها أولًا.");
            Need(p, "تركيب", PartStatus.Stock, PartStatus.Issued);
            await NoPendingAsync(p);
            var wo = await svc.RequireWorkOrderOrReasonAsync(i.WorkOrderId, i.Reason, v.Id);
            var c = await svc.CatalogAsync(p.CatalogId);
            var pos = Clean(i.Position);
            if (pos is null && c.Category == "إطار") throw new DomainException("حدد موضع الإطار على الشاحنة.");
            await PositionFreeAsync(v.Id, pos, p.Id);
            var first = await FirstInstallAsync(p.Id);
            var before = p.Status;
            var (fw, hk, hid, hn) = (p.WarehouseId, p.HolderKind, p.HolderId, p.HolderRef);
            (p.Status, p.VehicleId, p.Position, p.InstalledAt, p.InstallKm, p.InstallHours, p.WarehouseId, p.Bin, p.HolderKind, p.HolderId, p.HolderRef, p.HolderSince) =
                (PartStatus.Installed, v.Id, pos, DateTimeOffset.UtcNow, i.Km, i.Hours, null, null, null, null, null, null);
            Log(a, "install", p, before, m =>
            {
                m.VehicleId = v.Id; m.Position = pos; m.Km = i.Km; m.Hours = i.Hours; m.WorkOrderId = wo?.Id; m.Reason = Clean(i.Reason); m.Note = Clean(i.Note); m.FromWarehouseId = fw;
                m.FromHolderKind = hk; m.FromHolderId = hid; m.FromHolderRef = hn; m.Cost = first ? p.Cost : null; m.ApprovedBy = a.UserId;
            });
            audit.Write(a, "part", p.Id, "تركيب قطعة", $"{p.InternalNo} → {v.Plate}{(pos is null ? "" : " · " + pos)}");
            return Ok(new { p.Id, p.Status });
        });
    }

    /// <summary>Applies the chosen fate of a part that leaves a truck (remove or replace). Returns the fate key.</summary>
    private async Task<string> ApplyFateAsync(Actor a, Part p, FateInput? f, CustodyLookups L, Guid? workOrderId, Guid? fromVehicle)
    {
        var fate = f?.Fate?.Trim();
        if (fate is null || !CustodyText.Fates.ContainsKey(fate))
            throw new DomainException("اختر مصير القطعة القديمة: إرجاع للمستودع، إرسال للإصلاح، مطالبة ضمان، أو إتلاف معتمد.");
        var before = p.Status;
        var now = DateTimeOffset.UtcNow;
        switch (fate)
        {
            case "stock":
                var w = await svc.WarehouseAsync(f!.WarehouseId);
                (p.Status, p.WarehouseId, p.Bin, p.Condition) = (PartStatus.Stock, w.Id, Clean(f.Bin), "مستعمل");
                Log(a, "return", p, before, m => { m.ToWarehouseId = w.Id; m.FromVehicleId = fromVehicle; m.WorkOrderId = workOrderId; });
                break;
            case "repair":
                if (string.IsNullOrWhiteSpace(f!.Vendor)) throw new DomainException("حدد ورشة الإصلاح.");
                (p.Status, p.RepairVendor, p.RepairSentAt, p.RepairExpectedAt, p.RepairCost) = (PartStatus.Repair, f.Vendor.Trim(), now, f.ExpectedReturn, f.EstimatedCost);
                Log(a, "repair-send", p, before, m => { m.Vendor = p.RepairVendor; m.ExpectedReturn = f.ExpectedReturn; m.Cost = null; m.Note = f.EstimatedCost is { } e ? $"تكلفة تقديرية {CustodyText.N(e)}" : null; m.FromVehicleId = fromVehicle; m.WorkOrderId = workOrderId; });
                break;
            case "claim":
                if (string.IsNullOrWhiteSpace(f!.ClaimRef)) throw new DomainException("أدخل رقم مطالبة الضمان.");
                if (p.WarrantyEnd is { } we && we < Ksa.Day(now)) throw new DomainException($"ضمان القطعة {p.InternalNo} انتهى في {we:yyyy-MM-dd}؛ لا يمكن رفع مطالبة ضمان.");
                (p.Status, p.ClaimRef, p.ClaimOpenedAt, p.ClaimResult) = (PartStatus.ReturnedToSupplier, f.ClaimRef.Trim(), now, null);
                Log(a, "claim", p, before, m => { m.Vendor = p.Supplier; m.DocRef = p.ClaimRef; m.FromVehicleId = fromVehicle; m.WorkOrderId = workOrderId; });
                break;
            case "scrap":
                if (string.IsNullOrWhiteSpace(f!.ScrapApproval)) throw new DomainException("الإتلاف يحتاج اعتمادًا: أدخل رقم/ملاحظة الاعتماد.");
                (p.Status, p.ScrapApprovedBy, p.LastLocation) = (PartStatus.Scrapped, a.UserId, $"فُكّت من الشاحنة {L.Plate(fromVehicle)}");
                Log(a, "scrap", p, before, m => { m.ApprovedBy = a.UserId; m.DocRef = f.ScrapApproval.Trim(); m.FromVehicleId = fromVehicle; m.WorkOrderId = workOrderId; });
                break;
        }
        p.StatusReason = CustodyText.Fates[fate];
        return fate;
    }

    [HttpPost("parts/{id:guid}/remove")]
    public async Task<IActionResult> Remove(Guid id, RemoveInput i)
    {
        var a = await EditAsync();
        return await Tx(async () =>
        {
            var p = await svc.LockPartAsync(id);
            Need(p, "فك", PartStatus.Installed);
            await NoPendingAsync(p);
            if (i.Km is { } km && p.InstallKm is { } ik && km < ik) throw new DomainException($"قراءة العداد عند الفك ({CustodyText.N(km)}) أقل من قراءته عند التركيب ({CustodyText.N(ik)}).");
            if (i.WorkOrderId is { } w) await Refs.EnsureAsync<WorkOrder>(db, w, "أمر الصيانة");
            var L = await CustodyLookups.LoadAsync(db);
            var fromV = p.VehicleId; var pos = p.Position;
            var fate = i.Fate?.Fate;
            if (fate is null || !CustodyText.Fates.ContainsKey(fate)) throw new DomainException("اختر مصير القطعة المفكوكة: إرجاع للمستودع، إرسال للإصلاح، مطالبة ضمان، أو إتلاف معتمد.");
            (p.Status, p.VehicleId, p.Position) = (PartStatus.Stock, null, null);
            var rm = Log(a, "remove", p, PartStatus.Installed, m => { m.FromVehicleId = fromV; m.Position = pos; m.Km = i.Km; m.Hours = i.Hours; m.Reason = i.Reason.Trim(); m.OldPartFate = fate; m.WorkOrderId = i.WorkOrderId; m.Note = Clean(i.Note); });
            p.WarehouseId = null;
            await ApplyFateAsync(a, p, i.Fate, L, i.WorkOrderId, fromV);
            rm.StatusAfter = p.Status.ToString();
            audit.Write(a, "part", p.Id, "فك قطعة", $"{p.InternalNo} من {L.Plate(fromV)} · {CustodyText.Fates[fate]} · {i.Reason.Trim()}");
            return Ok(new { p.Id, p.Status });
        });
    }

    /// <summary>Replace an installed part by another one. The old part's fate is mandatory; issuing the new one needs a work order or an approved reason.</summary>
    [HttpPost("replace")]
    public async Task<IActionResult> Replace(ReplaceInput i)
    {
        var a = await EditAsync();
        if (i.OldPartId == i.NewPartId) throw new DomainException("القطعة الجديدة هي القديمة نفسها.");
        if (i.OldPartFate?.Fate is not { } fk || !CustodyText.Fates.ContainsKey(fk))
            throw new DomainException("اختر مصير القطعة القديمة: إرجاع للمستودع، إرسال للإصلاح، مطالبة ضمان، أو إتلاف معتمد.");
        return await Tx(async () =>
        {
            // lock both rows in a fixed order (no deadlock between two replacements)
            Part oldP, newP;
            if (i.OldPartId.CompareTo(i.NewPartId) < 0) { oldP = await svc.LockPartAsync(i.OldPartId); newP = await svc.LockPartAsync(i.NewPartId); }
            else { newP = await svc.LockPartAsync(i.NewPartId); oldP = await svc.LockPartAsync(i.OldPartId); }
            Need(oldP, "استبدال", PartStatus.Installed);
            if (newP.Status == PartStatus.Installed) throw new DomainException($"القطعة الجديدة {newP.InternalNo} مركّبة على شاحنة أخرى.");
            Need(newP, "تركيب", PartStatus.Stock, PartStatus.Issued);
            await NoPendingAsync(oldP); await NoPendingAsync(newP);
            var vid = oldP.VehicleId!.Value;
            var wo = await svc.RequireWorkOrderOrReasonAsync(i.WorkOrderId, i.IssueReason, vid);
            var cOld = await svc.CatalogAsync(oldP.CatalogId); var cNew = await svc.CatalogAsync(newP.CatalogId);
            if (cOld.Category != cNew.Category) throw new DomainException($"القطعة الجديدة ({cNew.Category}) ليست من فئة القديمة ({cOld.Category}).");
            if (i.Km is { } km && oldP.InstallKm is { } ik && km < ik) throw new DomainException($"قراءة العداد ({CustodyText.N(km)}) أقل من قراءته عند تركيب القطعة القديمة ({CustodyText.N(ik)}).");
            var L = await CustodyLookups.LoadAsync(db);
            var pos = oldP.Position;
            (oldP.Status, oldP.VehicleId, oldP.Position) = (PartStatus.Stock, null, null);
            var ro = Log(a, "replace-out", oldP, PartStatus.Installed, m =>
            {
                m.FromVehicleId = vid; m.Position = pos; m.Km = i.Km; m.Hours = i.Hours; m.Reason = i.Reason.Trim(); m.OldPartFate = fk; m.RelatedPartId = newP.Id; m.WorkOrderId = wo?.Id; m.Note = Clean(i.Note);
            });
            await ApplyFateAsync(a, oldP, i.OldPartFate, L, wo?.Id, vid);
            ro.StatusAfter = oldP.Status.ToString();
            await db.SaveChangesAsync();   // the position is free before the new part takes it

            var first = await FirstInstallAsync(newP.Id);
            var before = newP.Status;
            var (fw, hk, hid, hn) = (newP.WarehouseId, newP.HolderKind, newP.HolderId, newP.HolderRef);
            (newP.Status, newP.VehicleId, newP.Position, newP.InstalledAt, newP.InstallKm, newP.InstallHours, newP.WarehouseId, newP.Bin, newP.HolderKind, newP.HolderId, newP.HolderRef, newP.HolderSince) =
                (PartStatus.Installed, vid, pos, DateTimeOffset.UtcNow, i.Km, i.Hours, null, null, null, null, null, null);
            Log(a, "replace-in", newP, before, m =>
            {
                m.VehicleId = vid; m.Position = pos; m.Km = i.Km; m.Hours = i.Hours; m.Reason = i.Reason.Trim(); m.RelatedPartId = oldP.Id; m.WorkOrderId = wo?.Id; m.FromWarehouseId = fw;
                m.FromHolderKind = hk; m.FromHolderId = hid; m.FromHolderRef = hn; m.Cost = first ? newP.Cost : null; m.ApprovedBy = a.UserId; m.Note = wo is null ? i.IssueReason : null;
            });
            audit.Write(a, "part", oldP.Id, "استبدال قطعة", $"{L.Plate(vid)}{(pos is null ? "" : " · " + pos)}: {oldP.InternalNo} ← {newP.InternalNo} · {CustodyText.Fates[fk]} · {i.Reason.Trim()}");
            return Ok(new { oldPartId = oldP.Id, oldStatus = oldP.Status, newPartId = newP.Id, newStatus = newP.Status });
        });
    }

    // ── repair, damage, loss, scrap, warranty, return, inspection ─────────────────────────────────────────────────
    private static (string? Kind, Guid? Id, string? Name, Guid? Vehicle, Guid? Wh) Detach(Part p)
    {
        var r = (p.HolderKind, p.HolderId, p.HolderRef, p.VehicleId, p.WarehouseId);
        (p.HolderKind, p.HolderId, p.HolderRef, p.HolderSince, p.VehicleId, p.Position, p.WarehouseId, p.Bin) = (null, null, null, null, null, null, null, null);
        return r;
    }

    private void FillFrom(PartMovement m, (string? Kind, Guid? Id, string? Name, Guid? Vehicle, Guid? Wh) f) =>
        (m.FromHolderKind, m.FromHolderId, m.FromHolderRef, m.FromVehicleId, m.FromWarehouseId) = (f.Kind, f.Id, f.Name, f.Vehicle, f.Wh);

    [HttpPost("parts/{id:guid}/repair-send")]
    public async Task<IActionResult> RepairSend(Guid id, RepairSendInput i)
    {
        var a = await EditAsync();
        return await Tx(async () =>
        {
            var p = await svc.LockPartAsync(id);
            if (p.Status == PartStatus.Installed) throw new DomainException("القطعة مركّبة: استخدم «فك» واختر «إرسال للإصلاح».");
            Need(p, "إرسال للإصلاح", PartStatus.Stock, PartStatus.Issued, PartStatus.Damaged);
            await NoPendingAsync(p);
            if (i.WorkOrderId is { } w) await Refs.EnsureAsync<WorkOrder>(db, w, "أمر الصيانة");
            var before = p.Status;
            var from = Detach(p);
            (p.Status, p.RepairVendor, p.RepairSentAt, p.RepairExpectedAt, p.RepairCost, p.StatusReason) = (PartStatus.Repair, i.Vendor.Trim(), DateTimeOffset.UtcNow, i.ExpectedReturn, i.EstimatedCost, i.Reason.Trim());
            Log(a, "repair-send", p, before, m => { FillFrom(m, from); m.Vendor = p.RepairVendor; m.ExpectedReturn = i.ExpectedReturn; m.Reason = i.Reason.Trim(); m.WorkOrderId = i.WorkOrderId; m.DocRef = Clean(i.DocRef); m.Note = i.EstimatedCost is { } e ? $"تكلفة تقديرية {CustodyText.N(e)}" : null; });
            audit.Write(a, "part", p.Id, "إرسال للإصلاح", $"{p.InternalNo} → {p.RepairVendor}");
            return Ok(new { p.Id, p.Status });
        });
    }

    [HttpPost("parts/{id:guid}/repair-return")]
    public async Task<IActionResult> RepairReturn(Guid id, RepairReturnInput i)
    {
        var a = await EditAsync();
        return await Tx(async () =>
        {
            var p = await svc.LockPartAsync(id);
            Need(p, "تسجيل عودة من الإصلاح", PartStatus.Repair);
            var before = p.Status; var vendor = p.RepairVendor;
            var wo = await db.PartMovements.Where(m => m.PartId == p.Id && m.Type == "repair-send").OrderByDescending(m => m.At).Select(m => m.WorkOrderId).FirstOrDefaultAsync();
            p.RepairCost = i.Cost ?? p.RepairCost;
            if (i.Fixed)
            {
                var w = await svc.WarehouseAsync(i.WarehouseId);
                (p.Status, p.WarehouseId, p.Bin, p.Condition, p.StatusReason) = (PartStatus.Stock, w.Id, Clean(i.Bin), "مُصلحة", "عادت من الإصلاح");
            }
            else (p.Status, p.LastLocation, p.StatusReason) = (PartStatus.Damaged, $"لدى ورشة {vendor}", "تعذّر إصلاحها");
            Log(a, "repair-return", p, before, m => { m.Vendor = vendor; m.ToWarehouseId = p.WarehouseId; m.Cost = i.Cost; m.WorkOrderId = wo; m.DocRef = Clean(i.DocRef); m.Note = (i.Fixed ? "تم الإصلاح" : "تعذّر الإصلاح") + (Clean(i.Note) is { } n ? " · " + n : ""); });
            audit.Write(a, "part", p.Id, "عودة من الإصلاح", $"{p.InternalNo} · {(i.Fixed ? "مُصلحة" : "غير قابلة للإصلاح")}");
            return Ok(new { p.Id, p.Status });
        });
    }

    [HttpPost("parts/{id:guid}/damaged")] public Task<IActionResult> Damaged(Guid id, ReasonInput i) => MarkAsync(id, i, PartStatus.Damaged, "damaged", "تسجيل قطعة تالفة");
    [HttpPost("parts/{id:guid}/lost")] public Task<IActionResult> Lost(Guid id, ReasonInput i) => MarkAsync(id, i, PartStatus.Lost, "lost", "تسجيل قطعة مفقودة");

    private async Task<IActionResult> MarkAsync(Guid id, ReasonInput i, PartStatus to, string type, string action)
    {
        var a = await EditAsync();
        return await Tx(async () =>
        {
            var p = await svc.LockPartAsync(id);
            Need(p, action, PartStatus.Stock, PartStatus.Issued, PartStatus.Installed, PartStatus.Repair, to == PartStatus.Lost ? PartStatus.Damaged : PartStatus.Stock);
            await NoPendingAsync(p);
            var L = await CustodyLookups.LoadAsync(db);
            var before = p.Status;
            var where = Who(p, L);
            var from = Detach(p);
            (p.Status, p.LastLocation, p.StatusReason, p.ReviewOutcome) = (to, where, i.Reason.Trim(), null);
            Log(a, type, p, before, m => { FillFrom(m, from); m.Reason = i.Reason.Trim(); m.Note = Clean(i.Note) ?? $"آخر موقع/حامل: {where}"; });
            audit.Write(a, "part", p.Id, action, $"{p.InternalNo} · {where} · {i.Reason.Trim()}");
            if (from.Kind == "driver" && from.Id is { } did) audit.Notify(did, $"سُجّلت القطعة {p.InternalNo} من عهدتك «{CustodyText.Status[to]}»: {i.Reason.Trim()}", p.InternalNo);
            return Ok(new { p.Id, p.Status });
        });
    }

    /// <summary>Outcome of the review of a damaged or lost part (e.g. charged to the employee, normal wear, accident).</summary>
    [HttpPost("parts/{id:guid}/review")]
    public async Task<IActionResult> Review(Guid id, ReviewOutcomeInput i)
    {
        var a = await EditAsync();
        return await Tx(async () =>
        {
            var p = await svc.LockPartAsync(id);
            Need(p, "تسجيل نتيجة المراجعة", PartStatus.Damaged, PartStatus.Lost, PartStatus.Scrapped);
            var old = p.ReviewOutcome;
            p.ReviewOutcome = i.Outcome.Trim();
            Log(a, "review", p, p.Status, m => { m.Reason = p.ReviewOutcome; });
            audit.Write(a, "part", p.Id, "نتيجة مراجعة", p.ReviewOutcome, old, p.ReviewOutcome);
            return Ok(new { p.Id, p.ReviewOutcome });
        });
    }

    [HttpPost("parts/{id:guid}/scrap")]
    public async Task<IActionResult> Scrap(Guid id, ScrapInput i)
    {
        var a = await EditAsync();
        return await Tx(async () =>
        {
            var p = await svc.LockPartAsync(id);
            if (p.Status == PartStatus.Installed) throw new DomainException("القطعة مركّبة: استخدم «فك» واختر «إتلاف معتمد».");
            Need(p, "إتلاف", PartStatus.Damaged, PartStatus.Stock, PartStatus.Repair, PartStatus.ReturnedToSupplier);
            await NoPendingAsync(p);
            var L = await CustodyLookups.LoadAsync(db);
            var before = p.Status;
            var where = Who(p, L);
            var from = Detach(p);
            (p.Status, p.ScrapApprovedBy, p.StatusReason) = (PartStatus.Scrapped, a.UserId, i.Reason.Trim());
            p.LastLocation ??= where;
            Log(a, "scrap", p, before, m => { FillFrom(m, from); m.Reason = i.Reason.Trim(); m.DocRef = i.Approval.Trim(); m.ApprovedBy = a.UserId; });
            audit.Write(a, "part", p.Id, "إتلاف معتمد", $"{p.InternalNo} · {i.Reason.Trim()} · اعتماد: {i.Approval.Trim()}");
            return Ok(new { p.Id, p.Status });
        });
    }

    [HttpPost("parts/{id:guid}/claim")]
    public async Task<IActionResult> Claim(Guid id, ClaimInput i)
    {
        var a = await EditAsync();
        return await Tx(async () =>
        {
            var p = await svc.LockPartAsync(id);
            if (p.Status == PartStatus.Installed) throw new DomainException("القطعة مركّبة: استخدم «فك» واختر «مطالبة ضمان».");
            Need(p, "رفع مطالبة ضمان", PartStatus.Stock, PartStatus.Issued, PartStatus.Damaged, PartStatus.Repair);
            await NoPendingAsync(p);
            if (p.WarrantyEnd is { } we && we < Ksa.Day(DateTimeOffset.UtcNow)) throw new DomainException($"ضمان القطعة {p.InternalNo} انتهى في {we:yyyy-MM-dd}.");
            var before = p.Status;
            var from = Detach(p);
            (p.Status, p.ClaimRef, p.ClaimOpenedAt, p.ClaimResult, p.StatusReason) = (PartStatus.ReturnedToSupplier, i.ClaimRef.Trim(), DateTimeOffset.UtcNow, null, i.Reason.Trim());
            Log(a, "claim", p, before, m => { FillFrom(m, from); m.Vendor = p.Supplier; m.DocRef = p.ClaimRef; m.Reason = i.Reason.Trim(); });
            audit.Write(a, "part", p.Id, "مطالبة ضمان", $"{p.InternalNo} · {p.Supplier} · {p.ClaimRef}");
            return Ok(new { p.Id, p.Status });
        });
    }

    [HttpPost("parts/{id:guid}/claim-result")]
    public async Task<IActionResult> ClaimResult(Guid id, ClaimResultInput i)
    {
        var a = await EditAsync();
        return await Tx(async () =>
        {
            var p = await svc.LockPartAsync(id);
            Need(p, "تسجيل نتيجة المطالبة", PartStatus.ReturnedToSupplier);
            var before = p.Status;
            p.ClaimResult = (i.Accepted ? "مقبولة: " : "مرفوضة: ") + i.Result.Trim();
            if (i.Accepted)
            {
                var w = await svc.WarehouseAsync(i.WarehouseId);
                (p.Status, p.WarehouseId, p.Bin, p.Condition, p.StatusReason) = (PartStatus.Stock, w.Id, Clean(i.Bin), "مستبدلة/مُصلحة بالضمان", "مطالبة ضمان مقبولة");
            }
            else (p.Status, p.LastLocation, p.StatusReason) = (PartStatus.Damaged, $"أعادها المورد {p.Supplier}", "رُفضت مطالبة الضمان");
            Log(a, "claim-result", p, before, m => { m.Vendor = p.Supplier; m.DocRef = p.ClaimRef; m.Reason = p.ClaimResult; m.ToWarehouseId = p.WarehouseId; });
            audit.Write(a, "part", p.Id, "نتيجة مطالبة ضمان", $"{p.InternalNo} · {p.ClaimResult}");
            return Ok(new { p.Id, p.Status });
        });
    }

    /// <summary>The holder returns a part at the warehouse counter (the storekeeper is the receiver, so it completes at once).</summary>
    [HttpPost("parts/{id:guid}/return")]
    public async Task<IActionResult> Return(Guid id, ReturnInput i)
    {
        var a = await EditAsync();
        var w = await svc.WarehouseAsync(i.WarehouseId);
        return await Tx(async () =>
        {
            var p = await svc.LockPartAsync(id);
            Need(p, "إرجاع للمستودع", PartStatus.Issued);
            await NoPendingAsync(p);
            var before = p.Status;
            var from = Detach(p);
            (p.Status, p.WarehouseId, p.Bin) = (PartStatus.Stock, w.Id, Clean(i.Bin));
            if (Clean(i.Condition) is { } cond) p.Condition = cond;
            Log(a, "return", p, before, m => { FillFrom(m, from); m.ToWarehouseId = w.Id; m.Note = Clean(i.Note); m.ApprovedBy = a.UserId; });
            audit.Write(a, "part", p.Id, "إرجاع قطعة للمستودع", $"{p.InternalNo} من {from.Name} → {w.Name}");
            return Ok(new { p.Id, p.Status });
        });
    }

    [HttpPost("parts/{id:guid}/inspect")]
    public async Task<IActionResult> Inspect(Guid id, InspectInput i)
    {
        var a = await EditAsync();
        return await Tx(async () =>
        {
            var p = await svc.LockPartAsync(id);
            if (p.Status is PartStatus.Scrapped or PartStatus.Lost) throw new DomainException("لا يمكن فحص قطعة متلفة أو مفقودة.");
            var c = await svc.CatalogAsync(p.CatalogId);
            var today = Ksa.Day(DateTimeOffset.UtcNow);
            p.LastInspection = today;
            p.NextInspection = i.NextInspection ?? (c.InspectEveryDays is { } d ? today.AddDays(d) : p.NextInspection);
            if (!i.Passed) p.Condition = "لم تجتز الفحص";
            Log(a, "inspect", p, p.Status, m => { m.VehicleId = p.VehicleId; m.Reason = i.Passed ? "اجتازت الفحص" : "لم تجتز الفحص"; m.Note = Clean(i.Note); m.ExpectedReturn = p.NextInspection; });
            audit.Write(a, "part", p.Id, "فحص دوري", $"{p.InternalNo} · {(i.Passed ? "سليمة" : "لم تجتز")}");
            return Ok(new { p.Id, p.LastInspection, p.NextInspection });
        });
    }

    /// <summary>Correction of a wrongly recorded state: the part is put back in a warehouse. Nothing is deleted; a "correction" movement keeps the reason.</summary>
    [HttpPost("parts/{id:guid}/correct")]
    public async Task<IActionResult> Correct(Guid id, CorrectInput i)
    {
        var a = await EditAsync();
        var w = await svc.WarehouseAsync(i.WarehouseId);
        return await Tx(async () =>
        {
            var p = await svc.LockPartAsync(id);
            await NoPendingAsync(p);
            if (i.CorrectsMovementId is { } mid && !await db.PartMovements.AnyAsync(m => m.Id == mid && m.PartId == p.Id)) throw new NotFoundException("الحركة");
            var before = p.Status;
            var from = Detach(p);
            (p.Status, p.WarehouseId, p.StatusReason) = (PartStatus.Stock, w.Id, "تصحيح: " + i.Reason.Trim());
            Log(a, "correction", p, before, m => { FillFrom(m, from); m.ToWarehouseId = w.Id; m.Reason = i.Reason.Trim(); m.CorrectsId = i.CorrectsMovementId; m.ApprovedBy = a.UserId; });
            audit.Write(a, "part", p.Id, "تصحيح حالة قطعة", i.Reason.Trim(), before.ToString(), "Stock");
            return Ok(new { p.Id, p.Status });
        });
    }

    /// <summary>Custody clearance (transfer / end of service): everything the person holds returns to a warehouse in one step.</summary>
    [HttpPost("clearance")]
    public async Task<IActionResult> Clearance(ClearanceInput i)
    {
        var a = await EditAsync();
        var h = await svc.HolderAsync(i.HolderKind, i.HolderId);
        var w = await svc.WarehouseAsync(i.WarehouseId);
        return await Tx(async () =>
        {
            var pending = await db.CustodyTransfers.AnyAsync(t => t.Status == "Pending" && ((t.ToKind == h.Kind && t.ToId == h.Id) || (t.FromKind == h.Kind && t.FromId == h.Id)));
            if (pending) throw new DomainException($"لدى {h.Name} تسليمات معلّقة؛ أكملها أو ارفضها قبل إخلاء العهدة.");
            var ids = await db.Parts.Where(p => p.Status == PartStatus.Issued && p.HolderKind == h.Kind && p.HolderId == h.Id).Select(p => p.Id).ToListAsync();
            var returned = new List<string>();
            foreach (var pid in ids)
            {
                var p = await svc.LockPartAsync(pid);
                var from = Detach(p);
                (p.Status, p.WarehouseId) = (PartStatus.Stock, w.Id);
                Log(a, "return", p, PartStatus.Issued, m => { FillFrom(m, from); m.ToWarehouseId = w.Id; m.Reason = "إخلاء عهدة"; m.Note = Clean(i.Note); m.ApprovedBy = a.UserId; });
                returned.Add(p.InternalNo);
            }
            var bal = await db.HolderBalances.Where(x => x.HolderKind == h.Kind && x.HolderId == h.Id && x.Qty > 0).ToListAsync();
            var qty = new List<object>();
            foreach (var b in bal)
            {
                var c = await svc.CatalogAsync(b.CatalogId);
                await svc.TakeHolderAsync(h, c, b.Qty);
                await svc.AddStockAsync(c.Id, w.Id, b.Qty, null);
                LogQty(a, "stock-return", c.Id, b.Qty, m => { m.FromHolderKind = h.Kind; m.FromHolderId = h.Id; m.FromHolderRef = h.Name; m.ToWarehouseId = w.Id; m.Reason = "إخلاء عهدة"; m.Note = Clean(i.Note); });
                qty.Add(new { c.Code, c.Name, b.Qty });
            }
            audit.Write(a, h.Kind, h.Id, "إخلاء عهدة", $"{h.Name}: {returned.Count} قطعة و{qty.Count} صنف كمية → {w.Name}");
            return Ok(new { holder = h.Name, parts = returned, quantities = qty });
        });
    }

    // ── quantity stock ────────────────────────────────────────────────────────────────────────────────────────────
    [HttpGet("stock")]
    public async Task<IActionResult> Stock([FromQuery] Guid? warehouseId)
    {
        var a = await ViewAsync();
        var L = await CustodyLookups.LoadAsync(db);
        var q = db.StockBalances.AsQueryable();
        if (warehouseId is { } w) q = q.Where(x => x.WarehouseId == w);
        var rows = await q.ToListAsync();
        var holders = await db.HolderBalances.Where(x => x.Qty > 0).ToListAsync();
        var totals = rows.GroupBy(x => x.CatalogId).ToDictionary(g => g.Key, g => g.Sum(x => x.Qty));
        return Ok(new
        {
            balances = rows.OrderBy(x => L.Item(x.CatalogId)).Select(x =>
            {
                var c = L.Catalog.GetValueOrDefault(x.CatalogId);
                return new { x.CatalogId, code = c?.Code, name = c?.Name, unit = c?.Unit, category = c?.Category, x.WarehouseId, warehouse = L.Wh(x.WarehouseId), x.Qty, x.Bin, min = c?.MinQty,
                    total = totals[x.CatalogId], belowMin = c?.MinQty is { } m && totals[x.CatalogId] <= m, value = SeesCost(a) ? c?.UnitCost * x.Qty : null };
            }),
            custody = holders.OrderBy(x => x.HolderName).Select(x => new { x.HolderKind, x.HolderId, holder = x.HolderName, x.CatalogId, code = L.Code(x.CatalogId), name = L.Item(x.CatalogId), unit = L.Catalog.GetValueOrDefault(x.CatalogId)?.Unit, x.Qty, x.Since }),
        });
    }

    private async Task<PartCatalog> QtyCatalogAsync(Guid id)
    {
        var c = await svc.CatalogAsync(id);
        if (c.Tracking != PartTracking.Quantity) throw new DomainException($"«{c.Name}» صنف يُتابع فرديًا (لكل قطعة رقم)؛ استخدم «القطع».");
        return c;
    }

    [HttpPost("stock/receive")]
    public async Task<IActionResult> StockReceive(StockReceiveInput i)
    {
        var a = await EditAsync();
        var c = await QtyCatalogAsync(i.CatalogId);
        var w = await svc.WarehouseAsync(i.WarehouseId);
        return await Tx(async () =>
        {
            await svc.AddStockAsync(c.Id, w.Id, i.Qty, Clean(i.Bin));
            if (i.UnitCost is { } uc) c.UnitCost = uc;
            LogQty(a, "stock-receive", c.Id, i.Qty, m => { m.ToWarehouseId = w.Id; m.UnitCost = i.UnitCost ?? c.UnitCost; m.Cost = (i.UnitCost ?? c.UnitCost) * i.Qty; m.Vendor = Clean(i.Supplier); m.DocRef = Clean(i.InvoiceNo); m.BatchNo = Clean(i.BatchNo); m.Note = Clean(i.Note); });
            audit.Write(a, "stock", c.Id, "استلام كمية", $"{c.Name} × {CustodyText.N(i.Qty)} → {w.Name}");
            return Ok(new { c.Id, qty = await db.StockBalances.Where(x => x.CatalogId == c.Id && x.WarehouseId == w.Id).Select(x => x.Qty).FirstAsync() });
        });
    }

    /// <summary>Issue a quantity to a truck (consumed at once) or to a person (custody, completed when he accepts). Never below zero.</summary>
    [HttpPost("stock/issue")]
    public async Task<IActionResult> StockIssue(StockIssueInput i)
    {
        var a = await EditAsync();
        var c = await QtyCatalogAsync(i.CatalogId);
        var w = await svc.WarehouseAsync(i.WarehouseId);
        if (i.VehicleId is null && i.HolderId is null) throw new DomainException("حدد الشاحنة أو المستلم.");
        var v = i.VehicleId is { } vid ? await svc.VehicleAsync(vid) : null;
        var h = i.HolderId is not null ? await svc.HolderAsync(i.HolderKind, i.HolderId) : null;
        var wo = await svc.RequireWorkOrderOrReasonAsync(i.WorkOrderId, i.Reason, v?.Id);
        var L = await CustodyLookups.LoadAsync(db);
        return await Tx(async () =>
        {
            await svc.TakeStockAsync(c, w.Id, i.Qty);
            if (h is null)
            {
                LogQty(a, "stock-issue", c.Id, i.Qty, m => { m.FromWarehouseId = w.Id; m.VehicleId = v!.Id; m.WorkOrderId = wo?.Id; m.Reason = Clean(i.Reason); m.Km = i.Km; m.UnitCost = c.UnitCost; m.Cost = c.UnitCost * i.Qty; m.Note = Clean(i.Note); m.ApprovedBy = a.UserId; });
                audit.Write(a, "stock", c.Id, "صرف كمية لشاحنة", $"{c.Name} × {CustodyText.N(i.Qty)} → {v!.Plate}");
                return Ok(new { issued = true });
            }
            var t = new CustodyTransfer
            {
                No = await svc.NextNoAsync("TR", "TR-", 5), Kind = "qty-issue", CatalogId = c.Id, Qty = i.Qty, FromKind = "warehouse", FromId = w.Id, FromName = w.Name, ToKind = h.Kind, ToId = h.Id, ToName = h.Name,
                WorkOrderId = wo?.Id, Reason = Clean(i.Reason), HandoverBy = Clean(i.HandoverBy), HandoverNote = Clean(i.Note), RequestedAt = DateTimeOffset.UtcNow, RequestedBy = a.UserId,
            };
            db.CustodyTransfers.Add(t);
            LogQty(a, "handover", c.Id, i.Qty, m => { m.TransferId = t.Id; m.FromWarehouseId = w.Id; m.HolderKind = h.Kind; m.HolderId = h.Id; m.HolderRef = h.Name; m.WorkOrderId = wo?.Id; m.Reason = t.Reason; });
            audit.Write(a, "stock", c.Id, "صرف كمية (تسليم)", $"{c.Name} × {CustodyText.N(i.Qty)} → {h.Name} · {t.No}");
            if (h.Kind == "driver") audit.Notify(h.Id, $"لديك عهدة بانتظار تأكيد الاستلام: {c.Name} × {CustodyText.N(i.Qty)} ({t.No}).", t.No);
            await db.SaveChangesAsync();
            if (i.ReceiverSigned) await AcceptAsync(a, t, new DecideInput("استلم بتوقيعه عند أمين المستودع", null, null, null, null));
            return Ok(TransferRow(t, L));
        });
    }

    [HttpPost("stock/return")]
    public async Task<IActionResult> StockReturn(StockReturnInput i)
    {
        var a = await EditAsync();
        var c = await QtyCatalogAsync(i.CatalogId);
        var w = await svc.WarehouseAsync(i.WarehouseId);
        var h = await svc.HolderAsync(i.HolderKind, i.HolderId);
        return await Tx(async () =>
        {
            await svc.TakeHolderAsync(h, c, i.Qty);
            await svc.AddStockAsync(c.Id, w.Id, i.Qty, null);
            LogQty(a, "stock-return", c.Id, i.Qty, m => { m.FromHolderKind = h.Kind; m.FromHolderId = h.Id; m.FromHolderRef = h.Name; m.ToWarehouseId = w.Id; m.Note = Clean(i.Note); m.ApprovedBy = a.UserId; });
            audit.Write(a, "stock", c.Id, "إرجاع كمية", $"{c.Name} × {CustodyText.N(i.Qty)} من {h.Name}");
            return Ok(new { returned = true });
        });
    }

    /// <summary>The holder used custody material on a truck (e.g. topped up oil on the road).</summary>
    [HttpPost("stock/consume")]
    public async Task<IActionResult> Consume(ConsumeInput i)
    {
        var a = await EditAsync();
        var c = await QtyCatalogAsync(i.CatalogId);
        var h = await svc.HolderAsync(i.HolderKind, i.HolderId);
        var v = await svc.VehicleAsync(i.VehicleId);
        var wo = await svc.RequireWorkOrderOrReasonAsync(i.WorkOrderId, i.Reason, v.Id);
        return await Tx(async () =>
        {
            await svc.TakeHolderAsync(h, c, i.Qty);
            LogQty(a, "consume", c.Id, i.Qty, m => { m.FromHolderKind = h.Kind; m.FromHolderId = h.Id; m.FromHolderRef = h.Name; m.VehicleId = v.Id; m.WorkOrderId = wo?.Id; m.Reason = Clean(i.Reason); m.Km = i.Km; m.UnitCost = c.UnitCost; m.Cost = c.UnitCost * i.Qty; });
            audit.Write(a, "stock", c.Id, "استهلاك من العهدة", $"{c.Name} × {CustodyText.N(i.Qty)} · {h.Name} → {v.Plate}");
            return Ok(new { consumed = true });
        });
    }

    [HttpPost("stock/adjust")]
    public async Task<IActionResult> Adjust(StockAdjustInput i)
    {
        var a = await EditAsync();
        var c = await QtyCatalogAsync(i.CatalogId);
        var w = await svc.WarehouseAsync(i.WarehouseId);
        return await Tx(async () =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM stock_balances WHERE catalog_id = {c.Id} AND warehouse_id = {w.Id} FOR UPDATE");
            var cur = await db.StockBalances.Where(x => x.CatalogId == c.Id && x.WarehouseId == w.Id).Select(x => (decimal?)x.Qty).FirstOrDefaultAsync() ?? 0;
            var delta = i.NewQty - cur;
            if (delta == 0) throw new DomainException("الرصيد مطابق؛ لا توجد تسوية.");
            if (delta > 0) await svc.AddStockAsync(c.Id, w.Id, delta, null); else await svc.TakeStockAsync(c, w.Id, -delta);
            LogQty(a, "stock-adjust", c.Id, delta, m => { m.ToWarehouseId = w.Id; m.Reason = i.Reason.Trim(); m.Note = $"من {CustodyText.N(cur)} إلى {CustodyText.N(i.NewQty)}"; m.ApprovedBy = a.UserId; });
            audit.Write(a, "stock", c.Id, "تسوية مخزون", i.Reason.Trim(), CustodyText.N(cur), CustodyText.N(i.NewQty));
            return Ok(new { qty = i.NewQty });
        });
    }

    [HttpPost("stock/move")]
    public async Task<IActionResult> StockMove(StockMoveInput i)
    {
        var a = await EditAsync();
        var c = await QtyCatalogAsync(i.CatalogId);
        var f = await svc.WarehouseAsync(i.FromWarehouseId); var t = await svc.WarehouseAsync(i.ToWarehouseId);
        if (f.Id == t.Id) throw new DomainException("المستودعان متطابقان.");
        return await Tx(async () =>
        {
            await svc.TakeStockAsync(c, f.Id, i.Qty);
            await svc.AddStockAsync(c.Id, t.Id, i.Qty, null);
            LogQty(a, "stock-move", c.Id, i.Qty, m => { m.FromWarehouseId = f.Id; m.ToWarehouseId = t.Id; m.Note = Clean(i.Note); });
            audit.Write(a, "stock", c.Id, "نقل بين المستودعات", $"{c.Name} × {CustodyText.N(i.Qty)}: {f.Name} → {t.Name}");
            return Ok(new { moved = true });
        });
    }

    /// <summary>Quantity custody between people or back to a warehouse, with acceptance by the receiver.</summary>
    [HttpPost("holders/transfer")]
    public async Task<IActionResult> HolderTransfer(HolderTransferInput i)
    {
        var a = await EditAsync();
        var c = await QtyCatalogAsync(i.CatalogId);
        var from = await svc.HolderAsync(i.FromKind, i.FromId);
        string toName = i.ToKind == "warehouse" ? (await svc.WarehouseAsync(i.ToId)).Name : (await svc.HolderAsync(i.ToKind, i.ToId)).Name;
        if (i.ToKind == from.Kind && i.ToId == from.Id) throw new DomainException("المستلم هو حامل العهدة نفسه.");
        var L = await CustodyLookups.LoadAsync(db);
        return await Tx(async () =>
        {
            await svc.TakeHolderAsync(from, c, i.Qty);
            var t = new CustodyTransfer
            {
                No = await svc.NextNoAsync("TR", "TR-", 5), Kind = "qty-transfer", CatalogId = c.Id, Qty = i.Qty, FromKind = from.Kind, FromId = from.Id, FromName = from.Name, ToKind = i.ToKind, ToId = i.ToId, ToName = toName,
                HandoverBy = Clean(i.HandoverBy) ?? from.Name, HandoverNote = Clean(i.Note), RequestedAt = DateTimeOffset.UtcNow, RequestedBy = a.UserId,
            };
            db.CustodyTransfers.Add(t);
            LogQty(a, "handover", c.Id, i.Qty, m => { m.TransferId = t.Id; m.FromHolderKind = from.Kind; m.FromHolderId = from.Id; m.FromHolderRef = from.Name; m.Note = $"إلى {toName}"; });
            audit.Write(a, "stock", c.Id, "طلب نقل كمية", $"{c.Name} × {CustodyText.N(i.Qty)}: {from.Name} → {toName} · {t.No}");
            if (t.ToKind == "driver") audit.Notify(t.ToId, $"لديك عهدة بانتظار تأكيد الاستلام: {c.Name} × {CustodyText.N(i.Qty)} ({t.No}).", t.No);
            return Ok(TransferRow(t, L));
        });
    }

    // ── stock count ───────────────────────────────────────────────────────────────────────────────────────────────
    [HttpGet("counts")]
    public async Task<IActionResult> Counts()
    {
        await ViewAsync();
        var L = await CustodyLookups.LoadAsync(db);
        var list = await db.StockCounts.OrderByDescending(x => x.At).Take(200).ToListAsync();
        return Ok(list.Select(x => new { x.Id, x.No, x.WarehouseId, warehouse = L.Wh(x.WarehouseId), x.At, by = L.User(x.ByUserId), x.Note, x.Lines, x.MissingParts, x.UnexpectedParts,
            differences = x.Lines.Count(l => l.Actual != l.Recorded) + x.MissingParts.Count + x.UnexpectedParts.Count }));
    }

    /// <summary>Records a physical count: quantity lines are compared with the system and differences posted as "count-adjust";
    /// individual parts expected in the warehouse but not scanned are listed as missing (not marked lost automatically).</summary>
    [HttpPost("counts")]
    public async Task<IActionResult> Count(CountInput i)
    {
        var a = await EditAsync();
        var w = await svc.WarehouseAsync(i.WarehouseId);
        if (i.Lines.Count == 0 && (i.FoundParts?.Count ?? 0) == 0) throw new DomainException("أدخل الكميات المعدودة أو أرقام القطع الموجودة.");
        return await Tx(async () =>
        {
            var count = new StockCount { No = await svc.NextNoAsync("CNT", "CNT-", 4), WarehouseId = w.Id, At = DateTimeOffset.UtcNow, ByUserId = a.UserId, Note = Clean(i.Note) };
            foreach (var l in i.Lines.GroupBy(x => x.CatalogId).Select(g => g.Last()))
            {
                var c = await QtyCatalogAsync(l.CatalogId);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM stock_balances WHERE catalog_id = {c.Id} AND warehouse_id = {w.Id} FOR UPDATE");
                var bal = await db.StockBalances.Where(x => x.CatalogId == c.Id && x.WarehouseId == w.Id).Select(x => new { x.Qty, x.Bin }).FirstOrDefaultAsync();
                var rec = bal?.Qty ?? 0;
                count.Lines.Add(new CountLine(c.Id, c.Code, c.Name, Clean(l.Bin) ?? bal?.Bin, rec, l.Actual));
                var delta = l.Actual - rec;
                if (delta > 0) await svc.AddStockAsync(c.Id, w.Id, delta, Clean(l.Bin));
                else if (delta < 0) await svc.TakeStockAsync(c, w.Id, -delta);
                if (delta != 0) LogQty(a, "count-adjust", c.Id, delta, m => { m.ToWarehouseId = w.Id; m.Reason = $"جرد {count.No}"; m.Note = $"المسجل {CustodyText.N(rec)} · الفعلي {CustodyText.N(l.Actual)}"; m.ApprovedBy = a.UserId; });
            }
            if (i.FoundParts is { Count: > 0 } found)
            {
                var scanned = found.Select(NormSerial).Where(x => x != null).Select(x => x!).ToHashSet();
                var expected = await db.Parts.Where(p => p.Status == PartStatus.Stock && p.WarehouseId == w.Id).Select(p => new { p.InternalNo, p.Serial }).ToListAsync();
                count.MissingParts = expected.Where(p => !scanned.Contains(p.InternalNo) && (p.Serial is null || !scanned.Contains(p.Serial))).Select(p => p.InternalNo).ToList();
                var known = expected.SelectMany(p => new[] { p.InternalNo, p.Serial }).Where(x => x != null).ToHashSet();
                count.UnexpectedParts = scanned.Where(s => !known.Contains(s)).ToList();
            }
            db.StockCounts.Add(count);
            audit.Write(a, "stockcount", count.Id, "جرد مستودع", $"{count.No} · {w.Name} · فروقات: {count.Lines.Count(l => l.Actual != l.Recorded)} صنف، مفقود {count.MissingParts.Count}");
            return Ok(new { count.Id, count.No, count.Lines, count.MissingParts, count.UnexpectedParts });
        });
    }

    // ── work orders ───────────────────────────────────────────────────────────────────────────────────────────────
    private async Task<Actor> WoEditAsync()
    {
        var a = await actor.GetAsync();
        if (!a.Can(Section.Custody, PermissionLevel.Edit) && !(a.Can(Section.VehicleFile, PermissionLevel.Edit) && a.Can(Section.Custody))) throw new ForbiddenException();
        return a;
    }

    [HttpGet("workorders")]
    public async Task<IActionResult> WorkOrders([FromQuery] Guid? vehicleId, [FromQuery] bool? open)
    {
        var a = await actor.GetAsync();
        if (!a.Can(Section.Custody) && !a.Can(Section.VehicleFile)) throw new ForbiddenException();
        var L = await CustodyLookups.LoadAsync(db);
        var q = db.WorkOrders.AsQueryable();
        if (vehicleId is { } v) q = q.Where(x => x.VehicleId == v);
        if (open is { } o) q = q.Where(x => x.Closed != o);
        var list = await q.OrderByDescending(x => x.OpenedAt).Take(500).ToListAsync();
        var ids = list.Select(x => x.Id).ToList();
        var parts = await db.PartMovements.Where(m => m.WorkOrderId != null && ids.Contains(m.WorkOrderId.Value) && m.Cost != null && CostTypes.Contains(m.Type))
            .GroupBy(m => m.WorkOrderId!.Value).Select(g => new { g.Key, s = g.Sum(m => m.Cost!.Value), n = g.Count() }).ToDictionaryAsync(x => x.Key);
        var drivers = await db.Drivers.ToDictionaryAsync(x => x.Id, x => x.Name);
        var cost = SeesCost(a);
        return Ok(list.Select(w => new
        {
            w.Id, w.No, w.VehicleId, plate = L.Plate(w.VehicleId), w.Type, w.Description, w.Workshop, w.Km, w.Closed, w.OpenedAt, openedBy = L.User(w.OpenedBy), w.ClosedAt, closedBy = L.User(w.ClosedBy), w.CloseNote,
            driver = w.DriverId is { } d ? drivers.GetValueOrDefault(d) : null, operations = parts.GetValueOrDefault(w.Id)?.n ?? 0,
            laborCost = cost ? w.LaborCost : null, partsCost = cost ? parts.GetValueOrDefault(w.Id)?.s ?? 0 : (decimal?)null,
            total = cost ? (w.LaborCost ?? 0) + (parts.GetValueOrDefault(w.Id)?.s ?? 0) : (decimal?)null,
        }));
    }

    /// <summary>Movement types whose cost is a maintenance cost of a truck.</summary>
    public static readonly string[] CostTypes = ["install", "replace-in", "stock-issue", "consume", "repair-return"];

    [HttpGet("workorders/{id:guid}")]
    public async Task<IActionResult> WorkOrder(Guid id)
    {
        var a = await actor.GetAsync();
        if (!a.Can(Section.Custody) && !a.Can(Section.VehicleFile)) throw new ForbiddenException();
        var w = await db.WorkOrders.FirstOrDefaultAsync(x => x.Id == id) ?? throw new NotFoundException("أمر الصيانة");
        var L = await CustodyLookups.LoadAsync(db);
        var moves = await db.PartMovements.Where(m => m.WorkOrderId == id).OrderBy(m => m.At).ToListAsync();
        var nos = await PartNosAsync(moves.SelectMany(m => new[] { m.PartId, m.RelatedPartId }).Where(x => x != null).Select(x => x!.Value));
        var cost = SeesCost(a);
        var partsCost = moves.Where(m => CostTypes.Contains(m.Type)).Sum(m => m.Cost ?? 0);
        var files = await db.Attachments.Where(x => x.OwnerType == "workorder" && x.OwnerId == id).Select(x => new { x.Id, x.Kind, x.FileName, x.ContentType, x.UploadedAt }).ToListAsync();
        return Ok(new
        {
            w.Id, w.No, w.VehicleId, plate = L.Plate(w.VehicleId), w.Type, w.Description, w.Workshop, w.Km, w.Closed, w.OpenedAt, openedBy = L.User(w.OpenedBy), w.ClosedAt, closedBy = L.User(w.ClosedBy), w.CloseNote,
            laborCost = cost ? w.LaborCost : null, partsCost = cost ? partsCost : (decimal?)null, total = cost ? partsCost + (w.LaborCost ?? 0) : (decimal?)null,
            movements = moves.Select(m => MoveRow(m, L, cost, nos)), attachments = files,
        });
    }

    [HttpPost("workorders")]
    public async Task<IActionResult> OpenWorkOrder(WorkOrderInput i)
    {
        var a = await WoEditAsync();
        var v = await svc.VehicleAsync(i.VehicleId);
        return await Tx(async () =>
        {
            var w = new WorkOrder
            {
                No = await svc.NextNoAsync("WO", "WO-", 5), VehicleId = v.Id, Type = i.Type.Trim(), Description = i.Description.Trim(), Workshop = Clean(i.Workshop), Km = i.Km, LaborCost = i.LaborCost,
                OpenedAt = DateTimeOffset.UtcNow, OpenedBy = a.UserId, DriverId = await svc.CurrentDriverAsync(v.Id),
            };
            db.WorkOrders.Add(w);
            audit.Write(a, "workorder", w.Id, "فتح أمر صيانة", $"{w.No} · {v.Plate} · {w.Type}");
            return Ok(new { w.Id, w.No });
        });
    }

    [HttpPut("workorders/{id:guid}")]
    public async Task<IActionResult> EditWorkOrder(Guid id, WorkOrderUpdate i)
    {
        var a = await WoEditAsync();
        var w = await db.WorkOrders.FirstOrDefaultAsync(x => x.Id == id) ?? throw new NotFoundException("أمر الصيانة");
        if (w.Closed) throw new DomainException($"أمر الصيانة {w.No} مغلق.");
        var before = JsonSerializer.Serialize(w);
        (w.Description, w.Workshop, w.Km, w.LaborCost) = (i.Description.Trim(), Clean(i.Workshop), i.Km, i.LaborCost);
        audit.Write(a, "workorder", w.Id, "تعديل أمر صيانة", i.Reason.Trim(), before, JsonSerializer.Serialize(w));
        await db.SaveChangesAsync();
        return Ok(new { w.Id });
    }

    [HttpPost("workorders/{id:guid}/close")]
    public async Task<IActionResult> CloseWorkOrder(Guid id, WorkOrderClose i)
    {
        var a = await WoEditAsync();
        var w = await db.WorkOrders.FirstOrDefaultAsync(x => x.Id == id) ?? throw new NotFoundException("أمر الصيانة");
        if (w.Closed) throw new DomainException($"أمر الصيانة {w.No} مغلق مسبقًا.");
        if (await db.CustodyTransfers.AnyAsync(t => t.WorkOrderId == id && t.Status == "Pending")) throw new DomainException("على هذا الأمر تسليمات معلّقة بانتظار قبول المستلم.");
        (w.Closed, w.ClosedAt, w.ClosedBy, w.CloseNote) = (true, DateTimeOffset.UtcNow, a.UserId, Clean(i.Note));
        if (i.LaborCost is { } lc) w.LaborCost = lc;
        audit.Write(a, "workorder", w.Id, "إغلاق أمر صيانة", $"{w.No} · أجور {CustodyText.N(w.LaborCost)}");
        await db.SaveChangesAsync();
        return Ok(new { w.Id, w.Closed });
    }

    // ── search, truck file, my custody ────────────────────────────────────────────────────────────────────────────
    /// <summary>One box for plate, employee name, serial, internal number or a scanned barcode/QR (exact number opens the part).</summary>
    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery, StringLength(80)] string q)
    {
        var a = await actor.GetAsync();
        if (!a.Can(Section.Custody) && !a.Can(Section.VehicleFile)) throw new ForbiddenException();
        var t = (q ?? "").Trim();
        if (t.Length < 2) throw new DomainException("اكتب حرفين على الأقل.");
        var up = NormSerial(t)!;
        var vehicles = await db.Vehicles.Where(v => v.Plate.Contains(t) || (v.InternalNo != null && v.InternalNo.ToUpper() == up)).OrderBy(v => v.Plate).Take(20).Select(v => new { v.Id, v.Plate, v.InternalNo, v.Make, v.Model }).ToListAsync();
        if (!a.Can(Section.Custody)) return Ok(new { exactPartId = (Guid?)null, parts = Array.Empty<object>(), vehicles, holders = Array.Empty<object>(), catalog = Array.Empty<object>() });
        var L = await CustodyLookups.LoadAsync(db);
        var exact = await db.Parts.Where(p => p.InternalNo == up || p.Serial == up).Select(p => (Guid?)p.Id).FirstOrDefaultAsync();
        var parts = await db.Parts.Where(p => p.InternalNo.Contains(up) || (p.Serial != null && p.Serial.Contains(up)) || (p.HolderRef != null && p.HolderRef.Contains(t)) || db.Vehicles.Any(v => v.Id == p.VehicleId && v.Plate.Contains(t)))
            .OrderBy(p => p.InternalNo).Take(50).ToListAsync();
        var holders = (await db.Drivers.Where(d => d.Name.Contains(t) || (d.EmpNo != null && d.EmpNo == t)).Select(d => new { kind = "driver", d.Id, d.Name, role = "سائق" }).ToListAsync())
            .Concat(await db.Employees.Where(e => e.Name.Contains(t) || (e.EmpNo != null && e.EmpNo == t)).Select(e => new { kind = "employee", e.Id, e.Name, role = e.Role ?? "موظف" }).ToListAsync()).Take(20);
        var catalog = await db.PartCatalog.Where(c => c.Name.Contains(t) || c.Code.Contains(up) || (c.MfrPartNo != null && c.MfrPartNo.ToUpper() == up)).Take(20).Select(c => new { c.Id, c.Code, c.Name, c.Category, c.Tracking }).ToListAsync();
        return Ok(new { exactPartId = exact, parts = parts.Select(p => PartRow(p, L, SeesCost(a))), vehicles, holders, catalog });
    }

    /// <summary>Trucks with their current driver and number of installed parts (the custody staff may not have master-data access).</summary>
    [HttpGet("vehicles")]
    public async Task<IActionResult> Vehicles()
    {
        var a = await actor.GetAsync();
        if (!a.Can(Section.Custody) && !a.Can(Section.VehicleFile)) throw new ForbiddenException();
        var drivers = await db.VehicleAssignments.Where(x => x.To == null && x.DriverId != null)
            .Join(db.Drivers, x => x.DriverId, d => d.Id, (x, d) => new { x.VehicleId, d.Id, d.Name }).ToListAsync();
        var counts = await db.Parts.Where(p => p.Status == PartStatus.Installed).GroupBy(p => p.VehicleId!.Value).Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n);
        var openWo = await db.WorkOrders.Where(w => !w.Closed).GroupBy(w => w.VehicleId).Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n);
        var list = await db.Vehicles.OrderBy(v => v.Plate).ToListAsync();
        return Ok(list.Select(v => new
        {
            v.Id, v.Plate, v.InternalNo, v.Make, v.Model, v.Year, v.MeterId, driverId = drivers.FirstOrDefault(d => d.VehicleId == v.Id)?.Id, driver = drivers.FirstOrDefault(d => d.VehicleId == v.Id)?.Name,
            installed = counts.GetValueOrDefault(v.Id), openWorkOrders = openWo.GetValueOrDefault(v.Id),
        }));
    }

    /// <summary>Fuel meters, to link a meter part to its master record (serial, truck, calibration).</summary>
    [HttpGet("meters")]
    public async Task<IActionResult> Meters()
    {
        await ViewAsync();
        var plates = await db.Vehicles.ToDictionaryAsync(v => v.Id, v => v.Plate);
        return Ok((await db.Meters.OrderBy(m => m.Serial).ToListAsync()).Select(m => new { m.Id, m.Serial, m.Kind, plate = m.VehicleId is { } v ? plates.GetValueOrDefault(v) : null, m.LastCalibration, m.NextCalibration }));
    }

    /// <summary>Truck file: current parts, replacement history, maintenance cost, and custody held by its current driver.
    /// Installed parts belong to the truck; the driver's custody is listed separately (a driver change does not move parts).</summary>
    [HttpGet("vehicles/{id:guid}")]
    public async Task<IActionResult> TruckFile(Guid id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to)
    {
        var a = await actor.GetAsync();
        if (!a.Can(Section.Custody) && !a.Can(Section.VehicleFile, PermissionLevel.View)) throw new ForbiddenException();
        if (a.Role == Role.Driver && !a.Can(Section.Custody)) throw new ForbiddenException();
        var v = await svc.VehicleAsync(id);
        var L = await CustodyLookups.LoadAsync(db);
        var cost = SeesCost(a);
        var driverId = await svc.CurrentDriverAsync(id);
        var driver = driverId is { } did ? await db.Drivers.Where(d => d.Id == did).Select(d => new { d.Id, d.Name, d.EmpNo, d.Phone }).FirstOrDefaultAsync() : null;
        var installed = await db.Parts.Where(p => p.VehicleId == id && p.Status == PartStatus.Installed).ToListAsync();
        var lastKm = await svc.LastKmAsync(id);
        var moves = await db.PartMovements.Where(m => m.VehicleId == id || m.FromVehicleId == id || (m.WorkOrderId != null && db.WorkOrders.Any(w => w.Id == m.WorkOrderId && w.VehicleId == id)))
            .OrderByDescending(m => m.At).Take(1000).ToListAsync();
        var nos = await PartNosAsync(moves.SelectMany(m => new[] { m.PartId, m.RelatedPartId }).Where(x => x != null).Select(x => x!.Value));
        var wos = await db.WorkOrders.Where(w => w.VehicleId == id).OrderByDescending(w => w.OpenedAt).ToListAsync();
        var f = from is { } fd ? new DateTimeOffset(fd.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(3)) : DateTimeOffset.MinValue;
        var t = to is { } td ? new DateTimeOffset(td.ToDateTime(TimeOnly.MaxValue), TimeSpan.FromHours(3)) : DateTimeOffset.MaxValue;
        var costMoves = moves.Where(m => CostTypes.Contains(m.Type) && m.Cost != null && m.At >= f && m.At <= t).ToList();
        var labor = wos.Where(w => w.OpenedAt >= f && w.OpenedAt <= t).Sum(w => w.LaborCost ?? 0);
        object? custody = null;
        if (driverId is { } dd)
        {
            var held = await db.Parts.Where(p => p.Status == PartStatus.Issued && p.HolderKind == "driver" && p.HolderId == dd).ToListAsync();
            var qty = await db.HolderBalances.Where(x => x.HolderKind == "driver" && x.HolderId == dd && x.Qty > 0).ToListAsync();
            custody = new { parts = held.Select(p => PartRow(p, L, cost)), quantities = qty.Select(x => new { x.CatalogId, code = L.Code(x.CatalogId), name = L.Item(x.CatalogId), x.Qty, x.Since }) };
        }
        var meter = v.MeterId is { } mid ? await db.Meters.Where(m => m.Id == mid).Select(m => new { m.Id, m.Serial, m.Kind, m.LastCalibration, m.NextCalibration }).FirstOrDefaultAsync() : null;
        return Ok(new
        {
            vehicle = new { v.Id, v.Plate, v.InternalNo, v.Make, v.Model, v.Year, v.Vin, v.TrailerPlate }, driver, lastKm, meter,
            installed = installed.OrderBy(p => L.Catalog.GetValueOrDefault(p.CatalogId)?.Category).ThenBy(p => p.Position).Select(p => new
            {
                part = PartRow(p, L, cost), kmSinceInstall = lastKm is { } lk && p.InstallKm is { } ik ? lk - ik : (decimal?)null, daysInstalled = p.InstalledAt is { } ia ? (int)(DateTimeOffset.UtcNow - ia).TotalDays : (int?)null,
            }),
            replacements = moves.Where(m => m.Type is "replace-out" or "remove" or "install" or "replace-in").Select(m => MoveRow(m, L, cost, nos)),
            movements = moves.Select(m => MoveRow(m, L, cost, nos)),
            workOrders = wos.Select(w => new { w.Id, w.No, w.Type, w.Description, w.Closed, w.OpenedAt, w.ClosedAt, laborCost = cost ? w.LaborCost : null,
                partsCost = cost ? moves.Where(m => m.WorkOrderId == w.Id && CostTypes.Contains(m.Type)).Sum(m => m.Cost ?? 0) : (decimal?)null }),
            costs = cost ? new { parts = costMoves.Sum(m => m.Cost ?? 0), labor, total = costMoves.Sum(m => m.Cost ?? 0) + labor, from, to } : null,
            driverCustody = custody,
        });
    }

    /// <summary>What I hold now and what waits for my acceptance (drivers and employees with an account).</summary>
    [HttpGet("my")]
    public async Task<IActionResult> Mine()
    {
        var a = await actor.GetAsync();
        if (a.DriverId is not { } did) return Ok(new { parts = Array.Empty<object>(), quantities = Array.Empty<object>(), pending = Array.Empty<object>() });
        var L = await CustodyLookups.LoadAsync(db);
        var parts = await db.Parts.Where(p => p.Status == PartStatus.Issued && p.HolderKind == "driver" && p.HolderId == did).ToListAsync();
        var qty = await db.HolderBalances.Where(x => x.HolderKind == "driver" && x.HolderId == did && x.Qty > 0).ToListAsync();
        var pending = await db.CustodyTransfers.Where(t => t.Status == "Pending" && t.ToKind == "driver" && t.ToId == did).OrderBy(t => t.RequestedAt).ToListAsync();
        var nos = await PartNosAsync(pending.Where(t => t.PartId != null).Select(t => t.PartId!.Value));
        return Ok(new
        {
            parts = parts.Select(p => new { p.Id, p.InternalNo, p.Serial, name = L.Item(p.CatalogId), p.HolderSince, p.Condition }),
            quantities = qty.Select(x => new { x.CatalogId, name = L.Item(x.CatalogId), unit = L.Catalog.GetValueOrDefault(x.CatalogId)?.Unit, x.Qty, x.Since }),
            pending = pending.Select(t => TransferRow(t, L, nos)),
        });
    }
}
