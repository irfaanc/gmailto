using System.Diagnostics;
using System.Windows.Forms;

namespace GmailTo;

internal static class Mail
{
    /// <summary>
    /// Opens the Gmail compose window for this message in the default browser.
    ///
    /// Worth being precise about what this does and does not do: it opens a
    /// *draft*. Nothing is sent. That is what makes forwarding without asking
    /// defensible, since a rule that fires wrongly produces a visible draft in
    /// the wrong account rather than a delivered message.
    /// </summary>
    /// <returns>False if the browser could not be launched, having said so.</returns>
    public static bool Open(MailtoRequest request, Profile profile, IWin32Window? owner)
    {
        string url = request.ToGmailComposeUrl(profile.EmailAddress);
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            string message = "Could not open the browser:\r\n\r\n" + ex.Message + "\r\n\r\nURL:\r\n" + url;
            if (owner is null)
                MessageBox.Show(message, "gmailto", MessageBoxButtons.OK, MessageBoxIcon.Error);
            else
                MessageBox.Show(owner, message, "gmailto", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }
}
