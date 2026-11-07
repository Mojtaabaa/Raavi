using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MimeKit;
using Raavi.Application.Abstractions.Email;

namespace Raavi.Infrastructure.Email;

public enum EmailMode
{
    Disabled = 0,

    /// <summary>Deliver through an SMTP relay (MailKit).</summary>
    Smtp = 1,

    /// <summary>Write .eml files to a directory (development, or a relay that watches a pickup folder).</summary>
    Pickup = 2,
}

public sealed class SmtpSettings
{
    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    /// <summary><c>StartTls</c> (default), <c>SslOnConnect</c> or <c>None</c> (trusted internal relays only).</summary>
    public string Security { get; set; } = "StartTls";

    public string? UserName { get; set; }

    /// <summary>Set through <c>RAAVI_Raavi__Email__Smtp__Password</c> or a secret store; never logged.</summary>
    public string? Password { get; set; }

    public int TimeoutSeconds { get; set; } = 30;
}

public sealed class EmailOptions
{
    public const string Section = "Raavi:Email";

    public EmailMode Mode { get; set; } = EmailMode.Disabled;

    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = "Raavi";

    public SmtpSettings Smtp { get; set; } = new();

    public string PickupDirectory { get; set; } = string.Empty;

    public void Validate()
    {
        if (Mode == EmailMode.Disabled)
        {
            return;
        }

        if (!MailboxAddress.TryParse(FromAddress, out _))
        {
            throw new OptionsValidationException(Section, typeof(EmailOptions), [$"{Section}:FromAddress must be a valid address when e-mail is enabled."]);
        }

        if (Mode == EmailMode.Smtp && (string.IsNullOrWhiteSpace(Smtp.Host) || Smtp.Port is <= 0 or > 65535))
        {
            throw new OptionsValidationException(Section, typeof(EmailOptions), [$"{Section}:Smtp:Host and Port are required in Smtp mode."]);
        }

        if (Mode == EmailMode.Smtp && Smtp.Security is not ("StartTls" or "SslOnConnect" or "None"))
        {
            throw new OptionsValidationException(Section, typeof(EmailOptions), [$"{Section}:Smtp:Security must be StartTls, SslOnConnect or None."]);
        }

        if (Mode == EmailMode.Pickup && string.IsNullOrWhiteSpace(PickupDirectory))
        {
            throw new OptionsValidationException(Section, typeof(EmailOptions), [$"{Section}:PickupDirectory is required in Pickup mode."]);
        }
    }
}

/// <summary>MailKit-based sender for both SMTP and pickup-directory delivery.</summary>
public sealed class MailKitEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public bool IsEnabled => _options.Mode != EmailMode.Disabled;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!IsEnabled)
        {
            return;
        }

        using var mime = Build(message);
        if (_options.Mode == EmailMode.Pickup)
        {
            Directory.CreateDirectory(_options.PickupDirectory);
            var path = Path.Combine(_options.PickupDirectory, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.eml");
            await mime.WriteToAsync(path, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var client = new SmtpClient { Timeout = _options.Smtp.TimeoutSeconds * 1000 };
        var security = _options.Smtp.Security switch
        {
            "SslOnConnect" => SecureSocketOptions.SslOnConnect,
            "None" => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };
        await client.ConnectAsync(_options.Smtp.Host, _options.Smtp.Port, security, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(_options.Smtp.UserName))
        {
            await client.AuthenticateAsync(_options.Smtp.UserName, _options.Smtp.Password ?? string.Empty, cancellationToken).ConfigureAwait(false);
        }

        await client.SendAsync(mime, cancellationToken).ConfigureAwait(false);
        await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
    }

    private MimeMessage Build(EmailMessage message)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        mime.To.Add(new MailboxAddress(message.ToName, message.To));
        mime.Subject = message.Subject;
        mime.Headers.Add("Auto-Submitted", "auto-generated");
        mime.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();
        return mime;
    }
}

public static class EmailServiceCollectionExtensions
{
    public static IServiceCollection AddRaaviEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EmailOptions>().Bind(configuration.GetSection(EmailOptions.Section)).Validate(o =>
        {
            o.Validate();
            return true;
        }).ValidateOnStart();
        services.AddSingleton<IEmailSender, MailKitEmailSender>();
        return services;
    }
}
