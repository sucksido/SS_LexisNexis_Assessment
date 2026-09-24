namespace OrderIntake.Domain.Common;

/// <summary>
/// Base type for every error the domain raises on purpose.
/// The API layer maps these to RFC 7807 ProblemDetails, so the domain never
/// needs a reference to ASP.NET Core to produce a good HTTP response.
/// </summary>
public abstract class DomainException : Exception, IHasErrorCode
{
    protected DomainException(string code, string message) : base(message)
    {
        Code = code;
    }

    /// <summary>Stable, machine-readable error code returned to API clients.</summary>
    public string Code { get; }
}

/// <summary>Raised when input violates an invariant of the domain model.</summary>
public sealed class DomainValidationException : DomainException
{
    public DomainValidationException(string message)
        : base("DOMAIN_VALIDATION_FAILED", message)
    {
    }
}
