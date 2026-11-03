using System;
using Bloom.Ai;
using Bloom.Core;
using Bloom.Ui;

namespace Bloom.Tests;

internal static class AntigravityUsageTests
{
    public static void SummarySeparatesActualFiveHourAndWeeklyGeminiBuckets()
    {
        var result = AntigravityUsage.ParseSummary("""
            {"response":{"groups":[
              {"displayName":"Gemini Models","buckets":[
                {"bucketId":"gemini-weekly","window":"weekly","remainingFraction":0.63,"resetTime":"2026-10-25T04:12:00Z"},
                {"bucketId":"gemini-5h","window":"5h","remainingFraction":0.88,"resetTime":"2026-10-22T14:03:00Z"}]},
              {"displayName":"Claude and GPT models","buckets":[
                {"bucketId":"3p-5h","window":"5h","remainingFraction":0.13},
                {"bucketId":"3p-weekly","window":"weekly","remainingFraction":0.01}]}
            ]}}
            """, new DateTime(2026, 10, 22, 20, 0, 0));
        Check.True(result is not null, "group summary returns Gemini usage");
        Check.Eq(result!.Id, "antigravity", "the summary has the Antigravity card identity");
        Check.Eq(result.Limits.Count, 2, "both Gemini windows and neither Claude window");
        Check.Eq((result.Limits[0].Title, result.Limits[0].WindowMins), ("5시간", (int?)300), "five-hour window");
        Check.Near(result.Limits[0].Percent, 12, 0.001, "remaining fraction is converted to used percent");
        Check.Eq(result.Limits[0].Resets, (DateTime?)DateTimeOffset.Parse("2026-10-22T14:03:00Z").LocalDateTime, "time zone conversion");
        Check.Eq((result.Limits[1].Title, result.Limits[1].WindowMins), ("주간", (int?)10080), "weekly window");
        Check.Near(result.Limits[1].Percent, 37, 0.001, "weekly fraction converted separately");
    }

    public static void MissingWeeklyQuotaIsNeverInvented()
    {
        var onlySession = AntigravityUsage.ParseSummary("""
            {"groups":[{"displayName":"Gemini Models","buckets":[
              {"bucketId":"gemini-5h","window":"5h","remainingFraction":0.2}]}]}
            """);
        Check.Eq(onlySession?.Limits.Count, 1, "no synthetic weekly window");
        Check.Eq(onlySession?.Limits[0].Title, "5시간", "real five-hour quota");
        Check.Eq(AntigravityUsage.ParseSummary("""{"groups":[{"displayName":"Claude and GPT models","buckets":[{"bucketId":"3p-weekly","window":"weekly","remainingFraction":0.4}]}]}"""),
            null, "do not mistake Claude/GPT weekly quota for Gemini");
        Check.Eq(AntigravityUsage.ParseSummary("""{"groups":[{"displayName":"Gemini Models","buckets":[{"bucketId":"gemini-weekly","window":"weekly","remainingFraction":3}]}]}"""),
            null, "out-of-range quota cannot appear");
    }

    public static void LegacyModelResponseKeepsTheWindowUnknown()
    {
        var result = AntigravityUsage.ParseModels("""
            {"userStatus":{"cascadeModelConfigData":{"clientModelConfigs":[
              {"label":"Gemini Pro (High)","quotaInfo":{"remainingFraction":0.9,"resetTime":"2026-10-22T14:20:00Z"}},
              {"label":"Gemini Flash","quotaInfo":{"remainingFraction":0.4,"resetTime":"2026-10-22T14:35:00Z"}},
              {"label":"Claude Opus","quotaInfo":{"remainingFraction":0.01}}]}}}
            """);
        Check.Eq(result?.Limits.Count, 1, "only known model quota");
        Check.Eq(result?.Limits[0].WindowMins, null, "period is not inferred from a reset timestamp");
        Check.Eq(result?.Limits[0].Title, "모델 한도", "unknown period is identified accurately");
        Check.Near(result!.Limits[0].Percent, 60, 0.001, "use the most restrictive Gemini fraction");
        Check.Eq(AntigravityUsage.ParseModels("""{"userStatus":{"cascadeModelConfigData":{"clientModelConfigs":[{"label":"Claude Opus","quotaInfo":{"remainingFraction":0.2}}]}}}"""),
            null, "no Gemini response yields no card");
    }

    public static void UsageCardDurationShowsMinutesForHoursAndDays()
    {
        Check.Eq(HomeViews.Duration(TimeSpan.FromMinutes(59)), "59분", "minutes only");
        Check.Eq(HomeViews.Duration(TimeSpan.FromMinutes(62)), "1시간 2분", "hours plus minutes");
        Check.Eq(HomeViews.Duration(TimeSpan.FromMinutes(120)), "2시간 0분", "whole hour still displays minutes");
        Check.Eq(HomeViews.Duration(TimeSpan.FromDays(1) + TimeSpan.FromHours(2) + TimeSpan.FromMinutes(17)),
            "1일 2시간 17분", "days, hours and minutes");
        Lang.Force(true);
        Check.Eq(HomeViews.Duration(TimeSpan.FromMinutes(62)), "1 h 2 min", "English language setting");
    }
}
