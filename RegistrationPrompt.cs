using System.Diagnostics;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GmailTo;

/// <summary>
/// The user-facing half of registration. Kept apart from
/// <see cref="Registration"/> so the registry code stays free of any UI.
///
/// Note what is *not* here: an offer to register. Writing the registry entries
/// is unguarded, silent, and reversible, and with no installer the first run is
/// the installation -- so it just happens. The only thing worth a dialog is the
/// step Windows genuinely reserves for the user: choosing the default handler.
/// </summary>
internal static class RegistrationPrompt
{
    private const string Title = "gmailto";

    /// <summary>
    /// Checks whether Windows is routing mail links here, and walks the user
    /// through Settings if not. Asked on every launch while the answer is no:
    /// handling mail links is the only thing this app does, and until it is the
    /// chosen handler it does nothing at all.
    /// </summary>
    /// <returns>True if the app is the default handler by the time this returns.</returns>
    public static bool EnsureDefaultHandler(IWin32Window? owner)
    {
        if (Registration.IsEffectiveHandler()) return true;

        // Two versions, because the middle sentence has nothing to name when
        // Windows will not say what it is using. Reaching here at all means
        // something else is handling mail links: an empty UserChoice only means
        // Windows has not recorded which, not that the slot is free.
        string? current = Registration.DefaultHandlerProgId();
        string message = string.IsNullOrEmpty(current)
            ? "Windows is not set up to use gmailto yet.\r\n\r\n" +
              "If you want gmailto to handle your mail, we need to change that. " +
              "Set it up now?"
            : "Windows is not set up to use gmailto yet. It's currently using " +
              FriendlyHandlerName(current!) + ".\r\n\r\nIf you want gmailto to handle " +
              "your mail, it needs to replace it. Set it up now?";

        DialogResult answer = Show(owner, message,
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        return answer == DialogResult.Yes && Claim(owner);
    }

    /// <summary>
    /// Takes the association, without asking first. Used by the settings button,
    /// where the button's own label was the question.
    /// </summary>
    public static bool Claim(IWin32Window? owner)
    {
        if (Registration.IsEffectiveHandler()) return true;

        // The direct route: clear the recorded choice and let Windows fall
        // through to our class entry. Windows blocks this for some protocols on
        // some builds, so it is attempted rather than assumed.
        if (Registration.TryClaimDefault(out string? claimError))
        {
            Show(owner, "All done! Mail links will now be handled by gmailto.",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }

        Show(owner,
            "Windows would not let the change be made directly" +
            (claimError is null ? "" : $" ({claimError})") + ".  You'll have to set " +
            "it yourself.\r\n\r\nBut don't worry - we'll open the Settings screen for " +
            "you.  Remember to choose gmailto!",
            MessageBoxButtons.OK, MessageBoxIcon.Information);

        try
        {
            Registration.OpenDefaultAppsSettings();
        }
        catch (Exception ex)
        {
            Show(owner,
                "We weren't able to open Windows Settings:\r\n\r\n" + ex.Message +
                "\r\n\r\nYou'll have to do it manually:\r\n" +
                "Open \"Settings > Apps > Default apps\", and set MAILTO to gmailto.",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        // Settings runs out of process and tells us nothing when the user is
        // done, so the only way to know is to look again when they say so.
        bool firstTry = true;
        while (true)
        {
            // Spelled out because the Default apps page is not obvious: the app
            // list at the bottom is the wrong place to look, and the link-type
            // box at the top is the route that works.
            const string Directions =
                "In Settings > Apps > Default apps, type MAILTO into " +
                "\"Set a default for a file type or link type\", then use the " +
                "MAILTO row that appears to choose gmailto.";

            string message = firstTry
                ? "Settings is open.\r\n\r\n" + Directions + "\r\n\r\nThen choose Retry to check."
                : "Windows still has mail links pointed somewhere else" +
                  DescribeCurrentHandler() + ".\r\n\r\n" + Directions +
                  "\r\n\r\nThen choose Retry.";

            DialogResult retry = Show(owner, message, MessageBoxButtons.RetryCancel,
                firstTry ? MessageBoxIcon.Information : MessageBoxIcon.Warning);

            if (retry == DialogResult.Cancel) return false;
            if (Registration.IsEffectiveHandler()) return true;

            firstTry = false;
        }
    }

    /// <summary>
    /// Friendly names for the handlers most likely to be found, because the
    /// ProgID Windows records is an identifier rather than a product name:
    /// "MSEdgeHTM" is not a useful thing to show somebody.
    ///
    /// Matched on a prefix, since ProgIDs carry version suffixes. Outlook 2016
    /// registers Outlook.URL.mailto.15.
    ///
    /// Deliberately short, and only entries that could be checked. A name that
    /// is wrong is worse than a ProgID that is merely ugly: it sends the reader
    /// looking for the wrong application. Anything not listed here is shown
    /// exactly as Windows recorded it.
    /// </summary>
    private static readonly KeyValuePair<string, string>[] KnownHandlers =
    {
        new("Outlook.URL.mailto", "Outlook"),
        new("Microsoft.OutlookForWindows", "Outlook"),
        new("Thunderbird.Url.mailto", "Thunderbird"),
        new("MSEdge", "Microsoft Edge"),
        new("ChromeHTML", "Google Chrome"),
        new("FirefoxURL", "Firefox"),
        new("microsoft.windowscommunicationsapps", "Mail"),
    };

    private static string FriendlyHandlerName(string progId)
    {
        foreach (KeyValuePair<string, string> known in KnownHandlers)
        {
            if (progId.StartsWith(known.Key, StringComparison.OrdinalIgnoreCase))
                return known.Value;
        }

        return DescribeFromRegistry(progId) ?? progId;
    }

    /// <summary>
    /// What the handler calls itself, for anything the table does not name.
    ///
    /// The exe's own FileDescription first, since that is the product name as
    /// its author wrote it, and it cannot go stale the way a list would. The
    /// class description second: "Microsoft Edge HTML Document" is clumsy, but
    /// it beats showing somebody "MSEdgeHTM".
    ///
    /// Packaged apps register an activation ProgID with no exe behind it, so
    /// this finds nothing for those and the caller falls back to the ProgID.
    /// </summary>
    private static string? DescribeFromRegistry(string progId)
    {
        try
        {
            using (RegistryKey? command =
                Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command"))
            {
                if (command?.GetValue(null) is string line)
                {
                    string? exe = Registration.ExtractExePath(line);
                    if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                    {
                        string? described = FileVersionInfo.GetVersionInfo(exe!).FileDescription;
                        if (!string.IsNullOrWhiteSpace(described)) return described!.Trim();
                    }
                }
            }

            using (RegistryKey? cls = Registry.ClassesRoot.OpenSubKey(progId))
            {
                if (cls?.GetValue(null) is string label && !string.IsNullOrWhiteSpace(label))
                    return label.Trim();
            }
        }
        catch
        {
            // Naming the current handler is a nicety. It must never be the
            // reason the prompt fails to appear.
        }

        return null;
    }

    private static string DescribeCurrentHandler()
    {
        string? progId = Registration.DefaultHandlerProgId();
        return string.IsNullOrEmpty(progId) ? "" : $" ({FriendlyHandlerName(progId!)})";
    }

    /// <summary>Reports the outcome of the startup registry pass, if it failed.</summary>
    public static void ReportIfFailed(IWin32Window? owner, RegistrationStatus status, string? error)
    {
        if (status != RegistrationStatus.Failed) return;

        Show(owner,
            "We couldn't update the Windows registry settings. Which means gmailto " +
            "won't be able to handle your mail.\r\n\r\n" + error,
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static DialogResult Show(IWin32Window? owner, string text,
                                     MessageBoxButtons buttons, MessageBoxIcon icon) =>
        owner is null
            ? MessageBox.Show(text, Title, buttons, icon)
            : MessageBox.Show(owner, text, Title, buttons, icon);
}
