using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace ClaudeBuddy.UnitTests;

// Six decisions that were only reachable from the machine the tests happened to
// be running on, or from a platform CI only half covers, until each was given
// an argument instead of a question.
//
// They have nothing in common except that: a rule that reads its own
// environment cannot be asserted, only observed. Grouped here rather than
// scattered because the pattern is the point — see each case's own note for
// what was being read and what is now passed in.
public class LastReachableArmsTests
{
    // ---- Codex's prompts directory -----------------------------------------

    // ForCodex used to read ~/.codex/prompts off the real home directory, so
    // whether this arm ran at all depended on whether the person running the
    // tests keeps prompts there. That is not a test of this code.
    [Fact]
    public void ACodexPromptBecomesAPrefixedCommand()
    {
        var home = TempHome();
        var prompts = Directory.CreateDirectory(Path.Combine(home, ".codex", "prompts"));
        File.WriteAllText(Path.Combine(prompts.FullName, "standup.md"), "Write the standup note.\n");

        var commands = SlashCommandCatalog.ForCodex(home);

        var standup = Assert.Single(commands, c => c.Name == "/prompts:standup");
        Assert.Equal("Write the standup note.", standup.Description);
    }

    // "/prompts:<name>", never a bare "/<name>" — Codex's own docs are explicit
    // about that, and a catalogue that offered the bare form would be offering
    // something the CLI does not accept.
    [Fact]
    public void APromptIsNeverOfferedUnderItsBareName()
    {
        var home = TempHome();
        Directory.CreateDirectory(Path.Combine(home, ".codex", "prompts"));
        File.WriteAllText(Path.Combine(home, ".codex", "prompts", "standup.md"), "x");

        var names = SlashCommandCatalog.ForCodex(home).Select(c => c.Name).ToList();

        Assert.Contains("/prompts:standup", names);
        Assert.DoesNotContain("/standup", names);
    }

    // Top-level files only. Codex's docs say it "scans only the top-level
    // Markdown files", so a prompt filed in a subdirectory is not a command,
    // and offering it would be offering something that does not exist.
    [Fact]
    public void APromptInASubdirectoryIsNotACommand()
    {
        var home = TempHome();
        var nested = Directory.CreateDirectory(
            Path.Combine(home, ".codex", "prompts", "archive"));
        File.WriteAllText(Path.Combine(nested.FullName, "old.md"), "x");

        Assert.DoesNotContain(SlashCommandCatalog.ForCodex(home),
            c => c.Name.Contains("old", StringComparison.Ordinal));
    }

    // A home with no prompts directory at all still produces the built-ins
    // rather than nothing — the ordinary case for anyone who has never written
    // one.
    [Fact]
    public void AHomeWithNoPromptsStillOffersTheBuiltIns()
    {
        Assert.NotEmpty(SlashCommandCatalog.ForCodex(TempHome()));
    }

    // A prompt named after a built-in replaces it, because it is what actually
    // runs. The merge order is what decides this, and it is the same rule the
    // Claude Code side follows for a custom command shadowing a built-in.
    [Fact]
    public void APromptCannotBeShadowedByABuiltInBecauseItIsMergedLast()
    {
        var home = TempHome();
        Directory.CreateDirectory(Path.Combine(home, ".codex", "prompts"));

        var builtin = SlashCommandCatalog.ForCodex(home).First().Name;
        File.WriteAllText(
            Path.Combine(home, ".codex", "prompts",
                builtin.TrimStart('/').Replace(":", "-") + ".md"),
            "mine\n");

        // Named "/prompts:<file>", so it cannot collide with a built-in at all
        // — which is the answer, and worth asserting rather than assuming: the
        // prefix is what makes the two namespaces separate.
        Assert.Contains(SlashCommandCatalog.ForCodex(home), c => c.Name == builtin);
    }

    private static string TempHome()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "cb-codex-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- where a profile's logs are ----------------------------------------

    // Two genuinely different rules rather than two spellings of one path, and
    // until the platform became an argument only one CI leg ever executed
    // either. A rule only one runner reaches is a rule nobody reads until it is
    // wrong.
    [Fact]
    public void OnWindowsThereIsOneLogDirectory()
    {
        var candidates = ClaudeDesktopManager
            .LogCandidates(@"C:\Users\x\AppData\Roaming\Claude-Work", windows: true, Never)
            .ToList();

        Assert.Single(candidates);
        Assert.EndsWith("logs", candidates[0]);
    }

    // Electron's userData resolves the same way on Windows whether or not
    // --user-data-dir was passed, so there was never a Default/created split
    // there — and that arm is the one that turned out to be right about macOS
    // too. It is asserted here as the invariant it always was: the answer
    // depends on the profile directory and on nothing else.
    [Fact]
    public void OnWindowsTheAnswerDependsOnlyOnTheProfileDirectory()
    {
        var once = ClaudeDesktopManager
            .LogCandidates(@"C:\Users\x\AppData\Roaming\Claude", windows: true, Never);
        var again = ClaudeDesktopManager
            .LogCandidates(@"C:\Users\x\AppData\Roaming\Claude", windows: true, Recent);

        Assert.Equal(once, again);
    }

    // macOS no longer has a Default/created split either, and this is the test
    // that used to assert the bug.
    //
    // It read: a created profile's logs are at <profile>/Logs, full stop. That
    // was only ever true because CLAUDE_USER_DATA_DIR made it true — Claude
    // Desktop's own startup called app.setPath("logs", …) inside the variable's
    // branch. --user-data-dir sets Chromium's userData and nothing else, so on
    // a current build the logs stay at ~/Library/Logs/Claude and the
    // <profile>/Logs left over from when the variable worked is stale. The old
    // assertion passed the whole time and pinned Reveal logs to the stale
    // directory, which is worse than having had no test at all.
    [Fact]
    public void OnMacOsTheLiveLogDirectoryComesFirstWhereverItIs()
    {
        var electron = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Logs", "Claude");
        var inside = Path.Combine("/tmp/Claude-Work", "Logs");

        // A current build: the switch moved the data, Electron kept the logs.
        var current = ClaudeDesktopManager.LogCandidates(
            "/tmp/Claude-Work", windows: false,
            path => path == electron ? Recent(path) : Stale(path));

        Assert.Equal(new[] { electron, inside, "/tmp/Claude-Work" }, current);

        // An older build that still honours the variable, which is why this app
        // still sends it. Same list, ordered by the same evidence, no opinion
        // about which build is installed.
        var older = ClaudeDesktopManager.LogCandidates(
            "/tmp/Claude-Work", windows: false,
            path => path == inside ? Recent(path) : Stale(path));

        Assert.Equal(new[] { inside, electron, "/tmp/Claude-Work" }, older);
    }

    // The profile directory is last however the clock falls, and deliberately
    // outside the comparison: Chromium writes Cookies and Local Storage into it
    // continuously, so ranking it by mtime would make it win every time and
    // Reveal logs would stop revealing logs.
    [Fact]
    public void TheProfileDirectoryIsAlwaysTheLastResort()
    {
        var candidates = ClaudeDesktopManager.LogCandidates(
            "/tmp/Claude-Work", windows: false, _ => DateTime.UtcNow);

        Assert.Equal("/tmp/Claude-Work", candidates[^1]);
        Assert.Equal(3, candidates.Count);
    }

    // Neither log directory has ever been written to — a profile that has not
    // run yet. Nothing to order on, so the static preference stands, and
    // Electron's path leads because that is where a current build will write.
    [Fact]
    public void WithNothingWrittenTheStaticPreferenceStands()
    {
        var candidates = ClaudeDesktopManager.LogCandidates(
            "/tmp/Claude-Work", windows: false, Never);

        Assert.Contains("Library", candidates[0]);
        Assert.Contains("Logs", candidates[0]);
        Assert.Equal(Path.Combine("/tmp/Claude-Work", "Logs"), candidates[1]);
    }

    // ---- ByRecency ---------------------------------------------------------

    [Fact]
    public void ByRecencyPutsTheMostRecentlyWrittenFirst()
    {
        var when = new Dictionary<string, DateTime>
        {
            ["a"] = new(2026, 8, 1),
            ["b"] = new(2026, 8, 25),
            ["c"] = new(2026, 8, 14)
        };

        Assert.Equal(
            new[] { "b", "c", "a" },
            ClaudeDesktopManager.ByRecency(new[] { "a", "b", "c" }, path => when[path]));
    }

    // Unwritten candidates sort behind written ones and keep the order they
    // came in, which is the tie-break the list itself still encodes.
    [Fact]
    public void ByRecencyKeepsTheIncomingOrderForCandidatesWithNoWrites()
    {
        Assert.Equal(
            new[] { "a", "b", "c" },
            ClaudeDesktopManager.ByRecency(new[] { "a", "b", "c" }, Never));

        Assert.Equal(
            new[] { "c", "a", "b" },
            ClaudeDesktopManager.ByRecency(
                new[] { "a", "b", "c" },
                path => path == "c" ? new DateTime(2026, 8, 25) : (DateTime?)null));
    }

    [Fact]
    public void ByRecencyHandlesAnEmptyList()
    {
        Assert.Empty(ClaudeDesktopManager.ByRecency(Array.Empty<string>(), Never));
    }

    // ---- NewestWrite -------------------------------------------------------

    [Fact]
    public void NewestWriteIsNullForADirectoryThatIsNotThere()
    {
        Assert.Null(ClaudeDesktopManager.NewestWrite(
            Path.Combine(Path.GetTempPath(), "cb-absent-" + Guid.NewGuid().ToString("N"))));
    }

    // The file inside, not the directory around it. Appending to main.log does
    // not touch its parent, so a live log directory can carry a much older
    // mtime than its contents — measured on the machine this was found on, and
    // reading the directory's own stamp would have preferred the stale
    // candidate, which is the entire bug.
    [Fact]
    public void NewestWriteReadsTheFilesRatherThanTheDirectory()
    {
        var dir = TempDirectory();
        try
        {
            var log = Path.Combine(dir, "main.log");
            File.WriteAllText(log, "x");

            var future = DateTime.UtcNow.AddHours(1);
            File.SetLastWriteTimeUtc(log, future);
            Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow.AddDays(-30));

            var newest = ClaudeDesktopManager.NewestWrite(dir);

            Assert.NotNull(newest);
            Assert.True(newest > DateTime.UtcNow.AddMinutes(30),
                "the file's stamp should win, not the directory's");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NewestWriteTakesTheNewestOfSeveralFiles()
    {
        var dir = TempDirectory();
        try
        {
            var old = Path.Combine(dir, "old.log");
            var recent = Path.Combine(dir, "recent.log");
            File.WriteAllText(old, "x");
            File.WriteAllText(recent, "y");

            var newest = DateTime.UtcNow.AddHours(1);
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-10));
            File.SetLastWriteTimeUtc(recent, newest);

            Assert.Equal(newest, ClaudeDesktopManager.NewestWrite(dir)!.Value,
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Two files stamped identically, which is a log rotation writing both
    // halves inside the same second and is the only way to reach the "not
    // newer" arm on purpose. NewestWriteTakesTheNewestOfSeveralFiles reaches it
    // too, but only when the filesystem happens to enumerate the newest file
    // first — a coin toss, and a coin toss is not coverage.
    [Fact]
    public void FilesWrittenAtTheSameInstantSettleOnThatInstant()
    {
        var dir = TempDirectory();
        try
        {
            var when = DateTime.UtcNow.AddHours(1);
            foreach (var name in new[] { "a.log", "b.log" })
            {
                var file = Path.Combine(dir, name);
                File.WriteAllText(file, "x");
                File.SetLastWriteTimeUtc(file, when);
            }

            Assert.Equal(when, ClaudeDesktopManager.NewestWrite(dir)!.Value,
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // An empty directory still beats one that is not there: it is evidence the
    // app created it, which a missing path is not.
    [Fact]
    public void AnEmptyDirectoryFallsBackToItsOwnStamp()
    {
        var dir = TempDirectory();
        try
        {
            Assert.NotNull(ClaudeDesktopManager.NewestWrite(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // The catch. A directory that exists and cannot be enumerated would
    // otherwise throw out of a menu click, on a background thread, with nothing
    // to catch it.
    //
    // Unix only for staging reasons, not because the rule is: mode bits are the
    // one portable-enough way to make a real directory unreadable, and Windows
    // needs an ACL edit that a test has no business making. The same
    // split-by-platform pattern BundleCacheLayoutTests uses for its unreadable
    // marker.
    [Fact]
    public void ADirectoryThatCannotBeReadIsTreatedAsUnwritten()
    {
        if (OperatingSystem.IsWindows()) return;

        var dir = TempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir, "main.log"), "x");
            File.SetUnixFileMode(dir, UnixFileMode.None);

            Assert.Null(ClaudeDesktopManager.NewestWrite(dir));
        }
        finally
        {
            File.SetUnixFileMode(dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(dir, recursive: true);
        }
    }

    private static DateTime? Never(string path) => null;

    private static DateTime? Recent(string path) => new(2026, 8, 25);

    private static DateTime? Stale(string path) => new(2026, 7, 1);

    private static string TempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cb-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- every colour has a name -------------------------------------------

    // NameFor ends in a fallback that cannot run, and this is why: every colour
    // For() can hand it is in Named. Asserted rather than assumed, because the
    // fallback is excluded from coverage on the strength of exactly this — and
    // an invariant nothing checks is a comment, not a guarantee.
    //
    // The palette is deliberately a copy of OrbWindow.AgentColors' values
    // rather than a reference to them, so the two can drift; this is what
    // catches a drift that leaves a profile with a colour the settings window
    // cannot name.
    [Fact]
    public void EveryColourAProfileCanGetHasAName()
    {
        var named = ClaudeDesktopColors.NamedColours;

        Assert.All(ClaudeDesktopColors.EveryColourAProfileCanGet,
            colour => Assert.Contains(colour, named));
    }

    // ---- a title with no agent in the key ----------------------------------

    // Everything the gateway currently reports has an "agent:…" key, so the two
    // fallbacks below are for a session shape this app does not produce and
    // cannot stop the gateway producing. They are the difference between an orb
    // labelled with something and an orb labelled with a key.
    [Fact]
    public void ASessionWithNoAgentInItsKeyFallsBackToItsOwnLabel()
    {
        Assert.Equal("Standup notes",
            OpenClawSessions.TitleFor(Json("""{"label":"Standup notes"}"""),
                                      Json("{}"), "room:general"));
    }

    // Then origin's label, which is where a conversation that came from
    // somewhere else carries its name.
    [Fact]
    public void WithNoLabelOfItsOwnTheOriginsLabelIsUsed()
    {
        Assert.Equal("#general",
            OpenClawSessions.TitleFor(Json("{}"),
                                      Json("""{"label":"#general"}"""), "room:general"));
    }

    // A blank label in origin is not a name. Without this the orb would be
    // titled with a space.
    [Fact]
    public void ABlankOriginLabelIsNotAName()
    {
        Assert.Equal("room:general",
            OpenClawSessions.TitleFor(Json("{}"),
                                      Json("""{"label":"   "}"""), "room:general"));
    }

    // And with nothing anywhere, the key — which at least identifies the
    // session uniquely, where an empty title identifies nothing.
    [Fact]
    public void WithNothingToGoOnTheKeyIsTheTitle()
    {
        Assert.Equal("room:general",
            OpenClawSessions.TitleFor(Json("{}"), Json("{}"), "room:general"));
    }

    // origin is absent on 12 of the 70 sessions this was measured against, and
    // arrives as an undefined element rather than an object when it is. Reading
    // a property off that is what the ValueKind check in front of it prevents.
    [Fact]
    public void AMissingOriginIsNotReadAsAnObject()
    {
        Assert.Equal("room:general",
            OpenClawSessions.TitleFor(Json("{}"), default, "room:general"));
    }

    private static JsonElement Json(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    // ---- what the composer says --------------------------------------------

    // Whether there is a pane to type into needs a real tmux and a real
    // session, so it is asked at the property and decided here — which is what
    // makes "no pane" something a test can state rather than something the
    // machine happens to be.
    [Fact]
    public void WithNoPaneTheComposerSaysSoRatherThanOfferingToSend()
    {
        Assert.Equal("No terminal to type into",
            LocalCliChatSession.ComposerHintFor(
                canSendQuietly: false, canDeliver: false, replyEnabled: true,
                LocalSessionShape.Terminal, OrbPresence.Present));
    }

    // No pane beats replying-off, because it is the more specific answer: the
    // setting is not what is stopping this one.
    [Fact]
    public void NoPaneBeatsReplyingOff()
    {
        Assert.Equal("No terminal to type into",
            LocalCliChatSession.ComposerHintFor(
                canSendQuietly: false, canDeliver: false, replyEnabled: false,
                LocalSessionShape.Terminal, OrbPresence.Present));
    }

    // A background job has no pane and no terminal to be told to go to, which is
    // the point of it: a daemon runs it so that nothing has to hold it open. The
    // old wording sent the user to a window that does not exist; the box now names
    // what the button does, which is the same thing the orb's own click does. It
    // said "open the agents view" for an hour, between the roster becoming the
    // click default and live use reversing that — see ClickRouting.AgentsView.
    //
    // And it uses the daemon's own words for the state. "Needs input" is what
    // `claude agents` calls a blocked job, and several of them are literally
    // holding a question — so a box that said only "no pane" was hiding the more
    // interesting half of what was true.
    [Fact]
    public void ABackgroundJobIsToldWhereToAnswerItAndWhatItWants()
    {
        Assert.Equal("Needs input — attach to reply",
            LocalCliChatSession.ComposerHintFor(
                canSendQuietly: false, canDeliver: false, replyEnabled: true,
                LocalSessionShape.Background, OrbPresence.NeedsInput));

        // Still the more specific answer than replying-off, for the reason above.
        Assert.Equal("Needs input — attach to reply",
            LocalCliChatSession.ComposerHintFor(
                canSendQuietly: false, canDeliver: false, replyEnabled: false,
                LocalSessionShape.Background, OrbPresence.NeedsInput));
    }

    // A job that has finished is a third thing again, and nobody should be typing
    // at it: the box says so rather than inviting a reply that would go nowhere.
    [Fact]
    public void AFinishedJobSaysSoRatherThanInvitingAReply()
    {
        Assert.Equal("Finished — attach to read it",
            LocalCliChatSession.ComposerHintFor(
                canSendQuietly: false, canDeliver: false, replyEnabled: true,
                LocalSessionShape.Background, OrbPresence.Finished));
    }

    // A background job that is *working*, or one whose presence nothing has said
    // anything about: still nowhere to type, still the same answer, without
    // claiming a state it does not have.
    [Fact]
    public void AWorkingBackgroundJobIsOfferedAnAttachWithNoStateClaimed()
    {
        Assert.Equal("Attach to reply",
            LocalCliChatSession.ComposerHintFor(
                canSendQuietly: false, canDeliver: false, replyEnabled: true,
                LocalSessionShape.Background, OrbPresence.Present));
    }

    // An orphaned team member is dimmed like a parked job and is *not* one of
    // these: its status file carries a real tmux pane, so it can be typed into
    // exactly as before. Asserted so the shape argument does not quietly grow to
    // mean "dimmed".
    [Fact]
    public void ATeammateWithNoPaneKeepsTheOrdinaryWording()
    {
        Assert.Equal("No terminal to type into",
            LocalCliChatSession.ComposerHintFor(
                canSendQuietly: false, canDeliver: false, replyEnabled: true,
                LocalSessionShape.Teammate, OrbPresence.Parked));
    }

    [Fact]
    public void WithAPaneTheHintFollowsTheReplySetting()
    {
        foreach (var shape in new[]
                 {
                     LocalSessionShape.Terminal,
                     LocalSessionShape.Background,
                     LocalSessionShape.Teammate,
                 })
        {
            // A background job that somehow does have a pane — attached, and its
            // hook has since recorded one — is an ordinary session from here on.
            // canDeliver is asked but must lose to a real pane either way.
            Assert.Equal("Message…",
                LocalCliChatSession.ComposerHintFor(
                    canSendQuietly: true, canDeliver: true, replyEnabled: true, shape, OrbPresence.NeedsInput));
            Assert.Equal("Replying is off",
                LocalCliChatSession.ComposerHintFor(
                    canSendQuietly: true, canDeliver: false, replyEnabled: false, shape, OrbPresence.Present));
        }
    }

    // ---- CB-105: messaging a session that has no pane but a live registry ---

    // The new arm, ahead of every no-pane wording above: a background job or an
    // agent-mode direct child with a live registry entry has somewhere to go
    // even though there is no pane, and the hint has to say that rather than
    // "No terminal to type into" or the daemon-attach wording, neither of which
    // is true any more once delivery is possible.
    [Fact]
    public void ADeliverableSessionIsToldItCanBeMessaged()
    {
        Assert.Equal("Message it — it reads this at its next turn",
            LocalCliChatSession.ComposerHintFor(
                canSendQuietly: false, canDeliver: true, replyEnabled: true,
                LocalSessionShape.Terminal, OrbPresence.Present));

        // Still the answer for a background job — canDeliver outranks the
        // Background-specific wording below it, not just the ordinary one.
        Assert.Equal("Message it — it reads this at its next turn",
            LocalCliChatSession.ComposerHintFor(
                canSendQuietly: false, canDeliver: true, replyEnabled: true,
                LocalSessionShape.Background, OrbPresence.NeedsInput));

        // And beats replying-off, the same way every no-pane answer already does.
        Assert.Equal("Message it — it reads this at its next turn",
            LocalCliChatSession.ComposerHintFor(
                canSendQuietly: false, canDeliver: true, replyEnabled: false,
                LocalSessionShape.Terminal, OrbPresence.Present));
    }

    // A job that has already finished is the one exception: delivering to it
    // would land in a transcript nothing is going to read, so it keeps the
    // same "attach and read it" wording a finished job with no registry gets,
    // rather than inviting a message that goes nowhere.
    [Fact]
    public void ADeliverableSessionThatHasFinishedStillSaysToAttach()
    {
        Assert.Equal("Finished — attach to read it",
            LocalCliChatSession.ComposerHintFor(
                canSendQuietly: false, canDeliver: true, replyEnabled: true,
                LocalSessionShape.Background, OrbPresence.Finished));
    }

    // ---- the notes a refused send leaves -----------------------------------

    // Two different problems that both end in "nothing was typed", and the note
    // has to say which: a session outside tmux can still be replied to in its
    // own terminal, where a missing tmux binary cannot be worked around at all.
    // Telling someone to go to a terminal that isn't there is the failure this
    // distinction exists to avoid.
    [Fact]
    public void ASessionOutsideTmuxIsToldWhereItCanReply()
    {
        Assert.Contains("Reply in the terminal instead",
            LocalCliChatSession.NoPaneNote(new SessionStatus { Cli = "claude" }, LocalSessionShape.Terminal, onMacOS: true, onWindows: false));
        Assert.Contains("Reply in the terminal instead",
            LocalCliChatSession.NoPaneNote(new SessionStatus { Cli = "claude", TermProgram = "Ghostty" }, LocalSessionShape.Terminal, onMacOS: true, onWindows: false));
    }

    [Fact]
    public void AMachineWithNoTmuxIsToldThatInstead()
    {
        Assert.Equal("Couldn't find tmux to type with.",
            LocalCliChatSession.NoPaneNote(new SessionStatus { Cli = "claude", TmuxPane = "%12" }, LocalSessionShape.Terminal, onMacOS: true, onWindows: false));

        // The pane is what decides this one, not the shape: a machine with no
        // tmux binary cannot be worked around by attaching, and offering an
        // attach to someone whose tmux is missing would be a third wrong
        // answer — `claude attach` is placed in a tmux window.
        Assert.Equal("Couldn't find tmux to type with.",
            LocalCliChatSession.NoPaneNote(new SessionStatus { Cli = "claude", TmuxPane = "%12" }, LocalSessionShape.Background, onMacOS: true, onWindows: false));
    }

    // The note a background job's refused send leaves. The sentence it replaces
    // was actively wrong rather than merely unhelpful — it named a terminal that
    // does not exist — so this asserts both halves: that the old advice is gone,
    // and that the new advice points at somewhere that exists.
    [Fact]
    public void ABackgroundJobsRefusedSendPointsAtTheAttach()
    {
        var note = LocalCliChatSession.NoPaneNote(new SessionStatus { Cli = "claude" }, LocalSessionShape.Background, onMacOS: true, onWindows: false);

        Assert.Contains("Attach it", note);
        Assert.Contains("background job", note);
        Assert.DoesNotContain("Reply in the terminal instead", note);
    }

    // Every refusal in this app names the setting that would lift it. A note
    // that says only "no" is a dead end for whoever reads it.
    [Fact]
    public void EveryRefusalNamesTheSettingThatWouldLiftIt()
    {
        Assert.Contains("Settings", LocalCliChatSession.ReplyingOffNote);
        Assert.Contains("Allow replying to sessions", LocalCliChatSession.ReplyingOffNote);

        Assert.Contains("Settings", RemoteControlChatSession.RemoteControlOffNote);
        Assert.Contains("Show sessions from other machines",
            RemoteControlChatSession.RemoteControlOffNote);
    }

    // ---- CB-105: what the panel says once a delivery attempt has been made ---

    // Mid-turn is worth saying separately from an ordinary accept: "handed to
    // X" alone reads as done, and it is not — Claude Code queues it behind the
    // running turn rather than answering it now.
    [Fact]
    public void AnAcceptWhileWorkingSaysItWillBeReadWhenTheTurnEnds()
    {
        var note = LocalCliChatSession.DeliveryNote(
            new DeliveryReceipt(DeliveryResult.Accepted, "working"), "job-hunter");

        Assert.Contains("job-hunter", note);
        Assert.Contains("mid-turn", note);
    }

    // An ordinary accept — not working, not null — names the session and says
    // slash commands will not run, since a delivered message is never typed.
    [Theory]
    [InlineData("idle")]
    [InlineData(null)]
    public void AnOrdinaryAcceptNamesTheSessionAndDisclaimsSlashCommands(string? agentStatus)
    {
        var note = LocalCliChatSession.DeliveryNote(
            new DeliveryReceipt(DeliveryResult.Accepted, agentStatus), "job-hunter");

        Assert.Contains("job-hunter", note);
        Assert.Contains("slash commands", note);
        Assert.DoesNotContain("mid-turn", note);
    }

    [Fact]
    public void NoRegistryEntryPointsAtTheAttach()
    {
        var note = LocalCliChatSession.DeliveryNote(
            new DeliveryReceipt(DeliveryResult.NoRegistryEntry, null), "job-hunter");

        Assert.Contains("job-hunter", note);
        Assert.Contains("isn't registered", note);
        Assert.Contains("Attach it", note);
    }

    [Fact]
    public void AnUnsupportedProtocolSaysNothingWasSent()
    {
        var note = LocalCliChatSession.DeliveryNote(
            new DeliveryReceipt(DeliveryResult.UnsupportedProtocol, "idle"), "job-hunter");

        Assert.Contains("job-hunter", note);
        Assert.Contains("peer protocol", note);
        Assert.Contains("nothing was sent", note);
    }

    // SocketRefused and WriteFailed share one wildcard arm: WriteFailed is
    // never actually produced (see SessionMessenger's own doc comment on the
    // enum member), so there is nothing to distinguish them for even in
    // principle.
    //
    // Not a [Theory]: DeliveryResult is internal, and a public test method
    // cannot expose an internal type as one of its own parameters — xUnit
    // requires test methods to be public, so the two cases are two [Fact]s
    // instead of one parameterised test.
    [Fact]
    public void ARefusedSocketSaysNothingWasSent()
    {
        foreach (var result in new[] { DeliveryResult.SocketRefused, DeliveryResult.WriteFailed })
        {
            var note = LocalCliChatSession.DeliveryNote(new DeliveryReceipt(result, null), "job-hunter");

            Assert.Contains("refused the connection", note);
            Assert.Contains("nothing was sent", note);
        }
    }

    // Every arm but one names the session, which is the property that
    // actually matters for the four that do: this text is the only thing on
    // screen after a delivery did not land the way "sent" would imply.
    // SocketRefused is the deliberate exception — the socket refused to
    // connect at all, before anything session-specific was learned, so its
    // sentence is the generic one ARefusedSocketSaysNothingWasSent already
    // covers rather than a claim this method cannot back up.
    [Fact]
    public void EveryDeliveryNoteThatCanNameTheSessionDoes()
    {
        foreach (var (result, agentStatus) in new (DeliveryResult, string?)[]
                 {
                     (DeliveryResult.Accepted, "idle"),
                     (DeliveryResult.Accepted, "working"),
                     (DeliveryResult.NoRegistryEntry, null),
                     (DeliveryResult.UnsupportedProtocol, "idle"),
                 })
        {
            Assert.Contains("job-hunter",
                LocalCliChatSession.DeliveryNote(new DeliveryReceipt(result, agentStatus), "job-hunter"));
        }
    }

    // ---- CB-105: unwrapping a delivered message's own echo -------------------

    // What SessionMessageFrame.Wrap actually produces — no from-name, unlike
    // the relay tag BridgeProtocol.ParseInboundMessages reads — so this is the
    // shape DeliveredBody has to unwrap on its own.
    [Fact]
    public void ADeliveredMessageBodyIsUnwrapped()
    {
        var row = "<cross-session-message from=\"Claude Buddy on mini\" from-mode=\"prompting\">\n"
                  + "check the deploy\n</cross-session-message>";

        Assert.Equal("check the deploy", LocalCliChatSession.DeliveredBody(row));
    }

    // The shape BridgeProtocol.ParseInboundMessages reads — a from-name
    // attribute — parses fine here too: this reads the body regardless of
    // which attributes are present, unlike that parser which requires one.
    [Fact]
    public void ARowWithAFromNameAttributeStillUnwraps()
    {
        var row = "<cross-session-message from=\"bridge:abc\" from-name=\"job-hunter\" "
                  + "from-mode=\"prompting\">hello</cross-session-message>";

        Assert.Equal("hello", LocalCliChatSession.DeliveredBody(row));
    }

    [Fact]
    public void ARowWithNoTagIsNotABody()
    {
        Assert.Null(LocalCliChatSession.DeliveredBody("just an ordinary typed message"));
    }
}
