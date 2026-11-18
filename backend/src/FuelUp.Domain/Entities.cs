namespace FuelUp.Domain;

/// <summary>Every business row belongs to one company. The API stamps it from the signed-in user; clients never send it.</summary>
public interface ITenantOwned { Guid CompanyId { get; set; } }

public abstract class Entity : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
}

/// <summary>A customer company of the platform. Data of one company is invisible to others (EF filters + PostgreSQL row-level security).</summary>
public class Company
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// ---------- accounts ----------
public class User : Entity
{
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public Role Role { get; set; }
    public Guid? DriverId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<UserPermission> Permissions { get; set; } = new();

    // sign-in protection
    public int FailedLogins { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public string? LastLoginIp { get; set; }
    public DateTimeOffset PasswordChangedAt { get; set; } = DateTimeOffset.UtcNow;

    // two-step verification (TOTP, RFC 6238). Secrets are encrypted with ASP.NET Data Protection before storage.
    public bool MfaEnabled { get; set; }
    public string? MfaSecretProtected { get; set; }
    public string? MfaPendingProtected { get; set; }
    public long MfaLastStep { get; set; }
    public List<string> RecoveryCodeHashes { get; set; } = new();
}

/// <summary>One signed-in device. The access token carries its id (sid) and is refused as soon as the session is revoked.</summary>
public class UserSession : Entity
{
    public Guid UserId { get; set; }
    public string Client { get; set; } = "";
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }
    public bool MfaVerified { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }
}

public class UserPermission : ITenantOwned
{
    public Guid CompanyId { get; set; }
    public Guid UserId { get; set; }
    public Section Section { get; set; }
    public PermissionLevel Level { get; set; }
}

/// <summary>Single-use refresh token (only its SHA-256 is stored). Presenting a used token again means it was stolen: the whole session is revoked.</summary>
public class RefreshToken : Entity
{
    public Guid UserId { get; set; }
    public Guid SessionId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

// ---------- master data ----------
public class Station : Entity
{
    public string Name { get; set; } = "";
    public StationType Type { get; set; }
    public string? City { get; set; }
    public double? Lat { get; set; }
    public double? Lng { get; set; }
}

public record Compartment(int No, decimal Capacity);

public class Vehicle : Entity
{
    public string Plate { get; set; } = "";
    public string? InternalNo { get; set; }
    public string? Vin { get; set; }
    public string? Make { get; set; }
    public string? Model { get; set; }
    public int? Year { get; set; }
    public string? AramcoNo { get; set; }
    public string? AfaqyUnit { get; set; }
    public decimal Capacity { get; set; }
    public List<Compartment> Compartments { get; set; } = new();
    public Guid? MeterId { get; set; }
    public string? TrailerPlate { get; set; }
    public List<string> PlateHistory { get; set; } = new();
}

public class Driver : Entity
{
    public string Name { get; set; } = "";
    public string? EmpNo { get; set; }
    public string? Phone { get; set; }
    public string? NationalId { get; set; }     // shown only to users allowed to see driver details
    public DriverType Type { get; set; }
}

public class Customer : Entity
{
    public string Name { get; set; } = "";
    public string? Project { get; set; }
    public string Site { get; set; } = "";
    public string? Recipient { get; set; }
    public string? RecipientPhone { get; set; }
    public double? Lat { get; set; }
    public double? Lng { get; set; }
}

public class Meter : Entity
{
    public string Serial { get; set; } = "";
    public string? Kind { get; set; }
    public Guid? VehicleId { get; set; }
    public DateOnly? LastCalibration { get; set; }
    public DateOnly? NextCalibration { get; set; }
}

public class Setting : ITenantOwned
{
    public Guid CompanyId { get; set; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

// ---------- assignment ----------
public class VehicleAssignment : Entity
{
    public Guid VehicleId { get; set; }
    public Guid? DriverId { get; set; }
    public DateTimeOffset From { get; set; }
    public DateTimeOffset? To { get; set; }
    public Guid ByUserId { get; set; }
    public string? Note { get; set; }
}

public class CustomerAssignment : Entity
{
    public Guid CustomerId { get; set; }
    public Guid? DriverId { get; set; }
    public DateTimeOffset From { get; set; }
    public DateTimeOffset? To { get; set; }
    public Guid ByUserId { get; set; }
}

// ---------- trips ----------
/// <summary>Names as they were at execution time, so old operations keep them after master data changes.</summary>
public record TripSnapshot(string DriverName, string? DriverEmpNo, string Plate, string CustomerName, string? Project, string Site,
    string StationName, StationType StationType, double? StationLat, double? StationLng, string? MeterSerial, DateTimeOffset TakenAt);

public class Trip : Entity
{
    public string No { get; set; } = "";
    public TripStatus Status { get; set; } = TripStatus.WaitingLoad;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid CreatedBy { get; set; }
    public Guid StationId { get; set; }
    public string SourceRef { get; set; } = "";
    public Guid CustomerId { get; set; }
    public Guid DriverId { get; set; }
    public Guid? VehicleId { get; set; }
    public Guid? MeterId { get; set; }
    public string Product { get; set; } = "ديزل";
    public decimal QtyRequested { get; set; }
    public DateTimeOffset? Eta { get; set; }
    public decimal? Rate { get; set; }
    public bool DriverOverride { get; set; }
    public bool SelfInitiated { get; set; }
    /// <summary>The customer order (طلب العميل) this trip executes. Null for trips created before orders existed.</summary>
    public Guid? OrderId { get; set; }
    public string? ReviewReason { get; set; }
    public string? Resolution { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? ApprovalNote { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset? DepartedAt { get; set; }
    public TripSnapshot? Snapshot { get; set; }
    public TripLoading? Loading { get; set; }
    public List<TripDelivery> Deliveries { get; set; } = new();
    public List<TripHandover> Handovers { get; set; } = new();
    public List<TripCorrection> Corrections { get; set; } = new();
}

public class TripLoading : ITenantOwned
{
    public Guid CompanyId { get; set; }
    public Guid TripId { get; set; }
    public decimal Qty { get; set; }
    public string DocNo { get; set; } = "";
    public decimal? Odometer { get; set; }
    public decimal? MeterStart { get; set; }
    public decimal? MeterEnd { get; set; }
    public string? OutletSeals { get; set; }
    public string? Location { get; set; }
    public double? Lat { get; set; }
    public double? Lng { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid ByUserId { get; set; }
}

public class TripDelivery : Entity
{
    public Guid TripId { get; set; }
    public int Seq { get; set; }
    public string Site { get; set; } = "";
    public DateTimeOffset ArrivedAt { get; set; }
    public string? ArrivalLocation { get; set; }
    public double? Lat { get; set; }
    public double? Lng { get; set; }
    public bool SiteMismatch { get; set; }
    public bool SealsBroken { get; set; }
    public string? SealNotes { get; set; }
    public DateTimeOffset? UnloadStartAt { get; set; }
    public decimal? MeterBefore { get; set; }
    public decimal? MeterAfter { get; set; }
    public string? TicketNo { get; set; }
    public decimal? Received { get; set; }
    public decimal LeftInTank { get; set; }
    public decimal Returned { get; set; }
    public string? ReceiptNo { get; set; }
    public bool HasMoreStops { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public decimal? MeterQty => MeterBefore is { } b && MeterAfter is { } a ? a - b : null;
}

public class TripHandover : Entity
{
    public Guid TripId { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid FromDriverId { get; set; }
    public Guid ToDriverId { get; set; }
    public Guid? VehicleFrom { get; set; }
    public Guid? VehicleTo { get; set; }
    public decimal? Remaining { get; set; }
    public decimal? MeterReading { get; set; }
    public string Note { get; set; } = "";
    public AckState Ack { get; set; } = AckState.Pending;
    public DateTimeOffset? AckAt { get; set; }
    public string? AckNote { get; set; }
    public Guid ByUserId { get; set; }
}

public class TripCorrection : Entity
{
    public Guid TripId { get; set; }
    public string Field { get; set; } = "";
    public string? OldValue { get; set; }
    public string NewValue { get; set; } = "";
    public string Reason { get; set; } = "";
    public Guid RequestedBy { get; set; }
    public DateTimeOffset At { get; set; }
    public ApprovalState State { get; set; }
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

// ---------- meter ledger ----------
public class MeterReading : Entity
{
    public Guid MeterId { get; set; }
    public long Seq { get; set; }
    public decimal Reading { get; set; }
    public string Kind { get; set; } = "";
    public Guid? TripId { get; set; }
    public DateTimeOffset At { get; set; }
    public DateTimeOffset? CapturedAt { get; set; }
    public Guid ByUserId { get; set; }
}

public class MeterBaseline : Entity
{
    public Guid MeterId { get; set; }
    public decimal Reading { get; set; }
    public ApprovalState State { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid ByUserId { get; set; }
    public Guid? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
}

public class MeterReset : Entity
{
    public Guid MeterId { get; set; }
    public string Type { get; set; } = "";
    public string Reason { get; set; } = "";
    public decimal? OldLast { get; set; }
    public decimal NewBaseline { get; set; }
    public string? NewSerial { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid ByUserId { get; set; }
}

// ---------- shared ----------
public class Attachment : Entity
{
    public string OwnerType { get; set; } = "";
    public Guid OwnerId { get; set; }
    public string Kind { get; set; } = "";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public DateTimeOffset? CapturedAt { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public bool FromGallery { get; set; }
    public double? Lat { get; set; }
    public double? Lng { get; set; }
    public string StoragePath { get; set; } = "";
    public Guid ByUserId { get; set; }
    /// <summary>Id generated by the phone for this photo: a retried upload after a network loss is recognised, never stored twice.</summary>
    public Guid? ClientId { get; set; }
    /// <summary>Why no location was recorded (permission refused, GPS off, timeout…).</summary>
    public string? LocationNote { get; set; }
}

public class AuditEntry
{
    public long Id { get; set; }
    /// <summary>Null only for security events that cannot be tied to a company (e.g. sign-in with an unknown username).</summary>
    public Guid? CompanyId { get; set; }
    public string? Ip { get; set; }
    public string? Severity { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid? UserId { get; set; }
    public string Entity { get; set; } = "";
    public Guid EntityId { get; set; }
    public string Action { get; set; } = "";
    public string? Detail { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}

public class Notification : Entity
{
    public Guid? DriverId { get; set; }
    public Guid? UserId { get; set; }
    public string Text { get; set; } = "";
    public string? Ref { get; set; }
    public DateTimeOffset At { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

// ---------- later phases (schema ready) ----------
public class DeviceToken : Entity { public Guid UserId { get; set; } public string Token { get; set; } = ""; public string Platform { get; set; } = ""; }
public class VehicleQrToken : ITenantOwned { public Guid CompanyId { get; set; } public string Token { get; set; } = ""; public Guid VehicleId { get; set; } public bool Active { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset? DisabledAt { get; set; } public string? DisabledReason { get; set; } }
public class VehicleEvent : Entity { public Guid VehicleId { get; set; } public string Type { get; set; } = ""; public string Title { get; set; } = ""; public string? Detail { get; set; } public decimal? Km { get; set; } public decimal? Cost { get; set; } public string Status { get; set; } = ""; public DateTimeOffset At { get; set; } public Guid ByUserId { get; set; } public string? CorrectionsJson { get; set; } }
public class VehicleDocument : Entity { public Guid VehicleId { get; set; } public string Kind { get; set; } = ""; public string? Number { get; set; } public DateOnly? IssueDate { get; set; } public DateOnly Expiry { get; set; } public DateTimeOffset At { get; set; } }
public class GpsPoint : ITenantOwned { public Guid CompanyId { get; set; } public long Id { get; set; } public Guid VehicleId { get; set; } public DateTimeOffset At { get; set; } public double Lat { get; set; } public double Lng { get; set; } public double? Speed { get; set; } public string? Address { get; set; } }
// ---------- custody & truck spare parts (العُهد وقطع الغيار) ----------
public class Warehouse : Entity { public string Name { get; set; } = ""; public string? Location { get; set; } public string? Keeper { get; set; } public bool IsActive { get; set; } = true; }

/// <summary>A person who can hold custody but is not a driver: technician, mechanic, storekeeper, office employee.</summary>
public class Employee : Entity { public string Name { get; set; } = ""; public string? Role { get; set; } public string? EmpNo { get; set; } public string? Phone { get; set; } public bool IsActive { get; set; } = true; }

/// <summary>Item definition. Individual items (tyre, battery, meter, pump, tracker…) are tracked one by one as <see cref="Part"/>;
/// quantity items (oil, filters, bolts) by code and quantity in <see cref="StockBalance"/> / <see cref="HolderBalance"/>.</summary>
public class PartCatalog : Entity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public PartTracking Tracking { get; set; }
    public string Category { get; set; } = "";
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? MfrPartNo { get; set; }
    public string? Unit { get; set; }
    public decimal? MinQty { get; set; }
    public decimal? ReorderQty { get; set; }
    public int? InspectEveryDays { get; set; }
    public decimal? UnitCost { get; set; }          // last purchase cost per unit (quantity items) or reference cost
    public string? Specs { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// One individually tracked item. Where it is (warehouse / truck + position / repair vendor) and who holds it (driver or employee)
/// are separate: changing a truck's driver never changes the parts installed on the truck.
/// Its history is the append-only <see cref="PartMovement"/> ledger.
/// </summary>
public class Part : Entity
{
    public Guid CatalogId { get; set; }
    public string InternalNo { get; set; } = "";          // P-000123, issued by the server; printed as barcode / QR
    public string? Serial { get; set; }                   // factory serial (upper-case, no spaces)
    public string? SerialKey { get; set; }                // MAKER|SERIAL: unique per company (same serial from the same maker refused)
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? Supplier { get; set; }
    public string? InvoiceNo { get; set; }
    public DateOnly? PurchaseDate { get; set; }
    public decimal? Cost { get; set; }
    public DateOnly? WarrantyStart { get; set; }
    public DateOnly? WarrantyEnd { get; set; }
    public string? WarrantyTerms { get; set; }
    public string? BatchNo { get; set; }
    public string? Specs { get; set; }
    public DateOnly? ExpiryDate { get; set; }             // hoses, extinguishers, safety equipment
    public DateOnly? LastInspection { get; set; }
    public DateOnly? NextInspection { get; set; }
    public string? Condition { get; set; }                // جديد / جيد / مستعمل / يحتاج فحص
    public string? Notes { get; set; }
    public Guid? MeterId { get; set; }                    // a fuel meter part linked to the meter master record
    public PartStatus Status { get; set; }
    public string? StatusReason { get; set; }
    // location
    public Guid? WarehouseId { get; set; }
    public string? Bin { get; set; }
    public Guid? VehicleId { get; set; }
    public string? Position { get; set; }
    public DateTimeOffset? InstalledAt { get; set; }
    public decimal? InstallKm { get; set; }
    public decimal? InstallHours { get; set; }
    // custody holder (separate from the truck)
    public string? HolderKind { get; set; }               // driver | employee
    public Guid? HolderId { get; set; }
    public string? HolderRef { get; set; }                // holder name at hand-over time
    public DateTimeOffset? HolderSince { get; set; }
    // repair / warranty claim / damage review
    public string? RepairVendor { get; set; }
    public DateTimeOffset? RepairSentAt { get; set; }
    public DateOnly? RepairExpectedAt { get; set; }
    public decimal? RepairCost { get; set; }
    public string? ClaimRef { get; set; }
    public DateTimeOffset? ClaimOpenedAt { get; set; }
    public string? ClaimResult { get; set; }
    public string? LastLocation { get; set; }             // where / with whom it was when declared damaged or lost
    public string? ReviewOutcome { get; set; }
    public Guid? ScrapApprovedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class StockBalance : ITenantOwned { public Guid CompanyId { get; set; } public Guid CatalogId { get; set; } public Guid WarehouseId { get; set; } public decimal Qty { get; set; } public string? Bin { get; set; } }

/// <summary>Quantity items held in custody by a person (e.g. a driver carrying 2 oil cans).</summary>
public class HolderBalance : ITenantOwned
{
    public Guid CompanyId { get; set; }
    public string HolderKind { get; set; } = "";
    public Guid HolderId { get; set; }
    public Guid CatalogId { get; set; }
    public decimal Qty { get; set; }
    public string HolderName { get; set; } = "";
    public DateTimeOffset Since { get; set; }
}

/// <summary>Append-only ledger of everything that happened to a part or a quantity item. Corrections are new rows with a reason.</summary>
public class PartMovement : Entity
{
    public string Type { get; set; } = "";
    public Guid? PartId { get; set; }
    public Guid CatalogId { get; set; }
    public decimal Qty { get; set; }
    public string? StatusBefore { get; set; }
    public string? StatusAfter { get; set; }
    public Guid? FromWarehouseId { get; set; }
    public Guid? ToWarehouseId { get; set; }
    public Guid? FromVehicleId { get; set; }
    public Guid? VehicleId { get; set; }
    public string? Position { get; set; }
    public string? FromHolderKind { get; set; }
    public Guid? FromHolderId { get; set; }
    public string? FromHolderRef { get; set; }
    public string? HolderKind { get; set; }
    public Guid? HolderId { get; set; }
    public string? HolderRef { get; set; }
    public Guid? WorkOrderId { get; set; }
    public string? Reason { get; set; }
    public string? OldPartFate { get; set; }
    public Guid? RelatedPartId { get; set; }           // replacement: the other part (old ↔ new)
    public Guid? TransferId { get; set; }
    public decimal? Km { get; set; }
    public decimal? Hours { get; set; }
    public decimal? UnitCost { get; set; }
    public decimal? Cost { get; set; }
    public string? Vendor { get; set; }
    public string? DocRef { get; set; }
    public string? BatchNo { get; set; }
    public DateOnly? ExpectedReturn { get; set; }
    public string? Note { get; set; }
    public Guid? CorrectsId { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid ByUserId { get; set; }
    public Guid? ApprovedBy { get; set; }
}

/// <summary>Hand-over of custody between people, trucks and warehouses: recorded by the giver, completed only when the receiver accepts.</summary>
public class CustodyTransfer : Entity
{
    public string No { get; set; } = "";
    public string Kind { get; set; } = "transfer";    // issue | transfer | qty-issue | qty-transfer
    public Guid? PartId { get; set; }
    public Guid CatalogId { get; set; }
    public decimal Qty { get; set; } = 1;
    public Guid? WorkOrderId { get; set; }
    public string? Reason { get; set; }
    public string FromKind { get; set; } = "";        // driver | employee | warehouse | vehicle
    public Guid FromId { get; set; }
    public string FromName { get; set; } = "";
    public string ToKind { get; set; } = "";
    public Guid ToId { get; set; }
    public string ToName { get; set; } = "";
    public string? Position { get; set; }
    public decimal? Km { get; set; }
    public string Status { get; set; } = "Pending";   // Pending | Accepted | Rejected
    public string? Condition { get; set; }
    public string? HandoverBy { get; set; }
    public string? HandoverNote { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public Guid RequestedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public Guid? DecidedBy { get; set; }
    public string? DecisionNote { get; set; }
}

public record CountLine(Guid CatalogId, string Code, string Name, string? Bin, decimal Recorded, decimal Actual);

/// <summary>A physical stock count of one warehouse: recorded vs actual, differences posted as adjustment movements.</summary>
public class StockCount : Entity
{
    public string No { get; set; } = "";
    public Guid WarehouseId { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid ByUserId { get; set; }
    public string? Note { get; set; }
    public List<CountLine> Lines { get; set; } = new();
    public List<string> MissingParts { get; set; } = new();
    public List<string> UnexpectedParts { get; set; } = new();
}

public class WorkOrder : Entity
{
    public string No { get; set; } = "";
    public Guid VehicleId { get; set; }
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Workshop { get; set; }
    public decimal? Km { get; set; }
    public decimal? LaborCost { get; set; }
    public bool Closed { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public Guid? OpenedBy { get; set; }
    public Guid? DriverId { get; set; }               // driver of the truck when the order was opened (snapshot)
    public DateTimeOffset? ClosedAt { get; set; }
    public Guid? ClosedBy { get; set; }
    public string? CloseNote { get; set; }
}

/// <summary>Per-company, per-month counter for order numbers (atomic, see TripService.NextNoAsync).</summary>
public class TripCounter : ITenantOwned { public Guid CompanyId { get; set; } public string Period { get; set; } = ""; public int Last { get; set; } }

/// <summary>
/// One work period of a user: check-in and check-out. Times are taken from the server clock (a phone or browser cannot
/// back-date them). Corrections by a manager keep the original values in the audit log.
/// </summary>
public class AttendanceRecord : Entity
{
    public Guid UserId { get; set; }
    public DateOnly Day { get; set; }                 // Saudi date of the check-in
    public DateTimeOffset CheckInAt { get; set; }
    public DateTimeOffset? CheckOutAt { get; set; }
    public double? InLat { get; set; }
    public double? InLng { get; set; }
    public double? OutLat { get; set; }
    public double? OutLng { get; set; }
    public string? InIp { get; set; }
    public string? OutIp { get; set; }
    public string? Note { get; set; }
    public bool Corrected { get; set; }
}

/// <summary>
/// "تقرير التعبئة": the driver documents one tanker trip from before filling to after unloading, with six photos
/// (tank before/after filling, tank full at the customer, secret meter before unloading, tank after unloading, secret meter after)
/// and the secret meter readings. Linked 1:1 to a trip; trip, vehicle, driver and customer details are copied at start.
/// Photos are Attachments with OwnerType "fillreport" and Kind = the photo slot.
/// </summary>
public class FillReport : Entity
{
    public string No { get; set; } = "";
    public Guid TripId { get; set; }
    public Guid DriverId { get; set; }
    public Guid StartedBy { get; set; }
    public FillReportStatus Status { get; set; } = FillReportStatus.Draft;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    // copied from the trip at start (what the driver confirmed he was documenting)
    public string TripNo { get; set; } = "";
    public string Plate { get; set; } = "";
    public string? TrailerPlate { get; set; }
    public string DriverName { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string DeliverySite { get; set; } = "";
    public string LoadingSite { get; set; } = "";
    public string Product { get; set; } = "";
    public decimal QtyRequested { get; set; }

    // stages
    public DateTimeOffset? PreFillAt { get; set; }
    public DateTimeOffset? PostFillAt { get; set; }
    public decimal? LoadedQty { get; set; }
    public bool LoadedFromTrip { get; set; }
    public DateTimeOffset? ArrivedAt { get; set; }
    public string? SecretMeterSerial { get; set; }
    public decimal? MeterBefore { get; set; }
    public DateTimeOffset? UnloadedAt { get; set; }
    public decimal? MeterAfter { get; set; }
    public UnloadOutcome Outcome { get; set; } = UnloadOutcome.Full;
    public string? OutcomeReason { get; set; }
    public string? TankState { get; set; }          // "فارغ" or what actually remained
    /// <summary>«رقم سند التحميل / فاتورة المحطة» typed by the driver after filling (optional; the trip's loading document is the fallback).</summary>
    public string? LoadingDocNo { get; set; }

    // result and review
    public DateTimeOffset? SubmittedAt { get; set; }
    public string? ReviewReasons { get; set; }
    public Guid? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewNote { get; set; }
    public decimal? DeliveredQty => MeterBefore is { } b && MeterAfter is { } a && a >= b ? a - b : null;
}

/// <summary>
/// «طلب إعادة التصوير»: a supervisor asks the driver to photograph one slot of a fill report again, with a note.
/// While open, the driver may upload that slot even if the report was already sent or approved; the upload resolves it.
/// </summary>
public class RetakeRequest : Entity
{
    public Guid ReportId { get; set; }
    public string Kind { get; set; } = "";
    public string Note { get; set; } = "";
    public Guid RequestedBy { get; set; }
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
    public Guid? ResolvedAttachmentId { get; set; }
}

// ---------- customer orders (طلبات العملاء) ----------
/// <summary>
/// «طلب العميل»: the hub that links all departments. One order can need several trucks (trips). Its execution totals are
/// computed from the trips; Status New/Planned/InProgress/Delivered is derived from them, Closed/Cancelled are manual with a reason.
/// Money fields are entered by hand for now (a later phase takes them from customer contracts).
/// </summary>
public class CustomerOrder : Entity
{
    public string No { get; set; } = "";
    public Guid CustomerId { get; set; }
    public OrderType Type { get; set; }
    public string Product { get; set; } = "ديزل";
    public decimal QtyRequested { get; set; }
    public OrderPriority Priority { get; set; }
    public DateTimeOffset? RequestedFrom { get; set; }
    public DateTimeOffset? RequestedTo { get; set; }
    public string? CustomerRef { get; set; }
    public string? Notes { get; set; }
    public decimal? TransportRatePerLiter { get; set; }
    public decimal? FuelPricePerLiter { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.New;
    public string? StatusReason { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public Guid? ClosedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid CreatedBy { get; set; }
    /// <summary>Names at creation time (customer may be renamed later).</summary>
    public string CustomerName { get; set; } = "";
    public string Site { get; set; } = "";
}

/// <summary>Per-company, per-month counter for customer order numbers SO-yyMM-0001.</summary>
public class OrderCounter : ITenantOwned { public Guid CompanyId { get; set; } public string Period { get; set; } = ""; public int Last { get; set; } }

// ---------- fleet documents (وثائق المركبات والسائقين) ----------
/// <summary>A field shown for a document type: key is one of number, category, issuer, issueDate, expiry, image.</summary>
public record DocField(string Key, string Label, bool Required);

/// <summary>Kind of document (الفحص الدوري، التأمين، رخصة القيادة…): applies to a vehicle or a driver; mandatory ones block dispatch when expired.</summary>
public class DocumentType : Entity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string AppliesTo { get; set; } = "vehicle";      // vehicle | driver
    public List<DocField> Fields { get; set; } = new();
    public bool MandatoryForDispatch { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>
/// One document of one vehicle or one driver (a driver's licence stays with the driver when his truck changes).
/// The row holds the current values; every registration and renewal is kept as a <see cref="FleetDocumentVersion"/> (never deleted).
/// </summary>
public class FleetDocument : Entity
{
    public Guid TypeId { get; set; }
    public string OwnerKind { get; set; } = "vehicle";      // vehicle | driver
    public Guid OwnerId { get; set; }
    public string? Number { get; set; }
    public string? Category { get; set; }
    public string? Issuer { get; set; }
    public DateOnly? IssueDate { get; set; }
    public DateOnly? Expiry { get; set; }
    public int Version { get; set; } = 1;
    public Guid CurrentVersionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Append-only history of a document: the first registration and each renewal. Copies (images/PDF) are attachments of the version.</summary>
public class FleetDocumentVersion : Entity
{
    public Guid DocumentId { get; set; }
    public int Version { get; set; }
    public string Kind { get; set; } = "";                   // تسجيل | تجديد
    public string? Number { get; set; }
    public string? Category { get; set; }
    public string? Issuer { get; set; }
    public DateOnly? IssueDate { get; set; }
    public DateOnly? Expiry { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public Guid ByUserId { get; set; }
}

/// <summary>Marker of an expiry alert already sent: exactly one per (document, expiry date, offset), so restarts never resend.</summary>
public class DocumentAlert : Entity
{
    public Guid DocumentId { get; set; }
    public DateOnly Expiry { get; set; }
    public int OffsetDays { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public int Recipients { get; set; }
}
