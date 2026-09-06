# Chatting with a local Claude Code session — findings

Everything here was measured against **Claude Code 2.1.233** on macOS 27.0,
tmux 3.x, with real transcripts (0.6 MB to 33 MB) and a live throwaway session
in its own tmux session, unless it says otherwise. Where something is assumed
rather than observed, it says so.

The short version:

1. **No sync is needed.** The transcript file *is* the conversation, and typing
   goes through the terminal's own input line, so the panel and the terminal are
   two views of one thing rather than two copies.
2. **The tail size that matters is bytes, not turns**, and the intuitive number
   is off by an order of magnitude — 64 KB of transcript can hold **one**
   displayable turn.
3. **A permission dialog looks nothing like a dialog.** No box, content below
   the options, and the only reliable way to tell it from a numbered list the
   assistant wrote in prose is that a real dialog *replaces the input box*.

---

## The transcript is the conversation

Claude Code writes an append-only JSONL transcript per session at
`~/.claude/projects/<escaped-cwd>/<session-id>.jsonl`. The hook already records
the path — `transcript_path` has been in `SessionStatus` all along, used only by
`TranscriptReader` for the speak button.

Rows land **during** a turn, not at the end of it. Measured on a live session:

| row | when written |
| --- | --- |
| `assistant` / `thinking` | when that thinking pass completes |
| `assistant` / `tool_use` | when the call is made |
| `user` / `tool_result` | when it returns |
| `assistant` / `text` | when the paragraph completes |

Consecutive timestamps within one turn were 1–7 s apart. So the panel updates a
block at a time and runs a few seconds behind the terminal's token streaming.
There is **no per-token record anywhere in the file** — a panel that wanted
word-by-word streaming could not have it from this source at any price.

### Row types actually present

Counted across four recent transcripts:

```
assistant 1195   user 651   attachment 625   mode 169   bridge-session 169
last-prompt 168  ai-title 168  custom-title 165  agent-name 165
agent-color 165  permission-mode 159  system 77  file-history-snapshot 48
queue-operation 42  file-history-delta 19  pr-link 4
```

Three matter. Everything else is bookkeeping and is skipped.

**`queue-operation` was the surprise.** A message sent while the session is busy
is recorded as `{"operation":"enqueue","content":"…"}` and later `dequeue`. So a
message sent from the panel mid-turn can be shown as *queued* rather than
appearing to have vanished — which is the difference between trusting the panel
and not.

`isSidechain: true` marks subagent rows. One team run produces thousands; they
are dropped, or the conversation is buried in them.

### Tail size — measured, and not what it looks like

The panel shows a dozen rows, so 64 KB of tail sounds generous. It isn't:
almost every byte of a transcript is tool results and file-history snapshots.

| transcript | size | turns in 64 KB | in 256 KB | in 512 KB | in 1 MB |
| --- | --- | --- | --- | --- | --- |
| A | 0.6 MB | 14 | 49 | 86 | 90 |
| B | 18.6 MB | 3 | 6 | 29 | 123 |
| C | 33.0 MB | 6 | 6 | 34 | 34 |
| D | 11.0 MB | **1** | 13 | 14 | 50 |
| E | 1.4 MB | 16 | 42 | 75 | 133 |
| F | 27.5 MB | 12 | 48 | 79 | 206 |

64 KB opens transcript D on a single line. **512 KB** is the smallest window
where every transcript measured had more than a screenful, and is what ships.
Paging back uses 1 MB, because a small page in a tool-heavy transcript can walk
hundreds of kilobytes and surface almost nothing.

A cheap substring pre-filter (`"type":"assistant"` and two others) avoids
building a `JsonDocument` for the snapshot rows, which are individually larger
than the whole conversation around them.

## Writing: tmux, and only tmux

`tmux send-keys` writes into a pane's input regardless of whether its window is
on screen, which is the entire reason the panel can exist without stealing
focus. The alternatives can't: `System Events keystroke` and Win32 `SendInput`
both type into whatever is *frontmost*, so they require raising the terminal
first. That is a fine trade for dictation and the wrong one for a chat panel, so
**non-tmux sessions are read-only** rather than focus-stealing.

Three measured details:

- **Multi-line needs the paste buffer, not `send-keys -l`.** A literal newline
  sent as a keystroke is indistinguishable from pressing Return, so a two-line
  message submits its first line and leaves the rest behind. `set-buffer` +
  `paste-buffer -p` wraps it in bracketed-paste markers and the TUI takes it as
  one message. **Verified**: a three-line message pasted as one unsubmitted
  block, then `send-keys Enter` submitted it and the session replied.
- `-p` is safe when the pane's application never requested bracketed paste —
  tmux then sends the text unwrapped, which for a single line is what
  `send-keys -l` would have done.
- The pane id in `SessionStatus.TmuxPane` matches `tmux list-panes` exactly, and
  Claude Code's own `~/.claude/sessions/<pid>.json` records the same pane.

### There is an IPC channel, and it is now used for exactly the case `tmux send-keys` cannot cover

Claude Code registers every running session in `~/.claude/sessions/<pid>.json`
with `messagingSocketPath: /tmp/cc-socks/<pid>.sock`, `peerProtocol: 1`, and a
sibling `.key` file. That is how one session's `SendMessage` reaches another.

This section used to end there, on the reasoning that `tmux send-keys` is a
public, stable interface that does the same job and there was no reason to
touch an undocumented, integer-versioned channel instead. **That reasoning
holds whenever a pane exists, and it is exactly false when one does not.** A
background job (`claude bg-...`) or an `--agent` direct child never has a
tmux pane to send keys into in the first place — there is nothing for the
public interface to do the same job *as*. CB-105 (`docs/headless-delivery-
findings.md`) adds this socket as the second delivery route, used only when
`TerminalTyping.ChannelFor` has already established there is no pane: the
protocol risk this section originally warned about (undocumented, versioned by
a bare integer) is unchanged and is why `SessionRegistry.Speaks` refuses
anything but `peerProtocol: 1` by name rather than guessing at a newer shape.
See that doc for what was actually confirmed live against Claude Code
`2.1.263`, including a receiver-side behaviour (a permission-mode gate that can
hold a delivery unattended) the original reverse-engineering did not surface.

## The permission dialog

The dialog is drawn by the TUI and **never reaches the transcript**. The hook
does report it — `Notification`/`permission_prompt` writes `state: waiting` —
but only that it exists, not what it says. So the wording is read off the pane
with `tmux capture-pane -p`, which returns the rendered screen as plain text
(no escape sequences without `-e`).

The first parser was written against an invented fixture: a box-drawn dialog
with the question directly above the options and nothing below them. **It failed
on every real dialog.** What a real one looks like, transcribed from a live
capture:

```
 Bash command

   pid=$$; for i in 1 2 3 4 5 6 7 8; do line=$(ps -o pid=,ppid= -p $pid)
   Walk the process ancestry of this shell

 Contains simple_expansion

 Do you want to proceed?
 ❯ 1. Yes
   2. No

 Esc to cancel · Tab to amend · ctrl+e to explain
```

Three ways the guess was wrong:

1. **There is no box.** A horizontal rule above, and no frame at all. The parser
   stripping box edges stopped dead on the `╰────╯` it expected.
2. **Things come after the options** — a hint footer, and in a plan prompt an
   indented continuation under the last option:

   ```
    ❯ 1. Yes, and use auto mode
      2. Yes, manually approve edits
      3. Tell Claude what to change
         shift+tab to approve with this feedback

    ctrl+g to edit in nano · ~/.claude/plans/….md
   ```

   So the options cannot be found by reading up from the last non-blank line.
   They have to be searched for.
3. **The dialog replaces the input box.** This is the useful one. The assistant
   writes numbered lists in prose constantly, and the only reliable difference
   is that prose has the input box — and its two full-width horizontal rules —
   drawn below it, while a dialog has nothing below but a hint. Refusing to
   parse when a horizontal rule follows the options is the whole defence against
   pressing a key to answer a question nobody asked.

The parser also refuses anything not numbered exactly 1..n, and any run shorter
than two. Refusing means the panel offers "answer in the terminal", which is the
honest answer when the screen can't be read.

**Verified end to end** against a live session: a real Bash approval parsed to
`Do you want to proceed?` / `[1] Yes` / `[2] No`, and `send-keys 2` answered it
— the dialog closed and nothing was typed into the input box. Sending the same
digit when no dialog is up types a literal "2" instead, which is why the outer
guard is `state == waiting` and not the parse alone.

`dotnet run --project tests/TranscriptTests -- <file>` runs either parser
against a real capture or a real transcript, which is how the fixtures in that
suite were confirmed rather than composed.

## Still unknown

- **Windows.** All of the above is tmux, so the chat panel is macOS/Linux in
  practice. A Windows session gets the read-only panel; nothing was tested
  there.
- **Elicitation dialogs.** The hook treats `elicitation_dialog` as `waiting`
  too, and those were never seen during this work. If one is not a 1..n numbered
  list, it falls back to the terminal button, which is the intended behaviour
  rather than a gap — but it is untested.
- **A transcript rewritten in place.** `/clear` starts a new file, which is
  handled (a shorter file than the read offset restarts the reader). Whether
  anything else rewrites history mid-session was not established.
- **Very old status files.** A session whose hook predates `transcript_path`
  falls back to `TranscriptReader.FindTranscriptFor`, which was already in the
  codebase for the speak button. Not re-tested here.
