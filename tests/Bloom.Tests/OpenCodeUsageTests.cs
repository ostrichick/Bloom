using System;
using Bloom.Ai;

namespace Bloom.Tests;

internal static class OpenCodeUsageTests
{
    private const string Status = """
        {"subscriberUserId":"acc_1","product":"go","renewalProduct":"go",
         "access":{"startsAt":"2026-10-28T06:48:07.000Z","endsAt":"2026-11-28T06:48:07.000Z",
           "meters":{
             "fiveHour":{"startsAt":null,"resetsAt":null,"limitMicroCents":"1200000000","usedMicroCents":"300000000"},
             "week":{"startsAt":"2026-10-26T00:00:00.000Z","resetsAt":"2026-11-02T00:00:00.000Z","limitMicroCents":"3000000000","usedMicroCents":"14953550"},
             "month":{"resetsAt":"2026-11-28T06:48:07.000Z","limitMicroCents":"6000000000","usedMicroCents":"14953550"}}}}
        """;

    public static void StatusMapsTheGoWindows()
    {
        var result = OpenCodeUsage.ParseStatus(Status, "someone@example.com", new DateTime(2026, 10, 28, 12, 0, 0));
        Check.True(result is not null, "Go status returns a card");
        Check.Eq(result!.Id, "opencode", "the card has the OpenCode identity");
        Check.Eq((result.Name, result.Plan), ("OpenCode", "Go"), "name and plan");
        Check.Eq(result.Who, "someone@example.com", "the account's e-mail is carried for the card");
        Check.Eq(result.Limits.Count, 3, "five-hour, week and month");
        Check.Eq((result.Limits[0].Title, result.Limits[0].WindowMins), ("5시간", (int?)300), "five-hour window");
        Check.Near(result.Limits[0].Percent, 25, 0.001, "used over the five-hour limit");
        Check.Eq(result.Limits[0].Resets, null, "no reset before the first request");
        Check.Eq((result.Limits[1].Title, result.Limits[1].WindowMins), ("주간", (int?)10080), "weekly window");
        Check.Near(result.Limits[1].Percent, 0.4984516667, 0.000001, "weekly percent from the cents fields");
        Check.Eq(result.Limits[1].Resets, (DateTime?)DateTimeOffset.Parse("2026-11-02T00:00:00Z").LocalDateTime, "weekly reset in local time");
        Check.Eq((result.Limits[2].Title, result.Limits[2].WindowMins), ("월간", (int?)43200), "monthly window");
        Check.Near(result.Limits[2].Percent, 0.2492258333, 0.000001, "monthly percent from the cents fields");
    }

    public static void GoPlusAndNumericMetersAreRead()
    {
        var result = OpenCodeUsage.ParseStatus("""
            {"product":"go-plus","access":{"meters":{
              "fiveHour":{"limitMicroCents":4800000000,"usedMicroCents":1200000000},
              "week":{"limitMicroCents":12000000000,"usedMicroCents":0}}}}
            """);
        Check.Eq(result?.Plan, "Go Plus", "the higher plan keeps its name");
        Check.Eq(result?.Limits.Count, 2, "only the windows the server sent");
        Check.Near(result!.Limits[0].Percent, 25, 0.001, "numeric cents are read too");
        Check.Near(result.Limits[1].Percent, 0, 0.001, "an unused week is 0%");
    }

    public static void NoAccessOrLimitsYieldsNoCard()
    {
        Check.Eq(OpenCodeUsage.ParseStatus("""{"product":"go"}"""), null, "no access block");
        Check.Eq(OpenCodeUsage.ParseStatus("""{"product":"go","access":{"meters":{}}}"""), null, "no meters");
        Check.Eq(OpenCodeUsage.ParseStatus("""{"access":{"meters":{"week":{"limitMicroCents":"0","usedMicroCents":"10"}}}}"""),
            null, "a zero limit cannot make a percentage");
        Check.Eq(OpenCodeUsage.ParseStatus("not json"), null, "an error page is not a card");
    }

    public static void LoginRowCarriesTheSessionAndWorkspace()
    {
        var login = OpenCodeUsage.ParseLogin("""
            [{"access_token":"token-value","email":"someone@example.com","active_org_id":"wrk_123"}]
            """);
        Check.Eq(login?.Token, "token-value", "session token");
        Check.Eq(login?.Email, "someone@example.com", "account e-mail");
        Check.Eq(login?.Org, "wrk_123", "workspace header");
        Check.Eq(OpenCodeUsage.ParseLogin("[]"), null, "no signed-in account");
        Check.Eq(OpenCodeUsage.ParseLogin("""[{"access_token":"","active_org_id":"wrk_1"}]"""), null, "no token");
        Check.Eq(OpenCodeUsage.ParseLogin("""[{"access_token":"t","active_org_id":null}]"""), null, "no workspace");
    }
}
