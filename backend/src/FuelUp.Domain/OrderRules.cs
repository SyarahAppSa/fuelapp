namespace FuelUp.Domain;

/// <summary>Customer order rules: labels, quantities per trip and the status derived from the order's trips.</summary>
public static class OrderRules
{
    public static string TypeLabel(OrderType t) => t == OrderType.Transport ? "نقل فقط" : "نقل وتوريد";
    public static string PriorityLabel(OrderPriority p) => p == OrderPriority.Urgent ? "عاجل" : "مجدول";

    public static string StatusLabel(OrderStatus s) => s switch
    {
        OrderStatus.New => "جديد",
        OrderStatus.Planned => "مخطط",
        OrderStatus.InProgress => "قيد التنفيذ",
        OrderStatus.Delivered => "تم التسليم",
        OrderStatus.Closed => "مغلق",
        OrderStatus.Cancelled => "ملغى",
        _ => s.ToString(),
    };

    public static bool IsOpen(OrderStatus s) => s is not (OrderStatus.Closed or OrderStatus.Cancelled);

    /// <summary>Planned quantity of a trip: what was loaded once loading is recorded, otherwise what was requested.</summary>
    public static decimal Planned(Trip t) => t.Loading?.Qty ?? t.QtyRequested;

    /// <summary>Delivered quantity of a trip: by the unloading meter when the truck has one, otherwise by the customer's receipt.</summary>
    public static decimal? Delivered(Trip t)
    {
        var done = t.Deliveries.Where(d => d.SubmittedAt != null).ToList();
        if (done.Count == 0) return null;
        return done.Any(d => d.MeterQty != null) ? done.Sum(d => d.MeterQty ?? 0) : done.Sum(d => d.Received ?? 0);
    }

    public static decimal? Receipt(Trip t)
    {
        var done = t.Deliveries.Where(d => d.SubmittedAt != null).ToList();
        return done.Count == 0 ? null : done.Sum(d => d.Received ?? 0);
    }

    /// <summary>A trip whose unloading is finished (whatever the approval state).</summary>
    public static bool Finished(Trip t) => t.Status is TripStatus.AwaitingApproval or TripStatus.Approved or TripStatus.UnderReview;

    /// <summary>
    /// Status from the trips (cancelled trips ignored): none → New; all waiting to load → Planned;
    /// every trip finished and the delivered total reaches the requested quantity (within tolerance) → Delivered; otherwise InProgress.
    /// Closed and Cancelled are kept as set by a person.
    /// </summary>
    public static OrderStatus Derive(CustomerOrder o, IReadOnlyCollection<Trip> trips, Tolerance tol)
    {
        if (!IsOpen(o.Status)) return o.Status;
        var active = trips.Where(t => t.Status != TripStatus.Cancelled).ToList();
        if (active.Count == 0) return OrderStatus.New;
        if (active.All(t => t.Status == TripStatus.WaitingLoad)) return OrderStatus.Planned;
        var delivered = active.Sum(t => Delivered(t) ?? 0);
        if (active.All(Finished) && delivered >= o.QtyRequested - tol.For(o.QtyRequested)) return OrderStatus.Delivered;
        return OrderStatus.InProgress;
    }
}

/// <summary>Expiry status of fleet documents and the alert schedule (offsets in days before the expiry date).</summary>
public static class DocRules
{
    public const string Valid = "valid", Soon = "soon", Expired = "expired", NoDate = "nodate";
    public static readonly int[] DefaultOffsets = [30, 7, 1, 0];

    public static string Status(DateOnly? expiry, DateOnly today, int warnDays) =>
        expiry is not { } e ? NoDate : e < today ? Expired : e.DayNumber - today.DayNumber <= warnDays ? Soon : Valid;

    public static string Label(string status) => status switch
    {
        Valid => "سارية",
        Soon => "قريبة من الانتهاء",
        Expired => "منتهية",
        _ => "تاريخ الانتهاء غير مسجّل",
    };

    /// <summary>
    /// The alert due today for a document, or null. With offsets 30/7/1/0: from 30 days before → the 30-day alert, from 7 days → the 7-day alert…
    /// on and after the expiry day → the expiry alert (0). Only the closest step is due, so a document registered 5 days before its expiry
    /// gets one alert (the 7-day one), not two. Each (document, expiry, offset) is sent once; a renewal changes the expiry and restarts the cycle.
    /// </summary>
    public static int? DueOffset(DateOnly? expiry, DateOnly today, IReadOnlyCollection<int> offsets)
    {
        if (expiry is not { } e || offsets.Count == 0) return null;
        var left = e.DayNumber - today.DayNumber;
        if (left < 0) return offsets.Contains(0) ? 0 : offsets.Min();
        var due = offsets.Where(o => o >= left).ToList();
        return due.Count == 0 ? null : due.Min();
    }
}
