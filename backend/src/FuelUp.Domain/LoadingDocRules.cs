namespace FuelUp.Domain;

/// <summary>
/// «توثيق التعبئة»: documentation status of the loading part of a fill report (tank before / after filling, loading document),
/// computed from the photos the driver sent, open retake requests and the loaded quantity. Pure, so it is unit-tested.
/// </summary>
public static class LoadingDocRules
{
    public const string PreFill = "tank-pre-fill", PostFill = "tank-post-fill", Arrival = "tank-arrival", PostUnload = "tank-post-unload", LoadingDoc = "loading-doc";

    /// <summary>The four mandatory photos of a loading-and-delivery operation, in the order the driver takes them.</summary>
    public static readonly (string Kind, string Stage)[] Required =
        [(PreFill, "عند بدء التعبئة"), (PostFill, "بعد التعبئة"), (Arrival, "قبل التفريغ"), (PostUnload, "بعد التفريغ")];
    public static string StageLabel(string kind) => Required.FirstOrDefault(r => r.Kind == kind).Stage ?? (kind == LoadingDoc ? "سند التحميل" : kind);

    /// <summary>The photos of this record. The first one of them to reach the server makes the record appear.</summary>
    public static readonly string[] Kinds = [PreFill, PostFill, Arrival, PostUnload, LoadingDoc];

    public const string NotStarted = "NotStarted", Incomplete = "Incomplete", Complete = "Complete", NeedsReview = "NeedsReview";
    /// <summary>Older name of <see cref="Incomplete"/> (when only the two loading photos were tracked); still accepted as a filter.</summary>
    public const string AwaitingPostFill = "AwaitingPostFill";
    public static readonly string[] Statuses = [Incomplete, Complete, NeedsReview];
    public static string Normalize(string? status) => status == AwaitingPostFill ? Incomplete : status ?? "";

    /// <summary>A photo uploaded later than this after it was taken is shown as «وصلت بعد …» (sent from the offline queue).</summary>
    public static readonly TimeSpan LateUpload = TimeSpan.FromMinutes(10);
    /// <summary>Grace for typing the loaded quantity after the post-fill photo before it becomes a review reason.</summary>
    public static readonly TimeSpan QtyGrace = TimeSpan.FromMinutes(30);

    /// <param name="missing">Stage names still required (only used for «بانتظار صورة …»).</param>
    public static string Label(string status, IReadOnlyList<string>? missing = null) => status switch
    {
        Incomplete => missing is { Count: > 0 } ? $"بانتظار صورة {missing[0]}" : "غير مكتمل", Complete => "مكتمل", NeedsReview => "يحتاج مراجعة", _ => "لم تبدأ",
    };

    public static string SourceKey(StationType t) => t switch { StationType.Aramco => "Aramco", StationType.MaysanYard => "MaysanYard", _ => "Station" };
    public static string SourceLabel(StationType t) => t switch { StationType.Aramco => "أرامكو", StationType.MaysanYard => "حوش ميسان", _ => "محطة" };

    public record Photo(string Kind, string Label, DateTimeOffset? CapturedAt, DateTimeOffset UploadedAt, double? Lat, string? LocationNote, bool FromGallery);
    public record OpenRetake(string Label, string Note);

    /// <summary>Minutes between capture and upload when longer than <see cref="LateUpload"/>; otherwise null.</summary>
    public static int? LateMinutes(DateTimeOffset? captured, DateTimeOffset uploaded) =>
        captured is { } c && uploaded - c > LateUpload ? (int)Math.Round((uploaded - c).TotalMinutes) : null;

    /// <param name="latest">The newest photo of each slot of the record (<see cref="Kinds"/>).</param>
    /// <param name="open">Open retake requests on any slot of the report.</param>
    /// <returns>Status, review reasons, and the stage names still required (in order).</returns>
    public static (string Status, List<string> Reasons, List<string> Missing) Evaluate(IReadOnlyCollection<Photo> latest, IReadOnlyCollection<OpenRetake> open, decimal? loadedQty, bool postFillConfirmed, DateTimeOffset now)
    {
        var mine = latest.Where(p => Kinds.Contains(p.Kind)).ToList();
        var missing = Required.Where(r => mine.All(p => p.Kind != r.Kind)).Select(r => r.Stage).ToList();
        if (mine.Count == 0) return (NotStarted, [], missing);
        var reasons = new List<string>();
        foreach (var r in open) reasons.Add($"طلب إعادة تصوير ({r.Label}): {r.Note}");
        foreach (var p in mine.OrderBy(p => Array.IndexOf(Kinds, p.Kind)))
        {
            if (p.Lat is null) reasons.Add($"صورة {p.Label} بدون موقع: {p.LocationNote ?? "لم يُرسل الموقع"}");
            if (p.FromGallery) reasons.Add($"وقت التقاط صورة {p.Label} غير موثوق (أقدم من 48 ساعة عند رفعها أو غير مُرسل)");
        }
        var post = mine.FirstOrDefault(p => p.Kind == PostFill);
        if (post is not null && loadedQty is not > 0 && (postFillConfirmed || now - post.UploadedAt > QtyGrace))
            reasons.Add("الكمية المحمّلة غير مسجّلة بعد التعبئة");
        if (reasons.Count > 0) return (NeedsReview, reasons, missing);
        return (missing.Count > 0 ? Incomplete : Complete, reasons, missing);
    }
}
