using System.Diagnostics;
using Microsoft.Win32;

namespace GmailTo;

/// <summary>Outcome of the startup pass that keeps the registry entries honest.</summary>
internal enum RegistrationStatus
{
    /// <summary>The entries were absent and have just been written.</summary>
    Created,

    /// <summary>Already registered, and pointing at this exe.</summary>
    Current,

    /// <summary>The registered exe was gone, so the entries now point here.</summary>
    Repaired,

    /// <summary>
    /// The registration named another copy that still exists, and this one has
    /// taken it over. Worth distinguishing from <see cref="Repaired"/> only so
    /// it can be reported: the other copy is now orphaned.
    /// </summary>
    TakenOver,

    /// <summary>The entries could not be written.</summary>
    Failed,
}

/// <summary>
/// Registers this exe as a *candidate* mailto: handler, entirely under
/// HKEY_CURRENT_USER. Nothing here makes the app the default; Windows guards
/// the UserChoice key in the kernel (UCPD.sys), so the user has to pick us
/// once in Settings > Apps > Default apps.
/// </summary>
internal static class Registration
{
    public const string AppKeyName = "GmailTo";
    public const string DisplayName = "gmailto";
    public const string Description = "Choose which Gmail account opens a mailto: link.";
    public const string ProgId = "GmailTo.Url.Mailto";

    private const string CapabilitiesPath = @"Software\" + AppKeyName + @"\Capabilities";
    private const string BackupPath = @"Software\" + AppKeyName + @"\PreviousMailtoHandler";
    private const string MailtoClassPath = @"Software\Classes\mailto";

    /// <summary>
    /// The path to record in the registry. Normally the copy being run, but the
    /// installed copy once <see cref="SelfInstall"/> has put one in place, so a
    /// mail link opens the permanent copy rather than the one in Downloads that
    /// is about to be tidied away.
    /// </summary>
    public static string ExePath => SelfInstall.PathToRegister;

    private static string CommandFor(string exe) => $"\"{exe}\" \"%1\"";

    private static string IconFor(string exe) => $"\"{exe}\",0";

    /// <summary>
    /// True if a registered command line belongs to this app rather than some
    /// other handler.
    ///
    /// Matched on the file name rather than the full path, so a registration
    /// left by a copy that has since moved is still recognised as ours, and by
    /// prefix rather than exactly, so the x86 and x64 builds recognise each
    /// other. They ship under different names, and without this, replacing one
    /// with the other would leave the shared mailto class pointing at an exe
    /// that is no longer there.
    ///
    /// The prefix is tested against the file name alone, so a stray folder
    /// named after this app cannot make someone else's handler look like ours.
    /// </summary>
    private static bool ReferencesThisApp(string command)
    {
        string? exe = ExtractExePath(command);
        return exe is not null &&
               Path.GetFileNameWithoutExtension(exe)
                   .StartsWith(AppKeyName, StringComparison.OrdinalIgnoreCase);
    }

    public static void Register()
    {
        string exe = ExePath;
        string command = CommandFor(exe);
        string icon = IconFor(exe);

        // The protocol class itself. This is the fallback Windows uses when no
        // explicit UserChoice exists for mailto. Whatever is there now gets
        // stashed first so Unregister can put it back.
        BackUpExistingMailtoClass();
        WriteProtocolClass(MailtoClassPath, "URL:MailTo Protocol", command, icon);

        // A private ProgID, which is what the Capabilities entry points at.
        WriteProtocolClass($@"Software\Classes\{ProgId}", DisplayName, command, icon);

        using (RegistryKey capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesPath, true))
        {
            capabilities.SetValue("ApplicationName", DisplayName);
            capabilities.SetValue("ApplicationDescription", Description);
            capabilities.SetValue("ApplicationIcon", icon);
            using RegistryKey urls = capabilities.CreateSubKey("UrlAssociations", true);
            urls.SetValue("mailto", ProgId);
        }

        using (RegistryKey registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications", true))
        {
            registered.SetValue(AppKeyName, CapabilitiesPath);
        }
    }

    /// <summary>
    /// Steps aside: clears the recorded choice if it names this app, then removes
    /// the registration entirely.
    ///
    /// This cannot hand the association back to whoever held it before, because
    /// UserChoice can be deleted but never written. Windows simply falls through
    /// to whatever is next, which is the same thing that happens to any file
    /// association when the app holding it is uninstalled.
    /// </summary>
    /// <returns>False if anything still routes mail links here, having said why.</returns>
    public static bool TryStopHandling(out string? error)
    {
        error = null;
        try
        {
            // Only if it names this app. Another app's recorded choice is not
            // ours to clear.
            if (string.Equals(DefaultHandlerProgId(), ProgId, StringComparison.OrdinalIgnoreCase))
                Registry.CurrentUser.DeleteSubKeyTree(UserChoicePath, throwOnMissingSubKey: false);

            Unregister();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        if (!IsEffectiveHandler()) return true;

        error = "Windows is still routing mail links to this app.";
        return false;
    }

    public static void Unregister()
    {
        using (RegistryKey? registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true))
        {
            if (registered?.GetValue(AppKeyName) is not null)
                registered.DeleteValue(AppKeyName, throwOnMissingValue: false);
        }

        // Before wiping our own key, which is where the backup lives.
        RestoreOrRemoveMailtoClass();

        DeleteTree(@"Software\" + AppKeyName);
        DeleteTree($@"Software\Classes\{ProgId}");
    }

    /// <summary>
    /// Remembers the mailto class as we found it, so uninstalling this app does
    /// not silently destroy whatever handler was registered before.
    /// </summary>
    private static void BackUpExistingMailtoClass()
    {
        using (RegistryKey? alreadySaved = Registry.CurrentUser.OpenSubKey(BackupPath))
        {
            if (alreadySaved is not null) return;   // never overwrite with our own values
        }

        if (OwnsMailtoClass()) return;

        using RegistryKey? mailto = Registry.CurrentUser.OpenSubKey(MailtoClassPath);
        if (mailto is null) return;                 // nothing there to lose

        // The subkeys are recorded even when empty: an existing-but-blank
        // DefaultIcon is still a difference worth putting back.
        using RegistryKey backup = Registry.CurrentUser.CreateSubKey(BackupPath, true);
        if (mailto.GetValue(null) is string description)
            backup.SetValue("Default", description);
        if (mailto.GetValue("URL Protocol") is not null)
            backup.SetValue("HadUrlProtocol", 1, RegistryValueKind.DWord);

        using (RegistryKey? iconKey = mailto.OpenSubKey("DefaultIcon"))
        {
            if (iconKey is not null)
            {
                backup.SetValue("HadDefaultIcon", 1, RegistryValueKind.DWord);
                if (iconKey.GetValue(null) is string existingIcon)
                    backup.SetValue("DefaultIcon", existingIcon);
            }
        }

        using RegistryKey? commandKey = mailto.OpenSubKey(@"shell\open\command");
        if (commandKey is not null)
        {
            backup.SetValue("HadCommand", 1, RegistryValueKind.DWord);
            if (commandKey.GetValue(null) is string existingCommand)
                backup.SetValue("Command", existingCommand);
        }
    }

    private static void RestoreOrRemoveMailtoClass()
    {
        // If something else has taken over the class since we registered, leave
        // it well alone.
        if (!OwnsMailtoClass()) return;

        DeleteTree(MailtoClassPath);

        using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(BackupPath);
        if (backup is null) return;                 // the key did not exist before us

        using RegistryKey mailto = Registry.CurrentUser.CreateSubKey(MailtoClassPath, true);
        if (backup.GetValue("Default") is string description)
            mailto.SetValue(null, description);
        if (backup.GetValue("HadUrlProtocol") is not null)
            mailto.SetValue("URL Protocol", "");

        if (backup.GetValue("HadDefaultIcon") is not null)
        {
            using RegistryKey iconKey = mailto.CreateSubKey("DefaultIcon", true);
            if (backup.GetValue("DefaultIcon") is string icon)
                iconKey.SetValue(null, icon);
        }

        if (backup.GetValue("HadCommand") is not null)
        {
            using RegistryKey commandKey = mailto.CreateSubKey(@"shell\open\command", true);
            if (backup.GetValue("Command") is string command)
                commandKey.SetValue(null, command);
        }
    }

    public static bool IsRegistered()
    {
        using RegistryKey? registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications");
        return registered?.GetValue(AppKeyName) is not null;
    }

    /// <summary>
    /// The path in UserChoice that decides which app Windows actually hands
    /// mailto links to. Readable by anyone; only writing it is guarded.
    /// </summary>
    private const string UserChoicePath =
        @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\mailto\UserChoice";

    /// <summary>
    /// Where the registration pointed before this run took it over, when that
    /// copy still exists. Only meaningful alongside
    /// <see cref="RegistrationStatus.TakenOver"/>, and only so the settings
    /// window can name the copy that has just been orphaned.
    /// </summary>
    public static string? PreviousExePath { get; private set; }

    /// <summary>The ProgID Windows is currently routing mailto to, if any.</summary>
    public static string? DefaultHandlerProgId()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(UserChoicePath);
        return key?.GetValue("ProgId") as string;
    }

    /// <summary>
    /// Whether Windows will actually launch this app for a mailto link.
    ///
    /// Checking UserChoice alone is not enough. When no choice is recorded,
    /// Windows falls through to the protocol class itself -- so the app can be
    /// the real handler while UserChoice is empty, and reporting "not the
    /// default" then would be visibly wrong to anyone watching their mail links
    /// open in it.
    /// </summary>
    public static bool IsEffectiveHandler()
    {
        string? chosen = DefaultHandlerProgId();
        return string.IsNullOrEmpty(chosen)
            ? OwnsMailtoClass()
            : string.Equals(chosen, ProgId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Clears the recorded default so mailto falls through to this app's own
    /// class entry, which is the only lever available: UserChoice itself cannot
    /// be written usefully, because Windows validates it against an undocumented
    /// hash and discards entries that do not match.
    ///
    /// This replaces whatever the user's current mail handler is, so it belongs
    /// behind an explicit, clearly labelled action rather than happening quietly
    /// at startup.
    ///
    /// It is not always permitted. UCPD, the User Choice Protection Driver,
    /// blocks these keys for the protocols it covers, and that coverage varies
    /// by Windows build -- so the result is read back from the registry rather
    /// than inferred from the call succeeding, and the caller falls back to
    /// walking the user through Settings.
    /// </summary>
    public static bool TryClaimDefault(out string? error)
    {
        error = null;
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UserChoicePath, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        if (IsEffectiveHandler()) return true;

        error = "Windows kept the previous handler.";
        return false;
    }

    /// <summary>
    /// Brings the registry entries into line with this exe, writing them if they
    /// are missing. There is no installer, so first run *is* installation, and
    /// registering a protocol handler is what installation does -- it makes the
    /// app selectable and nothing more. It cannot make itself the default.
    ///
    /// Repointing an existing registration fires only when the registered exe is
    /// *gone from disk*, not merely different from this one. A path that still
    /// resolves is a working registration belonging to another copy, and running
    /// a second copy out of a build folder must not quietly steal it.
    /// </summary>
    public static RegistrationStatus Prepare(out string? error)
    {
        error = null;
        try
        {
            if (!IsRegistered())
            {
                Register();
                return RegistrationStatus.Created;
            }

            string? registeredExe = RegisteredExePath();
            if (registeredExe is null)
            {
                // Registered, but the command is missing or unreadable.
                return Repair(out error);
            }

            if (string.Equals(registeredExe, ExePath, StringComparison.OrdinalIgnoreCase))
                return RegistrationStatus.Current;

            // The registration names a different location, so the copy being run
            // now takes it over. Whether the old path still exists changes only
            // what gets reported, not what happens: "I moved the app and mail
            // links silently kept going to the old one" is a confusing failure
            // with no signal, while a build-output copy taking the registration
            // is both obvious and undone by next running the real one.
            bool replacingALiveCopy = File.Exists(registeredExe);
            PreviousExePath = replacingALiveCopy ? registeredExe : null;

            RegistrationStatus outcome = Repair(out error);
            if (outcome != RegistrationStatus.Repaired) return outcome;

            return replacingALiveCopy ? RegistrationStatus.TakenOver : RegistrationStatus.Repaired;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return RegistrationStatus.Failed;
        }
    }

    /// <summary>The exe path recorded in our ProgID's open command, if any.</summary>
    public static string? RegisteredExePath()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
        return key?.GetValue(null) is string command ? ExtractExePath(command) : null;
    }

    /// <summary>Pulls the executable out of a "path" "%1" style command line.</summary>
    private static string? ExtractExePath(string command)
    {
        command = command.Trim();
        if (command.Length == 0) return null;

        if (command[0] == '"')
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command.Substring(1, end - 1) : null;
        }

        int space = command.IndexOf(' ');
        return space < 0 ? command : command.Substring(0, space);
    }

    private static RegistrationStatus Repair(out string? error)
    {
        error = null;
        try
        {
            string exe = ExePath;
            string command = CommandFor(exe);
            string icon = IconFor(exe);

            // The ProgID is namespaced to this app, so it is always safe to
            // rewrite. This is also the entry that matters most: when the user
            // has picked us in Default apps, Windows records the ProgID name and
            // resolves the exe through here.
            WriteProtocolClass($@"Software\Classes\{ProgId}", DisplayName, command, icon);

            using (RegistryKey capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesPath, true))
            {
                capabilities.SetValue("ApplicationIcon", icon);
            }

            // The bare mailto class is shared ground -- only touch it while it
            // still names this app. If something else has taken it over, leave
            // it alone. Note this deliberately does not re-run the backup: what
            // is there is our own stale entry, not a third party's.
            if (OwnsMailtoClass())
                WriteProtocolClass(MailtoClassPath, "URL:MailTo Protocol", command, icon);

            return RegistrationStatus.Repaired;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return RegistrationStatus.Failed;
        }
    }

    private static bool OwnsMailtoClass()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(MailtoClassPath + @"\shell\open\command");
        return key?.GetValue(null) is string command && ReferencesThisApp(command);
    }

    private static void WriteProtocolClass(string path, string description, string command, string icon)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(path, true);
        key.SetValue(null, description);
        key.SetValue("URL Protocol", "");

        using (RegistryKey iconKey = key.CreateSubKey("DefaultIcon", true))
            iconKey.SetValue(null, icon);

        using RegistryKey commandKey = key.CreateSubKey(@"shell\open\command", true);
        commandKey.SetValue(null, command);
    }

    private static void DeleteTree(string path)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        catch (ArgumentException)
        {
            // Key was already gone.
        }
    }

    /// <summary>
    /// Opens Settings > Apps > Default apps.
    ///
    /// No deep link: "?registeredAppUser=GmailTo" was tried and Windows 11
    /// ignores the parameter, landing on the same generic page. The walkthrough
    /// text carries the directions instead.
    /// </summary>
    public static void OpenDefaultAppsSettings()
    {
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
    }
}
