// Project : Api.Web.FinanceIA
// Created : 2026-04-20
// Modified: 2026-04-20

namespace Api.Web.FinanceIA.Clases.Exceptions;

/// <summary>
/// Thrown when a Firebase ID token is invalid, expired, or issued by the wrong project.
/// Maps to HTTP 401.
/// </summary>
public sealed class FirebaseAuthException : AppException
{
    /// <param name="innerException">Original Firebase SDK exception, if available.</param>
    public FirebaseAuthException(Exception? innerException = null)
        : base("firebase_auth_error", 401, "Falló la autenticación.", innerException)
    {
    }

    /// <param name="message">Internal diagnostic message (never sent to the client).</param>
    /// <param name="innerException">Original Firebase SDK exception.</param>
    public FirebaseAuthException(string message, Exception? innerException = null)
        : base("firebase_auth_error", 401, "Falló la autenticación.", innerException)
    {
    }
}

/// <summary>
/// Thrown when the Firebase Admin SDK fails for non-authentication reasons,
/// such as FirebaseApp initialization failure, Firestore write failure,
/// or user creation failure. Maps to HTTP 502.
/// </summary>
public sealed class FirebaseServiceException : ExternalServiceException
{
    /// <param name="message">Internal diagnostic message (never sent to the client).</param>
    /// <param name="innerException">Original exception that caused the failure.</param>
    public FirebaseServiceException(string message, Exception? innerException = null)
        : base("Firebase", "firebase_service_error", 502,
               "Error al conectar con los servicios de identidad.", innerException)
    {
    }
}

/// <summary>
/// Thrown when an internal JWT could not be generated.
/// Maps to HTTP 500.
/// </summary>
public sealed class TokenGenerationException : AppException
{
    /// <param name="innerException">Original exception that caused the failure.</param>
    public TokenGenerationException(Exception? innerException = null)
        : base("token_generation_error", 500, "Authentication error.", innerException)
    {
    }

    /// <param name="message">Internal diagnostic message (never sent to the client).</param>
    /// <param name="innerException">Original exception that caused the failure.</param>
    public TokenGenerationException(string message, Exception? innerException = null)
        : base("token_generation_error", 500, "Authentication error.", innerException)
    {
    }
}
