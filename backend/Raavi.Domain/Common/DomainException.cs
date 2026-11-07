namespace Raavi.Domain.Common;

/// <summary>Raised when a domain invariant is violated. Carries a stable, localizable error code.</summary>
public class DomainException : Exception
{
    public DomainException() : this("domain.error", "A domain rule was violated.") { }

    public DomainException(string message) : this("domain.error", message) { }

    public DomainException(string message, Exception innerException) : base(message, innerException)
    {
        Code = "domain.error";
    }

    public DomainException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
