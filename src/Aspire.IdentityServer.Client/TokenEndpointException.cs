using System.Globalization;
using System.Net;

namespace Aspire.IdentityServer.Client;

/// <summary>
/// An error answer of a token endpoint, with its OAuth error code (RFC 6749, section 5.2), such
/// as <c>invalid_scope</c> or <c>invalid_target</c>.
/// </summary>
public sealed class TokenEndpointException : HttpRequestException
{
    /// <summary>Creates the exception for an error answer of the token endpoint.</summary>
    /// <param name="statusCode">The HTTP status of the answer.</param>
    /// <param name="error">The OAuth <c>error</c> code, when the answer carried one.</param>
    /// <param name="errorDescription">The OAuth <c>error_description</c>, when the answer carried one.</param>
    public TokenEndpointException(
        HttpStatusCode statusCode,
        string? error,
        string? errorDescription
    )
        : base(Describe(statusCode, error, errorDescription), null, statusCode)
    {
        Error = error;
        ErrorDescription = errorDescription;
    }

    /// <summary>The OAuth <c>error</c> code, when the token endpoint returned one.</summary>
    public string? Error { get; }

    /// <summary>The OAuth <c>error_description</c>, when the token endpoint returned one.</summary>
    public string? ErrorDescription { get; }

    private static string Describe(HttpStatusCode statusCode, string? error, string? description)
    {
        var status = ((int)statusCode).ToString(CultureInfo.InvariantCulture);
        if (error is null)
            return $"The token endpoint answered {status} without an OAuth error.";
        return description is null
            ? $"The token endpoint answered {status} {error}."
            : $"The token endpoint answered {status} {error}: {description}";
    }
}
