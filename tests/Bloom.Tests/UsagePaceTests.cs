using System;
using Bloom.Ai;
using Bloom.Ui;

namespace Bloom.Tests;

/// The straight-line pace behind the usage cards' "−10%p 여유 / +8%p 초과 / 권장 20%".
internal static class UsagePaceTests
{
    public static void FiveHourPace()
    {
        var now = new DateTime(2026, 11, 2, 12, 0, 0);
        // 1시간 경과(240분 남음): 권장 20%.
        var limit = new UsageLimit("5시간", 10, now.AddMinutes(240), 300);
        var pace = HomeViews.Pace(limit, now);
        Check.True(pace is not null, "window and reset known: a pace");
        Check.Near(pace!.Value.Expected, 20, 0.001, "one of five hours elapsed");
        Check.Near(pace.Value.Delta, -10, 0.001, "10% used sits 10%p under the pace");

        var over = new UsageLimit("5시간", 30, now.AddMinutes(240), 300);
        Check.Near(HomeViews.Pace(over, now)!.Value.Delta, 10, 0.001, "30% used sits 10%p over the pace");
    }

    public static void WeeklyPace()
    {
        var now = new DateTime(2026, 11, 2, 12, 0, 0);
        // 4일 경과(3일 남음): 권장 4/7 ≈ 57.14%.
        var limit = new UsageLimit("주간", 40, now.AddDays(3), 10080);
        Check.Near(HomeViews.Pace(limit, now)!.Value.Expected, 400.0 / 7, 0.01, "four of seven days elapsed");
    }

    public static void NoLineWithoutWindowOrReset()
    {
        var now = new DateTime(2026, 11, 2, 12, 0, 0);
        Check.Eq(HomeViews.Pace(new UsageLimit("모델 한도", 60, now.AddHours(1)), now), null, "unknown window: no pace");
        Check.Eq(HomeViews.Pace(new UsageLimit("5시간", 60, null, 300), now), null, "unknown reset: no pace");
    }

    public static void PaceClampsOutsideTheWindow()
    {
        var now = new DateTime(2026, 11, 2, 12, 0, 0);
        Check.Near(HomeViews.Pace(new UsageLimit("5시간", 95, now.AddMinutes(-10), 300), now)!.Value.Expected, 100, 0.001, "reset passed: the whole window is spent");
        Check.Near(HomeViews.Pace(new UsageLimit("5시간", 0, now.AddMinutes(400), 300), now)!.Value.Expected, 0, 0.001, "reset further than the window: nothing should be used yet");
    }
}
