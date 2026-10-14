using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using Microsoft.Extensions.Options;

namespace Elsa.TieredApproval.Web;

/// <summary>Bound from the "Smtp" configuration section.</summary>
public class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 25;
    public bool EnableSsl { get; set; }
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string DefaultSender { get; set; } = "";

    /// <summary>When set, messages are written as .eml files to this directory instead of being sent over SMTP.</summary>
    public string? PickupDirectory { get; set; }
}

/// <summary>Sends HTML email with System.Net.Mail. Failures throw so the calling activity faults.</summary>
public class EmailSender(IOptions<SmtpOptions> options)
{
    public async Task SendAsync(string to, IEnumerable<string>? cc, string subject, string htmlBody, CancellationToken cancellationToken = default)
    {
        var smtp = options.Value;

        // MailMessage rejects line breaks in the subject; titles are user input, so flatten them rather than fault.
        using var message = new MailMessage(smtp.DefaultSender, to, subject.ReplaceLineEndings(" "), htmlBody)
        {
            IsBodyHtml = true,
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8
        };

        foreach (var address in cc ?? [])
            message.CC.Add(address);

        using var client = new SmtpClient();

        if (!string.IsNullOrWhiteSpace(smtp.PickupDirectory))
        {
            Directory.CreateDirectory(smtp.PickupDirectory);
            client.DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory;
            client.PickupDirectoryLocation = smtp.PickupDirectory;

            // Plain 8-bit body keeps the .eml readable (links are not quoted-printable mangled). SMTP keeps the default encoding.
            message.BodyTransferEncoding = TransferEncoding.EightBit;
        }
        else
        {
            client.Host = smtp.Host ?? throw new InvalidOperationException("Smtp:Host or Smtp:PickupDirectory must be configured.");
            client.Port = smtp.Port;
            client.EnableSsl = smtp.EnableSsl;

            if (!string.IsNullOrWhiteSpace(smtp.UserName))
                client.Credentials = new NetworkCredential(smtp.UserName, smtp.Password);
        }

        await client.SendMailAsync(message, cancellationToken);
    }
}
