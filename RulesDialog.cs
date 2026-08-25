using System.Drawing;
using System.Windows.Forms;

namespace GmailTo;

/// <summary>
/// Lists the rules and lets them be deleted. Deliberately no add and no edit:
/// the picker already creates rules, and choosing again there overwrites one,
/// so building an editor here would be a second, competing authoring path for
/// the same thing.
///
/// Like everywhere else, removals are written to disk as they are made.
/// </summary>
internal sealed class RulesDialog : Form
{
    private readonly AppConfig _config;
    private readonly ListView _list = new();
    private readonly Button _remove = new();
    private readonly Button _close = new();
    private readonly Label _hint = new();

    public RulesDialog(AppConfig config)
    {
        _config = config;

        SuspendLayout();

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.MultiSelect = false;
        _list.Columns.Add("Applies to");
        _list.Columns.Add("Sends from");
        _list.SetBounds(12, 12, 420, 210);
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();

        _hint.Text = "Rules are created in the picker, using \"Remember\" when you send.";
        _hint.ForeColor = SystemColors.GrayText;
        _hint.AutoEllipsis = true;
        _hint.SetBounds(12, 230, 420, 18);

        _remove.Text = "Remove";
        _remove.SetBounds(444, 12, 90, 26);
        _remove.Click += OnRemove;

        _close.Text = "Close";
        _close.SetBounds(444, 222, 90, 26);
        _close.DialogResult = DialogResult.OK;

        ClientSize = new Size(546, 260);
        Controls.AddRange(new Control[] { _list, _hint, _remove, _close });

        Text = "Sender rules";
        AppIcon.Apply(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AcceptButton = _close;
        CancelButton = _close;

        ResumeLayout(performLayout: false);
        PerformLayout();

        Reload();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // ListView column widths are not covered by the form's auto-scaling.
        _list.Columns[0].Width = LogicalToDeviceUnits(250);
        _list.Columns[1].Width = LogicalToDeviceUnits(150);
    }

    private void Reload()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (Rule rule in _config.Rules)
        {
            string applies = rule.Kind == RuleKind.Domain
                ? $"anyone at {rule.Match}"
                : rule.Match;

            // Fall back to the raw address if the profile has since gone, so a
            // hand-edited config shows something rather than a blank cell.
            Profile? profile = _config.FindByAddress(rule.EmailAddress);
            string sender = profile is null ? $"{rule.EmailAddress} (missing)" : profile.Name;

            var item = new ListViewItem(applies) { Tag = rule };
            item.SubItems.Add(sender);
            _list.Items.Add(item);
        }
        _list.EndUpdate();

        UpdateButtons();
    }

    private void UpdateButtons() => _remove.Enabled = _list.SelectedItems.Count > 0;

    private void OnRemove(object? sender, EventArgs e)
    {
        if (_list.SelectedItems.Count == 0) return;
        if (_list.SelectedItems[0].Tag is not Rule rule) return;

        DialogResult answer = MessageBox.Show(this,
            $"Remove the rule for {rule.Match}?\r\n\r\n" +
            "Mail to it will go back to asking which profile to use.",
            "gmailto", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        _config.Rules.Remove(rule);
        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save {AppConfig.FilePath}:\r\n\r\n{ex.Message}",
                "gmailto", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        Reload();
    }
}
