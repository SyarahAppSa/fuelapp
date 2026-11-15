using System.ComponentModel.DataAnnotations;
using FuelUp.Api.Security;
using FuelUp.Domain;
using FuelUp.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace FuelUp.Api.Controllers;

[ApiController, Route("api/me"), Authorize]
public class MeController(FuelUpDb db, ActorAccessor actor) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var a = await actor.GetAsync();
        var u = await db.Users.FirstAsync(x => x.Id == a.UserId);
        var vehicles = a.DriverId is { } d ? await db.Vehicles.Where(v => db.VehicleAssignments.Any(x => x.VehicleId == v.Id && x.DriverId == d && x.To == null)).ToListAsync() : [];
        return Ok(new { u.Id, u.Username, u.DisplayName, role = u.Role, u.DriverId, permissions = a.Levels, vehicles });
    }
}

[ApiController, Route("api/master"), Authorize]
public class MasterDataController(FuelUpDb db, ActorAccessor actor, AuditWriter audit) : ControllerBase
{
    private async Task<Actor> Need(PermissionLevel l) { var a = await actor.GetAsync(); if (!a.Can(Section.Master, l) && !(l == PermissionLevel.View && (a.Can(Section.Ops) || a.Can(Section.Driver)))) throw new ForbiddenException(); return a; }

    [HttpGet("stations")] public async Task<IActionResult> Stations() { await Need(PermissionLevel.View); return Ok(await db.Stations.OrderBy(x => x.Name).ToListAsync()); }
    [HttpGet("vehicles")] public async Task<IActionResult> Vehicles() { await Need(PermissionLevel.View); return Ok(await db.Vehicles.OrderBy(x => x.Plate).ToListAsync()); }
    [HttpGet("drivers")] public async Task<IActionResult> Drivers() { await Need(PermissionLevel.View); return Ok(await db.Drivers.OrderBy(x => x.Name).ToListAsync()); }
    [HttpGet("customers")] public async Task<IActionResult> Customers() { await Need(PermissionLevel.View); return Ok(await db.Customers.OrderBy(x => x.Name).ToListAsync()); }
    [HttpGet("meters")] public async Task<IActionResult> Meters() { await Need(PermissionLevel.View); return Ok(await db.Meters.OrderBy(x => x.Serial).ToListAsync()); }

    // Inputs are explicit DTOs: a client can never set Id, CompanyId, history or other server-owned fields (no mass assignment).
    [HttpPost("stations")] public Task<IActionResult> Add(StationInput i) => Save(new Station { Name = i.Name.Trim(), Type = i.Type, City = i.City, Lat = i.Lat, Lng = i.Lng }, "موقع تعبئة");
    [HttpPost("drivers")] public Task<IActionResult> Add(DriverInput i) => Save(new Driver { Name = i.Name.Trim(), EmpNo = i.EmpNo, Phone = i.Phone, Type = i.Type }, "سائق");
    [HttpPost("customers")] public Task<IActionResult> Add(CustomerInput i) => Save(new Customer { Name = i.Name.Trim(), Project = i.Project, Site = i.Site, Recipient = i.Recipient, RecipientPhone = i.RecipientPhone, Lat = i.Lat, Lng = i.Lng }, "وجهة");
    [HttpPost("meters")] public Task<IActionResult> Add(MeterInput i) => Save(new Meter { Serial = i.Serial.Trim(), Kind = i.Kind, LastCalibration = i.LastCalibration, NextCalibration = i.NextCalibration }, "عداد");

    [HttpPost("vehicles")]
    public async Task<IActionResult> Add(VehicleInput i)
    {
        await Refs.EnsureAsync<Meter>(db, i.MeterId, "العداد");
        var v = new Vehicle();
        Apply(v, i);
        return await Save(v, "مركبة");
    }

    [HttpPut("vehicles/{id:guid}")]
    public async Task<IActionResult> UpdateVehicle(Guid id, VehicleInput input, [FromQuery, StringLength(500)] string reason)
    {
        var a = await Need(PermissionLevel.Edit);
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("سبب التعديل مطلوب.");
        var v = await db.Vehicles.FindAsync(id) ?? throw new NotFoundException("المركبة");
        await Refs.EnsureAsync<Meter>(db, input.MeterId, "العداد");
        var before = System.Text.Json.JsonSerializer.Serialize(v);
        if (v.Plate != input.Plate.Trim()) v.PlateHistory.Add(v.Plate);
        Apply(v, input);
        audit.Write(a, "vehicle", id, "تعديل بيانات المركبة", reason, before, System.Text.Json.JsonSerializer.Serialize(v));
        await db.SaveChangesAsync();
        return Ok(v);
    }

    private static void Apply(Vehicle v, VehicleInput i) =>
        (v.Plate, v.InternalNo, v.Vin, v.Make, v.Model, v.Year, v.AramcoNo, v.AfaqyUnit, v.Capacity, v.Compartments, v.MeterId) =
        (i.Plate.Trim(), i.InternalNo, i.Vin, i.Make, i.Model, i.Year, i.AramcoNo, i.AfaqyUnit, i.Capacity, i.Compartments ?? [], i.MeterId);

    private async Task<IActionResult> Save<T>(T x, string label) where T : Entity
    {
        var a = await Need(PermissionLevel.Edit);
        db.Add(x);
        audit.Write(a, typeof(T).Name.ToLowerInvariant(), x.Id, "إضافة " + label);
        await db.SaveChangesAsync();
        return Ok(x);
    }
}

public record StationInput([Required, StringLength(100)] string Name, StationType Type, [StringLength(60)] string? City, [Range(-90, 90)] double? Lat, [Range(-180, 180)] double? Lng);
public record DriverInput([Required, StringLength(100)] string Name, [StringLength(30)] string? EmpNo, [StringLength(20), RegularExpression(@"^\+?[0-9 ]{7,20}$")] string? Phone, DriverType Type);
public record CustomerInput([Required, StringLength(150)] string Name, [StringLength(150)] string? Project, [Required, StringLength(200)] string Site, [StringLength(100)] string? Recipient,
    [StringLength(20), RegularExpression(@"^\+?[0-9 ]{7,20}$")] string? RecipientPhone, [Range(-90, 90)] double? Lat, [Range(-180, 180)] double? Lng);
public record MeterInput([Required, StringLength(50)] string Serial, [StringLength(80)] string? Kind, DateOnly? LastCalibration, DateOnly? NextCalibration);
public record VehicleInput([Required, StringLength(20)] string Plate, [StringLength(20)] string? InternalNo, [StringLength(30)] string? Vin, [StringLength(40)] string? Make,
    [StringLength(40)] string? Model, [Range(1980, 2100)] int? Year, [StringLength(30)] string? AramcoNo, [StringLength(40)] string? AfaqyUnit,
    [Range(typeof(decimal), "0", "100000")] decimal Capacity, [MaxLength(10)] List<Compartment>? Compartments, Guid? MeterId);

public record AssignRequest(Guid? DriverId, [StringLength(500)] string? Note);

[ApiController, Route("api/assignments"), Authorize]
public class AssignmentsController(FuelUpDb db, ActorAccessor actor, AssignmentService svc) : ControllerBase
{
    [HttpGet("vehicles")]
    public async Task<IActionResult> Vehicles() { (await actor.GetAsync()).Require(Section.Assign, PermissionLevel.View); return Ok(await db.VehicleAssignments.OrderByDescending(x => x.From).ToListAsync()); }

    [HttpGet("customers")]
    public async Task<IActionResult> Customers() { (await actor.GetAsync()).Require(Section.Assign, PermissionLevel.View); return Ok(await db.CustomerAssignments.OrderByDescending(x => x.From).ToListAsync()); }

    [HttpPost("vehicles/{vehicleId:guid}")]
    public async Task<IActionResult> AssignVehicle(Guid vehicleId, AssignRequest r) { await svc.AssignVehicleAsync(await actor.GetAsync(), vehicleId, r.DriverId, r.Note); await db.SaveChangesAsync(); return NoContent(); }

    [HttpPost("customers/{customerId:guid}")]
    public async Task<IActionResult> AssignCustomer(Guid customerId, AssignRequest r) { await svc.AssignCustomerAsync(await actor.GetAsync(), customerId, r.DriverId); await db.SaveChangesAsync(); return NoContent(); }

    /// <summary>Who was responsible for each vehicle at a given moment.</summary>
    [HttpGet("who-at")]
    public async Task<IActionResult> WhoAt([FromQuery] DateTimeOffset at) { (await actor.GetAsync()).Require(Section.Assign, PermissionLevel.View); return Ok(await svc.WhoAtAsync(at)); }
}

public record BaselineRequest([Range(typeof(decimal), "0", "999999999")] decimal Reading);
public record ResetRequest([Required, StringLength(40)] string Type, [Required, StringLength(500)] string Reason, [Range(typeof(decimal), "0", "999999999")] decimal NewBaseline, [StringLength(50)] string? NewSerial);

[ApiController, Route("api/meters"), Authorize]
public class MetersController(FuelUpDb db, ActorAccessor actor, MeterLedger ledger) : ControllerBase
{
    [HttpGet("{id:guid}/ledger")]
    public async Task<IActionResult> Ledger(Guid id)
    {
        var a = await actor.GetAsync();
        if (!a.Can(Section.Meters) && !a.Can(Section.Driver)) throw new ForbiddenException();
        return Ok(new
        {
            last = await ledger.LastAsync(id),
            baselinePending = await ledger.BaselinePendingAsync(id),
            readings = await db.MeterReadings.Where(r => r.MeterId == id).OrderByDescending(r => r.Seq).Take(300).ToListAsync(),
            resets = await db.MeterResets.Where(r => r.MeterId == id).OrderByDescending(r => r.At).ToListAsync(),
        });
    }

    [HttpPost("{id:guid}/baseline")]
    public async Task<IActionResult> Baseline(Guid id, BaselineRequest r) { await ledger.SetBaselineAsync(await actor.GetAsync(), id, r.Reading); await db.SaveChangesAsync(); return NoContent(); }

    [HttpPost("{id:guid}/approve-baseline")]
    public async Task<IActionResult> Approve(Guid id) { await ledger.ApproveBaselineAsync(await actor.GetAsync(), id); await db.SaveChangesAsync(); return NoContent(); }

    [HttpPost("{id:guid}/reset")]
    public async Task<IActionResult> Reset(Guid id, ResetRequest r)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await ledger.ResetAsync(await actor.GetAsync(), id, r.Type, r.Reason, r.NewBaseline, r.NewSerial);
        await db.SaveChangesAsync(); await tx.CommitAsync();
        return NoContent();
    }
}

[ApiController, Route("api/attachments"), Authorize]
public class AttachmentsController(FuelUpDb db, ActorAccessor actor, FileStore store, SecurityEvents events) : ControllerBase
{
    public const long MaxBytes = 10 * 1024 * 1024;
    private static readonly HashSet<string> Kinds = ["load-doc", "meter-start", "meter-end", "seals", "unload-meter-before", "unload-meter-after", "receipt", "baseline", "other"];

    /// <summary>
    /// Upload one photo or PDF for an operation. The server checks: the caller may act on that operation, size ≤ 10 MB,
    /// real file type from its first bytes (JPEG/PNG/WebP/HEIC/PDF only — no SVG, HTML or scripts), and stores it under a random name.
    /// The SHA-256 fingerprint flags a picture already used on another operation.
    /// </summary>
    [HttpPost, RequestSizeLimit(MaxBytes + 64 * 1024), RequestFormLimits(MultipartBodyLengthLimit = MaxBytes + 64 * 1024), EnableRateLimiting("upload")]
    public async Task<IActionResult> Upload([FromForm] IFormFile file, [FromForm, Required, RegularExpression("^trip$")] string ownerType, [FromForm] Guid ownerId,
        [FromForm, Required, StringLength(40)] string kind, [FromForm] DateTimeOffset? capturedAt, [FromForm, Range(-90, 90)] double? lat, [FromForm, Range(-180, 180)] double? lng)
    {
        var a = await actor.GetAsync();
        await CanActOnAsync(a, ownerType, ownerId, write: true);
        if (!Kinds.Contains(kind)) throw new DomainException("نوع المرفق غير معروف.");
        if (file.Length == 0 || file.Length > MaxBytes) throw new DomainException("حجم الملف يجب ألا يتجاوز 10 ميجابايت.");
        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, HttpContext.RequestAborted);
        var bytes = ms.ToArray();
        if (FileSignatures.Detect(bytes) is not var (contentType, ext))
        {
            await events.RecordAsync("upload.rejected", Severity.Medium, "رُفض ملف مرفوع لأن محتواه ليس صورة أو PDF.", a.CompanyId, a.UserId);
            throw new DomainException("نوع الملف غير مدعوم. المسموح: صور JPEG/PNG/WebP/HEIC أو PDF.");
        }
        var (path, sha) = await store.SaveAsync(bytes, ext, HttpContext.RequestAborted);
        var duplicateOf = await db.Attachments.Where(x => x.Sha256 == sha && x.OwnerId != ownerId).Select(x => (Guid?)x.OwnerId).FirstOrDefaultAsync();
        var safeName = new string(Path.GetFileName(file.FileName ?? "").Where(ch => !char.IsControl(ch) && ch is not ('<' or '>' or '"' or '\\' or '/')).Take(100).ToArray());
        var att = new Attachment
        {
            OwnerType = ownerType, OwnerId = ownerId, Kind = kind, FileName = safeName, ContentType = contentType, Size = bytes.Length, Sha256 = sha, StoragePath = path,
            CapturedAt = capturedAt, UploadedAt = DateTimeOffset.UtcNow, FromGallery = capturedAt is not { } c || DateTimeOffset.UtcNow - c > TimeSpan.FromMinutes(10),
            Lat = lat, Lng = lng, ByUserId = a.UserId,
        };
        db.Attachments.Add(att);
        await db.SaveChangesAsync();
        return Ok(new { att.Id, att.Sha256, att.FromGallery, duplicateOf });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Download(Guid id)
    {
        var a = await actor.GetAsync();
        var att = await db.Attachments.FirstOrDefaultAsync(x => x.Id == id) ?? throw new NotFoundException("الملف");
        await CanActOnAsync(a, att.OwnerType, att.OwnerId, write: false);
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";   // a PDF can never run scripts in our origin
        var inline = att.ContentType.StartsWith("image/");
        return PhysicalFile(store.FullPath(att.StoragePath), att.ContentType, inline ? null : $"{att.Id:N}{Path.GetExtension(att.StoragePath)}");
    }

    [HttpGet("by-owner/{ownerId:guid}")]
    public async Task<IActionResult> List(Guid ownerId, [FromQuery] string ownerType = "trip")
    {
        var a = await actor.GetAsync();
        await CanActOnAsync(a, ownerType, ownerId, write: false);
        return Ok(await db.Attachments.Where(x => x.OwnerType == ownerType && x.OwnerId == ownerId).OrderBy(x => x.UploadedAt)
            .Select(x => new { x.Id, x.Kind, x.FileName, x.ContentType, x.Size, x.Sha256, x.CapturedAt, x.UploadedAt, x.FromGallery, x.Lat, x.Lng, x.ByUserId }).ToListAsync());
    }

    /// <summary>Object-level check: operations staff see any trip of their company; a driver only his own trips.</summary>
    private async Task CanActOnAsync(Actor a, string ownerType, Guid ownerId, bool write)
    {
        if (ownerType != "trip") throw new NotFoundException("العملية");
        var t = await db.Trips.Where(x => x.Id == ownerId).Select(x => new { x.DriverId }).FirstOrDefaultAsync() ?? throw new NotFoundException("العملية");
        var staff = a.Can(Section.Ops, write ? PermissionLevel.Edit : PermissionLevel.View) || (!write && (a.Can(Section.Moves) || a.Can(Section.Approve)));
        if (!staff && !(a.Can(Section.Driver, PermissionLevel.Edit) && a.DriverId == t.DriverId)) throw new ForbiddenException();
    }
}

[ApiController, Route("api/notifications"), Authorize]
public class NotificationsController(FuelUpDb db, ActorAccessor actor) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Mine()
    {
        var a = await actor.GetAsync();
        return Ok(await db.Notifications.Where(n => n.UserId == a.UserId || (a.DriverId != null && n.DriverId == a.DriverId)).OrderByDescending(n => n.At).Take(100).ToListAsync());
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> ReadAll()
    {
        var a = await actor.GetAsync();
        await db.Notifications.Where(n => (n.UserId == a.UserId || (a.DriverId != null && n.DriverId == a.DriverId)) && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTimeOffset.UtcNow));
        return NoContent();
    }
}
