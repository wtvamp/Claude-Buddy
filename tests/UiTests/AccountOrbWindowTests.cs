using System;
using Avalonia.Headless.XUnit;
using Xunit;

namespace ClaudeBuddy.Tests;

// AccountOrbWindow driven through the one method the poll calls, and asserted
// on what a person would have seen.
//
// No Show(), no clicks. UpdateFrom needs neither, and the click path here
// reaches AccountOrbs, which starts a `claude` subprocess per account — the
// same reason OrbWindowUpdateFromTests refuses to synthesize a click on a
// session orb, where the pointer path reaches TerminalFocuser. The card's
// hover and pin behaviour is covered in AccountOrbsTests against a fake source
// instead, which is where the decisions actually live.
//
// Joins the Settings collection because constructing the window reads
// TwoLetterGlyphs while deciding its letters, and a sibling test flipping that
// setting mid-run would otherwise change what this one draws.
[Collection("Settings")]
public class AccountOrbWindowTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    private static AccountUsage Usage(
        double? session = 20,
        double? weekly = 30,
        ExtraUsage? extra = null,
        bool available = true,
        DateTimeOffset? readAt = null,
        string label = "board",
        AccountUsageSource source = AccountUsageSource.ClaudeCode) =>
        new(
            ConfigDir: null,
            Label: label,
            Available: available,
            SubscriptionType: "team",
            Session: session is null ? null : new UsageWindow(session.Value, Now.AddHours(3)),
            Weekly: weekly is null ? null : new UsageWindow(weekly.Value, Now.AddDays(3)),
            Extra: extra,
            ReadAt: readAt ?? Now,
            Source: source);

    [AvaloniaFact]
    public void ConstructsHeadlessWithNoException()
    {
        Assert.NotNull(new AccountOrbWindow("k"));
    }

    [AvaloniaFact]
    public void WearsTheAccountsInitials()
    {
        var orb = new AccountOrbWindow("k");

        orb.UpdateFrom(Usage(label: "board"), Now);

        // Whatever OrbGlyph decides for this machine's glyph setting — asserted
        // against OrbGlyph rather than a hardcoded string so this test says "the
        // orb uses the app's letters" instead of quietly re-implementing them.
        Assert.Equal(
            OrbGlyph.For("board", ClaudeBuddySettings.TwoLetterGlyphs),
            orb.GlyphText);
    }

    [AvaloniaFact]
    public void WearsTheCliMarkForItsSource()
    {
        var claude = new AccountOrbWindow("c");
        var codex = new AccountOrbWindow("x");
        var grok = new AccountOrbWindow("g");

        claude.UpdateFrom(Usage(source: AccountUsageSource.ClaudeCode), Now);
        codex.UpdateFrom(Usage(source: AccountUsageSource.Codex), Now);
        grok.UpdateFrom(Usage(source: AccountUsageSource.Grok), Now);

        Assert.Equal("claude", claude.CliMarkName);
        Assert.Equal("codex", codex.CliMarkName);
        Assert.Equal("grok", grok.CliMarkName);
        Assert.True(claude.CliMarkVisible);
        Assert.True(codex.CliMarkVisible);
        Assert.True(grok.CliMarkVisible);
        Assert.NotEqual(claude.CliMarkFill, codex.CliMarkFill);
        Assert.NotEqual(claude.CliMarkFill, grok.CliMarkFill);
        Assert.NotEqual(codex.CliMarkFill, grok.CliMarkFill);
    }

    [AvaloniaTheory]
    [InlineData(10, AccountOrbWindow.CalmHex)]
    [InlineData(70, AccountOrbWindow.WarnHex)]
    [InlineData(95, AccountOrbWindow.DangerHex)]
    public void RingColourFollowsHeadroom(double weekly, string expected)
    {
        var orb = new AccountOrbWindow("k");

        orb.UpdateFrom(Usage(weekly: weekly), Now);

        Assert.Equal(expected, orb.WeeklyColour);
    }

    [AvaloniaFact]
    public void TheTwoRingsAreColouredIndependently()
    {
        var orb = new AccountOrbWindow("k");

        orb.UpdateFrom(Usage(session: 5, weekly: 92), Now);

        // The case the whole design is for: a fresh five hours inside an
        // exhausted week. One ring calm, one alarming, on the same orb.
        Assert.Equal(AccountOrbWindow.CalmHex, orb.SessionColour);
        Assert.Equal(AccountOrbWindow.DangerHex, orb.WeeklyColour);
    }

    // A window past its reset is not a stale number, it is a number about a
    // period that has ended. Drawing it would be a confident wrong answer.
    [AvaloniaFact]
    public void AnExpiredWindowIsNotDrawn()
    {
        var orb = new AccountOrbWindow("k");

        var usage = Usage() with
        {
            Weekly = new UsageWindow(89, Now.AddMinutes(-1))
        };

        orb.UpdateFrom(usage, Now);

        Assert.Null(orb.WeeklyColour);
    }

    [AvaloniaFact]
    public void AReadingNobodyHasRefreshedIsDimmed()
    {
        var orb = new AccountOrbWindow("k");

        orb.UpdateFrom(Usage(readAt: Now - AccountUsage.StaleAfter), Now);

        Assert.True(orb.IsDimmed);
    }

    [AvaloniaFact]
    public void AFreshReadingIsNotDimmed()
    {
        var orb = new AccountOrbWindow("k");

        orb.UpdateFrom(Usage(readAt: Now.AddMinutes(-1)), Now);

        Assert.False(orb.IsDimmed);
    }

    // CB-85. A Codex reading now arrives two ways, and only one of them has an
    // age: the app-server answers about *now* and carries no ObservedAt, while
    // a rollout snapshot carries the moment the last session wrote it. Same
    // percentage, same orb, opposite verdicts — and dimming is the only thing
    // on screen that tells the two apart, so it is worth pinning rather than
    // inferring from the AsOf tests.
    [AvaloniaFact]
    public void ALiveCodexReadingIsNotDimmedWhereTheSameSnapshotWouldBe()
    {
        var live = new AccountOrbWindow("k");
        var fromDisk = new AccountOrbWindow("k");

        var reading = Usage(readAt: Now) with { Source = AccountUsageSource.Codex };

        live.UpdateFrom(reading, Now);
        fromDisk.UpdateFrom(reading with { ObservedAt = Now - AccountUsage.StaleAfter }, Now);

        Assert.False(live.IsDimmed);
        Assert.True(fromDisk.IsDimmed);
    }

    // The inner ring is a gauge only when there is a cap to be a share of.
    [AvaloniaFact]
    public void ExtraUsageDisabledDrawsAnAbsenceRatherThanAZero()
    {
        var orb = new AccountOrbWindow("k");

        var off = new ExtraUsage(false, 0, null, "USD", 2, "never_enabled");
        orb.UpdateFrom(Usage(extra: off), Now);

        Assert.True(orb.ExtraIsAbsent);
    }

    // ...but a budget that has been *spent* is the opposite of an absent one,
    // and the first version drew them the same. An account that had used every
    // penny of its extra usage looked exactly like one that had never had any.
    [AvaloniaFact]
    public void ASpentBudgetIsAFullRingNotAnAbsentOne()
    {
        var orb = new AccountOrbWindow("k");

        var spent = new ExtraUsage(
            Enabled: false, UsedMinor: null, LimitMinor: null, Currency: "USD",
            DecimalPlaces: 2, DisabledReason: "org_level_disabled_until",
            UserDisabled: false, SpendLimitReached: true);

        orb.UpdateFrom(Usage(extra: spent), Now);

        Assert.False(orb.ExtraIsAbsent);
    }

    [AvaloniaFact]
    public void ExtraUsageWithACapIsAGauge()
    {
        var orb = new AccountOrbWindow("k");

        var on = new ExtraUsage(true, 1000, 2000, "USD", 2, null);
        orb.UpdateFrom(Usage(extra: on), Now);

        Assert.False(orb.ExtraIsAbsent);
    }

    [AvaloniaFact]
    public void ThePinBadgeFollowsThePin()
    {
        var orb = new AccountOrbWindow("k");

        Assert.False(orb.IsPinned);

        orb.SetPinned(true);
        Assert.True(orb.IsPinned);

        orb.SetPinned(false);
        Assert.False(orb.IsPinned);
    }

    // The tooltip is the only place the rings are spelled out in words, and it
    // has to distinguish the three silences: an account with no limits, one
    // nobody has read yet, and one whose reading has gone cold.
    [AvaloniaFact]
    public void TheSummarySaysWhichKindOfNothingItIs()
    {
        Assert.Equal(
            "no subscription limits on this account",
            AccountOrbWindow.Summary(Usage(available: false), Now));

        Assert.Equal(
            "no reading yet",
            AccountOrbWindow.Summary(Usage(session: null, weekly: null), Now));

        Assert.Equal("5h 20% · 7d 30%", AccountOrbWindow.Summary(Usage(), Now));

        Assert.Equal(
            "5h 20% · 7d 30% · stale",
            AccountOrbWindow.Summary(Usage(readAt: Now - AccountUsage.StaleAfter), Now));
    }

    [AvaloniaFact]
    public void TheSummaryFloorsTheWayTheCliDoes()
    {
        // 84.9 prints as 84 in `claude`'s own /usage, and two tools disagreeing
        // by a point about the same number is the kind of thing that costs an
        // afternoon.
        Assert.Equal("5h 84% · 7d 0%", AccountOrbWindow.Summary(Usage(84.9, 0.4), Now));
    }

    // --- the thought bubble tooltip --------------------------------------
    // Same guard as OrbWindowUpdateFromTests' own section, and for the same
    // reason: UpdateFrom used to call ToolTip.SetTip with a brand-new Border
    // on every poll, whether or not the label/summary had changed. This is a
    // separate window class from OrbWindow (see the class comment at the top
    // of this file) and so carried its own, unfixed copy of the same call —
    // CB-104's first round only touched OrbWindow. An account orb's poll is
    // five minutes apart (UsagePoller.MinimumInterval) rather than two
    // seconds, so the same flicker was real here too, just far rarer.

    [AvaloniaFact]
    public void RepeatedIdenticalUpdatesReuseTheSameTooltipInstance()
    {
        var orb = new AccountOrbWindow("k");
        var usage = Usage();

        orb.UpdateFrom(usage, Now);
        var first = orb.CurrentThoughtBubble;
        Assert.NotNull(first);

        // Same label, same summary, same everything the tooltip reads — a
        // typical re-poll that answered with the same reading as before.
        orb.UpdateFrom(usage, Now);
        var second = orb.CurrentThoughtBubble;

        Assert.Same(first, second);
    }

    [AvaloniaFact]
    public void ALabelChangeRebuildsTheTooltip()
    {
        var orb = new AccountOrbWindow("k");

        orb.UpdateFrom(Usage(label: "board"), Now);
        var first = orb.CurrentThoughtBubble;

        orb.UpdateFrom(Usage(label: "other"), Now);
        var second = orb.CurrentThoughtBubble;

        Assert.NotSame(first, second);
    }

    [AvaloniaFact]
    public void ASummaryChangeRebuildsTheTooltip()
    {
        var orb = new AccountOrbWindow("k");

        orb.UpdateFrom(Usage(session: 20), Now);
        var first = orb.CurrentThoughtBubble;

        // Same label, but a fresh poll reporting the reading moved — the
        // tooltip's second line (Summary) changes even though nothing else
        // about the orb's identity did.
        orb.UpdateFrom(Usage(session: 21), Now);
        var second = orb.CurrentThoughtBubble;

        Assert.NotSame(first, second);
    }
}
