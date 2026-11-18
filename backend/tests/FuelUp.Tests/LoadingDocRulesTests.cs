using FuelUp.Domain;
using Xunit;
using static FuelUp.Domain.LoadingDocRules;

namespace FuelUp.Tests;

public class LoadingDocRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);
    private static Photo P(string kind, double? lat = 24.7, bool gallery = false, int minutesAgo = 5) =>
        new(kind, kind, Now.AddMinutes(-minutesAgo - 1), Now.AddMinutes(-minutesAgo), lat, lat is null ? "لا توجد إشارة GPS في المكان" : null, gallery);

    [Fact]
    public void No_loading_photo_is_not_started() => Assert.Equal(NotStarted, Evaluate([P("tank-arrival")], [], null, false, Now).Status);

    [Fact]
    public void Pre_fill_only_waits_for_the_post_fill_photo() => Assert.Equal(AwaitingPostFill, Evaluate([P(PreFill)], [], null, false, Now).Status);

    [Fact]
    public void Both_photos_with_quantity_are_complete() => Assert.Equal(Complete, Evaluate([P(PreFill), P(PostFill)], [], 20000, true, Now).Status);

    [Fact]
    public void Photo_without_location_needs_review_with_its_reason()
    {
        var (s, reasons) = Evaluate([P(PreFill, lat: null)], [], null, false, Now);
        Assert.Equal(NeedsReview, s);
        Assert.Contains(reasons, r => r.Contains("بدون موقع") && r.Contains("GPS"));
    }

    [Fact]
    public void Open_retake_request_needs_review_even_when_complete()
    {
        var (s, reasons) = Evaluate([P(PreFill), P(PostFill)], [new OpenRetake("الصهريج بعد التعبئة", "الصورة مظلمة")], 20000, true, Now);
        Assert.Equal(NeedsReview, s);
        Assert.Equal("طلب إعادة تصوير (الصهريج بعد التعبئة): الصورة مظلمة", reasons[0]);
    }

    [Fact]
    public void Old_capture_is_a_reason() => Assert.Equal(NeedsReview, Evaluate([P(PreFill, gallery: true)], [], null, false, Now).Status);

    [Fact]
    public void Missing_quantity_is_a_reason_only_after_the_grace_period_or_the_stage()
    {
        Assert.Equal(Complete, Evaluate([P(PreFill), P(PostFill)], [], null, false, Now).Status);
        Assert.Equal(NeedsReview, Evaluate([P(PreFill), P(PostFill, minutesAgo: 45)], [], null, false, Now).Status);
        Assert.Equal(NeedsReview, Evaluate([P(PreFill), P(PostFill)], [], 0, true, Now).Status);
    }

    [Fact]
    public void Post_fill_without_pre_fill_needs_review() =>
        Assert.Contains("لا توجد صورة للصهريج قبل التعبئة", Evaluate([P(PostFill)], [], 1000, true, Now).Reasons);

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
