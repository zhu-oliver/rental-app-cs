using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rental.Business;

namespace Rental.WinForms;

internal sealed record EmailSettings(string Sender, string ProtectedKey);

internal sealed class SendGridSender(string directory) : IEmailSender
{
    private static readonly HttpClient Client = new();
    private readonly string path = Path.Combine(directory, "email-settings.json");

    public bool Configured => File.Exists(path);

    public string Sender => Configured ? Load().Sender : "";

    private EmailSettings Load() => JsonSerializer.Deserialize<EmailSettings>(File.ReadAllText(path))
        ?? throw new InvalidOperationException("Email settings are invalid. Configure SendGrid again.");

    public void Configure(string sender, string? key)
    {
        if (!MailAddress.TryCreate(sender, out _)) throw new ArgumentException("Enter a valid sender email address.");
        string protectedKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            if (!Configured) throw new ArgumentException("A SendGrid API key is required.");
            protectedKey = Load().ProtectedKey;
        }
        else
            protectedKey = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser));
        File.WriteAllText(path, JsonSerializer.Serialize(new EmailSettings(sender.Trim(), protectedKey)));
    }

    public async Task SendAsync(string recipient, string subject, string message, CancellationToken cancellationToken = default)
    {
        if (!Configured) throw new InvalidOperationException("Configure SendGrid before sending email.");
        var settings = Load();
        var key = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(settings.ProtectedKey), null, DataProtectionScope.CurrentUser));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.sendgrid.com/v3/mail/send");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(new
        {
            personalizations = new[] { new { to = new[] { new { email = recipient } } } },
            from = new { email = settings.Sender },
            subject,
            content = new[] { new { type = "text/plain", value = message } }
        });
        using var response = await Client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"SendGrid rejected the request (HTTP {(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(cancellationToken)}");
    }
}
