A small Windows utility that accepts your `mailto:` links, asks which Gmail account you want to send from, and opens Gmail's compose window in your default browser, in the right email account.

Teach it what account to route your email to (ex: *anything at this domain goes from my work account*), and it will do it automatically! Without scripting, or hand-edited configuration files.

## Why

Click a mail link on Windows and it opens whatever Windows picked years ago, usually Outlook, whether or not you have ever used it.

Pointing it at Gmail instead is the obvious fix, and it works... until you have a second account. Gmail's own handler doesn't understand multiple gmail accounts: it just sends everything to the first mailbox signed into that browser profile, and composes there without asking. And as you sign in and out of email accounts, who knows what account it's going to pick to send from next time.

This app puts the choice back in front of you: named mailboxes you pick, so the right account can send the right email.

Service wrapper clients like [Ferdium](https://ferdium.org/) are one common route to this. They host Gmail in a desktop window but cannot route a mail link to a particular account. It's a frustrating mess. But not anymore!

## The download

One file, `GmailTo.exe`, with no installer. It runs natively as 64-bit on 64-bit Windows and as 32-bit on 32-bit Windows, so there is nothing to choose between.

## Requirements

Windows 10 or 11, and nothing to install. It runs on .NET Framework 4.8, which ships with Windows 10 from the May 2019 update and with every Windows 11. On an older Windows 10 it is a [free download from Microsoft](https://dotnet.microsoft.com/download/dotnet-framework/net48).

## First run

Windows will say **"Windows protected your PC"**, because the file isn't code-signed. Click **More info**, then **Run anyway**.

Run it from Downloads, your Desktop, or a temp folder and it puts itself in `%LocalAppData%\Programs\GmailTo\` and carries on from there. Nothing to place by hand, and your download is left where it is. Put the file somewhere yourself first and it stays where you put it.

Then add an account, say yes to becoming the mail handler, and you're done.
