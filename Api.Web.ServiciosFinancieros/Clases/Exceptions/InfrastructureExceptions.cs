// Project : Api.Web.ServiciosFinancieros
// Created : 2025-01-01
// Modified: 2025-01-01

namespace Api.Web.ServiciosFinancieros.Clases.Exceptions;

public sealed class DatabaseException : AppException
{
    public DatabaseException(Exception? innerException = null)
        : base("database_error", 500, "Database error.", innerException)
    {
    }
}

public sealed class EncryptionException : AppException
{
    public EncryptionException(Exception? innerException = null)
        : base("encryption_error", 500, "Configuration error.", innerException)
    {
    }
}

public sealed class RecaptchaException : AppException
{
    /// <param name="innerException">Original exception that caused the failure.</param>
    public RecaptchaException(Exception? innerException = null)
        : base("recaptcha_error", 502, "Verification service unavailable.", innerException)
    {
    }

    /// <param name="message">Internal diagnostic message (never sent to the client).</param>
    /// <param name="innerException">Original exception that caused the failure.</param>
    public RecaptchaException(string message, Exception? innerException = null)
        : base("recaptcha_error", 502, "Verification service unavailable.", innerException)
    {
    }
}
