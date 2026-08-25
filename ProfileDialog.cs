using System.Drawing;
using System.Windows.Forms;

namespace GmailTo;

/// <summary>Add/edit dialog for a single profile entry.</summary>
internal sealed class ProfileDialog : Form
{
    private const string SuggestedDomain = "gmail.com";

    /// <summary>Guards against the completion re-entering its own TextChanged.</summary>
    private bool _suggesting;

    /// <summary>True while the current edit is a Backspace or Delete.</summary>
    private bool _deleting;

    private readonly Label _nameLabel = new();
    private readonly TextBox _name = new();
    private readonly Label _emailLabel = new();
    private readonly TextBox _email = new();
    private readonly Button _ok = new();
    private readonly Button _cancel = new();

    public Profile Result { get; private set; } = new();

    public ProfileDialog(Profile? existing)
    {
        // 96 DPI units throughout; see the note in PickerForm about why the
        // scaling declaration has to sit inside SuspendLayout/ResumeLayout.
        SuspendLayout();

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        _nameLabel.Text = "Display name";
        _nameLabel.SetBounds(12, 12, 336, 18);

        _name.Text = existing?.Name ?? "";
        _name.SetBounds(12, 32, 336, 23);

        _emailLabel.Text = "Gmail address to send from";
        _emailLabel.SetBounds(12, 66, 336, 18);

        _email.Text = existing?.EmailAddress ?? "";
        _email.SetBounds(12, 86, 336, 23);

        // Wired after the initial text is set, so loading an existing profile
        // does not trigger a completion.
        _email.KeyDown += (_, e) => _deleting = e.KeyCode is Keys.Back or Keys.Delete;
        _email.TextChanged += (_, _) => SuggestDomain();

        _ok.Text = "OK";
        _ok.SetBounds(186, 122, 80, 27);
        _ok.DialogResult = DialogResult.OK;
        _ok.Click += OnOk;

        _cancel.Text = "Cancel";
        _cancel.SetBounds(272, 122, 80, 27);
        _cancel.DialogResult = DialogResult.Cancel;

        ClientSize = new Size(364, 161);
        Controls.AddRange(new Control[] { _nameLabel, _name, _emailLabel, _email, _ok, _cancel });

        Text = existing is null ? "Add profile" : "Edit profile";
        AppIcon.Apply(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AcceptButton = _ok;
        CancelButton = _cancel;

        ResumeLayout(performLayout: false);
        PerformLayout();
    }

    /// <summary>
    /// Fills in the rest of "gmail.com" as the user types the domain, leaving
    /// the added part selected so the next keystroke replaces it. Typing along
    /// with the suggestion keeps it (@g -> @g[mail.com]); typing anything else
    /// drops it and leaves what was typed.
    ///
    /// Only a guess at the common case: Workspace accounts live on their own
    /// domains, so this has to stay effortless to type straight past.
    /// </summary>
    private void SuggestDomain()
    {
        if (_suggesting) return;

        // Never re-suggest what the user is trying to erase, or backspace could
        // not get past the completion.
        if (_deleting)
        {
            _deleting = false;
            return;
        }

        string text = _email.Text;
        int at = text.IndexOf('@');
        if (at < 0) return;

        // Only when typing at the very end. Editing mid-string should not append.
        if (_email.SelectionStart != text.Length || _email.SelectionLength != 0) return;

        string typed = text.Substring(at + 1);
        if (typed.Length >= SuggestedDomain.Length) return;
        if (!SuggestedDomain.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return;

        string rest = SuggestedDomain.Substring(typed.Length);
        _suggesting = true;
        try
        {
            _email.Text = text + rest;
            _email.SelectionStart = text.Length;
            _email.SelectionLength = rest.Length;
        }
        finally
        {
            _suggesting = false;
        }
    }

    private void OnOk(object? sender, EventArgs e)
    {
        string name = _name.Text.Trim();
        if (name.Length == 0)
        {
            Reject("Give the profile a name.", _name);
            return;
        }

        // Only a sanity check. A wrong-but-plausible address means Gmail shows
        // its account chooser, which is a visible failure rather than a
        // silently wrong sender.
        string email = _email.Text.Trim();
        if (email.Length == 0)
        {
            Reject("Enter the Gmail address this profile will send from.", _email);
            return;
        }

        if (!email.Contains('@'))
        {
            Reject("That does not look like an email address.", _email);
            return;
        }

        Result = new Profile { Name = name, EmailAddress = email };
    }

    private void Reject(string message, Control focus)
    {
        MessageBox.Show(this, message, "gmailto", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        DialogResult = DialogResult.None;
        focus.Focus();
    }
}
