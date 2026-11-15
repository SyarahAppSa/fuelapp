using System.Globalization;

namespace FuelUp.Domain;

public sealed class DomainException(string message) : Exception(message);

/// <summary>Cumulative fuel meter rules. The reading belongs to the meter serial, never to the driver.</summary>
public static class MeterRules
{
    public static string RejectMessage(decimal last) =>
        $"لا يمكن اعتماد القراءة؛ القراءة المدخلة أقل من آخر قراءة معتمدة لهذا العداد وهي «{last.ToString("#,0.##", CultureInfo.InvariantCulture)}». يرجى التحقق من القراءة أو التواصل مع المشرف.";

    public const string NeedIncreaseMessage = "لإتمام العملية بكمية موجبة يجب أن تكون قراءة النهاية أكبر من قراءة البداية.";

    /// <summary>
    /// Validates readings in the order they were taken. Each must be &gt;= the previous one (equal is allowed at the start
    /// of a new operation). When <paramref name="requireIncrease"/> is set, the last reading must be strictly greater than
    /// the one before it, because an operation with a positive quantity must move the meter.
    /// </summary>
    public static void Validate(decimal? lastApproved, IReadOnlyList<decimal> sequence, bool requireIncrease)
    {
        if (sequence.Count == 0) throw new DomainException("أدخل قراءات العداد كاملة.");
        var last = lastApproved;
        foreach (var r in sequence)
        {
            if (r < 0) throw new DomainException("قراءة العداد لا يمكن أن تكون سالبة.");
            if (last is { } l && r < l) throw new DomainException(RejectMessage(l));
            last = r;
        }
        if (requireIncrease && sequence.Count >= 2 && !(sequence[^1] > sequence[^2]))
            throw new DomainException(NeedIncreaseMessage);
    }
}

public record Tolerance(decimal Absolute, decimal Percent)
{
    public static readonly Tolerance Default = new(20m, 0.3m);
    public decimal For(decimal? loaded) => Math.Max(Absolute, (loaded ?? 0) * Percent / 100m);
}

public record Reconciliation(
    decimal? Loaded, decimal? LoadMeterQty, decimal? Delivered, decimal? Receipt, decimal Returned, decimal LeftInTank,
    decimal? Unsettled, decimal? MeterVsReceipt, decimal Tolerance)
{
    /// <summary>Unsettled = loaded − delivered by meter − documented remaining or returned.</summary>
    public static Reconciliation Of(Trip t, Tolerance tol)
    {
        var loaded = t.Loading?.Qty;
        var loadMeter = t.Loading is { MeterStart: { } s, MeterEnd: { } e } ? e - s : (decimal?)null;
        var done = t.Deliveries.Where(d => d.SubmittedAt != null).OrderBy(d => d.Seq).ToList();
        decimal? delivered = done.Count == 0 ? null : done.Sum(d => d.MeterQty ?? 0);
        decimal? receipt = done.Count == 0 ? null : done.Sum(d => d.Received ?? 0);
        var returned = done.Sum(d => d.Returned);
        var last = done.LastOrDefault();
        var left = last is { HasMoreStops: false } ? last.LeftInTank : 0;
        decimal? unsettled = loaded is null || done.Count == 0 ? null : loaded - (delivered ?? 0) - returned - left;
        decimal? meterVsReceipt = done.Count == 0 ? null : delivered - receipt;
        return new(loaded, loadMeter, delivered, receipt, returned, left, unsettled, meterVsReceipt, tol.For(loaded));
    }

    public bool Exceeds(decimal? v) => v is { } x && Math.Abs(x) > Tolerance;
}

/// <summary>Allowed status transitions. No jumping, no going back.</summary>
public static class TripWorkflow
{
    private static readonly Dictionary<TripStatus, TripStatus[]> Next = new()
    {
        [TripStatus.WaitingLoad] = [TripStatus.Loaded, TripStatus.Cancelled],
        [TripStatus.Loaded] = [TripStatus.EnRoute],
        [TripStatus.EnRoute] = [TripStatus.Arrived],
        [TripStatus.Arrived] = [TripStatus.Unloading],
        [TripStatus.Unloading] = [TripStatus.AwaitingApproval, TripStatus.UnderReview, TripStatus.EnRoute],
        [TripStatus.AwaitingApproval] = [TripStatus.Approved, TripStatus.UnderReview],
        [TripStatus.UnderReview] = [TripStatus.Approved],
        [TripStatus.Approved] = [],
        [TripStatus.Cancelled] = [],
    };

    public static void Move(Trip t, TripStatus to)
    {
        if (!Next[t.Status].Contains(to))
            throw new DomainException($"لا يمكن الانتقال من «{Label(t.Status)}» إلى «{Label(to)}».");
        t.Status = to;
    }

    public static bool IsOpen(TripStatus s) => s is not (TripStatus.Approved or TripStatus.Cancelled);

    /// <summary>After a delivery: more stops → back on the road; clean match → awaiting supervisor; otherwise review.</summary>
    public static (TripStatus Status, string Reason) Finalize(Trip t, Tolerance tol, bool baselinePending)
    {
        var last = t.Deliveries.Where(d => d.SubmittedAt != null).OrderBy(d => d.Seq).LastOrDefault();
        if (last is { HasMoreStops: true }) return (TripStatus.EnRoute, "");
        var c = Reconciliation.Of(t, tol);
        var problems = new List<string>();
        if (c.Exceeds(c.Unsettled)) problems.Add($"فرق غير مسوّى {Signed(c.Unsettled)} لتر");
        if (c.Exceeds(c.MeterVsReceipt)) problems.Add($"فرق العداد عن سند الاستلام {Signed(c.MeterVsReceipt)} لتر");
        if (c.LoadMeterQty is { } lm && c.Loaded is { } ld && Math.Abs(lm - ld) > c.Tolerance) problems.Add($"فرق عداد التعبئة عن سند التعبئة {Signed(lm - ld)} لتر");
        if (t.Deliveries.Any(d => d.SealsBroken)) problems.Add("أختام مختلفة عند الوصول");
        if (t.Deliveries.Any(d => d.SiteMismatch)) problems.Add("تفريغ في موقع غير الوجهة المحددة");
        if (baselinePending) problems.Add("القراءة التأسيسية للعداد لم تُعتمد بعد");
        if (t.Handovers.Any(h => h.Ack != AckState.Confirmed)) problems.Add("نقل مسؤولية لم يؤكده السائق المستلم");
        if (t.MeterId is null) problems.Add("المركبة بدون عداد مسجل");
        return problems.Count > 0
            ? (TripStatus.UnderReview, string.Join("، ", problems))
            : (TripStatus.AwaitingApproval, $"المطابقة سليمة: الفرق {Signed(c.Unsettled)} لتر ضمن الحد");
    }

    public static string Signed(decimal? v) => v is not { } x ? "—" : (x > 0 ? "+" : "") + x.ToString("#,0.##", CultureInfo.InvariantCulture);

    public static string Label(TripStatus s) => s switch
    {
        TripStatus.WaitingLoad => "بانتظار التحميل",
        TripStatus.Loaded => "تم التحميل",
        TripStatus.EnRoute => "في الطريق",
        TripStatus.Arrived => "وصل للتفريغ",
        TripStatus.Unloading => "جارٍ التفريغ",
        TripStatus.AwaitingApproval => "بانتظار الاعتماد",
        TripStatus.Approved => "معتمدة",
        TripStatus.UnderReview => "تحت المراجعة",
        TripStatus.Cancelled => "ملغاة",
        _ => s.ToString(),
    };
}

public static class Permissions
{
    public static readonly IReadOnlyDictionary<Role, IReadOnlyDictionary<Section, PermissionLevel>> Presets = new Dictionary<Role, IReadOnlyDictionary<Section, PermissionLevel>>
    {
        [Role.Admin] = Enum.GetValues<Section>().ToDictionary(s => s, _ => PermissionLevel.Edit),
        [Role.OpsSupervisor] = new Dictionary<Section, PermissionLevel>
        {
            [Section.Ops] = PermissionLevel.Edit, [Section.Moves] = PermissionLevel.View, [Section.Approve] = PermissionLevel.Edit,
            [Section.Assign] = PermissionLevel.Edit, [Section.Meters] = PermissionLevel.Edit, [Section.Tracking] = PermissionLevel.Edit,
            [Section.VehicleFile] = PermissionLevel.Edit, [Section.Custody] = PermissionLevel.View, [Section.Master] = PermissionLevel.View,
            [Section.Costs] = PermissionLevel.View, [Section.Export] = PermissionLevel.Edit,
        },
        [Role.Storekeeper] = new Dictionary<Section, PermissionLevel> { [Section.Custody] = PermissionLevel.Edit, [Section.VehicleFile] = PermissionLevel.View, [Section.Export] = PermissionLevel.Edit },
        [Role.Driver] = new Dictionary<Section, PermissionLevel> { [Section.Driver] = PermissionLevel.Edit, [Section.VehicleFile] = PermissionLevel.Edit },
        [Role.Viewer] = Enum.GetValues<Section>().Where(s => s is not (Section.Driver or Section.Approve)).ToDictionary(s => s, s => s == Section.Export ? PermissionLevel.Edit : PermissionLevel.View),
        [Role.Custom] = new Dictionary<Section, PermissionLevel>(),
    };
}
