namespace ColoringApp.Api.Integrations;

internal sealed class OpenAiResponseSizeLimitHandler : DelegatingHandler
{
    private const int MaxResponseBytes = 24 * 1024 * 1024;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        try
        {
            // SDK transport uses headers-only completion. Bound the body before the SDK reads it.
            await response.Content.LoadIntoBufferAsync(MaxResponseBytes, cancellationToken);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
