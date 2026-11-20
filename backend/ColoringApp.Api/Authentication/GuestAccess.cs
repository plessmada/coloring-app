using System.Security.Cryptography;
using ColoringApp.Api.Data;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace ColoringApp.Api.Authentication;

internal sealed class GuestAccess(ApplicationDbContext database)
{
    private const string CookieName = "coloring_guest";

    public byte[]? ReadDigest(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue(CookieName, out var token) || token.Length != 43)
        {
            return null;
        }

        try
        {
            var bytes = WebEncoders.Base64UrlDecode(token);
            return bytes.Length == 32 ? SHA256.HashData(bytes) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public async Task<(GuestSession Session, string? NewToken)> GetForSubmissionAsync(
        HttpRequest request, DateTime now, CancellationToken cancellationToken)
    {
        if (ReadDigest(request) is { } digest)
        {
            var existing = await database.GuestSessions.SingleOrDefaultAsync(
                session => session.TokenDigest == digest && session.ExpiresAt > now, cancellationToken);
            if (existing is not null)
            {
                return (existing, null);
            }
        }

        var bytes = RandomNumberGenerator.GetBytes(32);
        var session = new GuestSession
        {
            Id = Guid.NewGuid(),
            TokenDigest = SHA256.HashData(bytes),
            CreatedAt = now,
            ExpiresAt = now.AddHours(24)
        };
        database.GuestSessions.Add(session);
        return (session, WebEncoders.Base64UrlEncode(bytes));
    }

    public static void SetCookie(HttpContext context, string token, DateTime expiresAt)
    {
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            Expires = expiresAt,
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/api"
        });
    }
}
