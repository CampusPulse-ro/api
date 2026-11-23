namespace CampusPulse.Api.Accounts.Email;

/// <summary>The language of the sign-in email: the app's.</summary>
public enum SignInLanguage
{
    En,
    Ro,
}

/// <summary>The sign-in code email: the code large, that it expires in 10 minutes, and to ignore it when it wasn't
/// asked for. No links (nothing to phish with).</summary>
public static class SignInEmail
{
    public static EmailMessage Compose(string to, string code, SignInLanguage language) => language == SignInLanguage.Ro
        ? Message(to, $"Codul tău CampusPulse: {code}", "Codul tău de conectare la CampusPulse este:", code,
            "Codul expiră în 10 minute. Dacă nu l-ai cerut tu, ignoră acest email.", "ro")
        : Message(to, $"Your CampusPulse code: {code}", "Your CampusPulse sign-in code is:", code,
            "It expires in 10 minutes. If you didn't ask for this, ignore this email.", "en");

    private static EmailMessage Message(string to, string subject, string intro, string code, string footer, string lang) => new(
        to,
        subject,
        $"{intro}\n\n    {code}\n\n{footer}\n",
        $"""
        <!DOCTYPE html>
        <html lang="{lang}"><head><meta charset="utf-8"><title>CampusPulse</title></head><body style="font-family:sans-serif">
        <p>{intro}</p>
        <p style="font-size:32px;font-weight:bold;letter-spacing:6px">{code}</p>
        <p>{footer}</p>
        </body></html>
        """);
}
