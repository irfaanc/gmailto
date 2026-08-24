using System.Text;

namespace GmailTo;

/// <summary>
/// A mailto: URI broken into the fields Gmail's compose URL cares about.
/// Follows RFC 2368: recipients live between "mailto:" and "?", everything
/// else arrives as query parameters.
/// </summary>
internal sealed class MailtoRequest
{
    private const string Scheme = "mailto:";

    public string To { get; private set; } = "";
    public string Cc { get; private set; } = "";
    public string Bcc { get; private set; } = "";
    public string Subject { get; private set; } = "";
    public string Body { get; private set; } = "";

    public static bool IsMailto(string? value) =>
        value is not null && value.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses a mailto: URI. Throws <see cref="FormatException"/> if it isn't one.</summary>
    public static MailtoRequest Parse(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            throw new FormatException("The mailto: link was empty.");
        if (!IsMailto(uri))
            throw new FormatException($"Not a mailto: link:\r\n\r\n{Truncate(uri, 200)}");

        string rest = uri.Substring(Scheme.Length);

        // Some senders emit "mailto://someone@example.com"; the slashes are not
        // part of the address.
        while (rest.StartsWith("//", StringComparison.Ordinal))
            rest = rest.Substring(2);

        string path, query;
        int q = rest.IndexOf('?');
        if (q >= 0)
        {
            path = rest.Substring(0, q);
            query = rest.Substring(q + 1);
        }
        else
        {
            path = rest;
            query = "";
        }

        var result = new MailtoRequest { To = Decode(path).Trim() };

        foreach (string pair in query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string name = eq < 0 ? pair : pair.Substring(0, eq);
            string value = eq < 0 ? "" : pair.Substring(eq + 1);

            name = Decode(name).Trim().ToLowerInvariant();
            value = Decode(value);

            switch (name)
            {
                case "to":
                    result.To = Merge(result.To, value);
                    break;
                case "cc":
                    result.Cc = Merge(result.Cc, value);
                    break;
                case "bcc":
                    result.Bcc = Merge(result.Bcc, value);
                    break;
                case "subject":
                    result.Subject = value;
                    break;
                case "body":
                    result.Body = value;
                    break;
                // Other RFC 2368 headers (in-reply-to, keywords, ...) have no
                // equivalent in the Gmail compose URL, so they are dropped.
            }
        }

        return result;
    }

    /// <summary>
    /// Percent-decodes one component. Deliberately not HttpUtility.UrlDecode:
    /// that turns "+" into a space, which would mangle addresses like
    /// someone+tag@gmail.com. mailto: links encode spaces as %20.
    /// </summary>
    private static string Decode(string value) =>
        value.Length == 0 ? value : Uri.UnescapeDataString(value);

    private static string Merge(string existing, string addition)
    {
        if (string.IsNullOrEmpty(addition)) return existing;
        if (string.IsNullOrEmpty(existing)) return addition;
        return existing + ", " + addition;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value.Substring(0, max) + "...";

    /// <summary>
    /// Builds the Gmail compose URL for the given mailbox. Empty fields are left
    /// out entirely; every value is escaped with Uri.EscapeDataString so spaces
    /// become %20 rather than "+".
    /// </summary>
    /// <param name="authUser">
    /// The Gmail address to send from. Selects the mailbox by name, so the path
    /// slot below is only a starting point.
    /// </param>
    public string ToGmailComposeUrl(string authUser)
    {
        var parameters = new List<string>(7)
        {
            // First, matching the form this was verified against.
            "authuser=" + Uri.EscapeDataString(authUser.Trim()),
        };

        Append(parameters, "to", To);
        Append(parameters, "cc", Cc);
        Append(parameters, "bcc", Bcc);
        Append(parameters, "su", Subject);   // Gmail calls the subject "su"
        Append(parameters, "body", Body);
        parameters.Add("tf=cm");

        // authuser overrides whatever the path says, and slot 0 is the one slot
        // guaranteed to exist whenever anybody is signed in at all.
        return "https://mail.google.com/mail/u/0/?" + string.Join("&", parameters);
    }

    private static void Append(List<string> parameters, string name, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        parameters.Add(name + "=" + Uri.EscapeDataString(value));
    }

    /// <summary>
    /// The recipient that rules are matched against: the first To address, or
    /// the first Cc/Bcc if there is no To.
    ///
    /// A link can carry several recipients across different domains, so this is
    /// a choice rather than a fact. First To is the predictable one, and the
    /// picker names what it matched so a surprising pick is visible.
    /// </summary>
    public string PrimaryRecipient
    {
        get
        {
            foreach (string header in new[] { To, Cc, Bcc })
            {
                string address = EmailAddresses.Normalise(header);
                if (address.Length > 0) return address;
            }
            return "";
        }
    }

    /// <summary>Short one-line summary used in the picker window header.</summary>
    public string DescribeRecipient()
    {
        if (!string.IsNullOrEmpty(To)) return To;
        if (!string.IsNullOrEmpty(Cc)) return "(cc) " + Cc;
        if (!string.IsNullOrEmpty(Bcc)) return "(bcc) " + Bcc;
        return "(no recipient)";
    }
}
