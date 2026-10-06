using L = Diga.Core.Localization.AppText;
using System.Text;
using System.Text.Json;

namespace Diga.Core.Cloud;

/// <summary>Reads only the named error fields of a provider response, never the whole body, so tokens in a response cannot reach a message.</summary>
internal static class CloudErrorDetail
{
    private const int MaximumBodyBytes = 64 * 1024;
    private const int MaximumLength = 300;

    public static string WithDetail(string message, string? detail) => detail is null ? message : L.T("Core.Cloud.ProviderDetail", message, detail);

    /// <summary>OAuth errors are {"error":"code","error_description":"text"}; Graph and Drive errors are {"error":{"code":..,"message":".."}}.</summary>
    public static async Task<(string? Code, string? Detail)> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            if (response.Content.Headers.ContentLength is > MaximumBodyBytes) return (null, null);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length is 0 or > MaximumBodyBytes) return (null, null);
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("error", out var error)) return (null, null);
            if (error.ValueKind == JsonValueKind.String) return Describe(error.GetString(), Text(document.RootElement, "error_description"));
            return error.ValueKind == JsonValueKind.Object ? Describe(Text(error, "code"), Text(error, "message")) : (null, null);
        }
        // GetString throws InvalidOperationException for text that is not valid UTF-16, such as a lone surrogate escape.
        catch (Exception ex) when (ex is JsonException or IOException or HttpRequestException or InvalidOperationException) { return (null, null); }
    }

    public static (string? Code, string? Detail) Describe(string? code, string? description)
    {
        code = Clean(code);
        description = Clean(description);
        var detail = (code, description) switch
        {
            (null, null) => null,
            (_, null) => code,
            (null, _) => description,
            _ => code + ": " + description
        };
        return (code, detail);
    }

    private static string? Text(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Microsoft appends trace, correlation and timestamp text, on new lines or after spaces; the part before it carries the reason.
    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var line = value.Trim().Split('\r', '\n')[0];
        var trace = line.IndexOf(" Trace ID:", StringComparison.Ordinal);
        if (trace > 0) line = line[..trace];
        var text = new StringBuilder(Math.Min(line.Length, MaximumLength));
        foreach (var character in line)
        {
            if (text.Length == MaximumLength) break;
            text.Append(char.IsControl(character) ? ' ' : character);
        }
        if (text.Length > 0 && char.IsHighSurrogate(text[^1])) text.Length--;
        var cleaned = text.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }
}
