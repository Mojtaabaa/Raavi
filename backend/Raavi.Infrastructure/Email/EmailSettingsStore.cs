using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MimeKit;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Abstractions.Email;
using Raavi.Infrastructure.Http;

namespace Raavi.Infrastructure.Email;

/// <summary>What the administrator edits in the application (never contains the password; <see cref="HasPassword"/> says whether one is stored).</summary>
public sealed record EmailSettingsDto(string Mode, string FromAddress, string FromName, string Host, int Port, string Security, string? UserName, bool HasPassword, int TimeoutSeconds, string Source);

public sealed record EmailSettingsRequest(string? Mode, string? FromAddress, string? FromName, string? Host, int? Port, string? Security, string? UserName, string? Password, int? TimeoutSeconds);

public sealed record EmailTestRequest(string? To);

/// <summary>
/// Installation-wide e-mail settings edited in the application (ADR-053). Saved as <c>data/email-settings.json</c> next to the key ring, the password
/// protected with ASP.NET Data Protection (the API and the Workers share the key ring), and read at send time, so a change applies to both processes
/// without a restart. Without that file the values from <c>notifications.json</c> / environment apply as before.
/// </summary>
public sealed class EmailSettingsStore(IConfiguration configuration, IDataProtectionProvider protection, IOptions<EmailOptions> fallback)
{
    private sealed record Stored(string Mode, string FromAddress, string FromName, string Host, int Port, string Security, string? UserName, string? ProtectedPassword, int TimeoutSeconds);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Lock _gate = new();
    private DateTime _stamp;
    private EmailOptions? _cached;

    private string? FilePath => configuration["Raavi:Sandbox:DataPath"] is { Length: > 0 } d ? Path.Combine(d, "email-settings.json") : null;

    private IDataProtector Protector => protection.CreateProtector("Raavi.Email.Smtp.v1");

    /// <summary>The settings in force: the saved file when there is one, else the configuration.</summary>
    public EmailOptions Current()
    {
        var path = FilePath;
        if (path is null || !File.Exists(path))
        {
            return fallback.Value;
        }

        lock (_gate)
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (_cached is not null && stamp == _stamp)
            {
                return _cached;
            }

            try
            {
                var s = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json)!;
                string? password = null;
                if (!string.IsNullOrEmpty(s.ProtectedPassword))
                {
                    password = Protector.Unprotect(s.ProtectedPassword);
                }

                _cached = new EmailOptions
                {
                    Mode = Enum.TryParse<EmailMode>(s.Mode, true, out var m) ? m : EmailMode.Disabled,
                    FromAddress = s.FromAddress,
                    FromName = s.FromName,
                    PickupDirectory = fallback.Value.PickupDirectory,
                    Smtp = new SmtpSettings { Host = s.Host, Port = s.Port, Security = s.Security, UserName = s.UserName, Password = password, TimeoutSeconds = s.TimeoutSeconds },
                };
                _stamp = stamp;
                return _cached;
            }
            catch (Exception ex) when (ex is JsonException or System.Security.Cryptography.CryptographicException or IOException)
            {
                return fallback.Value; // an unreadable file never takes mail down: configuration applies
            }
        }
    }

    public EmailSettingsDto Describe()
    {
        var o = Current();
        var saved = FilePath is { } p && File.Exists(p);
        return new EmailSettingsDto(o.Mode.ToString(), o.FromAddress, o.FromName, o.Smtp.Host, o.Smtp.Port, o.Smtp.Security, o.Smtp.UserName, !string.IsNullOrEmpty(o.Smtp.Password), o.Smtp.TimeoutSeconds, saved ? "application" : "configuration");
    }

    /// <summary>Validates and saves. A null/empty password keeps the stored one; "-" removes it.</summary>
    public string? Save(EmailSettingsRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var path = FilePath;
        if (path is null)
        {
            return "email.no_storage";
        }

        if (!Enum.TryParse<EmailMode>(r.Mode, true, out var mode))
        {
            return "email.mode_invalid";
        }

        var current = Current();
        var candidate = new EmailOptions
        {
            Mode = mode,
            FromAddress = (r.FromAddress ?? string.Empty).Trim(),
            FromName = string.IsNullOrWhiteSpace(r.FromName) ? "Raavi" : r.FromName.Trim(),
            PickupDirectory = fallback.Value.PickupDirectory,
            Smtp = new SmtpSettings
            {
                Host = (r.Host ?? string.Empty).Trim(),
                Port = r.Port ?? 587,
                Security = r.Security ?? "StartTls",
                UserName = string.IsNullOrWhiteSpace(r.UserName) ? null : r.UserName.Trim(),
                TimeoutSeconds = Math.Clamp(r.TimeoutSeconds ?? 30, 5, 120),
                Password = string.IsNullOrEmpty(r.Password) ? current.Smtp.Password : r.Password == "-" ? null : r.Password,
            },
        };
        try
        {
            candidate.Validate();
        }
        catch (OptionsValidationException)
        {
            return "email.settings_invalid";
        }

        var stored = new Stored(mode.ToString(), candidate.FromAddress, candidate.FromName, candidate.Smtp.Host, candidate.Smtp.Port, candidate.Smtp.Security, candidate.Smtp.UserName,
            string.IsNullOrEmpty(candidate.Smtp.Password) ? null : Protector.Protect(candidate.Smtp.Password), candidate.Smtp.TimeoutSeconds);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(stored, Json));
        lock (_gate)
        {
            _cached = null;
        }

        return null;
    }
}

public static class EmailSettingsEndpoints
{
    public static void MapEmailSettingsEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var g = api.MapGroup("/system/email").RequireHostKind(RaaviHostKind.System).RequireAuthorization(RaaviPolicies.SystemAdministrator);
        g.MapGet(string.Empty, (EmailSettingsStore store) => Results.Ok(store.Describe()));
        g.MapPut(string.Empty, (EmailSettingsRequest body, EmailSettingsStore store) =>
        {
            var error = store.Save(body);
            return error is null ? Results.Ok(store.Describe()) : Results.Problem(statusCode: 400, title: error, extensions: new Dictionary<string, object?> { ["code"] = error });
        });
        g.MapPost("/test", async (EmailTestRequest body, IEmailSender sender, CancellationToken ct) =>
        {
            if (!MailboxAddress.TryParse(body.To ?? string.Empty, out var to))
            {
                return Results.Problem(statusCode: 400, title: "email.address_invalid", extensions: new Dictionary<string, object?> { ["code"] = "email.address_invalid" });
            }

            if (!sender.IsEnabled)
            {
                return Results.Problem(statusCode: 409, title: "email.disabled", extensions: new Dictionary<string, object?> { ["code"] = "email.disabled" });
            }

            try
            {
                await sender.SendAsync(new EmailMessage(to.Address, to.Name ?? to.Address, "Raavi test message", "This is a test message from Raavi. E-mail is working.", string.Empty), ct).ConfigureAwait(false);
                return Results.Ok(new { sent = true });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // the reason for the administrator, never the credentials: type and message of the transport failure only
                return Results.Problem(statusCode: 502, title: "email.send_failed", detail: ex.GetType().Name + ": " + ex.Message, extensions: new Dictionary<string, object?> { ["code"] = "email.send_failed" });
            }
        });
    }
}
