# Platform notes

Things learned about Windows, WinForms and Gmail while building this, with how
each was checked. Several are undocumented or contradict the obvious reading of
the documentation, and most cost an hour to establish, so they are written down
rather than rediscovered.

Nothing here is about *this app's* design choices. Those are in
[DESIGN.md](DESIGN.md). How to use the app is in [README.md](README.md).

Everything was observed on **Windows 11, .NET 8, a 144 DPI display**, in August
2026. Where a claim is likely to be version-specific it says so.

---

## Default handlers

### UserChoice cannot be forged, but it can be deleted

`HKCU\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\mailto\UserChoice`
decides which app actually receives mail links. It carries a `Hash` value derived
from the account SID, the protocol and the ProgID by an undocumented algorithm.
Windows discards any entry whose hash does not match, so an app cannot point the
key at itself.

Deleting the key is a different matter, and was not blocked:

```
attempt 1: set the ProgId value   -> write SUCCEEDED
attempt 2: delete the UserChoice key -> delete SUCCEEDED
```

With no recorded choice, Windows falls through to `HKCU\Software\Classes\mailto`.
Verified end to end: after deleting the key, invoking a real `mailto:` link
launched this app.

### UCPD was running and did not cover mailto

The User Choice Protection Driver is the thing that blocks those writes, and it
was **active** on the development machine:

```
UCPD service : Running / System
UCPD.sys present : True
```

Yet the write and the delete above both succeeded. So UCPD being installed says
nothing about whether a given protocol is protected; its coverage is per-protocol
and varies by build. It is usually discussed around `http`/`https`.

**Consequence for the code:** never infer success from the delete not throwing.
`TryClaimDefault` re-reads the registry afterwards and reports what it finds.

### SHOpenWithDialog no longer shows a picker

The obvious way to ask Windows for the "How do you want to open this?" chooser is
`SHOpenWithDialog` with `OAIF_URL_PROTOCOL`. On Windows 11 it returns a dialog
containing only:

> To change your default apps, go to Settings > Apps > Default apps.

with an OK button. Tested with a URL protocol *and* with a plain unclaimed file
extension — same message both times, so this is the API being retired rather than
protocols being special-cased. There is no programmatic route to that chooser.

### Nothing prompts, because the association is uncontested

Most file associations never involve a dialog. If no `UserChoice` exists, an
installer writing `HKCU\Software\Classes\<type>` simply takes effect. The chooser
appears when Explorer opens something unclaimed, and that flow still works — it
just cannot be summoned by an app. So "I have lots of associations and never saw
this screen" and "an app cannot set a default" are both true at once.

### ms-settings deep links ignore registeredAppUser

`ms-settings:defaultapps?registeredAppUser=GmailTo` lands on the generic
Default apps page, identical to plain `ms-settings:defaultapps`. The parameter is
accepted and ignored — no error page, no benefit.

### The Default apps page has two lists and the obvious one is wrong

Searching the **application** list ("Set defaults for applications") for a
registered Win32 app did not find it. The route that works is the box at the top,
*"Set a default for a file type or link type"*: typing `MAILTO` there produces a
`MAILTO, Choose a default` row, and that chooser does list the app.

The app's own registration shape was confirmed against **SumatraPDF**, a Win32
app that does appear in Default apps. It uses the same
`HKCU\Software\<App>\Capabilities` + `RegisteredApplications` pattern with no
`Classes\Applications` entry, no `Uninstall` entry and no App Paths — so no extra
installation metadata is needed.

---

## Gmail

### authuser selects the account; /mail/u/<address>/ does not

```
https://mail.google.com/mail/u/you@gmail.com/?to=...   -> Temporary Error (404)
https://mail.google.com/mail/u/0/?authuser=you@gmail.com&to=... -> correct account
```

Confirmed against a *different* account than the one at `u/0`, so the result is
not `u/0` answering by accident. Percent-encoding the address (`%40`) works
identically to a literal `@`.

`authuser` overrides the path segment, which is why the URL pins the path to
`u/0` — the one slot guaranteed to exist whenever anyone is signed in.

The 404 page claims a temporary fault. It is not: loading
`https://mail.google.com/mail/u/0/` at the same moment worked, so the error was
the URL form being rejected.

### The /mail/u/N/ index is a position, not an identity

`N` indexes the accounts signed into that browser profile, ordered by sign-in
sequence. Signing an account in or out renumbers the rest, and the same `N` means
different mailboxes in different browsers or profiles. Storing it is storing a
pointer into someone else's mutable list.

### Gmail's own mailto handler composes from slot 0, silently

Chrome and Edge let Gmail register itself as the `mailto` handler, via
`navigator.registerProtocolHandler("mailto",
"https://mail.google.com/mail/?extsrc=mailto&url=%s", "Gmail")`. The template
carries no account selector at all: the only variable is the mailto link, and
there is one registration per browser profile rather than one per account.

Measured 2026-08-18 with two accounts signed in, `u/0` and `u/1` confirmed
beforehand as different mailboxes. Loading the handler URL redirected to:

```
https://mail.google.com/mail/u/0/?fs=1&tf=cm&source=mailto&su=...&to=...
```

It composed straight from `u/0` with no account chooser. So Gmail's own handler
resolves through the positional index described above, with all of that index's
problems, and the user has no per-message say.

This is the closest thing to a built-in alternative to this app, and it is worth
being precise about the difference rather than dismissive: for a single-account
user it works fine and needs no software. What it cannot do is name a mailbox,
which is the whole reason `authuser` is used here.

---

## WinForms

### Assigning AutoScaleMode runs the scaling pass immediately

This one is worth the space, because the failure is silent and the code looks
correct.

Six variants of a hand-built form, all at 144 DPI, all designed at 300x100 with a
100x25 button:

| declaration | result |
| --- | --- |
| `AutoScaleMode` only, no dimensions | **no scaling** (300x100) |
| dimensions then mode, before controls | **no scaling** |
| mode then dimensions, before controls | **no scaling** |
| explicit `PerformAutoScale()` in `OnLoad` | **no scaling** |
| declared inside `SuspendLayout`/`ResumeLayout` | 429x167 |
| declared *after* the controls and `ClientSize` | 429x167 |

Assigning `AutoScaleMode` triggers the scaling pass **there and then**. Declaring
it first, on a form with no children and a default `ClientSize`, scales an empty
form and then stamps `AutoScaleDimensions` to the current value — so everything
added afterwards is treated as already scaled and nothing moves. Fonts still grow
with DPI, so the result is correct text in cramped, clipped containers.

`SuspendLayout` matters because it *defers* that pass, not because the designer
happens to emit it.

### AutoScaleMode.Font needs a measured baseline

The same test with `Font` mode and the standard `(7F, 15F)` scaled the axes by
**1.43x horizontally and 1.67x vertically** — a visibly skewed form. The designer
normally *measures* that baseline on the machine that generated the file. With no
designer there is nothing to measure, and an invented baseline is wrong in one
axis or both. `Dpi` mode with `(96F, 96F)` is exact by definition and scaled
uniformly.

### What auto-scaling does not touch

Only `Location`, `Size` and `Font`. Confirmed missing: `ListView` column widths,
and anything whose correctness depends on a measured value like
`ListBox.ItemHeight` (a scaled pixel height drifts out of step with the row
height, which silently collapses a list to one scrolled row).

### ListBox drops a SelectedIndex set before the handle exists

Setting `SelectedIndex` in the constructor is discarded. It has to be applied in
`OnLoad` or later.

### ListBox items do not expose bounds to UI Automation

`AutomationElement.BoundingRectangle` on a WinForms `ListBox` item returns `NaN`.
Use the parent list's rectangle, or `ItemHeight`, to work out row positions.

### Removing a lambda with -= removes nothing

`_timer.Tick -= (_, _) => Handler();` silently does nothing: each lambda is a
distinct delegate instance. Caused a fade timer to keep both handlers. Use a
single handler that branches on state.

### A single-file app has no Assembly.Location

It returns an empty string. Flagged by the compiler as **IL3000**. Use
`AppContext.BaseDirectory`, or `Environment.ProcessPath` for the executable
itself.

### A WinExe missing its .dll fails invisibly

Copying only `GmailTo.exe` out of a framework-dependent publish gives a
launcher with nothing to launch. It exits `0x8000809A` with no window and no
message, because the error goes to stderr and a `WinExe` has no console. The
Event Log has it:

> The application to execute does not exist: `...\GmailTo.dll`

This is why the project publishes as a single file.

### PerMonitorV2 on .NET Framework needs an app.config, and half of it is worse than none

`Application.SetHighDpiMode` and `HighDpiMode` do not exist on .NET Framework;
they arrived with .NET Core 3.0. The supported route is the
`System.Windows.Forms.ApplicationConfigurationSection` in an app.config, which
ships as a second file beside the exe.

The obvious dodge is to declare PerMonitorV2 in the manifest instead, since a
manifest is compiled into the exe. **It does not work, and it fails in the
expensive direction.** Measured 2026-08-24 on a 150% primary and a 175%
secondary, moving one window between them:

| | DPI reported | window size |
|---|---|---|
| on the 150% monitor | 144 | 514x237 |
| on the 175% monitor | 168 | 512x236 |

The manifest does take effect: the window reports `PER_MONITOR_AWARE`, and
Windows updates its DPI on the move. What does not happen is the re-layout.
WinForms keeps the old pixel size and draws text at the new scale, so labels
overflow their controls. "Remember:" rendered as "Remembe", the drop-down
overlapped it, and the hint line was clipped at both edges.

Being per-monitor aware tells Windows not to bitmap-scale the window, so the
usual safety net is gone and nothing else takes over. Plain `<dpiAware>true</dpiAware>`
is the honest setting without the config file: blurry on a
different-DPI monitor, but never the wrong size or clipped.

---

## Testing UI from a script

Notes for anyone automating this app again; several plausible approaches do not
work.

- **Synthetic mouse input is unreliable from a non-DPI-aware host.** `SetCursorPos`
  plus `mouse_event` at UIA-reported coordinates lands in the wrong place, because
  the coordinates the automating process sees are not the target's physical
  pixels.
- **Posting `WM_LBUTTONDOWN` to a WinForms `Button` does not click it.**
  `ButtonBase` verifies the press with `WindowFromPoint` against real screen
  coordinates. Posting to a `ListBox` *does* work, since it hit-tests the message
  coordinates.
- **`InvokePattern.Invoke` on anything that opens a modal times out**, and blocks
  the whole UIA connection while it does, so subsequent `FromHandle` calls fail
  too. The action usually still happens — check by other means rather than
  trusting the exception.
- **`TreeScope.Children` from the root does not enumerate owned dialogs.** An
  owned modal is invisible to it while being plainly on screen. Find windows by
  handle (`EnumWindows` + `GetWindowText`) and use `AutomationElement.FromHandle`.
- **`MessageBox` responds to `WM_COMMAND`** with `IDYES` (6), `IDNO` (7) or
  `IDOK` (1), which is the reliable way to answer one.
- **Kill stray processes between runs.** A leftover window from a previous test
  sits at the same screen position as the next one and quietly absorbs input,
  which produced one false failure during development.
