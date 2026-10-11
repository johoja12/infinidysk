using System.Net;

namespace NzbWebDAV.Clients;

/// <summary>
/// An Arr request was answered with an unsuccessful status code. Derives from
/// <see cref="HttpRequestException"/> with the same <see cref="HttpRequestException.StatusCode"/>
/// so existing classifiers keep treating it as a live-but-erroring host, while the message
/// says which request failed instead of the generic "Response status code does not indicate success".
/// </summary>
public sealed class ArrRequestFailedException : HttpRequestException
{
    public ArrRequestFailedException(string operation, string host, HttpStatusCode statusCode, Exception? innerException)
        : base(BuildMessage(operation, host, statusCode), innerException, statusCode)
    {
        Operation = operation;
    }

    public string Operation { get; }

    private static string BuildMessage(string operation, string host, HttpStatusCode statusCode)
    {
        // Scheme and authority only: never echo query strings or user info from the configured URL.
        var instance = Uri.TryCreate(host, UriKind.Absolute, out var uri)
            ? uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.SafeUnescaped)
            : "the configured instance";
        return $"{operation} request to {instance} returned HTTP {(int)statusCode} ({statusCode}); see that instance's logs for the cause.";
    }
}
