using System.Diagnostics;
using System.Windows.Forms;

namespace GmailTo;

internal static class Program
{
    private const string Title = "gmailto";

    [STAThread]
    private static int Main(string[] args)
    {
        // ApplicationConfiguration.Initialize() is generated for .NET, and does
        // not exist on .NET Framework. These are the two calls it makes that
        // matter here. DPI awareness is declared in the embedded manifest
        // instead, because it has to be set before the process creates a
        // window, and a manifest travels inside the exe rather than beside it.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            // Our own marker, taken off before anything else can mistake it for a
            // mailto: link. Set by the copy that installed us, so this process
            // knows it was just put in place and can say so.
            args = SelfInstall.TakeJustInstalledFlag(args);

            // Installing happens before anything else, and before the config is
            // read, because it is about where the app lives rather than what it
            // does. Deliberately not gated on StoppedHandling: that setting is
            // about handling mail links, and a downloaded copy still belongs in
            // a permanent home either way.
            //
            // Done here rather than in the two run paths, so the settings window
            // opening from inside RunPicker cannot start a second copy.
            if (SelfInstall.EnsureInstalled() is InstallOutcome.Installed or InstallOutcome.Updated
                && SelfInstall.TryHandOff(args))
                return 0;

            if (args.Length == 0 || IsSettingsFlag(args[0]))
                return RunSettings();

            if (!MailtoRequest.IsMailto(args[0]))
            {
                ShowError(
                    "This app expects a mailto: link.\r\n\r\nIt was started with:\r\n" +
                    args[0] + "\r\n\r\nRun it without any arguments to edit profiles.");
                return 2;
            }

            return RunPicker(args[0]);
        }
        catch (Exception ex)
        {
            ShowError("Something went wrong:\r\n\r\n" + ex.Message);
            return 1;
        }
    }

    private static bool IsSettingsFlag(string arg) =>
        arg.Equals("--settings", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("-settings", StringComparison.OrdinalIgnoreCase) ||
        arg.Equals("/settings", StringComparison.OrdinalIgnoreCase);

    private static int RunSettings()
    {
        AppConfig? config = LoadConfigOrExplain();
        if (config is null) return 3;

        // Skipped entirely once the user has stopped: registering writes the
        // mailto class, which would take the association straight back.
        RegistrationStatus status = RegistrationStatus.Current;
        string? registrationError = null;
        if (!config.StoppedHandling) status = Registration.Prepare(out registrationError);

        RetryStore.SweepIfStale();

        // Launching the app directly is the other way in to "what did it just do
        // without asking me", alongside clicking the notice.
        ShowLastForward(config);

        // ShowDialog rather than Application.Run: a modeless form ignores
        // DialogResult, so closing from a button would do nothing.
        using var form = new SettingsForm(config, status, registrationError);
        form.ShowDialog();
        return 0;
    }

    private static int RunPicker(string uri)
    {
        MailtoRequest request;
        try
        {
            request = MailtoRequest.Parse(uri);
        }
        catch (FormatException ex)
        {
            ShowError("That link could not be read.\r\n\r\n" + ex.Message);
            return 2;
        }

        AppConfig? config = LoadConfigOrExplain();
        if (config is null) return 3;

        // Write or repoint the registry entries as needed. Deliberately quiet
        // and non-fatal: the user clicked a mail link, and nothing about the
        // registry should interrupt or delay that. Skipped once the user has
        // stopped, since registering would take the association back.
        if (!config.StoppedHandling) Registration.Prepare(out _);

        if (config.Profiles.Count == 0)
        {
            // On a first run this is the tail of setup rather than a complaint:
            // the app has just put itself somewhere permanent and needs the one
            // thing it cannot infer. Said that way when an install just
            // happened, and as a plain gap otherwise.
            ShowWarning(SelfInstall.JustInstalled
                ? "Installed to\r\n" + SelfInstall.InstalledDirectory +
                  "\r\n\r\nLast step: You need a profile to send from. Add one on the next " +
                  $"screen, then we can write this email to {request.PrimaryRecipient}."
                : "No profiles are set up yet, so there is nowhere to send this.\r\n\r\n" +
                  "Add one in the next window, so we can finish writing this email to " +
                  $"{request.PrimaryRecipient}.");
            RunSettings();

            // Pick up whatever the settings window just wrote, then continue
            // with the original link rather than making the user click it again.
            AppConfig? updated = LoadConfigOrExplain();
            if (updated is null || updated.Profiles.Count == 0) return 3;
            config = updated;
        }

        RetryStore.SweepIfStale();

        // Holding Shift forces the picker. Without it a domain rule is a one way
        // door: that domain would never show the picker again, and the only way
        // back would be the explanation window.
        bool forcePicker = (Control.ModifierKeys & Keys.Shift) != 0;

        Rule? rule = config.MatchRule(request.PrimaryRecipient);
        Profile? byRule = config.FindByAddress(rule?.EmailAddress);
        if (rule is not null && byRule is not null && !forcePicker)
            return ForwardAutomatically(config, request, uri, rule, byRule);

        using var picker = new PickerForm(config, request);
        if (picker.ShowDialog() != DialogResult.OK || picker.SelectedProfile is null)
            return 0;   // Esc: do nothing at all.

        Profile profile = picker.SelectedProfile;
        if (!Mail.Open(request, profile, null)) return 4;

        // Showing the picker means nothing is unexplained any more, so any record
        // of an earlier automatic forward stops being worth keeping, and the
        // draft it saved stops being worth storing.
        bool dirty = config.LastAutomaticForward is not null;
        config.LastAutomaticForward = null;
        RetryStore.Clear();

        if (picker.RememberAs is RuleKind kind)
        {
            config.SetRule(kind, picker.RememberMatch, profile.EmailAddress);
            dirty = true;
        }

        // Worth a warning if it fails, but the mail is already open so this is
        // not fatal.
        if (dirty)
        {
            try
            {
                config.Save();
            }
            catch (Exception ex)
            {
                ShowWarning($"The message opened, but the rule could not be saved to\r\n{AppConfig.FilePath}\r\n\r\n{ex.Message}");
            }
        }

        // Asked only after the message is on its way: nothing should delay or
        // interrupt the thing the user actually clicked. Declining here simply
        // ends the run, which is all that is left to do anyway. Never asked of
        // someone who turned it off on purpose.
        if (!config.StoppedHandling) RegistrationPrompt.EnsureDefaultHandler(null);

        return 0;
    }

    /// <summary>
    /// Sends without showing the picker, because a rule said who to send as.
    /// Nothing is confirmed beforehand and nothing is sent: this opens a draft,
    /// and the notice afterwards is what makes it noticeable rather than silent.
    /// </summary>
    private static int ForwardAutomatically(
        AppConfig config, MailtoRequest request, string originalUri, Rule rule, Profile profile)
    {
        if (!Mail.Open(request, profile, null)) return 4;

        config.LastAutomaticForward = new ForwardRecord
        {
            Recipient = request.PrimaryRecipient,
            MatchedRule = rule.Match,
            SentFrom = profile.EmailAddress,
            When = DateTimeOffset.Now,
        };

        try
        {
            config.Save();
            RetryStore.Save(originalUri);
        }
        catch (Exception ex)
        {
            // The mail is open either way; this only costs the ability to undo.
            ShowWarning("The message opened, but what happened could not be recorded:\r\n\r\n" + ex.Message);
        }

        // Runs a message loop until the notice fades or is clicked, which is why
        // this launch outlives the browser handoff.
        using var toast = new ToastForm(profile.Name, request.PrimaryRecipient, rule.Match);
        Application.Run(new ApplicationContext(toast));

        if (toast.Clicked) ShowLastForward(config);
        return 0;
    }

    /// <summary>
    /// Shows what the last automatic forward did, and clears it. Viewing counts
    /// as having been told, so the record and the saved draft both go.
    /// </summary>
    private static void ShowLastForward(AppConfig config)
    {
        if (config.LastAutomaticForward is not ForwardRecord record) return;

        using (var dialog = new ForwardDialog(config, record, RetryStore.Load()))
        {
            dialog.ShowDialog();
        }

        config.LastAutomaticForward = null;
        RetryStore.Clear();
        try
        {
            config.Save();
        }
        catch
        {
            // Only costs showing the same explanation once more.
        }
    }

    /// <summary>
    /// Loads config.json. A corrupt file gets a visible dialog offering a reset;
    /// returns null when the user would rather go fix the file by hand.
    /// </summary>
    private static AppConfig? LoadConfigOrExplain()
    {
        try
        {
            return AppConfig.Load();
        }
        catch (Exception ex)
        {
            DialogResult answer = MessageBox.Show(
                "The settings file could not be loaded:\r\n\r\n" + ex.Message +
                "\r\n\r\nReplace it with a fresh default config?\r\n" +
                "(Choose No to leave the file alone so you can fix it yourself.)",
                Title, MessageBoxButtons.YesNo, MessageBoxIcon.Error);

            if (answer != DialogResult.Yes) return null;

            try
            {
                AppConfig fresh = AppConfig.CreateDefault();
                fresh.Save();
                return fresh;
            }
            catch (Exception saveEx)
            {
                ShowError($"Could not write {AppConfig.FilePath}:\r\n\r\n{saveEx.Message}");
                return null;
            }
        }
    }

    private static void ShowError(string message) =>
        MessageBox.Show(message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);

    private static void ShowWarning(string message) =>
        MessageBox.Show(message, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
