// Project : Api.Web.FinanceIA
// Created : 2026-04-21
// Modified: 2026-04-21

namespace Api.Web.FinanceIA.Clases.Exceptions;

/// <summary>
/// Thrown when an API-level business rule is violated (not from Dynamics).
/// The message is safe to surface to the client. Maps to HTTP 422.
/// </summary>
public sealed class BusinessException : AppException
{
    /// <param name="message">Business rule violation description, safe to send to the client.</param>
    public BusinessException(string message)
        : base("business_error", 422, message)
    {
    }
}

/// <summary>
/// Represents a single field-level validation failure.
/// </summary>
/// <param name="Field">Name of the invalid field.</param>
/// <param name="Message">Description of the validation violation.</param>
public sealed record ValidationError(string Field, string Message);

/// <summary>
/// Thrown when input validation fails. Contains a collection of field-level errors
/// safe to surface to the client. Maps to HTTP 400.
/// </summary>
public sealed class ValidationException : AppException
{
    /// <summary>Field-level validation errors.</summary>
    public IReadOnlyCollection<ValidationError> Errors { get; }

    /// <param name="errors">One or more field validation errors.</param>
    public ValidationException(IReadOnlyCollection<ValidationError> errors)
        : base("validation_error", 400, "Se produjeron uno o más errores de validación.")
    {
        Errors = errors;
    }
}
