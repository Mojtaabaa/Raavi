using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Raavi.Application.Abstractions.Time;
using Raavi.Identity.Domain;
using Raavi.Identity.Persistence;
using Raavi.Identity.Security;

namespace Raavi.Identity.Providers;

/// <summary>
/// Raavi's own credential store (e-mail + password). Asserts identity only; membership and authorization are decided by
/// the login service. Lockout and timing equalization prevent online guessing and account enumeration.
/// </summary>
public sealed class LocalAuthenticationProvider(string providerId, IServiceScopeFactory scopes) : IAuthenticationProvider
{
    public string ProviderId { get; } = providerId;

    public AuthenticationProviderType Type => AuthenticationProviderType.Local;

    public AuthenticationFlow SupportedFlows => AuthenticationFlow.Credentials;

    public CallbackMode CallbackMode => CallbackMode.PerHost;

    public IReadOnlyList<string> ValidateConfiguration() => [];

    public ValueTask<ProviderHealth> TestConnectionAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new ProviderHealth(true, "ok", null));

    public ValueTask<Uri> BeginRedirectAsync(RedirectRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("The local provider has no redirect flow.");

    public ValueTask<ExternalAuthenticationResult> CompleteRedirectAsync(RedirectCallback callback, CancellationToken cancellationToken) =>
        ValueTask.FromResult(ExternalAuthenticationResult.NotSupported);

    public async ValueTask<ExternalAuthenticationResult> AuthenticateWithCredentialsAsync(CredentialLogin login, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(login);
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<IdentityDbContext>();
            var passwords = sp.GetRequiredService<PasswordService>();
            var clock = sp.GetRequiredService<IClock>();
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RaaviAuthenticationOptions>>().Value.Local;

            var normalized = User.Normalize(login.Username);
            var user = normalized is null ? null : await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken).ConfigureAwait(false);
            if (user is null || user.PasswordHash is null)
            {
                passwords.SimulateVerify(login.Password);
                return ExternalAuthenticationResult.Failure("auth.unknown_user");
            }

            var now = clock.UtcNow;
            if (user.IsLockedOut(now))
            {
                passwords.SimulateVerify(login.Password);
                return ExternalAuthenticationResult.Failure("auth.locked_out");
            }

            var (valid, rehash) = passwords.Verify(user, login.Password);
            if (!valid)
            {
                user.RecordFailedLogin(now, options.LockoutThreshold, TimeSpan.FromMinutes(options.LockoutMinutes));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return ExternalAuthenticationResult.Failure(user.IsLockedOut(now) ? "auth.locked_out" : "auth.bad_password");
            }

            if (user.Status != UserStatus.Active)
            {
                return ExternalAuthenticationResult.Failure("auth.user_disabled");
            }

            if (rehash)
            {
                user.SetPasswordHash(passwords.Hash(user, login.Password));
            }

            user.RecordSuccessfulLogin(now);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ExternalAuthenticationResult.Success(new ExternalIdentity(ProviderId, user.Id.ToString(), user.Email, user.Email, user.EmailVerified, user.DisplayName, []));
        }
    }
}

public sealed class LocalAuthenticationProviderFactory(IServiceScopeFactory scopes) : IAuthenticationProviderFactory
{
    public AuthenticationProviderType Type => AuthenticationProviderType.Local;

    public IAuthenticationProvider Create(ProviderDefinition definition) => new LocalAuthenticationProvider(definition?.Id ?? "local", scopes);
}
