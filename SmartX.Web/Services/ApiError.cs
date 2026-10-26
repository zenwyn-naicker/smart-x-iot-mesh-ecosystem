using System.Text.Json;

namespace SmartX.Web.Services;

public static class ApiError
{
    public static bool IsExpected(Exception exception, CancellationToken cancellationToken)
    {
        return exception is HttpRequestException or InvalidOperationException or JsonException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;
    }

    public static string GetMessage(Exception exception, string fallback)
    {
        return exception is InvalidOperationException or ArgumentException
            ? exception.Message
            : fallback;
    }
}
