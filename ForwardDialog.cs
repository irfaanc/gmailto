using System.Drawing;
using System.Windows.Forms;

namespace GmailTo;

/// <summary>
/// Explains the last automatic forward and offers to undo it: send the same
/// message again from a different profile, or remove the rule that caused it.
///
/// Opening this counts as having seen the explanation, so closing it clears the
/// record and the stored message. The rule itself stays in the rules list, which
/// is the durable way to find and remove one later.
/// </summary>
internal sealed class ForwardDialog : Form
{
    private readonly AppConfig _config;
    private readonly ForwardRecord _record;
    private readonly string? _retryUri;

    private readonly Label _what = new();
    private readonly Label _when = new();
    private readonly Button _resend = new();
    private readonly Button _deleteRule = new();
    private readonly Button _close = new();

    public ForwardDialog(AppConfig config, ForwardRecord record, string? retryUri)
    {
        _config = config;
        _record = record;
        _retryUri = retryUri;

        SuspendLayout();

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        Profile? profile = config.FindByAddress(record.SentFrom);
        string sender = profile?.Name ?? record.SentFrom;

        _what.Text =
            $"A mail to {record.Recipient} was automatically handled by {sender}, " +
            $"because of your rule for {record.MatchedRule}.";
        _what.SetBounds(12, 12, 400, 56);

        _when.Text = record.When == default
            ? ""
            : record.When.ToLocalTime().ToString("dddd d MMMM, HH:mm");
        _when.ForeColor = SystemColors.GrayText;
        _when.SetBounds(12, 72, 400, 18);

        _resend.Text = "Send again from...";
        _resend.SetBounds(12, 102, 150, 28);
        _resend.Enabled = _retryUri is not null;
        _resend.Click += OnResend;

        _deleteRule.Text = "Remove this rule";
        _deleteRule.SetBounds(170, 102, 140, 28);
        _deleteRule.Click += OnDeleteRule;

        _close.Text = "Close";
        _close.SetBounds(337, 102, 75, 28);
        _close.DialogResult = DialogResult.OK;

        ClientSize = new Size(424, 142);
        Controls.AddRange(new Control[] { _what, _when, _resend, _deleteRule, _close });

        Text = "gmailto";
        AppIcon.Apply(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        AcceptButton = _close;
        CancelButton = _close;

        ResumeLayout(performLayout: false);
        PerformLayout();
    }

    private void OnResend(object? sender, EventArgs e)
    {
        if (_retryUri is null) return;

        MailtoRequest request;
        try
        {
            request = MailtoRequest.Parse(_retryUri);
        }
        catch (FormatException ex)
        {
            MessageBox.Show(this, "The saved message could not be read:\r\n\r\n" + ex.Message,
                "gmailto", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Always the picker here, never the rule: the rule is what went wrong.
        using var picker = new PickerForm(_config, request);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.SelectedProfile is null) return;

        if (!Mail.Open(request, picker.SelectedProfile, this)) return;

        if (picker.RememberAs is RuleKind kind)
        {
            _config.SetRule(kind, picker.RememberMatch, picker.SelectedProfile.EmailAddress);
            TrySave();
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private void OnDeleteRule(object? sender, EventArgs e)
    {
        int removed = _config.Rules.RemoveAll(r =>
            string.Equals(r.Match, _record.MatchedRule, StringComparison.OrdinalIgnoreCase));

        if (removed == 0)
        {
            MessageBox.Show(this, "This rule was already removed.", "gmailto",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            TrySave();
            MessageBox.Show(this,
                $"Removed. Next time, mail to {_record.MatchedRule} will ask which profile to use.",
                "gmailto", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private void TrySave()
    {
        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save {AppConfig.FilePath}:\r\n\r\n{ex.Message}",
                "gmailto", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
