using System.Drawing;
using System.Windows.Forms;

namespace GmailTo;

/// <summary>
/// A ListBox that reports when the user clicks the row that was already
/// selected. Intercepting WM_LBUTTONDOWN is the only way to see the selection
/// as it was *before* the click landed.
/// </summary>
internal sealed class AccountListBox : ListBox
{
    private const int WM_LBUTTONDOWN = 0x0201;

    public event EventHandler? SelectedItemClicked;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg != WM_LBUTTONDOWN)
        {
            base.WndProc(ref m);
            return;
        }

        int lParam = unchecked((int)(long)m.LParam);
        var point = new Point((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
        int clicked = IndexFromPoint(point);
        int selectedBefore = SelectedIndex;

        base.WndProc(ref m);

        if (clicked >= 0 && clicked == selectedBefore && SelectedIndex == clicked)
            SelectedItemClicked?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// The account chooser shown when a mailto: link is opened. Enter, or clicking
/// the highlighted row, sends; Esc cancels.
/// </summary>
internal sealed class PickerForm : Form
{
    private const int MaxVisibleRows = 10;

    private readonly AccountListBox _list = new();
    private readonly Label _recipient = new();
    private readonly Label _reason = new();
    private readonly Label _rememberLabel = new();
    private readonly ComboBox _remember = new();
    private readonly Label _hint = new();

    private readonly int _initialIndex;

    /// <summary>Options offered by the remember box, parallel to its items.</summary>
    private readonly List<(string Label, RuleKind? Kind, string Match)> _rememberOptions = new();

    public Account? SelectedAccount { get; private set; }

    /// <summary>The kind of rule to write on accept, or null to write none.</summary>
    public RuleKind? RememberAs { get; private set; }

    /// <summary>The address or domain the rule should match.</summary>
    public string RememberMatch { get; private set; } = "";

    public PickerForm(AppConfig config, MailtoRequest request)
    {
        SuspendLayout();

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        _recipient.Text = "To: " + request.DescribeRecipient();
        _recipient.AutoEllipsis = true;
        _recipient.ForeColor = SystemColors.GrayText;
        _recipient.SetBounds(12, 10, 316, 18);

        _list.IntegralHeight = true;
        _list.DisplayMember = nameof(Account.Name);
        _list.SetBounds(12, 34, 316, 44);
        foreach (Account account in config.Accounts)
            _list.Items.Add(account);
        _list.SelectedItemClicked += (_, _) => Accept();
        _list.DoubleClick += (_, _) => Accept();

        // A rule decides the highlighted account. With no rule the first account
        // wins: there is deliberately no "last used" memory, which would make
        // the default depend on invisible state from an unrelated message.
        string recipient = request.PrimaryRecipient;
        Rule? matched = config.MatchRule(recipient);
        Account? byRule = config.FindByAddress(matched?.EmailAddress);
        _initialIndex = byRule is null ? 0 : config.Accounts.IndexOf(byRule);

        // Rules written casually at send time are easy to forget, so the picker
        // says when one is responsible for the highlight.
        _reason.Text = matched is null ? "" : $"Rule: {matched.Match}";
        _reason.Visible = matched is not null;
        _reason.AutoEllipsis = true;
        _reason.ForeColor = SystemColors.GrayText;
        _reason.SetBounds(12, 0, 316, 18);

        _rememberLabel.Text = "Remember:";
        _rememberLabel.SetBounds(12, 0, 68, 18);

        BuildRememberOptions(recipient);
        _remember.DropDownStyle = ComboBoxStyle.DropDownList;
        _remember.SetBounds(82, 0, 246, 21);
        foreach ((string label, _, _) in _rememberOptions)
            _remember.Items.Add(label);
        _remember.SelectedIndex = 0;
        _remember.Enabled = _rememberOptions.Count > 1;

        _hint.Text = "Enter or click to open  ·  Esc to cancel";
        _hint.ForeColor = SystemColors.GrayText;
        _hint.SetBounds(12, 0, 316, 18);

        ClientSize = new Size(340, 180);
        Controls.Add(_recipient);
        Controls.Add(_list);
        Controls.Add(_reason);
        Controls.Add(_rememberLabel);
        Controls.Add(_remember);
        Controls.Add(_hint);

        Text = "Send with which account?";
        AppIcon.Apply(this);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        KeyPreview = true;
        TopMost = true;
        KeyDown += OnKeyDown;
        Shown += (_, _) =>
        {
            Activate();
            _list.Focus();
        };

        ResumeLayout(performLayout: false);
        PerformLayout();
    }

    /// <summary>
    /// The remember box names its targets rather than saying "this domain", so
    /// that a link with several recipients across different domains shows which
    /// one a rule would actually be written for.
    /// </summary>
    private void BuildRememberOptions(string recipient)
    {
        _rememberOptions.Add(("Do not remember", null, ""));
        if (recipient.Length == 0) return;

        _rememberOptions.Add(($"Always use for {recipient}", RuleKind.Address, recipient));

        string domain = EmailAddresses.DomainOf(recipient);
        if (domain.Length > 0)
            _rememberOptions.Add(($"Always use for {domain}", RuleKind.Domain, domain));
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitToRows();

        if (_list.Items.Count > 0)
            _list.SelectedIndex = Compat.Clamp(_initialIndex, 0, _list.Items.Count - 1);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        FitToRows();
    }

    /// <summary>
    /// Auto-scaling handles Location and Size, but it cannot know that this
    /// list's height is meant to be a whole number of rows: ItemHeight follows
    /// the font, so a scaled pixel height drifts out of step with it. Everything
    /// below the list is then stacked from wherever it actually ended up.
    /// </summary>
    private void FitToRows()
    {
        int rows = Compat.Clamp(_list.Items.Count, 1, MaxVisibleRows);
        _list.Height = (rows * _list.ItemHeight) + LogicalToDeviceUnits(8);

        int y = _list.Bottom + LogicalToDeviceUnits(8);

        if (_reason.Visible)
        {
            _reason.Top = y;
            y = _reason.Bottom + LogicalToDeviceUnits(6);
        }

        _remember.Top = y;
        _rememberLabel.Top = y + LogicalToDeviceUnits(3);   // baseline-ish against the box
        y = _remember.Bottom + LogicalToDeviceUnits(10);

        _hint.Top = y;
        ClientSize = new Size(ClientSize.Width, _hint.Bottom + LogicalToDeviceUnits(10));
        CenterToScreen();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // While the remember list is open, Enter and Esc belong to it.
        if (_remember.DroppedDown) return;

        switch (e.KeyCode)
        {
            case Keys.Enter:
                e.Handled = true;
                e.SuppressKeyPress = true;
                Accept();
                break;
            case Keys.Escape:
                e.Handled = true;
                e.SuppressKeyPress = true;
                DialogResult = DialogResult.Cancel;
                Close();
                break;
        }
    }

    private void Accept()
    {
        if (_list.SelectedItem is not Account account) return;

        SelectedAccount = account;

        int choice = _remember.SelectedIndex;
        if (choice > 0 && choice < _rememberOptions.Count)
        {
            (_, RuleKind? kind, string match) = _rememberOptions[choice];
            RememberAs = kind;
            RememberMatch = match;
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
