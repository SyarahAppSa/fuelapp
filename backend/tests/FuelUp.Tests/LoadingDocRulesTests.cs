using FuelUp.Domain;
using Xunit;
using static FuelUp.Domain.LoadingDocRules;

namespace FuelUp.Tests;

public class LoadingDocRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);
    private static Photo P(string kind, double? lat = 24.7, bool gallery = false, int minutesAgo = 5) =>
        new(kind, kind, Now.AddMinutes(-minutesAgo - 1), Now.AddMinutes(-minutesAgo), lat, lat is null ? "لا توجد إشارة GPS في المكان" : null, gallery);

    private static readonly Photo[] Four = [P(PreFill), P(PostFill), P(Arrival), P(PostUnload)];

    [Fact]
    public void No_photo_is_not_started() => Assert.Equal(NotStarted, Evaluate([P("meter-before")], [], null, false, Now).Status);

    [Fact]
    public void First_photo_waits_for_the_next_stage_in_order()
    {
        var e = Evaluate([P(PreFill)], [], null, false, Now);
        Assert.Equal(Incomplete, e.Status);
        Assert.Equal(["بعد التعبئة", "قبل التفريغ", "بعد التفريغ"], e.Missing);
        Assert.Equal("بانتظار صورة بعد التعبئة", Label(e.Status, e.Missing));
    }

    [Fact]
    public void Two_loading_photos_still_wait_for_the_delivery_photos()
    {
        var e = Evaluate([P(PreFill), P(PostFill)], [], 20000, true, Now);
        Assert.Equal(Incomplete, e.Status);
        Assert.Equal("بانتظار صورة قبل التفريغ", Label(e.Status, e.Missing));
    }

    [Fact]
    public void Four_photos_with_quantity_are_complete() => Assert.Equal(Complete, Evaluate(Four, [], 20000, true, Now).Status);

    [Fact]
    public void Old_filter_name_is_accepted() => Assert.Equal(Incomplete, Normalize(AwaitingPostFill));

    [Fact]
    public void Photo_without_location_needs_review_with_its_reason()
    {
        var (s, reasons, _) = Evaluate([P(PreFill, lat: null)], [], null, false, Now);
        Assert.Equal(NeedsReview, s);
        Assert.Contains(reasons, r => r.Contains("بدون موقع") && r.Contains("GPS"));
    }

    [Fact]
    public void Open_retake_request_needs_review_even_when_complete()
    {
        var (s, reasons, _) = Evaluate(Four, [new OpenRetake("الصهريج بعد التعبئة", "الصورة مظلمة")], 20000, true, Now);
        Assert.Equal(NeedsReview, s);
        Assert.Equal("طلب إعادة تصوير (الصهريج بعد التعبئة): الصورة مظلمة", reasons[0]);
    }

    [Fact]
    public void Old_capture_is_a_reason() => Assert.Equal(NeedsReview, Evaluate([P(PreFill, gallery: true)], [], null, false, Now).Status);

    [Fact]
    public void Missing_quantity_is_a_reason_only_after_the_grace_period_or_the_stage()
    {
        Assert.Equal(Complete, Evaluate(Four, [], null, false, Now).Status);
        Assert.Equal(NeedsReview, Evaluate([P(PreFill), P(PostFill, minutesAgo: 45), P(Arrival), P(PostUnload)], [], null, false, Now).Status);
        Assert.Equal(NeedsReview, Evaluate(Four, [], 0, true, Now).Status);
    }

    [Fact]
    public void A_skipped_stage_is_listed_as_still_required() =>
        Assert.Equal(["عند بدء التعبئة"], Evaluate([P(PostFill), P(Arrival), P(PostUnload)], [], 1000, true, Now).Missing);

    [Fact]
    public void Late_upload_is_reported_after_ten_minutes()
    {
        Assert.Null(LateMinutes(Now.AddMinutes(-9), Now));
        Assert.Equal(42, LateMinutes(Now.AddMinutes(-42), Now));
        Assert.Null(LateMinutes(null, Now));
    }

    [Fact]
    public void Source_labels() => Assert.Equal(["أرامكو", "محطة", "حوش ميسان"], new[] { StationType.Aramco, StationType.Station, StationType.MaysanYard }.Select(SourceLabel));
}
