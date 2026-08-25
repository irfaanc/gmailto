using System.Drawing;
using System.Windows.Forms;

namespace GmailTo;

/// <summary>
/// A small notice in the corner of the screen saying which profile a mail link
/// was sent from, shown when a rule forwarded without asking.
///
/// Drawn rather than raised as a Windows toast: a real toast needs an
/// AppUserModelID and a Start Menu shortcut, and making it clickable needs a
/// registered COM activator, which is more machinery than this notice is worth
/// on an app with no dependencies and no installer.
///
/// **It must never take focus.** The user is mid-sentence in something else.
/// <see cref="ShowWithoutActivation"/> stops it activating when shown, and
/// WS_EX_NOACTIVATE stops it activating when clicked, which matters because it
/// is clickable.
/// </summary>
internal sealed class ToastForm : Form
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;   // keeps it out of Alt+Tab

    private static readonly TimeSpan VisibleFor = TimeSpan.FromSeconds(6);

    private readonly Label _headline = new();
    private readonly Label _detail = new();
    private readonly Label _action = new();
    private readonly System.Windows.Forms.Timer _life = new();

    private bool _fading;

    /// <summary>True if the user clicked it rather than letting it fade.</summary>
    public bool Clicked { get; private set; }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    public ToastForm(string profileName, string recipient, string matchedRule)
    {
        SuspendLayout();

        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        _headline.Text = $"Sent from {profileName}";
        _headline.Font = new Font(Font, FontStyle.Bold);
        _headline.AutoEllipsis = true;
        _headline.SetBounds(14, 12, 292, 18);

        _detail.Text = $"{recipient}  ·  rule {matchedRule}";
        _detail.ForeColor = SystemColors.GrayText;
        _detail.AutoEllipsis = true;
        _detail.SetBounds(14, 32, 292, 18);

        _action.Text = "Click to change or undo the rule";
        _action.ForeColor = SystemColors.GrayText;
        _action.AutoEllipsis = true;
        _action.SetBounds(14, 54, 292, 18);

        ClientSize = new Size(320, 84);
        Controls.AddRange(new Control[] { _headline, _detail, _action });

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = SystemColors.Window;

        // The labels sit on top of the form, so they need the click too.
        Click += (_, _) => Dismiss(clicked: true);
        foreach (Control c in Controls) c.Click += (_, _) => Dismiss(clicked: true);

        _life.Interval = (int)VisibleFor.TotalMilliseconds;
        _life.Tick += (_, _) => OnTick();

        ResumeLayout(performLayout: false);
        PerformLayout();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Positioned from the working area so it clears the taskbar, on whichever
        // monitor the user is actually looking at rather than the primary one.
        Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(
            area.Right - Width - LogicalToDeviceUnits(16),
            area.Bottom - Height - LogicalToDeviceUnits(16));

        _life.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(SystemColors.ControlDark);
        e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    /// <summary>
    /// One timer doing two jobs: the first tick ends the dwell and starts the
    /// fade, every tick after that dims it. Kept as a single handler because
    /// unsubscribing a lambda with -= removes nothing, it being a different
    /// delegate instance each time.
    /// </summary>
    private void OnTick()
    {
        if (!_fading)
        {
            _fading = true;
            _life.Interval = 30;
            return;
        }

        Opacity -= 0.08;
        if (Opacity <= 0.05) Dismiss(clicked: false);
    }

    private void Dismiss(bool clicked)
    {
        Clicked = clicked;
        _life.Stop();
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _life.Dispose();
        base.Dispose(disposing);
    }
}
