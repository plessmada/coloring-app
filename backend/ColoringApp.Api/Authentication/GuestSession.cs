namespace ColoringApp.Api.Authentication;

internal sealed class GuestSession
{
    public Guid Id { get; init; }
    public byte[]? TokenDigest { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
}
