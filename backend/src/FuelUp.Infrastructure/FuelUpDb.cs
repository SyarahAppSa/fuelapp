using System.Text.Json;
using FuelUp.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FuelUp.Infrastructure;

public class FuelUpDb(DbContextOptions<FuelUpDb> options, TenantContext? tenant = null) : DbContext(options)
{
    /// <summary>Company of the current request. Null in the API means "not signed in yet": tenant tables then return no rows.</summary>
    public Guid? CurrentCompanyId => tenant?.CompanyId;
    /// <summary>False only for unit tests and tools that build the context by hand without a tenant.</summary>
    public bool TenantFilterOn => tenant != null;

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<UserSession> Sessions => Set<UserSession>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserPermission> UserPermissions => Set<UserPermission>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Station> Stations => Set<Station>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<Driver> Drivers => Set<Driver>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Meter> Meters => Set<Meter>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<VehicleAssignment> VehicleAssignments => Set<VehicleAssignment>();
    public DbSet<CustomerAssignment> CustomerAssignments => Set<CustomerAssignment>();
    public DbSet<Trip> Trips => Set<Trip>();
    public DbSet<TripLoading> TripLoadings => Set<TripLoading>();
    public DbSet<TripDelivery> TripDeliveries => Set<TripDelivery>();
    public DbSet<TripHandover> TripHandovers => Set<TripHandover>();
    public DbSet<TripCorrection> TripCorrections => Set<TripCorrection>();
    public DbSet<MeterReading> MeterReadings => Set<MeterReading>();
    public DbSet<MeterBaseline> MeterBaselines => Set<MeterBaseline>();
    public DbSet<MeterReset> MeterResets => Set<MeterReset>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();
    public DbSet<VehicleQrToken> VehicleQrTokens => Set<VehicleQrToken>();
    public DbSet<VehicleEvent> VehicleEvents => Set<VehicleEvent>();
    public DbSet<VehicleDocument> VehicleDocuments => Set<VehicleDocument>();
    public DbSet<GpsPoint> GpsPoints => Set<GpsPoint>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<PartCatalog> PartCatalog => Set<PartCatalog>();
    public DbSet<Part> Parts => Set<Part>();
    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<PartMovement> PartMovements => Set<PartMovement>();
    public DbSet<WorkOrder> WorkOrders => Set<WorkOrder>();
    public DbSet<HolderBalance> HolderBalances => Set<HolderBalance>();
    public DbSet<CustodyTransfer> CustodyTransfers => Set<CustodyTransfer>();
    public DbSet<StockCount> StockCounts => Set<StockCount>();
    public DbSet<TripCounter> TripCounters => Set<TripCounter>();
    public DbSet<AttendanceRecord> Attendance => Set<AttendanceRecord>();
    public DbSet<FillReport> FillReports => Set<FillReport>();
    public DbSet<CustomerOrder> Orders => Set<CustomerOrder>();
    public DbSet<OrderCounter> OrderCounters => Set<OrderCounter>();
    public DbSet<DocumentType> DocumentTypes => Set<DocumentType>();
    public DbSet<FleetDocument> Documents => Set<FleetDocument>();
    public DbSet<FleetDocumentVersion> DocumentVersions => Set<FleetDocumentVersion>();
    public DbSet<DocumentAlert> DocumentAlerts => Set<DocumentAlert>();

    public bool IsPostgres => Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Username).IsUnique();   // global: the username identifies the company at sign-in
            e.HasMany(x => x.Permissions).WithOne().HasForeignKey(p => p.UserId);
            e.Property(x => x.Role).HasConversion<string>();
            Json(e.Property(x => x.RecoveryCodeHashes));
        });
        b.Entity<UserSession>(e => { e.ToTable("user_sessions"); e.HasIndex(x => x.UserId); });
        b.Entity<RefreshToken>(e => { e.HasIndex(x => x.TokenHash).IsUnique(); e.HasIndex(x => x.SessionId); });
        b.Entity<UserPermission>(e => { e.HasKey(x => new { x.UserId, x.Section }); e.Property(x => x.Section).HasConversion<string>(); e.Property(x => x.Level).HasConversion<string>(); });
        b.Entity<Station>().Property(x => x.Type).HasConversion<string>();
        b.Entity<Driver>().Property(x => x.Type).HasConversion<string>();
        b.Entity<Vehicle>(e => { e.HasIndex(x => new { x.CompanyId, x.Plate }).IsUnique(); Json(e.Property(x => x.Compartments)); Json(e.Property(x => x.PlateHistory)); });
        b.Entity<Meter>().HasIndex(x => new { x.CompanyId, x.Serial }).IsUnique();
        b.Entity<Setting>().HasKey(x => new { x.CompanyId, x.Key });

        // one open assignment per vehicle / customer at a time
        b.Entity<VehicleAssignment>(e => { e.HasIndex(x => x.VehicleId).IsUnique().HasFilter("\"to\" IS NULL"); e.HasIndex(x => new { x.DriverId, x.From }); });
        b.Entity<CustomerAssignment>().HasIndex(x => x.CustomerId).IsUnique().HasFilter("\"to\" IS NULL");

        b.Entity<Trip>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.No }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.Status, x.CreatedAt });
            e.HasIndex(x => x.DriverId);
            e.HasIndex(x => x.OrderId);
            e.Property(x => x.Status).HasConversion<string>();
            Json(e.Property(x => x.Snapshot));
            e.HasOne(x => x.Loading).WithOne().HasForeignKey<TripLoading>(l => l.TripId);
            e.HasMany(x => x.Deliveries).WithOne().HasForeignKey(d => d.TripId);
            e.HasMany(x => x.Handovers).WithOne().HasForeignKey(h => h.TripId);
            e.HasMany(x => x.Corrections).WithOne().HasForeignKey(c => c.TripId);
        });
        b.Entity<TripLoading>(e => { e.HasKey(x => x.TripId); e.HasIndex(x => x.DocNo); });
        b.Entity<TripDelivery>(e => { e.HasIndex(x => new { x.TripId, x.Seq }).IsUnique(); e.HasIndex(x => x.ReceiptNo); e.Ignore(x => x.MeterQty); });
        b.Entity<TripHandover>().Property(x => x.Ack).HasConversion<string>();
        b.Entity<TripCorrection>().Property(x => x.State).HasConversion<string>();

        // append-only meter ledger, ordered per meter
        b.Entity<MeterReading>(e => { e.HasIndex(x => new { x.MeterId, x.Seq }).IsUnique(); e.HasIndex(x => x.TripId); });
        b.Entity<MeterBaseline>(e => { e.HasIndex(x => x.MeterId); e.Property(x => x.State).HasConversion<string>(); });
        b.Entity<MeterReset>().HasIndex(x => x.MeterId);

        b.Entity<Attachment>(e => { e.HasIndex(x => new { x.OwnerType, x.OwnerId }); e.HasIndex(x => x.Sha256); e.HasIndex(x => new { x.OwnerId, x.ClientId }).IsUnique().HasFilter("client_id IS NOT NULL"); });
        b.Entity<AuditEntry>(e => { e.HasIndex(x => new { x.Entity, x.EntityId }); e.HasIndex(x => new { x.CompanyId, x.At }); });
        b.Entity<Notification>(e => { e.HasIndex(x => new { x.DriverId, x.ReadAt }); e.HasIndex(x => x.UserId); });

        b.Entity<VehicleQrToken>(e => { e.HasKey(x => x.Token); e.HasIndex(x => x.VehicleId); });
        b.Entity<VehicleEvent>().HasIndex(x => new { x.VehicleId, x.At });
        b.Entity<VehicleDocument>().HasIndex(x => new { x.VehicleId, x.Kind });
        b.Entity<GpsPoint>().HasIndex(x => new { x.VehicleId, x.At });
        b.Entity<PartCatalog>(e => { e.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique(); e.Property(x => x.Tracking).HasConversion<string>(); });
        b.Entity<Part>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.InternalNo }).IsUnique();
            // the same factory serial from the same maker can exist only once (MAKER|SERIAL, normalised by the API)
            e.HasIndex(x => new { x.CompanyId, x.SerialKey }).IsUnique().HasFilter("serial_key IS NOT NULL").HasDatabaseName("ux_parts_serial_key");
            e.HasIndex(x => new { x.VehicleId, x.Position }).IsUnique().HasFilter("status = 'Installed' AND position IS NOT NULL"); // one part per position
            // an installed part sits on exactly one truck; a part on a truck must be "Installed"
            e.ToTable(t => t.HasCheckConstraint("ck_parts_installed_vehicle", "(status = 'Installed') = (vehicle_id IS NOT NULL)"));
            e.HasIndex(x => new { x.CompanyId, x.Status });
            e.HasIndex(x => new { x.HolderKind, x.HolderId });
            e.HasIndex(x => x.Serial);
            e.Property(x => x.Status).HasConversion<string>();
        });
        b.Entity<StockBalance>(e => { e.HasKey(x => new { x.CatalogId, x.WarehouseId }); e.ToTable(t => t.HasCheckConstraint("ck_stock_balances_qty", "qty >= 0")); });
        b.Entity<HolderBalance>(e => { e.ToTable("holder_balances", t => t.HasCheckConstraint("ck_holder_balances_qty", "qty >= 0")); e.HasKey(x => new { x.HolderKind, x.HolderId, x.CatalogId }); });
        b.Entity<PartMovement>(e => { e.HasIndex(x => new { x.PartId, x.At }); e.HasIndex(x => new { x.CompanyId, x.Type, x.At }); e.HasIndex(x => new { x.VehicleId, x.At }); e.HasIndex(x => x.WorkOrderId); });
        b.Entity<CustodyTransfer>(e =>
        {
            e.ToTable("custody_transfers");
            e.HasIndex(x => new { x.CompanyId, x.No }).IsUnique();
            e.HasIndex(x => x.PartId).IsUnique().HasFilter("status = 'Pending' AND part_id IS NOT NULL").HasDatabaseName("ux_custody_transfers_pending_part");
            e.HasIndex(x => new { x.ToKind, x.ToId, x.Status });
        });
        b.Entity<StockCount>(e => { e.ToTable("stock_counts"); e.HasIndex(x => new { x.CompanyId, x.No }).IsUnique(); Json(e.Property(x => x.Lines)); Json(e.Property(x => x.MissingParts)); Json(e.Property(x => x.UnexpectedParts)); });
        b.Entity<WorkOrder>(e => { e.HasIndex(x => new { x.CompanyId, x.No }).IsUnique(); e.HasIndex(x => x.VehicleId); });
        // one open period per user at a time (enforced by the database, so two quick taps cannot open two)
        b.Entity<AttendanceRecord>(e => { e.ToTable("attendance"); e.HasIndex(x => x.UserId).IsUnique().HasFilter("check_out_at IS NULL").HasDatabaseName("ux_attendance_open"); e.HasIndex(x => new { x.CompanyId, x.Day }); });
        b.Entity<FillReport>(e =>
        {
            e.ToTable("fill_reports");
            e.HasIndex(x => x.TripId).IsUnique();                    // one report per trip
            e.HasIndex(x => new { x.CompanyId, x.No }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.Status, x.StartedAt });
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.Outcome).HasConversion<string>();
            e.Ignore(x => x.DeliveredQty);
        });
        b.Entity<CustomerOrder>(e =>
        {
            e.ToTable("customer_orders");
            e.HasIndex(x => new { x.CompanyId, x.No }).IsUnique();
            e.HasIndex(x => new { x.CompanyId, x.Status, x.CreatedAt });
            e.HasIndex(x => x.CustomerId);
            e.Property(x => x.Type).HasConversion<string>();
            e.Property(x => x.Priority).HasConversion<string>();
            e.Property(x => x.Status).HasConversion<string>();
        });
        b.Entity<OrderCounter>(e => { e.ToTable("order_counters"); e.HasKey(x => new { x.CompanyId, x.Period }); e.Property(x => x.Period).HasMaxLength(4); });
        b.Entity<DocumentType>(e => { e.ToTable("document_types"); e.HasIndex(x => new { x.CompanyId, x.Name }).IsUnique(); Json(e.Property(x => x.Fields)); });
        b.Entity<FleetDocument>(e =>
        {
            e.ToTable("fleet_documents");
            e.HasIndex(x => new { x.CompanyId, x.TypeId, x.OwnerId }).IsUnique();   // one document of a type per vehicle / driver: renewals are versions
            e.HasIndex(x => x.OwnerId);
            e.HasIndex(x => new { x.CompanyId, x.Expiry });
        });
        b.Entity<FleetDocumentVersion>(e => { e.ToTable("fleet_document_versions"); e.HasIndex(x => new { x.DocumentId, x.Version }).IsUnique(); });
        b.Entity<DocumentAlert>(e => { e.ToTable("document_alerts"); e.HasIndex(x => new { x.DocumentId, x.Expiry, x.OffsetDays }).IsUnique(); });
        b.Entity<TripCounter>(e => { e.ToTable("trip_counters"); e.HasKey(x => new { x.CompanyId, x.Period }); e.Property(x => x.Period).HasMaxLength(4); });

        foreach (var p in b.Model.GetEntityTypes().SelectMany(t => t.GetProperties()).Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
            p.SetColumnType("numeric(14,2)");

        // Ids are assigned in code (Guid.NewGuid), so a new child added to a tracked parent must be inserted, not updated.
        foreach (var t in b.Model.GetEntityTypes().Where(t => typeof(Entity).IsAssignableFrom(t.ClrType)))
            b.Entity(t.ClrType).Property(nameof(Entity.Id)).ValueGeneratedNever();

        // Company isolation, application layer: every query on a company table is filtered to the current company.
        foreach (var t in b.Model.GetEntityTypes().Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType) && t.BaseType == null).ToList())
            typeof(FuelUpDb).GetMethod(nameof(TenantFilter), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .MakeGenericMethod(t.ClrType).Invoke(this, [b]);
        b.Entity<AuditEntry>().HasQueryFilter(e => !TenantFilterOn || e.CompanyId == CurrentCompanyId);
        b.Entity<Company>().HasQueryFilter(e => !TenantFilterOn || e.Id == CurrentCompanyId);
    }

    private void TenantFilter<T>(ModelBuilder b) where T : class, ITenantOwned =>
        b.Entity<T>().HasQueryFilter(e => !TenantFilterOn || e.CompanyId == CurrentCompanyId);

    public override int SaveChanges(bool acceptAllChangesOnSuccess) { StampTenant(); return base.SaveChanges(acceptAllChangesOnSuccess); }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default) { StampTenant(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct); }

    /// <summary>New rows get the current company; changing or deleting a row of another company is refused.</summary>
    private void StampTenant()
    {
        if (!TenantFilterOn) return;
        foreach (var e in ChangeTracker.Entries())
        {
            if (e.Entity is ITenantOwned o)
            {
                if (e.State == EntityState.Added)
                {
                    if (CurrentCompanyId is not { } cid) throw new TenantViolationException("no company in context");
                    if (o.CompanyId == Guid.Empty) o.CompanyId = cid;
                    else if (o.CompanyId != cid) throw new TenantViolationException("row of another company");
                }
                else if (e.State is EntityState.Modified or EntityState.Deleted)
                {
                    var original = (Guid)e.OriginalValues[nameof(ITenantOwned.CompanyId)]!;
                    if (original != CurrentCompanyId || o.CompanyId != original) throw new TenantViolationException("row of another company");
                }
            }
            else if (e.Entity is AuditEntry a && e.State == EntityState.Added)
                a.CompanyId ??= CurrentCompanyId;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static void Json<T>(PropertyBuilder<T> p)
    {
        p.HasConversion(v => JsonSerializer.Serialize(v, JsonOpts), v => JsonSerializer.Deserialize<T>(v, JsonOpts)!,
            new ValueComparer<T>((a, c) => JsonSerializer.Serialize(a, JsonOpts) == JsonSerializer.Serialize(c, JsonOpts),
                v => JsonSerializer.Serialize(v, JsonOpts).GetHashCode(), v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, JsonOpts), JsonOpts)!))
         .HasColumnType("jsonb");
    }
}
