using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Time;
using Raavi.Domain.Common;
using Raavi.Identity.Domain;
using Raavi.Identity.Persistence;
using Raavi.Identity.Security;

namespace Raavi.Identity.Application;

public enum BootstrapOutcome
{
    NotConfigured,
    AdministratorExists,
    Created,
    TemporaryPasswordReapplied,
    InvalidSettings,
}

/// <summary>
/// Creates the first system administrator from installation settings (<c>Raavi:Authentication:Bootstrap:SystemAdministrator</c>), so that a new
/// installation does not need an operator API key and an invitation. Nothing is shipped by default: the e-mail and the temporary password come
/// from the installation's own settings. The account has no session-capable password: it opens only the first-sign-in flow, where a new password
/// (meeting the policy) is chosen and an authenticator app is enrolled (see <see cref="SystemIdentityService.FirstLoginAsync"/>).
/// Runs at API start; does nothing once any system administrator has completed the setup. Failures are logged, never fatal.
/// </summary>
public sealed partial class BootstrapAdministratorSeeder(
    IServiceScopeFactory scopes, IOptions<RaaviAuthenticationOptions> options, IClock clock, ILogger<BootstrapAdministratorSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await EnsureAsync(onlyWhenNoAdministrators: true, cancellationToken).ConfigureAwait(false);
            if (outcome is BootstrapOutcome.Created or BootstrapOutcome.TemporaryPasswordReapplied)
            {
                PendingFirstLogin(logger, outcome == BootstrapOutcome.Created ? "created" : "temporary password re-applied");
            }
            else if (outcome == BootstrapOutcome.InvalidSettings)
            {
                InvalidBootstrap(logger);
            }
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Microsoft.Data.SqlClient.SqlException or TimeoutException)
        {
            BootstrapFailed(logger, ex.GetType().Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <param name="onlyWhenNoAdministrators">The hosted service always passes true; tests that share a database with other administrators pass false.</param>
    public async Task<BootstrapOutcome> EnsureAsync(bool onlyWhenNoAdministrators, CancellationToken ct)
    {
        var settings = options.Value.Bootstrap.SystemAdministrator;
        if (!settings.IsConfigured)
        {
            return BootstrapOutcome.NotConfigured;
        }

        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
            var passwords = scope.ServiceProvider.GetRequiredService<PasswordService>();
            var now = clock.UtcNow;

            string email;
            try
            {
                email = User.ValidateEmail(settings.Email);
            }
            catch (DomainException)
            {
                return BootstrapOutcome.InvalidSettings;
            }

            var normalized = User.Normalize(email);
            var existing = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                // Still waiting for its first sign-in: apply the configured temporary password again (lets an operator fix a typo or recover by restart).
                if (existing.MustChangePassword && await db.SystemRoles.AnyAsync(r => r.UserId == existing.Id && r.Role == SystemRole.SystemAdministrator, ct).ConfigureAwait(false))
                {
                    existing.SetPasswordHash(passwords.Hash(existing, settings.InitialPassword));
                    existing.RequirePasswordChange();
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    return BootstrapOutcome.TemporaryPasswordReapplied;
                }

                return BootstrapOutcome.AdministratorExists;
            }

            if (onlyWhenNoAdministrators && await db.SystemRoles.AnyAsync(r => r.Role == SystemRole.SystemAdministrator, ct).ConfigureAwait(false))
            {
                return BootstrapOutcome.AdministratorExists;
            }

            var user = User.CreateLocal(email, string.IsNullOrWhiteSpace(settings.DisplayName) ? "Administrator" : settings.DisplayName, now);
            user.SetPasswordHash(passwords.Hash(user, settings.InitialPassword));
            user.RequirePasswordChange();
            user.MarkEmailVerified();
            db.Users.Add(user);
            db.SystemRoles.Add(SystemRoleAssignment.Grant(user.Id, SystemRole.SystemAdministrator, "bootstrap", now));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return BootstrapOutcome.Created;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Bootstrap system administrator ({State}) is waiting for its first sign-in. Sign in at /system/login with the temporary password from the settings, then remove Raavi:Authentication:Bootstrap from the settings.")]
    private static partial void PendingFirstLogin(ILogger logger, string state);

    [LoggerMessage(Level = LogLevel.Error, Message = "Raavi:Authentication:Bootstrap:SystemAdministrator:Email is not a valid address; no bootstrap administrator was created.")]
    private static partial void InvalidBootstrap(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "The bootstrap system administrator could not be created ({Error}). Is the database migrated?")]
    private static partial void BootstrapFailed(ILogger logger, string error);
}
