using System.Diagnostics;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using OrderIntake.Application.Common;
using OrderIntake.Domain.Common;
using OrderIntake.Domain.Orders;

namespace OrderIntake.Api.Infrastructure;

/// <summary>
/// Turns exceptions into RFC 7807 ProblemDetails.
///
/// Centralising this is what lets the controllers stay free of try/catch and lets
/// the domain throw meaningful exceptions without knowing what HTTP is. Every
/// response carries a stable <c>code</c> the Angular client can branch on, because
/// matching on human-readable messages is how clients break when someone fixes a
/// typo.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private const string ProblemBaseUri = "https://orderintake.example/problems/";

    /// <summary>
    /// "Client closed request". Not an IANA code and therefore not in
    /// <c>StatusCodes</c>, but it is the established convention (nginx's) for a
    /// caller that hung up mid-request, and it keeps abandoned requests out of
    /// the 5xx bucket where they would look like our fault.
    /// </summary>
    private const int StatusClientClosedRequest = 499;

    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly IHostEnvironment _environment;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IHostEnvironment environment)
    {
        _logger = logger;
        _environment = environment;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var problem = Translate(exception);

        problem.Instance = httpContext.Request.Path;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        if (exception is IHasErrorCode coded)
        {
            problem.Extensions["code"] = coded.Code;
        }

        // Expected, rule-driven failures are information, not incidents. Only
        // genuine surprises get logged at Error, so the log stays worth reading.
        if (problem.Status >= StatusCodes.Status500InternalServerError)
        {
            _logger.LogError(exception, "Unhandled exception while handling {Method} {Path}.",
                httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            _logger.LogInformation("Request rejected with {Status}: {Detail}", problem.Status, problem.Detail);
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;

        // RFC 7807 says application/problem+json. WriteAsJsonAsync overwrites the
        // content type unless it is told one, so it is passed explicitly rather
        // than set on the response beforehand.
        await httpContext.Response.WriteAsJsonAsync(
            problem,
            options: null,
            contentType: "application/problem+json",
            cancellationToken);

        return true;
    }

    private ProblemDetails Translate(Exception exception) => exception switch
    {
        ValidationException validation => BuildValidationProblem(validation),

        DomainValidationException domain => Build(
            StatusCodes.Status400BadRequest,
            "invalid-order",
            "The order could not be accepted",
            domain.Message),

        OrderNotFoundException notFound => Build(
            StatusCodes.Status404NotFound,
            "order-not-found",
            "Order not found",
            notFound.Message),

        // 409 rather than 400: the request is well-formed, it just conflicts with
        // the current state of the resource. That distinction is what tells the
        // client "fix your payload" apart from "the world moved on".
        InvalidStatusTransitionException transition => BuildTransitionProblem(transition),

        ReferenceReusedException reused => BuildReferenceReusedProblem(reused),

        DuplicateReferenceException duplicate => Build(
            StatusCodes.Status409Conflict,
            "duplicate-reference",
            "Reference already in use",
            duplicate.Message),

        OperationCanceledException => Build(
            StatusClientClosedRequest,
            "request-cancelled",
            "Request cancelled",
            "The request was cancelled before it completed."),

        _ => Build(
            StatusCodes.Status500InternalServerError,
            "internal-error",
            "Something went wrong",
            // Never leak internals to a caller in production; in development the
            // message is far more useful than a trace id.
            _environment.IsDevelopment()
                ? exception.ToString()
                : "An unexpected error occurred. Quote the traceId when reporting it.")
    };

    private static ProblemDetails BuildValidationProblem(ValidationException exception)
    {
        var problem = Build(
            StatusCodes.Status400BadRequest,
            "validation-failed",
            "One or more fields need attention",
            "The submitted order did not pass validation. See 'errors' for details.");

        problem.Extensions["code"] = "VALIDATION_FAILED";

        // Grouped per field and returned all at once, so a form can highlight
        // every problem in one pass instead of the user fixing them one by one.
        problem.Extensions["errors"] = exception.Errors
            .GroupBy(failure => failure.PropertyName)
            .ToDictionary(
                group => ToCamelCase(group.Key),
                group => group.Select(failure => failure.ErrorMessage).Distinct().ToArray());

        return problem;
    }

    private static ProblemDetails BuildTransitionProblem(InvalidStatusTransitionException exception)
    {
        var problem = Build(
            StatusCodes.Status409Conflict,
            "invalid-status-transition",
            "That status change is not allowed",
            exception.Message);

        problem.Extensions["currentStatus"] = exception.CurrentStatus.ToString();
        problem.Extensions["requestedStatus"] = exception.TargetStatus.ToString();

        // The helpful part: tell the client what it *can* do next.
        problem.Extensions["allowedTransitions"] = exception.AllowedTransitions
            .Select(status => status.ToString())
            .ToArray();

        return problem;
    }

    private static ProblemDetails BuildReferenceReusedProblem(ReferenceReusedException exception)
    {
        var problem = Build(
            StatusCodes.Status409Conflict,
            "reference-reused",
            "That reference already belongs to a different order",
            exception.Message);

        problem.Extensions["externalReference"] = exception.ExternalReference;

        // Lets the UI link straight to the order that is in the way.
        problem.Extensions["existingOrderId"] = exception.ExistingOrderId;

        return problem;
    }

    private static ProblemDetails Build(int status, string type, string title, string detail) => new()
    {
        Status = status,
        Type = ProblemBaseUri + type,
        Title = title,
        Detail = detail
    };

    /// <summary>
    /// FluentValidation reports "Lines[0].Quantity"; the JSON body the client
    /// sent used "lines[0].quantity". Matching the wire format is what lets the
    /// Angular form bind errors back to controls without a translation table.
    /// </summary>
    private static string ToCamelCase(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            return propertyName;
        }

        var segments = propertyName.Split('.');

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];

            if (segment.Length > 0 && char.IsUpper(segment[0]))
            {
                segments[i] = char.ToLowerInvariant(segment[0]) + segment[1..];
            }
        }

        return string.Join('.', segments);
    }
}
