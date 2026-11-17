namespace FuelUp.Domain;

/// <summary>Where fuel is loaded: a fuel station, the Maysan yard, or Aramco.</summary>
public enum StationType { Station = 0, MaysanYard = 1, Aramco = 2 }

public enum DriverType { Aramco = 0, SmallTruck = 1 }

public enum TripStatus
{
    WaitingLoad = 0,
    Loaded = 1,
    EnRoute = 2,
    Arrived = 3,
    Unloading = 4,
    AwaitingApproval = 5,
    Approved = 6,
    UnderReview = 7,
    Cancelled = 8,
}

public enum ApprovalState { Pending = 0, Approved = 1, Rejected = 2 }

public enum AckState { Pending = 0, Confirmed = 1, Disputed = 2 }

/// <summary>Permission sections, identical to the current FuelUp build.</summary>
public enum Section
{
    Ops, Moves, Approve, Assign, Meters, Tracking, Custody, VehicleFile, Costs, Master, Users, Driver, Export, Attendance, FillReports,
}

public enum PermissionLevel { None = 0, View = 1, Edit = 2 }

public enum Role { Admin, OpsSupervisor, Storekeeper, Driver, Viewer, Custom }

public enum PartStatus { Stock, Issued, Installed, Repair, ReturnedToSupplier, Damaged, Lost, Scrapped }

public enum PartTracking { Individual, Quantity }

/// <summary>Driver's fill report: Draft while documenting, Submitted when complete and consistent, NeedsReview when something
/// needs a human look (partial/failed unloading, invalid or out-of-tolerance reading), Returned to the driver, Approved.</summary>
public enum FillReportStatus { Draft, Submitted, NeedsReview, Returned, Approved }
public enum UnloadOutcome { Full, Partial, Failed }
