// Project : Api.Web.FinanceIA
// Created : 2025-01-01
// Modified: 2025-01-01

namespace Api.Web.FinanceIA.Clases.Exceptions;

public abstract class AppException : Exception
{
    public string ErrorCode { get; }
    public int HttpStatusCode { get; }

    /// <summary>
    /// Safe, client-facing message. Never contains internal details,
    /// stack traces or infrastructure information.
    /// </summary>
    public string ClientMessage => Message;

    protected AppException(
        string errorCode,
        int httpStatusCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        HttpStatusCode = httpStatusCode;
    }
}

public abstract class ExternalServiceException : AppException
{
    public string ServiceName { get; }

    protected ExternalServiceException(
        string serviceName,
        string errorCode,
        int httpStatusCode,
        string message,
        Exception? innerException = null)
        : base(errorCode, httpStatusCode, message, innerException)
    {
        ServiceName = serviceName;
    }
}
