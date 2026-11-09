using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FleetApi.Security;

/// <summary>
/// HS256 JWT，零 NuGet 依赖的手写实现。与 Spring 版 jjwt 产出的三段式
/// token 同构：header.payload.signature，payload 带 sub/iat/exp。
/// 用 BCL 而非 Microsoft.IdentityModel 是为了让这个项目离线也能编译。
/// </summary>
public static class JwtService
{
    public static string Issue(string subject, string secret, int ttlSeconds = 86400)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = B64(JsonSerializer.SerializeToUtf8Bytes(new
        {
            sub = subject,
            iat = now,
            exp = now + ttlSeconds,
        }));
        var signingInput = header + "." + payload;
        return signingInput + "." + Sign(signingInput, secret);
    }

    /// <summary>验签 + 过期检查；失败一律 false，不向调用方泄漏原因。</summary>
    public static bool Verify(string token, string secret)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return false;
        var expected = Sign(parts[0] + "." + parts[1], secret);
        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(parts[2]);
        if (a.Length != b.Length || !CryptographicOperations.FixedTimeEquals(a, b)) return false;
        try
        {
            using var doc = JsonDocument.Parse(UnB64(parts[1]));
            if (!doc.RootElement.TryGetProperty("exp", out var exp)) return false;
            return exp.GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        catch
        {
            return false;
        }
    }

    private static string Sign(string input, string secret) =>
        B64(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(input)));

    private static string B64(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] UnB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        s += new string('=', (4 - s.Length % 4) % 4);
        return Convert.FromBase64String(s);
    }
}
