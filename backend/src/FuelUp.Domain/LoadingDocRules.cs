namespace FuelUp.Domain;

/// <summary>
/// «توثيق التعبئة»: documentation status of the loading part of a fill report (tank before / after filling, loading document),
/// computed from the photos the driver sent, open retake requests and the loaded quantity. Pure, so it is unit-tested.
/// </summary>
public static class LoadingDocRules
{
    public const string PreFill = "tank-pre-fill", PostFill = "tank-post-fill", LoadingDoc = "loading-doc";
    /// <summary>The photos that belong to loading. The first one of them to reach the server makes the record appear.</summary>
    public static readonly string[] Kinds = [PreFill, PostFill, LoadingDoc];

    public const string NotStarted = "NotStarted", AwaitingPostFill = "AwaitingPostFill", Complete = "Complete", NeedsReview = "NeedsReview";
    public static readonly string[] Statuses = [AwaitingPostFill, Complete, NeedsReview];

    /// <summary>A photo uploaded later than this after it was taken is shown as «وصلت بعد …» (sent from the offline queue).</summary>
    public static readonly TimeSpan LateUpload = TimeSpan.FromMinutes(10);
    /// <summary>Grace for typing the loaded quantity after the post-fill photo before it becomes a review reason.</summary>
    public static readonly TimeSpan QtyGrace = TimeSpan.FromMinutes(30);

    public static string Label(string status) => status switch
    {
        AwaitingPostFill => "بانتظار صورة بعد التعبئة", Complete => "مكتمل", NeedsReview => "يحتاج مراجعة", _ => "لم تبدأ",
    };

    public static string SourceKey(StationType t) => t switch { StationType.Aramco => "Aramco", StationType.MaysanYard => "MaysanYard", _ => "Station" };
    public static string SourceLabel(StationType t) => t switch { StationType.Aramco => "أرامكو", StationType.MaysanYard => "حوش ميسان", _ => "محطة" };

    public record Photo(string Kind, string Label, DateTimeOffset? CapturedAt, DateTimeOffset UploadedAt, double? Lat, string? LocationNote, bool FromGallery);
    public record OpenRetake(string Label, string Note);

    /// <summary>Minutes between capture and upload when longer than <see cref="LateUpload"/>; otherwise null.</summary>
    public static int? LateMinutes(DateTimeOffset? captured, DateTimeOffset uploaded) =>
        captured is { } c && uploaded - c > LateUpload ? (int)Math.Round((uploaded - c).TotalMinutes) : null;

    /// <param name="latest">The newest photo of each loading slot (<see cref="Kinds"/>).</param>
    /// <param name="open">Open retake requests on any slot of the report.</param>
    public static (string Status, List<string> Reasons) Evaluate(IReadOnlyCollection<Photo> latest, IReadOnlyCollection<OpenRetake> open, decimal? loadedQty, bool postFillConfirmed, DateTimeOffset now)
    {
        var loading = latest.Where(p => Kinds.Contains(p.Kind)).ToList();
        if (loading.Count == 0) return (NotStarted, []);
        var reasons = new List<string>();
        foreach (var r in open) reasons.Add($"طلب إعادة تصوير ({r.Label}): {r.Note}");
        var pre = loading.FirstOrDefault(p => p.Kind == PreFill);
        var post = loading.FirstOrDefault(p => p.Kind == PostFill);
        if (pre is null) reasons.Add("لا توجد صورة للصهريج قبل التعبئة");
        foreach (var p in loading.OrderBy(p => Array.IndexOf(Kinds, p.Kind)))
        {
            if (p.Lat is null) reasons.Add($"صورة {p.Label} بدون موقع: {p.LocationNote ?? "لم يُرسل الموقع"}");
            if (p.FromGallery) reasons.Add($"وقت التقاط صورة {p.Label} غير موثوق (أقدم من 48 ساعة عند رفعها أو غير مُرسل)");
        }
        if (post is not null && loadedQty is not > 0 && (postFillConfirmed || now - post.UploadedAt > QtyGrace))
            reasons.Add("الكمية المحمّلة غير مسجّلة بعد التعبئة");
        if (reasons.Count > 0) return (NeedsReview, reasons);
        return (post is null ? AwaitingPostFill : Complete, reasons);
    }
}
