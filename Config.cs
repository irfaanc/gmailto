using System.Runtime.Serialization;

namespace GmailTo;

internal sealed class Profile
{
    public string Name { get; set; } = "";

    /// <summary>
    /// The mailbox to send from, handed to Gmail as authuser.
    ///
    /// This is the only account selector. Gmail's /mail/u/N/ index was dropped:
    /// N is a position in the browser's signed-in list rather than an identity,
    /// so it renumbers whenever accounts are signed in or out and silently
    /// composes from the wrong account. An address names the mailbox and cannot
    /// drift, and a wrong one surfaces as Google's account chooser instead.
    /// </summary>
    public string EmailAddress { get; set; } = "";

    public override string ToString() => Name;

    public Profile Clone() => new() { Name = Name, EmailAddress = EmailAddress };
}

internal enum RuleKind
{
    /// <summary>Matches one exact recipient address.</summary>
    Address,

    /// <summary>Matches every recipient at a domain.</summary>
    Domain,
}

/// <summary>
/// "Mail to this recipient goes from this account." Written from the picker
/// rather than an editor, so the rule is declared at the moment its context is
/// on screen.
/// </summary>
internal sealed class Rule
{
    public RuleKind Kind { get; set; }

    /// <summary>The address or the domain, depending on <see cref="Kind"/>.</summary>
    public string Match { get; set; } = "";

    /// <summary>Address of the profile to send from. Profiles are identified by address everywhere.</summary>
    public string EmailAddress { get; set; } = "";

    public Rule Clone() => new() { Kind = Kind, Match = Match, EmailAddress = EmailAddress };
}

/// <summary>
/// What the app most recently did <em>without asking</em>. Exactly one is kept,
/// not a history: the useful question is "what just happened that I did not
/// choose", and once a picker has been shown nothing is unexplained any more.
/// </summary>
internal sealed class ForwardRecord
{
    public string Recipient { get; set; } = "";

    /// <summary>The <see cref="Rule.Match"/> that fired.</summary>
    public string MatchedRule { get; set; } = "";

    /// <summary>Address of the profile it was sent from.</summary>
    public string SentFrom { get; set; } = "";

    public DateTimeOffset When { get; set; }
}

internal sealed class AppConfig
{
    public List<Profile> Profiles { get; set; } = new();

    public List<Rule> Rules { get; set; } = new();

    /// <summary>Set only while an automatic forward is still unexplained. See <see cref="ForwardRecord"/>.</summary>
    public ForwardRecord? LastAutomaticForward { get; set; }

    /// <summary>
    /// True once the user has deliberately stopped this app handling mail links.
    ///
    /// Needed because the app registers itself on every launch, which writes the
    /// mailto class and would therefore re-take the association the moment it
    /// next ran, quietly undoing the choice. It also stops the offer to become
    /// the handler: nagging someone to re-enable what they just turned off is
    /// different from nagging someone who never set it up.
    /// </summary>
    public bool StoppedHandling { get; set; }

    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GmailTo");

    public static string FilePath => Path.Combine(DirectoryPath, "config.json");

    /// <summary>
    /// A new config starts empty on purpose. Seeding a guessed entry would put a
    /// plausible-looking profile in the list that nobody chose; the settings
    /// window asks for the first real one instead.
    /// </summary>
    public static AppConfig CreateDefault() => new();

    /// <summary>
    /// Reads config.json. A missing file is not an error: a default config is
    /// written and returned. A corrupt file throws so the caller can complain
    /// loudly rather than silently losing the user's profile list.
    /// </summary>
    public static AppConfig Load()
    {
        string path = FilePath;
        if (!File.Exists(path))
        {
            var fresh = CreateDefault();
            fresh.Save();
            return fresh;
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Could not read {path}\r\n\r\n{ex.Message}", ex);
        }

        AppConfig? config;
        try
        {
            config = ConfigJson.Read(json);
        }
        catch (SerializationException ex)
        {
            throw new InvalidDataException($"{path} is not valid JSON.\r\n\r\n{ex.Message}", ex);
        }

        if (config is null)
            throw new InvalidDataException($"{path} is empty or contains only \"null\".");

        if (config.Profiles is null) config.Profiles = new List<Profile>();

        // An entry without an address cannot select a mailbox, so it is dropped
        // rather than left in the picker to fail at send time. The settings
        // window then asks for a real one.
        config.Profiles.RemoveAll(a => a is null
            || string.IsNullOrWhiteSpace(a.Name)
            || string.IsNullOrWhiteSpace(a.EmailAddress));
        return config;
    }

    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        string path = FilePath;
        string temp = path + ".tmp";
        File.WriteAllText(temp, ConfigJson.Write(this));

        // Replace in one step so an interrupted write cannot leave a half file.
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }

    /// <summary>
    /// The rule that decides who sends to this recipient, if any. Exact address
    /// beats domain, always: precedence is by specificity, so rules never need
    /// ordering and the settings window needs no way to reorder them.
    /// </summary>
    public Rule? MatchRule(string? recipient)
    {
        string address = EmailAddresses.Normalise(recipient);
        if (address.Length == 0) return null;

        Rule? exact = Rules.FirstOrDefault(r =>
            r.Kind == RuleKind.Address &&
            string.Equals(r.Match, address, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        string domain = EmailAddresses.DomainOf(address);
        if (domain.Length == 0) return null;

        return Rules.FirstOrDefault(r =>
            r.Kind == RuleKind.Domain &&
            string.Equals(r.Match, domain, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The profile a matching rule points at, if the rule and profile both still exist.</summary>
    public Profile? MatchProfile(string? recipient) => FindByAddress(MatchRule(recipient)?.EmailAddress);

    /// <summary>
    /// Adds a rule, replacing any existing one for the same target. Choosing
    /// again in the picker is how a rule gets edited, so this must overwrite
    /// rather than accumulate a second, contradictory entry.
    /// </summary>
    public void SetRule(RuleKind kind, string match, string emailAddress)
    {
        if (string.IsNullOrWhiteSpace(match) || string.IsNullOrWhiteSpace(emailAddress)) return;

        Rules.RemoveAll(r => r.Kind == kind &&
            string.Equals(r.Match, match, StringComparison.OrdinalIgnoreCase));
        Rules.Add(new Rule { Kind = kind, Match = match.Trim(), EmailAddress = emailAddress.Trim() });
    }

    public Profile? FindByAddress(string? address) =>
        string.IsNullOrWhiteSpace(address)
            ? null
            : Profiles.FirstOrDefault(a =>
                string.Equals(a.EmailAddress, address, StringComparison.OrdinalIgnoreCase));
}
