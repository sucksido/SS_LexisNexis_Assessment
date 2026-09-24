namespace OrderIntake.Domain.Common;

/// <summary>
/// Implemented by any exception that carries a stable, machine-readable error code.
///
/// The API's exception handler matches on this interface rather than on a list of
/// concrete types, so a new rule in the domain or application layer gets a correct
/// HTTP response without anyone remembering to update a switch statement.
/// </summary>
public interface IHasErrorCode
{
    string Code { get; }
}
