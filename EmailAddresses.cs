namespace GmailTo;

/// <summary>
/// Pulling a bare address out of a recipient field, which is messier than it
/// looks: a mailto can carry display names, several recipients, or both.
/// </summary>
internal static class EmailAddresses
{
    /// <summary>
    /// The first bare address in a recipient field.
    ///
    /// Anchored on the "@" and expanded outwards rather than split on commas,
    /// because a quoted display name may itself contain one:
    /// "Doe, Jane" &lt;j@x.com&gt; splits into nonsense but resolves correctly here.
    /// </summary>
    public static string Normalise(string? recipient)
    {
        // Checked the long way round rather than with IsNullOrWhiteSpace:
        // .NET Framework's reference assemblies carry no nullable annotations,
        // so that call does not tell the compiler the value survived it.
        if (recipient is null || recipient.Trim().Length == 0) return "";

        int at = recipient.IndexOf('@');
        if (at < 0) return "";

        int start = at;
        while (start > 0 && !IsBoundary(recipient[start - 1])) start--;

        int end = at;
        while (end < recipient.Length - 1 && !IsBoundary(recipient[end + 1])) end++;

        string address = recipient.Substring(start, end + 1 - start).Trim();

        // An address of just "@" or with nothing either side is not one.
        int split = address.IndexOf('@');
        return split > 0 && split < address.Length - 1 ? address : "";
    }

    /// <summary>The domain part of an address, or empty if there isn't one.</summary>
    public static string DomainOf(string? address)
    {
        if (address is null || address.Trim().Length == 0) return "";
        int at = address.LastIndexOf('@');
        return at >= 0 && at < address.Length - 1 ? address.Substring(at + 1).Trim() : "";
    }

    private static bool IsBoundary(char c) =>
        c is ',' or ';' or '<' or '>' or '"' or '(' or ')' || char.IsWhiteSpace(c);
}
