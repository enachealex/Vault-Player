using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace VideoPlayer.Rendezvous.Vault;

/// <summary>
/// Sends account email over SMTP, so any provider works by setting env vars:
///   SMTP_HOST, SMTP_PORT, SMTP_USER, SMTP_PASS, SMTP_FROM, SMTP_FROM_NAME.
///
/// With no SMTP_HOST configured it writes each message to a dev outbox file
/// instead of sending, so the whole verification / reset flow is testable
/// locally with no provider account.
/// </summary>
public class EmailSender(IConfiguration cfg, ILogger<EmailSender> log)
{
    private string? Host => cfg["SMTP_HOST"];
    private int Port => int.TryParse(cfg["SMTP_PORT"], out var p) ? p : 587;
    private string From => cfg["SMTP_FROM"] ?? "no-reply@thejumpvault.com";
    private string FromName => cfg["SMTP_FROM_NAME"] ?? "Vault Movies";

    public async Task SendAsync(string toEmail, string subject, string body)
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            // Dev: no provider configured — record it so flows can be verified.
            var outbox = Path.Combine(AppContext.BaseDirectory, "dev-outbox.log");
            await File.AppendAllTextAsync(outbox,
                $"--- {DateTime.UtcNow:o}\nTo: {toEmail}\nSubject: {subject}\n\n{body}\n\n");
            log.LogInformation("Email (dev outbox) to {Email}: {Subject}", toEmail, subject);
            return;
        }

        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(FromName, From));
        msg.To.Add(MailboxAddress.Parse(toEmail));
        msg.Subject = subject;
        msg.Body = new TextPart("plain") { Text = body };

        using var client = new SmtpClient();
        // Port 465 = implicit TLS; anything else = STARTTLS.
        var security = Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
        await client.ConnectAsync(Host, Port, security);
        if (cfg["SMTP_USER"] is { Length: > 0 } user)
            await client.AuthenticateAsync(user, cfg["SMTP_PASS"] ?? "");
        await client.SendAsync(msg);
        await client.DisconnectAsync(true);
    }

    // ---- Templates ---------------------------------------------------------

    public Task SendVerifyCodeAsync(string email, string code) => SendAsync(email,
        "Your Vault Movies verification code",
        $"Welcome to Vault Movies.\n\nYour verification code is:\n\n    {code}\n\n" +
        "Enter it in the app to finish creating your account. It expires in 20 minutes.\n\n" +
        "If you didn't request this, you can ignore this email.");

    public Task SendWelcomeAsync(string email, string name) => SendAsync(email,
        "Welcome to Vault Movies",
        $"Hi {name},\n\nYour account is ready. Your library — resume points and watch counts — " +
        "now follows you to any computer you sign in on.\n\nEnjoy the movies.");

    public Task SendResetCodeAsync(string email, string code) => SendAsync(email,
        "Reset your Vault Movies password",
        $"We received a request to reset your password.\n\nYour reset code is:\n\n    {code}\n\n" +
        "Enter it in the app with your new password. It expires in 20 minutes.\n\n" +
        "If you didn't request this, you can ignore this email — your password won't change.");

    public Task SendDeletedAsync(string email, string name) => SendAsync(email,
        "Your Vault Movies account was deleted",
        $"Hi {name},\n\nYour Vault Movies account and its synced library have been deleted. " +
        "This can't be undone.\n\nIf this wasn't you, reply to this email right away.");
}
