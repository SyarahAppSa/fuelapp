using FuelUp.Domain;
using Xunit;

namespace FuelUp.Tests;

public class MeterRulesTests
{
    [Fact]
    public void Rejects_reading_below_last_approved_with_documented_message()
    {
        var ex = Assert.Throws<DomainException>(() => MeterRules.Validate(125000m, [124900m], requireIncrease: false));
        Assert.Equal("لا يمكن اعتماد القراءة؛ القراءة المدخلة أقل من آخر قراءة معتمدة لهذا العداد وهي «125,000». يرجى التحقق من القراءة أو التواصل مع المشرف.", ex.Message);
    }

    [Fact]
    public void Accepts_higher_reading_and_equal_start()
    {
        MeterRules.Validate(125000m, [125000m, 128000m], requireIncrease: true); // start equal to the previous end is allowed
    }

    [Fact]
    public void Positive_quantity_needs_the_meter_to_move()
    {
        var ex = Assert.Throws<DomainException>(() => MeterRules.Validate(125000m, [125000m, 125000m], requireIncrease: true));
        Assert.Equal(MeterRules.NeedIncreaseMessage, ex.Message);
    }

    [Fact]
    public void End_below_start_is_rejected_even_when_start_is_valid()
    {
        Assert.Throws<DomainException>(() => MeterRules.Validate(125000m, [126000m, 125500m], requireIncrease: true));
    }

    [Fact]
    public void First_reading_on_a_new_meter_has_no_lower_bound()
    {
        MeterRules.Validate(null, [0m, 10m], requireIncrease: true);
    }
}

public class ReconciliationTests
{
    private static Trip Delivered(decimal loaded, decimal before, decimal after, decimal receipt, bool more = false, decimal left = 0)
    {
        var t = new Trip { MeterId = Guid.NewGuid(), Loading = new TripLoading { Qty = loaded, MeterStart = 0, MeterEnd = loaded } };
        t.Deliveries.Add(new TripDelivery { Seq = 1, MeterBefore = before, MeterAfter = after, Received = receipt, SubmittedAt = DateTimeOffset.UtcNow, HasMoreStops = more, LeftInTank = left });
        return t;
    }

    [Fact]
    public void Unsettled_is_loaded_minus_delivered_minus_documented_remaining()
    {
        var c = Reconciliation.Of(Delivered(32000, 1000, 32500, 31500, left: 300), Tolerance.Default);
        Assert.Equal(31500, c.Delivered);
        Assert.Equal(200, c.Unsettled);   // 32000 − 31500 − 300
        Assert.Equal(0, c.MeterVsReceipt);
        Assert.Equal(96, c.Tolerance);    // max(20, 0.3% of 32000)
    }

    [Fact]
    public void Clean_match_waits_for_supervisor()
    {
        var (s, _) = TripWorkflow.Finalize(Delivered(3000, 128000, 131000, 3000), Tolerance.Default, baselinePending: false);
        Assert.Equal(TripStatus.AwaitingApproval, s);
    }

    [Fact]
    public void Difference_over_tolerance_goes_to_review()
    {
        var (s, reason) = TripWorkflow.Finalize(Delivered(4000, 135000, 138800, 4000), Tolerance.Default, baselinePending: false);
        Assert.Equal(TripStatus.UnderReview, s);
        Assert.Contains("فرق غير مسوّى +200", reason);
    }

    [Fact]
    public void Pending_baseline_blocks_approval()
    {
        var (s, reason) = TripWorkflow.Finalize(Delivered(3000, 0, 3000, 3000), Tolerance.Default, baselinePending: true);
        Assert.Equal(TripStatus.UnderReview, s);
        Assert.Contains("القراءة التأسيسية", reason);
    }

    [Fact]
    public void More_stops_keeps_the_trip_on_the_road()
    {
        var (s, _) = TripWorkflow.Finalize(Delivered(3000, 0, 1000, 1000, more: true), Tolerance.Default, false);
        Assert.Equal(TripStatus.EnRoute, s);
    }

    [Theory]
    [InlineData(TripStatus.WaitingLoad, TripStatus.Arrived)]
    [InlineData(TripStatus.Approved, TripStatus.UnderReview)]
    [InlineData(TripStatus.Loaded, TripStatus.Cancelled)]
    public void Workflow_rejects_skips_and_going_back(TripStatus from, TripStatus to)
    {
        var t = new Trip { Status = from };
        Assert.Throws<DomainException>(() => TripWorkflow.Move(t, to));
    }
}
