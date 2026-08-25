using System.Globalization;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace GmailTo;

/// <summary>
/// Reading and writing config.json without System.Text.Json, which is not part
/// of .NET Framework and would drag four assemblies alongside an exe that
/// installs itself by copying one file.
///
/// The two halves are deliberately different. Parsing text a user may have
/// hand-edited is where the bugs live, so that stays library code:
/// DataContractJsonSerializer is in-box, ignores members it does not know, and
/// throws on malformed input rather than returning something half-built.
/// Writing is the safe half, because the shape is ours, so it is done by hand
/// to get an indented file with the fields in a sensible order.
///
/// Two values are strings on the wire. DataContractJsonSerializer writes enums
/// as integers whatever [EnumMember] says, and renders a DateTimeOffset as
/// {"DateTime":"/Date(1787594700000)/","OffsetMinutes":-240}. Neither is
/// something to ask somebody to edit by hand.
/// </summary>
internal static class ConfigJson
{
    [DataContract]
    private sealed class WireProfile
    {
        [DataMember] public string? Name { get; set; }
        [DataMember] public string? EmailAddress { get; set; }
    }

    [DataContract]
    private sealed class WireRule
    {
        [DataMember] public string? Kind { get; set; }
        [DataMember] public string? Match { get; set; }
        [DataMember] public string? EmailAddress { get; set; }
    }

    [DataContract]
    private sealed class WireForward
    {
        [DataMember] public string? Recipient { get; set; }
        [DataMember] public string? MatchedRule { get; set; }
        [DataMember] public string? SentFrom { get; set; }
        [DataMember] public string? When { get; set; }
    }

    [DataContract]
    private sealed class WireConfig
    {
        [DataMember] public List<WireProfile>? Profiles { get; set; }
        [DataMember] public List<WireRule>? Rules { get; set; }
        [DataMember] public WireForward? LastAutomaticForward { get; set; }
        [DataMember] public bool StoppedHandling { get; set; }
    }

    /// <summary>Round-trip format: unambiguous, and sorts correctly as text.</summary>
    private const string TimeFormat = "o";

    private const string NewLine = "\r\n";

    /// <exception cref="SerializationException">Not valid JSON, or not this shape.</exception>
    public static AppConfig Read(string json)
    {
        // DataContractJsonSerializer accepts a JSON array, or any other value,
        // where an object was expected and hands back an empty result rather
        // than complaining. That reads as "no accounts configured", which sends
        // the user through first-run setup while their real config sits on disk
        // untouched. Refusing anything that is not an object keeps a damaged
        // file loud instead of quietly empty.
        string trimmed = json.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{')
            throw new SerializationException("The file does not contain a JSON object.");

        var serialiser = new DataContractJsonSerializer(typeof(WireConfig));
        WireConfig? wire;
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
        {
            wire = serialiser.ReadObject(stream) as WireConfig;
        }

        if (wire is null) throw new SerializationException("The file contains no configuration.");

        var config = new AppConfig { StoppedHandling = wire.StoppedHandling };

        foreach (WireProfile profile in wire.Profiles ?? new List<WireProfile>())
        {
            if (profile is null) continue;
            config.Profiles.Add(new Profile
            {
                Name = profile.Name ?? "",
                EmailAddress = profile.EmailAddress ?? "",
            });
        }

        foreach (WireRule rule in wire.Rules ?? new List<WireRule>())
        {
            if (rule is null) continue;
            config.Rules.Add(new Rule
            {
                Kind = ParseKind(rule.Kind),
                Match = rule.Match ?? "",
                EmailAddress = rule.EmailAddress ?? "",
            });
        }

        if (wire.LastAutomaticForward is WireForward forward)
        {
            config.LastAutomaticForward = new ForwardRecord
            {
                Recipient = forward.Recipient ?? "",
                MatchedRule = forward.MatchedRule ?? "",
                SentFrom = forward.SentFrom ?? "",
                When = ParseWhen(forward.When),
            };
        }

        return config;
    }

    /// <summary>
    /// An unrecognised kind reads as Address rather than throwing. Address is
    /// the narrower of the two, so a typo costs one recipient instead of
    /// quietly widening a rule to cover a whole domain.
    /// </summary>
    private static RuleKind ParseKind(string? value) =>
        string.Equals(value, nameof(RuleKind.Domain), StringComparison.OrdinalIgnoreCase)
            ? RuleKind.Domain
            : RuleKind.Address;

    /// <summary>
    /// An unreadable timestamp reads as the minimum, which the staleness sweep
    /// then treats as old and clears. Losing the ability to undo one automatic
    /// forward beats refusing to load the whole config.
    /// </summary>
    private static DateTimeOffset ParseWhen(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset when)
            ? when
            : DateTimeOffset.MinValue;

    public static string Write(AppConfig config)
    {
        var text = new StringBuilder();
        text.Append('{').Append(NewLine);

        text.Append("  \"Profiles\": ");
        AppendObjects(text, config.Profiles, (b, profile) =>
        {
            AppendField(b, "Name", profile.Name, last: false);
            AppendField(b, "EmailAddress", profile.EmailAddress, last: true);
        });
        text.Append(',').Append(NewLine);

        text.Append("  \"Rules\": ");
        AppendObjects(text, config.Rules, (b, rule) =>
        {
            AppendField(b, "Kind", rule.Kind.ToString(), last: false);
            AppendField(b, "Match", rule.Match, last: false);
            AppendField(b, "EmailAddress", rule.EmailAddress, last: true);
        });
        text.Append(',').Append(NewLine);

        // Left out entirely when there is nothing unexplained, which is what the
        // old serialiser did with nulls.
        if (config.LastAutomaticForward is ForwardRecord forward)
        {
            text.Append("  \"LastAutomaticForward\": {").Append(NewLine);
            AppendField(text, "Recipient", forward.Recipient, last: false, indent: "    ");
            AppendField(text, "MatchedRule", forward.MatchedRule, last: false, indent: "    ");
            AppendField(text, "SentFrom", forward.SentFrom, last: false, indent: "    ");
            AppendField(text, "When", forward.When.ToString(TimeFormat, CultureInfo.InvariantCulture), last: true, indent: "    ");
            text.Append("  },").Append(NewLine);
        }

        text.Append("  \"StoppedHandling\": ").Append(config.StoppedHandling ? "true" : "false").Append(NewLine);
        text.Append('}').Append(NewLine);
        return text.ToString();
    }

    private static void AppendObjects<T>(StringBuilder text, List<T> items, Action<StringBuilder, T> writeFields)
    {
        if (items.Count == 0)
        {
            text.Append("[]");
            return;
        }

        text.Append('[').Append(NewLine);
        for (int i = 0; i < items.Count; i++)
        {
            text.Append("    {").Append(NewLine);
            writeFields(text, items[i]);
            text.Append("    }").Append(i == items.Count - 1 ? NewLine : "," + NewLine);
        }

        text.Append("  ]");
    }

    private static void AppendField(StringBuilder text, string name, string? value, bool last, string indent = "      ")
    {
        text.Append(indent).Append('"').Append(name).Append("\": ").Append(Quote(value));
        if (!last) text.Append(',');
        text.Append(NewLine);
    }

    /// <summary>
    /// The only genuinely fiddly part of writing, so it follows the JSON spec
    /// rather than guessing: quote and backslash escaped, the handful of named
    /// escapes used, and anything else below space emitted as a \uXXXX escape.
    /// </summary>
    private static string Quote(string? value)
    {
        var text = new StringBuilder("\"");
        foreach (char c in value ?? "")
        {
            switch (c)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\b': text.Append("\\b"); break;
                case '\f': text.Append("\\f"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (c < ' ') text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else text.Append(c);
                    break;
            }
        }

        return text.Append('"').ToString();
    }
}
