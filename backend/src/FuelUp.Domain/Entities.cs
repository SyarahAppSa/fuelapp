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
    public List<string> PlateHistory { get; set; } = new();
}

public class Driver : Entity
{
    public string Name { get; set; } = "";
    public string? EmpNo { get; set; }
    public string? Phone { get; set; }
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
public class Warehouse : Entity { public string Name { get; set; } = ""; }
public class Employee : Entity { public string Name { get; set; } = ""; public string? Role { get; set; } public string? EmpNo { get; set; } }
public class PartCatalog : Entity { public string Code { get; set; } = ""; public string Name { get; set; } = ""; public PartTracking Tracking { get; set; } public string Category { get; set; } = ""; public string? Brand { get; set; } public string? Model { get; set; } public string? MfrPartNo { get; set; } public string? Unit { get; set; } public decimal? MinQty { get; set; } public decimal? ReorderQty { get; set; } public int? InspectEveryDays { get; set; } }
public class Part : Entity { public Guid CatalogId { get; set; } public string InternalNo { get; set; } = ""; public string? Serial { get; set; } public string? Brand { get; set; } public string? Model { get; set; } public string? Supplier { get; set; } public string? InvoiceNo { get; set; } public DateOnly? PurchaseDate { get; set; } public decimal? Cost { get; set; } public DateOnly? WarrantyEnd { get; set; } public PartStatus Status { get; set; } public Guid? WarehouseId { get; set; } public Guid? VehicleId { get; set; } public string? Position { get; set; } public string? HolderRef { get; set; } public DateTimeOffset? InstalledAt { get; set; } public decimal? InstallKm { get; set; } }
public class StockBalance : ITenantOwned { public Guid CompanyId { get; set; } public Guid CatalogId { get; set; } public Guid WarehouseId { get; set; } public decimal Qty { get; set; } }
public class PartMovement : Entity { public string Type { get; set; } = ""; public Guid? PartId { get; set; } public Guid CatalogId { get; set; } public decimal Qty { get; set; } public Guid? VehicleId { get; set; } public string? HolderRef { get; set; } public Guid? WorkOrderId { get; set; } public string? Reason { get; set; } public string? OldPartFate { get; set; } public decimal? Km { get; set; } public DateTimeOffset At { get; set; } public Guid ByUserId { get; set; } }
public class WorkOrder : Entity { public string No { get; set; } = ""; public Guid VehicleId { get; set; } public string Type { get; set; } = ""; public string Description { get; set; } = ""; public decimal? LaborCost { get; set; } public bool Closed { get; set; } public DateTimeOffset OpenedAt { get; set; } }

/// <summary>Per-company, per-month counter for order numbers (atomic, see TripService.NextNoAsync).</summary>
public class TripCounter : ITenantOwned { public Guid CompanyId { get; set; } public string Period { get; set; } = ""; public int Last { get; set; } }
