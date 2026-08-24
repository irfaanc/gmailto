using System.Diagnostics;
namespace GmailTo;

internal enum InstallOutcome
{
    /// <summary>Nothing to do: this copy is already the installed one.</summary>
    AlreadyInstalled,

    /// <summary>Left alone, because this copy is somewhere the user chose.</summary>
    Declined,

    /// <summary>Copied into place for the first time.</summary>
    Installed,

    /// <summary>Replaced an installed copy with the one being run now.</summary>
    Updated,

    /// <summary>Wanted to install and could not. Never fatal.</summary>
    Failed,
}

/// <summary>
/// Puts the app somewhere permanent by itself, so "copy this to its final home
/// before you use it" stops being a step the user has to know about. The
/// registration records an absolute path, so a copy left in Downloads produces
/// a handler that breaks the first time the folder is tidied.
/// </summary>
internal static class SelfInstall
{
    /// <summary>
    /// Per-user, no admin rights, and the convention Chrome and VS Code follow.
    /// The README recommended this exact path back when it was a manual step.
    /// </summary>
    public static string InstalledDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        Registration.AppKeyName);

    public static string InstalledExePath =>
        Path.Combine(InstalledDirectory, Registration.AppKeyName + ".exe");

    /// <summary>
    /// The copy actually executing, which is not always the one to register.
    ///
    /// Application.ExecutablePath rather than Assembly.Location, which reports
    /// the assembly rather than the host and is empty in some hosting setups,
    /// and rather than Environment.ProcessPath, which .NET Framework does not
    /// have.
    /// </summary>
    public static string RunningExePath
    {
        get
        {
            string path = Application.ExecutablePath;
            if (!string.IsNullOrEmpty(path)) return path;

            return Path.Combine(AppContext.BaseDirectory, Registration.AppKeyName + ".exe");
        }
    }

    /// <summary>
    /// Where the registry should point. Only an install performed or confirmed
    /// this run redirects it; otherwise the running copy takes the registration
    /// exactly as it did before this existed. Set once, at startup.
    /// </summary>
    public static string PathToRegister => _registerAs ?? RunningExePath;

    private static string? _registerAs;

    /// <summary>
    /// True when this run put the app in place, whether it did the copying or
    /// was handed the job by the copy that did.
    ///
    /// It has to survive the hand-off, because the process that copies exits
    /// immediately afterwards. Without carrying the fact across, nothing that
    /// reports an install could ever run.
    /// </summary>
    public static bool JustInstalled { get; private set; }

    /// <summary>
    /// Installs this copy if it is running from somewhere temporary.
    ///
    /// The transient test is the whole design. Installing unconditionally would
    /// override a deliberate choice: someone who put the exe in
    /// "C:\Program Files\Little Programs" meant it, and silently relocating them
    /// to LocalAppData is worse than the problem being solved. Downloads, Temp
    /// and the Desktop are where a file lands when nobody has decided yet, and
    /// Temp additionally covers running straight out of a zip.
    ///
    /// Re-running a downloaded copy over an existing install is how upgrades
    /// work, which is why replacing is allowed rather than only creating.
    /// </summary>
    public static InstallOutcome EnsureInstalled()
    {
        string running = RunningExePath;

        if (PathsEqual(running, InstalledExePath))
        {
            _registerAs = InstalledExePath;
            return InstallOutcome.AlreadyInstalled;
        }

        if (!LooksTransient(running)) return InstallOutcome.Declined;

        try
        {
            bool replacing = File.Exists(InstalledExePath);
            Directory.CreateDirectory(InstalledDirectory);
            File.Copy(running, InstalledExePath, overwrite: true);

            _registerAs = InstalledExePath;
            JustInstalled = true;
            return replacing ? InstallOutcome.Updated : InstallOutcome.Installed;
        }
        catch
        {
            // Non-fatal by design. A failed install just means the app carries on
            // registering the copy being run, which is what it always did. The
            // likeliest cause is the installed copy already running, and there is
            // nothing useful to say about that at a mail link.
            return InstallOutcome.Failed;
        }
    }

    /// <summary>
    /// True when the exe sits in a folder that holds things nobody has filed yet.
    /// Matches the folder itself or anything beneath it, so an unzipped subfolder
    /// in Downloads counts.
    /// </summary>
    private static bool LooksTransient(string exePath)
    {
        string? directory = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(directory)) return false;

        foreach (string root in TransientRoots())
        {
            if (string.IsNullOrEmpty(root)) continue;
            if (IsAtOrUnder(directory, root)) return true;
        }

        return false;
    }

    private static IEnumerable<string> TransientRoots()
    {
        yield return Path.GetTempPath();
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        // No SpecialFolder covers Downloads, and resolving the real known folder
        // needs SHGetKnownFolderPath. The default location is right for almost
        // everyone, and being wrong here only means the app does not install
        // itself, which is the old behaviour rather than a fault.
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile)) yield return Path.Combine(profile, "Downloads");
    }

    private static bool IsAtOrUnder(string directory, string root)
    {
        string a = Normalise(directory);
        string b = Normalise(root);
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
               a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string a, string b) =>
        Normalise(a).Equals(Normalise(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalise(string path)
    {
        try
        {
            return Compat.TrimEndingSeparator(Path.GetFullPath(path));
        }
        catch
        {
            return path;
        }
    }
    /// <summary>
    /// Hands over to the installed copy: starts it with the same arguments and
    /// steps aside, so the process that carries on is the permanent one and the
    /// registry, the running exe and the settings window all agree.
    ///
    /// The copy it was installed from is left alone. Installers do not delete
    /// themselves, and a downloaded file is the user's to keep or bin.
    /// </summary>
    /// <returns>True if the installed copy started and this process should exit.</returns>
    public static bool TryHandOff(string[] args)
    {
        try
        {
            var forwarded = new List<string>(args) { JustInstalledFlag };
            var start = new ProcessStartInfo(InstalledExePath)
            {
                UseShellExecute = false,
                Arguments = Compat.JoinArguments(forwarded.ToArray()),
            };

            return Process.Start(start) is not null;
        }
        catch
        {
            // Could not hand over, so carry on as this copy. The install still
            // happened and the registry still points at it, so the next launch
            // lands in the right place anyway.
            return false;
        }
    }

    /// <summary>
    /// Marker handed to the copy this one starts. Carries no path and no
    /// instruction, only "you were just put here", so the worst it can do is
    /// change what the app says about itself.
    /// </summary>
    public const string JustInstalledFlag = "--just-installed";

    /// <summary>Takes our own marker off the command line before normal handling sees it.</summary>
    public static string[] TakeJustInstalledFlag(string[] args)
    {
        int at = Array.FindIndex(args, a =>
            a.Equals(JustInstalledFlag, StringComparison.OrdinalIgnoreCase));
        if (at < 0) return args;

        JustInstalled = true;

        var rest = new List<string>(args.Length);
        for (int i = 0; i < args.Length; i++)
            if (i != at) rest.Add(args[i]);

        return rest.ToArray();
    }
}
