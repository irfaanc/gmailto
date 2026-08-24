using System.Diagnostics;
using System.Reflection;
using System.Drawing;
using System.Windows.Forms;

namespace GmailTo;

/// <summary>
/// Account list editor, shown when the app starts with no mailto: argument.
///
/// Every change is written to disk as it is made. There is no Save button and
/// no working copy: the list on screen is the config. Rules were already saved
/// this way, and the split was worse than either choice on its own, since
/// removing an account deleted its rules from memory and only persisted them if
/// Save happened to be pressed afterwards.
///
/// What guards against mistakes is confirmation, not a pending buffer: removing
/// an account asks first and says what else goes with it, and everything else
/// is trivially redone.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly AppConfig _config;
    private readonly ListView _list = new();

    private readonly Button _add = new();
    private readonly Button _edit = new();
    private readonly Button _remove = new();
    private readonly Button _up = new();
    private readonly Button _down = new();
    private readonly Button _rules = new();
    private readonly Button _handler = new();
    private readonly Button _close = new();
    private readonly Label _status = new();
    private readonly LinkLabel _about = new();

    private readonly RegistrationStatus _registrationStatus;
    private readonly string? _registrationError;

    private List<Account> Accounts => _config.Accounts;

    public SettingsForm(AppConfig config, RegistrationStatus status, string? registrationError)
    {
        _config = config;
        _registrationStatus = status;
        _registrationError = registrationError;

        // 96 DPI units throughout; see the note in PickerForm about why the
        // scaling declaration has to sit inside SuspendLayout/ResumeLayout.
        SuspendLayout();

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.MultiSelect = false;
        _list.Columns.Add("Display name");
        _list.Columns.Add("Gmail address");
        _list.SetBounds(12, 12, 340, 252);
        _list.DoubleClick += (_, _) => EditSelected();
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();

        Place(_add, "Add...", 364, 12, 140, 26, OnAdd);
        Place(_edit, "Edit...", 364, 44, 140, 26, (_, _) => EditSelected());
        Place(_remove, "Remove", 364, 76, 140, 26, OnRemove);
        Place(_up, "Move up", 364, 116, 140, 26, (_, _) => MoveSelected(-1));
        Place(_down, "Move down", 364, 148, 140, 26, (_, _) => MoveSelected(1));

        // Set apart from the account buttons: it acts on rules, not accounts.
        Place(_rules, "Rules...", 364, 206, 140, 26, OnRules);

        _status.AutoEllipsis = true;
        _status.SetBounds(12, 272, 492, 18);

        // One control for one question: is this the mail handler or not. Its
        // label carries the answer, so there is no second button to reason about
        // and no state to infer from which of a pair looks enabled.
        Place(_handler, "", 12, 300, 220, 28, OnToggleHandler);

        _close.Text = "Close";
        _close.SetBounds(429, 300, 75, 28);
        _close.DialogResult = DialogResult.OK;

        // Sits in the gap that was already there between the handler toggle and
        // Close. Worth carrying in the window rather than only in the README:
        // the app installs itself somewhere the user did not pick, so a year on
        // the exe may be the only trace of it they can find, and this is the
        // only route from there back to the source, to updates, or to somewhere
        // to report a fault.
        _about.Text = AboutText();
        _about.AutoSize = true;
        _about.SetBounds(248, 306, 0, 0);
        _about.LinkClicked += (_, _) => OpenProjectPage();

        ClientSize = new Size(516, 340);
        Controls.AddRange(new Control[]
        {
            _list, _add, _edit, _remove, _up, _down, _rules,
            _status, _about, _handler, _close,
        });

        Text = "gmailto settings";
        AppIcon.Apply(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        AcceptButton = _close;
        CancelButton = _close;

        ResumeLayout(performLayout: false);
        PerformLayout();

        ShowStatus();
        Reload(0);
    }

    /// <summary>
    /// Writes the config out. Called after every change rather than at the end,
    /// so a failure is reported next to the change that caused it.
    ///
    /// A failed write leaves the screen ahead of the file. It is not rolled
    /// back, because the config is written whole and the next successful change
    /// will carry everything anyway.
    /// </summary>
    private void Persist()
    {
        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Could not save {AppConfig.FilePath}:\r\n\r\n{ex.Message}\r\n\r\n" +
                "The change is shown here but is not on disk yet.",
                "gmailto", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Reports whether Windows is actually routing mail links here, which is the
    /// state the user cares about. Being registered only makes the app
    /// selectable, so saying "registered" while every mailto link goes elsewhere
    /// would be technically true and completely useless.
    /// </summary>
    private void ShowStatus()
    {
        _handler.Text = Registration.IsEffectiveHandler()
            ? "Stop handling mail links"
            : "Set as default mail handler...";

        if (_registrationStatus == RegistrationStatus.Failed)
        {
            Set("Could not write the registry entries. " + _registrationError, Color.Firebrick);
            return;
        }

        if (!Registration.IsEffectiveHandler())
        {
            // Turned off on purpose is a settled state, not a problem to flag.
            Set(_config.StoppedHandling
                    ? "Not handling mail links."
                    : "Windows is sending mail links elsewhere.",
                _config.StoppedHandling ? SystemColors.GrayText : Color.Firebrick);
            return;
        }

        string note = _registrationStatus switch
        {
            RegistrationStatus.Created => " Registry entries were just created.",
            RegistrationStatus.Repaired => " The stored path was stale and now points at this copy.",
            RegistrationStatus.TakenOver =>
                $" This copy has taken over from {Registration.PreviousExePath}, which can now be deleted.",
            _ => "",
        };

        // An install is the more useful thing to report when it happens: it is
        // the surprising event, and it explains why the registered path is not
        // the copy the user just double-clicked.
        if (SelfInstall.JustInstalled)
            note = $" Installed to {SelfInstall.InstalledDirectory}.";

        Set("Handling mail links." + note, SystemColors.GrayText);

        void Set(string text, Color colour)
        {
            _status.Text = text;
            _status.ForeColor = colour;
        }
    }

    /// <summary>Where this came from, for anyone who finds the exe and nothing else.</summary>
    private const string ProjectUrl = "https://github.com/irfaanc/gmailto";

    /// <summary>
    /// Name and version for the link. The informational version carries the
    /// commit as "1.0.0+&lt;sha&gt;", which belongs in the file properties rather
    /// than in a window, so only the number is shown.
    /// </summary>
    private static string AboutText()
    {
        string? version = typeof(SettingsForm).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        int plus = version?.IndexOf('+') ?? -1;
        if (plus >= 0) version = version!.Substring(0, plus);

        return string.IsNullOrEmpty(version)
            ? Registration.DisplayName
            : $"{Registration.DisplayName} {version}";
    }

    private static void OpenProjectPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true });
        }
        catch
        {
            // A convenience link that cannot open is not worth interrupting a
            // settings window over.
        }
    }

    private static void Place(Button button, string text, int x, int y, int width, int height, EventHandler onClick)
    {
        button.Text = text;
        button.SetBounds(x, y, width, height);
        button.Click += onClick;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ScaleColumns();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ScaleColumns();
    }

    /// <summary>
    /// ListView column widths are one of the properties auto-scaling does not
    /// touch, so they stay at their literal pixel value unless set by hand.
    /// </summary>
    private void ScaleColumns()
    {
        _list.Columns[0].Width = LogicalToDeviceUnits(135);
        _list.Columns[1].Width = LogicalToDeviceUnits(190);
    }

    private void Reload(int selectIndex)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (Account account in Accounts)
        {
            var item = new ListViewItem(account.Name);
            item.SubItems.Add(account.EmailAddress);
            _list.Items.Add(item);
        }
        _list.EndUpdate();

        if (Accounts.Count > 0)
        {
            int index = Compat.Clamp(selectIndex, 0, Accounts.Count - 1);
            _list.Items[index].Selected = true;
            _list.Items[index].Focused = true;
        }
        UpdateButtons();
    }

    private int SelectedIndex => _list.SelectedIndices.Count == 0 ? -1 : _list.SelectedIndices[0];

    private void UpdateButtons()
    {
        int index = SelectedIndex;
        _edit.Enabled = _remove.Enabled = index >= 0;
        _up.Enabled = index > 0;
        _down.Enabled = index >= 0 && index < Accounts.Count - 1;
    }

    private void OnAdd(object? sender, EventArgs e)
    {
        using var dialog = new AccountDialog(null);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        Accounts.Add(dialog.Result);
        Persist();
        Reload(Accounts.Count - 1);
    }

    private void EditSelected()
    {
        int index = SelectedIndex;
        if (index < 0) return;

        using var dialog = new AccountDialog(Accounts[index]);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        // Rules point at accounts by address, so changing an account's address
        // would otherwise orphan every rule aiming at it.
        string was = Accounts[index].EmailAddress;
        string now = dialog.Result.EmailAddress;
        if (!string.Equals(was, now, StringComparison.OrdinalIgnoreCase))
        {
            foreach (Rule rule in _config.Rules)
            {
                if (string.Equals(rule.EmailAddress, was, StringComparison.OrdinalIgnoreCase))
                    rule.EmailAddress = now;
            }
        }

        Accounts[index] = dialog.Result;
        Persist();
        Reload(index);
    }

    private void OnRemove(object? sender, EventArgs e)
    {
        int index = SelectedIndex;
        if (index < 0) return;

        if (Accounts.Count == 1)
        {
            MessageBox.Show(this,
                "This is the only account. Add another before removing this one.",
                "gmailto", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // A rule aiming at a removed account would sit there matching recipients
        // and resolving to nothing, so say what else is going and take it too.
        string address = Accounts[index].EmailAddress;
        int rules = _config.Rules.Count(r =>
            string.Equals(r.EmailAddress, address, StringComparison.OrdinalIgnoreCase));

        string question = rules == 0
            ? $"Remove \"{Accounts[index].Name}\"?"
            : $"Remove \"{Accounts[index].Name}\"?\r\n\r\n" +
              $"{rules} rule{(rules == 1 ? "" : "s")} pointing at it will be removed too.";

        DialogResult answer = MessageBox.Show(this, question, "gmailto",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        _config.Rules.RemoveAll(r =>
            string.Equals(r.EmailAddress, address, StringComparison.OrdinalIgnoreCase));
        Accounts.RemoveAt(index);
        Persist();
        Reload(index);
    }

    private void MoveSelected(int delta)
    {
        int index = SelectedIndex;
        int target = index + delta;
        if (index < 0 || target < 0 || target >= Accounts.Count) return;

        (Accounts[index], Accounts[target]) = (Accounts[target], Accounts[index]);
        Persist();
        Reload(target);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Both prompts live here rather than before the window opens, so they
        // have an owner and the user can see what they refer to behind them.
        if (!EnsureFirstAccount())
        {
            DialogResult = DialogResult.Cancel;   // closes a modal form
            return;
        }

        RegistrationPrompt.ReportIfFailed(this, _registrationStatus, _registrationError);

        // Declining does not close the window: the user may well have opened
        // settings to manage accounts, and throwing them out over an unrelated
        // question would make that impossible. Never asked of someone who
        // turned it off on purpose.
        if (!_config.StoppedHandling) RegistrationPrompt.EnsureDefaultHandler(this);
        ShowStatus();
    }

    /// <summary>
    /// Demands an account before anything else. Without one there is nowhere to
    /// send mail, so an empty list is not a state worth letting the user sit in.
    /// </summary>
    /// <returns>False if the user declined, in which case there is nothing to do.</returns>
    private bool EnsureFirstAccount()
    {
        if (Accounts.Count > 0) return true;

        using (var dialog = new AccountDialog(null))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                MessageBox.Show(this,
                    "At least one Gmail account is needed before this can send " +
                    "anything.\r\n\r\nRun it again when you are ready to add one.",
                    "gmailto", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            Accounts.Add(dialog.Result);
        }

        Persist();
        Reload(0);
        return true;
    }

    private void OnRules(object? sender, EventArgs e)
    {
        using var dialog = new RulesDialog(_config);
        dialog.ShowDialog(this);
    }

    private void OnToggleHandler(object? sender, EventArgs e)
    {
        if (Registration.IsEffectiveHandler()) StopHandling();
        else StartHandling();

        ShowStatus();
    }

    private void StartHandling()
    {
        // Clear the opt-out first, or the registration this needs would be
        // skipped as deliberately unwanted.
        _config.StoppedHandling = false;
        Persist();

        Registration.Prepare(out _);

        // Claim rather than EnsureDefaultHandler: the button's label already
        // asked the question, so a confirmation here would just repeat it.
        RegistrationPrompt.Claim(this);
    }

    private void StopHandling()
    {
        DialogResult answer = MessageBox.Show(this,
            "Stop gmailto handling mail links?",
            "gmailto", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        if (!Registration.TryStopHandling(out string? error))
        {
            MessageBox.Show(this, "Could not stop handling mail links:\r\n\r\n" + error,
                "gmailto", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Recorded, or registering on the next launch would take the association
        // straight back.
        _config.StoppedHandling = true;
        Persist();

        // No confirmation dialog: the button flips to "Set as default mail
        // handler" and the status line says so, which is the same news without
        // something to dismiss.
    }
}
