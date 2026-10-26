using System.Net;

namespace QuickTranslate.Infrastructure.Translation;

/// <summary>One URL policy for settings validation, probes and real requests.</summary>
public static class ChatCompletionsEndpoint
{
    public static Uri Normalize(string? address)
    {
        var value = (address ?? "").Trim();
        if (value.Length == 0 || value.Any(char.IsControl)) throw InvalidAddress();
        bool hasScheme = value.Contains("://", StringComparison.Ordinal);
        if (!Uri.TryCreate(hasScheme ? value : "https://" + value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0 || value.Contains('#', StringComparison.Ordinal))
            throw InvalidAddress();

        // Reparse instead of changing UriBuilder.Scheme: that would retain HTTPS's implicit 443.
        // Reparsing preserves an explicit :443 while assigning HTTP's default port to bare hosts.
        if (!hasScheme && IsLoopback(uri.Host)) uri = new Uri("http://" + value, UriKind.Absolute);
        var builder = new UriBuilder(uri);
        var path = uri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            path += "/chat/completions";
        builder.Path = path;
        return builder.Uri;
    }

    private static bool IsLoopback(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
           || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    private static ArgumentException InvalidAddress()
        => new("API 地址须为有效 HTTP(S) 地址，且不能包含用户名、密码或片段标识。", "address");
}
