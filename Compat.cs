using System.Text;

namespace GmailTo;

/// <summary>
/// The handful of things .NET Framework does not provide. Kept together and
/// named for what they are, so the cost of targeting an older framework sits in
/// one file instead of being scattered as small surprises.
/// </summary>
internal static class Compat
{
    /// <summary>Math.Clamp arrived in .NET Core 2.0.</summary>
    public static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;

    /// <summary>
    /// Path.TrimEndingDirectorySeparator arrived in .NET Core 2.1.
    ///
    /// A root is left alone: "C:\" and "\" mean something different from "C:"
    /// and "", so trimming them would quietly change which directory is meant.
    /// </summary>
    public static string TrimEndingSeparator(string path)
    {
        if (path.Length <= 1) return path;

        char last = path[path.Length - 1];
        if (last != Path.DirectorySeparatorChar && last != Path.AltDirectorySeparatorChar) return path;
        if (Path.GetPathRoot(path)?.Length == path.Length) return path;

        return path.Substring(0, path.Length - 1);
    }

    private static readonly char[] NeedsQuoting = { ' ', '\t', '"' };

    /// <summary>
    /// ProcessStartInfo.ArgumentList arrived in .NET Core 2.1. Without it the
    /// arguments have to be pasted into one string, and getting that wrong is
    /// how a path with a space in it silently becomes two arguments.
    ///
    /// This is the escaping CommandLineToArgvW expects, which is what the
    /// runtime itself does for ArgumentList: backslashes are only special
    /// immediately before a quote, so they are doubled there and left alone
    /// everywhere else. That is why a Windows path can end in a backslash and
    /// still survive being quoted.
    /// </summary>
    public static string JoinArguments(params string[] arguments)
    {
        var text = new StringBuilder();
        foreach (string argument in arguments)
        {
            if (text.Length > 0) text.Append(' ');
            AppendArgument(text, argument ?? "");
        }

        return text.ToString();
    }

    private static void AppendArgument(StringBuilder text, string argument)
    {
        // An empty argument still has to reach the other side, and only quotes
        // can say so.
        if (argument.Length > 0 && argument.IndexOfAny(NeedsQuoting) < 0)
        {
            text.Append(argument);
            return;
        }

        text.Append('"');
        for (int i = 0; i < argument.Length; i++)
        {
            int backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == argument.Length)
            {
                // Trailing backslashes would otherwise escape the closing quote.
                text.Append('\\', backslashes * 2);
                break;
            }

            if (argument[i] == '"') text.Append('\\', backslashes * 2 + 1).Append('"');
            else text.Append('\\', backslashes).Append(argument[i]);
        }

        text.Append('"');
    }
}
