using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ColoringApp.Api.Authentication;
using ColoringApp.Api.Data;
using ColoringApp.Api.Generations;
using ColoringApp.Api.Integrations;
using ColoringApp.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ColoringApp.Api.Tests;

[Collection("Foundation database")]
public sealed class GenerationTests(DatabaseFixture fixture)
{
    private const string Endpoint = "/api/generations";
    private const string Origin = "http://localhost:4200";
    private static readonly CancellationToken cancellation = CancellationToken.None;

    [Theory]
    [InlineData("success", "Succeeded", null, "transcribe,moderate_transcript,enrich,moderate_prompt,review_prompt,generate_image,moderate_image")]
    [InlineData("profanity", "Rejected", "content_rejected", "transcribe,moderate_transcript,enrich", "assessment", "profanity")]
    [InlineData("graphic", "Rejected", "content_rejected", "transcribe,moderate_transcript", "transcript_moderation", "moderation_flagged", "violence/graphic")]
    [InlineData("silence", "Failed", "unintelligible_audio", "transcribe")]
    [InlineData("unusable_audio", "Failed", "unintelligible_audio", "transcribe")]
    [InlineData("foreign_language", "Failed", "unintelligible_audio", "transcribe")]
    [InlineData("bad_enrichment", "Failed", "generation_failed", "transcribe,moderate_transcript,enrich")]
    [InlineData("prompt_flagged", "Rejected", "content_rejected", "transcribe,moderate_transcript,enrich,moderate_prompt", "prompt_moderation", "moderation_flagged", "violence/graphic")]
    [InlineData("prompt_rejected", "Rejected", "content_rejected", "transcribe,moderate_transcript,enrich,moderate_prompt,review_prompt", "prompt_review", "subject_changed")]
    [InlineData("image_blocked", "Rejected", "content_rejected", "transcribe,moderate_transcript,enrich,moderate_prompt,review_prompt,generate_image", "image_generation", "moderation_blocked", "violence", "output", "req_test_image")]
    [InlineData("bad_png", "Failed", "generation_failed", "transcribe,moderate_transcript,enrich,moderate_prompt,review_prompt,generate_image", "image_verification", "invalid_png")]
    [InlineData("wrong_dimensions", "Failed", "generation_failed", "transcribe,moderate_transcript,enrich,moderate_prompt,review_prompt,generate_image")]
    [InlineData("corrupt_png", "Failed", "generation_failed", "transcribe,moderate_transcript,enrich,moderate_prompt,review_prompt,generate_image")]
    [InlineData("corrupt_crc", "Failed", "generation_failed", "transcribe,moderate_transcript,enrich,moderate_prompt,review_prompt,generate_image")]
    [InlineData("image_rejected", "Rejected", "content_rejected", "transcribe,moderate_transcript,enrich,moderate_prompt,review_prompt,generate_image,moderate_image", "image_moderation", "moderation_flagged", "violence")]
    [InlineData("image_check_error", "Failed", "generation_failed", "transcribe,moderate_transcript,enrich,moderate_prompt,review_prompt,generate_image,moderate_image", "image_moderation", "provider_request_failed")]
    [InlineData("image_check_timeout", "Failed", "generation_failed", "transcribe,moderate_transcript,enrich,moderate_prompt,review_prompt,generate_image,moderate_image", "image_moderation", "stage_timeout")]
    public async Task Pipeline_stops_at_the_decision_and_removes_temporary_audio(
        string scenario, string status, string? code, string expectedCalls,
        string? expectedStage = null, string? expectedReason = null, string expectedCategories = "-",
        string expectedModerationStage = "-", string expectedRequestId = "-")
    {
        using var files = new GenerationTestEnvironment();
        var provider = new ControlledGenerationProvider();
        provider.Select(scenario);
        var log = new ProcessorLog();
        await using var baseApi = Api(files, provider);
        await using var api = baseApi.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ILogger<GenerationProcessor>>(log)));
        using var client = api.CreateClient();
        var (id, _) = await SubmitAsync(client, GenerationTestEnvironment.Recording);

        var terminal = await WaitForAsync(async () =>
        {
            var info = await client.GetFromJsonAsync<JobStatus>($"{Endpoint}/{id}", cancellation);
            return info?.Status == status ? info : null;
        });

        Assert.Equal(code, terminal.ErrorCode);
        Assert.Equal(expectedCalls, string.Join(',', provider.Calls(id)));
        if (expectedStage is not null)
        {
            Assert.Contains($"Generation {id} stopped at {expectedStage} with {code}; reason {expectedReason}; moderation categories {expectedCategories}; OpenAI moderation stage {expectedModerationStage}; OpenAI request ID {expectedRequestId}",
                log.Messages);
        }
        await WaitForAsync(() => Task.FromResult(files.CountFiles("recordings") == 0 ? terminal : null));
        Assert.Empty(files.ListFiles("staged"));
        var job = await ReadJobAsync(id);
        using var image = await client.GetAsync($"{Endpoint}/{id}/image", cancellation);
        if (status == "Succeeded")
        {
            Assert.Equal(HttpStatusCode.OK, image.StatusCode);
            Assert.Single(files.ListFiles("approved"));
            Assert.Equal("  Smok czyta książkę rycerzowi.  ", job.Transcript);
            Assert.Contains("No shading", job.FinalPrompt);
            Assert.Equal(new[] { "smok", "rycerz", "książka" }, job.Keywords);
        }
        else
        {
            Assert.Equal(HttpStatusCode.NotFound, image.StatusCode);
            Assert.Empty(files.ListFiles("approved"));
            Assert.Null(terminal.ImageUrl);
            Assert.Null(job.Transcript);
            Assert.Null(job.FinalPrompt);
            Assert.Null(job.Keywords);
            Assert.Null(job.ImageKey);
        }
    }

    [Fact]
    public async Task Missing_OpenAI_credentials_leave_health_usable_and_fail_generation_safely()
    {
        using var files = new GenerationTestEnvironment();
        await using var api = new GenerationApiFactory(fixture.Database.ConnectionString, files.StorageRoot, null);
        using var client = api.CreateClient();
        using var health = await client.GetAsync("/api/health/ready", cancellation);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var (id, _) = await SubmitAsync(client, GenerationTestEnvironment.Recording);
        await WaitForAsync(async () => (await ReadJobAsync(id)).AudioKey is null ? new object() : null);
        var status = await client.GetFromJsonAsync<JobStatus>($"{Endpoint}/{id}", cancellation);
        Assert.NotNull(status);
        Assert.Equal("Failed", status.Status);
        Assert.Equal("generation_failed", status.ErrorCode);
        Assert.Empty(files.ListFiles("recordings"));
        Assert.Empty(files.ListFiles("approved"));
    }

    [Fact]
    public async Task Https_submission_sets_secure_cookie_and_database_stores_only_the_digest()
    {
        using var files = new GenerationTestEnvironment();
        await using var api = Api(files, new ControlledGenerationProvider());
        using var client = api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var accepted = await PostAsync(client, GenerationTestEnvironment.Recording);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var cookie = Assert.Single(accepted.Headers.GetValues("Set-Cookie"));
        Assert.Contains("; secure", cookie, StringComparison.OrdinalIgnoreCase);
        var token = cookie.Split(';')[0].Split('=')[1];
        var bytes = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(token);
        Assert.Equal(32, bytes.Length);
        var submitted = await accepted.Content.ReadFromJsonAsync<JobStatus>(cancellation);
        Assert.NotNull(submitted);
        var job = await ReadJobAsync(submitted.Id);
        var digest = await fixture.Database.ScalarAsync($"SELECT token_digest FROM coloring_app.guest_sessions WHERE id = '{job.GuestSessionId}'");
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(bytes), Assert.IsType<byte[]>(digest));
        await WaitForAsync(async () => (await ReadJobAsync(job.Id)).AudioKey is null ? new object() : null);
    }

    [Fact]
    public async Task Foreign_expired_and_nonexistent_results_are_identical_and_success_survives_api_restart()
    {
        using var files = new GenerationTestEnvironment();
        var provider = new ControlledGenerationProvider();
        var recording = GenerationTestEnvironment.Recording;
        Guid id;
        string cookie;
        await using (var api = Api(files, provider))
        {
            using var owner = api.CreateClient();
            using var foreign = api.CreateClient();
            (id, cookie) = await SubmitAsync(owner, recording);
            await SubmitAsync(foreign, recording);
            await WaitForAsync(async () => (await ReadJobAsync(id)).Status == GenerationStatus.Succeeded ? new object() : null);

            using var ownerPreview = await owner.GetAsync($"{Endpoint}/{id}/image", cancellation);
            using var ownerDownload = await owner.GetAsync($"{Endpoint}/{id}/image?download=true", cancellation);
            Assert.Equal(HttpStatusCode.OK, ownerPreview.StatusCode);
            Assert.Equal(HttpStatusCode.OK, ownerDownload.StatusCode);
            Assert.Equal("no-store", ownerPreview.Headers.CacheControl?.ToString());
            Assert.Equal("image/png", ownerDownload.Content.Headers.ContentType?.MediaType);
            Assert.Equal("attachment", ownerDownload.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal($"kolorowanka-{id:N}.png", ownerDownload.Content.Headers.ContentDisposition?.FileNameStar);
            await AssertUnavailableAsync(foreign, id);
            await AssertUnavailableAsync(owner, Guid.NewGuid());

            using var again = await PostAsync(owner, recording);
            Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
            Assert.False(again.Headers.Contains("Set-Cookie"));
            var another = (await again.Content.ReadFromJsonAsync<JobStatus>(cancellation))!.Id;
            Assert.Equal((await ReadJobAsync(id)).GuestSessionId, (await ReadJobAsync(another)).GuestSessionId);
            await WaitForAsync(async () => await fixture.Database.ScalarAsync(
                $"SELECT count(*) FROM coloring_app.generations WHERE audio_key IS NOT NULL AND guest_session_id IN (SELECT id FROM coloring_app.guest_sessions)") is 0L
                    ? new object() : null);
        }

        await using var restarted = Api(files, provider);
        using var browser = ExplicitCookieClient(restarted, cookie);
        using var anonymous = ExplicitCookieClient(restarted, null);
        await AssertUnavailableAsync(anonymous, id);
        using var preview = await browser.GetAsync($"{Endpoint}/{id}/image", cancellation);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var before = await ReadJobAsync(id);
        await fixture.Database.ScalarAsync($"""
            UPDATE coloring_app.guest_sessions
            SET created_at = statement_timestamp() - interval '25 hours', expires_at = statement_timestamp() - interval '1 hour'
            WHERE id = '{before.GuestSessionId}'
            """);
        await AssertUnavailableAsync(browser, id);

        await restarted.Services.GetRequiredService<GenerationCleanup>().SweepAsync(cancellation);
        var after = await ReadJobAsync(id);
        Assert.Equal(before.Transcript, after.Transcript);
        Assert.Equal(before.FinalPrompt, after.FinalPrompt);
        Assert.Equal(before.Keywords, after.Keywords);
        Assert.Equal(before.ImageKey, after.ImageKey);
        Assert.True(File.Exists(Path.Combine(files.StorageRoot, "approved", $"{after.ImageKey:N}.png")));
        Assert.True(await fixture.Database.ScalarAsync(
            $"SELECT token_digest IS NULL FROM coloring_app.guest_sessions WHERE id = '{before.GuestSessionId}'") is true);
        Assert.Single(provider.Calls(id), call => call == "generate_image");
    }

    [Fact]
    public async Task Validates_upload_and_forwards_original_bytes_to_transcription()
    {
        using var files = new GenerationTestEnvironment();
        var provider = new ControlledGenerationProvider();
        provider.Select("verify_recording");
        await using var api = Api(files, provider);
        using var client = api.CreateClient();
        var normal = GenerationTestEnvironment.Recording;
        using var wrongOrigin = await PostAsync(client, normal, origin: "http://foreign.test");
        await AssertProblemAsync(wrongOrigin, HttpStatusCode.Forbidden, "origin_forbidden");
        using var missingOrigin = await PostAsync(client, normal, origin: null);
        await AssertProblemAsync(missingOrigin, HttpStatusCode.Forbidden, "origin_forbidden");
        using var unsupportedRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new ByteArrayContent(normal)
        };
        unsupportedRequest.Headers.Add("Origin", Origin);
        using var unsupported = await client.SendAsync(unsupportedRequest, cancellation);
        await AssertProblemAsync(unsupported, HttpStatusCode.UnsupportedMediaType, "unsupported_media");
        using var empty = await PostAsync(client, []);
        await AssertProblemAsync(empty, HttpStatusCode.BadRequest, "invalid_recording");
        using var tooBig = await PostAsync(client, new byte[5 * 1024 * 1024 + 1]);
        await AssertProblemAsync(tooBig, HttpStatusCode.RequestEntityTooLarge, "recording_too_large");
        using var duplicate = await PostAsync(client, normal, duplicate: true);
        await AssertProblemAsync(duplicate, HttpStatusCode.BadRequest, "invalid_recording");
        Assert.Empty(files.ListFiles("recordings"));
        var (id, _) = await SubmitAsync(client, normal);
        await WaitForAsync(async () =>
        {
            var job = await ReadJobAsync(id);
            return job.Status == GenerationStatus.Succeeded && job.AudioKey is null ? job : null;
        });
        Assert.Single(provider.Calls(id), stage => stage == "transcribe");
    }

    [Fact]
    public async Task Expired_owner_cannot_publish_and_pending_job_survives_api_restart()
    {
        using var files = new GenerationTestEnvironment();
        var provider = new ControlledGenerationProvider();
        provider.Select("pause_image");
        Guid active;
        Guid pending;
        string cookie;
        await using (var api = Api(files, provider, useDefaultPolling: true))
        {
            using var client = api.CreateClient();
            var recording = GenerationTestEnvironment.Recording;
            (active, cookie) = await SubmitAsync(client, recording);
            await WaitForAsync(() => Task.FromResult(provider.Calls(active).Contains("generate_image") ? new object() : null));
            (pending, _) = await SubmitAsync(client, recording, expectNewCookie: false);
            Assert.Equal(GenerationStatus.Pending, (await ReadJobAsync(pending)).Status);
        }

        var competingProvider = new ControlledGenerationProvider();
        await using var restarted = Api(files, competingProvider, useDefaultPolling: true);
        using var browser = ExplicitCookieClient(restarted, cookie);
        await WaitForAsync(async () => (await ReadJobAsync(pending)).Status == GenerationStatus.Succeeded ? new object() : null);
        Assert.Equal(GenerationStatus.Processing, (await ReadJobAsync(active)).Status);
        await ExpireLeaseAsync(active);
        await WaitForAsync(async () => (await ReadJobAsync(active)).AudioKey is null ? new object() : null,
            TimeSpan.FromSeconds(40));
        var failure = await browser.GetFromJsonAsync<JobStatus>($"{Endpoint}/{active}", cancellation);
        Assert.NotNull(failure);
        Assert.Equal("Failed", failure.Status);
        Assert.Equal("generation_failed", failure.ErrorCode);
        using var denied = await browser.GetAsync($"{Endpoint}/{active}/image", cancellation);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        Assert.Single(provider.Calls(active), call => call == "generate_image");
        Assert.Empty(competingProvider.Calls(active));
        Assert.Single(competingProvider.Calls(pending), call => call == "generate_image");
        Assert.Empty(files.ListFiles("recordings"));
        Assert.Single(files.ListFiles("approved"));
    }

    [Fact]
    public async Task Two_claimers_race_and_cleanup_retries_deletion_rejects_late_publication_and_preserves_live_files()
    {
        using var files = new GenerationTestEnvironment();
        var contexts = Contexts();
        var queue = new GenerationQueue(contexts);
        var id = Guid.NewGuid();
        await SeedAsync(id);
        var claims = await Task.WhenAll(queue.ClaimAsync(cancellation), queue.ClaimAsync(cancellation));
        var claim = Assert.Single(claims.OfType<ClaimedGeneration>());
        Assert.Equal(id, claim.Id);
        Assert.True(await queue.RenewAsync(claim, cancellation));
        Assert.Empty(await queue.ExpireAsync(cancellation));

        await using var api = Api(files, new ControlledGenerationProvider());
        var storage = api.Services.GetRequiredService<PrivateGenerationFiles>();
        var cleanup = new GenerationCleanup(contexts, queue, storage, GenerationTestEnvironment.Polling, NullLogger<GenerationCleanup>.Instance);
        var audioPath = storage.PathFor(GenerationFileKind.Audio, id);
        Directory.CreateDirectory(audioPath); // File.Delete can't remove this directory.
        await ExpireLeaseAsync(id);
        await WaitForAsync(async () => (await ReadJobAsync(id)).Status == GenerationStatus.Failed ? new object() : null);
        await cleanup.SweepAsync(cancellation);
        Assert.Equal(id, (await ReadJobAsync(id)).AudioKey);
        Assert.False(await queue.RenewAsync(claim, cancellation));
        Assert.False(await queue.SucceedAsync(claim, new ApprovedGeneration("late", "late", ["late"], []), Guid.NewGuid(), cancellation));

        Directory.Delete(audioPath);
        await File.WriteAllBytesAsync(audioPath, [1], cancellation);
        await cleanup.SweepAsync(cancellation);
        Assert.False(File.Exists(audioPath));
        Assert.Null((await ReadJobAsync(id)).AudioKey);
        await fixture.Database.ScalarAsync($"UPDATE coloring_app.generations SET completed_at = clock_timestamp() - interval '25 hours' WHERE id = '{id}'");
        await cleanup.SweepAsync(cancellation);
        Assert.Equal(0L, await fixture.Database.ScalarAsync($"SELECT count(*) FROM coloring_app.generations WHERE id = '{id}'"));
    }

    [Fact]
    public async Task Cleanup_keeps_referenced_files_and_removes_only_old_orphans()
    {
        using var files = new GenerationTestEnvironment();
        await using var api = Api(files, new ControlledGenerationProvider());
        var storage = api.Services.GetRequiredService<PrivateGenerationFiles>();
        var contexts = Contexts();
        var queue = new GenerationQueue(contexts);
        var cleanup = new GenerationCleanup(contexts, queue, storage, GenerationTestEnvironment.Polling, NullLogger<GenerationCleanup>.Instance);

        var pending = Guid.NewGuid();
        var succeeded = Guid.NewGuid();
        var orphanAudio = Guid.NewGuid();
        var orphanImage = Guid.NewGuid();
        var orphanStage = Guid.NewGuid();
        var freshStage = Guid.NewGuid();
        await SeedAsync(pending, old: true);
        await fixture.Database.ScalarAsync($"""
            UPDATE coloring_app.generations SET created_at = clock_timestamp() - interval '29 minutes',
                status = 'Processing', started_at = clock_timestamp(), owner_token = '{Guid.NewGuid()}',
                lease_until = clock_timestamp() + interval '1 minute', deadline_at = clock_timestamp() + interval '5 minutes'
            WHERE id = '{pending}'
            """);
        await SeedAsync(succeeded, old: true);
        var approvedKey = Guid.NewGuid();
        await fixture.Database.ScalarAsync($"""
            UPDATE coloring_app.generations SET status = 'Succeeded', started_at = clock_timestamp(),
                completed_at = clock_timestamp(), audio_key = NULL, transcript = 'smok', final_prompt = 'smok',
                keywords = ARRAY['smok'], image_key = '{approvedKey}' WHERE id = '{succeeded}'
            """);

        foreach (var (kind, key) in new[]
        {
            (GenerationFileKind.Audio, pending), (GenerationFileKind.Image, approvedKey),
            (GenerationFileKind.Audio, orphanAudio), (GenerationFileKind.Image, orphanImage),
            (GenerationFileKind.Staged, orphanStage), (GenerationFileKind.Staged, freshStage)
        })
        {
            using (storage.Create(kind, key)) { }
            if (key != freshStage)
            {
                File.SetLastWriteTimeUtc(storage.PathFor(kind, key), DateTime.UtcNow.AddHours(-2));
            }
        }

        await cleanup.SweepAsync(cancellation);
        Assert.Equal(storage.PathFor(GenerationFileKind.Audio, pending), Assert.Single(files.ListFiles("recordings")));
        Assert.Equal(storage.PathFor(GenerationFileKind.Image, approvedKey), Assert.Single(files.ListFiles("approved")));
        Assert.Equal(storage.PathFor(GenerationFileKind.Staged, freshStage), Assert.Single(files.ListFiles("staged")));
        await ExpireLeaseAsync(pending);
        await WaitForAsync(async () => (await ReadJobAsync(pending)).AudioKey is null ? new object() : null);
    }

    [Fact]
    public async Task Database_failure_after_publication_withholds_the_file_and_cleanup_reconciles_it()
    {
        using var files = new GenerationTestEnvironment();
        var provider = new ControlledGenerationProvider();
        await fixture.Database.ScalarAsync("""
            ALTER TABLE coloring_app.generations ADD CONSTRAINT simulate_publication_failure
            CHECK (status <> 'Succeeded') NOT VALID
            """);
        try
        {
            await using var api = Api(files, provider);
            using var client = api.CreateClient();
            var (id, _) = await SubmitAsync(client, GenerationTestEnvironment.Recording);
            await WaitForAsync(async () => (await ReadJobAsync(id)).AudioKey is null ? new object() : null);
            var state = await client.GetFromJsonAsync<JobStatus>($"{Endpoint}/{id}", cancellation);
            Assert.NotNull(state);
            Assert.Equal("Failed", state.Status);
            Assert.Equal("generation_failed", state.ErrorCode);
            using var image = await client.GetAsync($"{Endpoint}/{id}/image", cancellation);
            Assert.Equal(HttpStatusCode.NotFound, image.StatusCode);
            Assert.Single(provider.Calls(id), stage => stage == "generate_image");
            var orphan = Assert.Single(files.ListFiles("approved"));
            Assert.Empty(files.ListFiles("staged"));
            Assert.Empty(files.ListFiles("recordings"));
            File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddHours(-2));
            await api.Services.GetRequiredService<GenerationCleanup>().SweepAsync(cancellation);
            Assert.Empty(files.ListFiles("approved"));
        }
        finally
        {
            await fixture.Database.ScalarAsync("ALTER TABLE coloring_app.generations DROP CONSTRAINT simulate_publication_failure");
        }
    }

    private async Task SeedAsync(Guid id, bool old = false)
    {
        await using var database = Contexts().CreateDbContext();
        var now = DateTime.UtcNow;
        var guest = new GuestSession { Id = Guid.NewGuid(), CreatedAt = now, ExpiresAt = now.AddHours(24) };
        database.GuestSessions.Add(guest);
        database.Generations.Add(new Generation
        {
            Id = id,
            AudioKey = id,
            GuestSessionId = guest.Id,
            CreatedAt = old ? now.AddMinutes(-31) : now,
            Status = GenerationStatus.Pending
        });
        await database.SaveChangesAsync(cancellation);
    }

    private Task<object?> ExpireLeaseAsync(Guid id) => fixture.Database.ScalarAsync(
        $"UPDATE coloring_app.generations SET lease_until = clock_timestamp() - interval '1 second' WHERE id = '{id}'");

    private async Task<Generation> ReadJobAsync(Guid id)
    {
        await using var database = Contexts().CreateDbContext();
        return await database.Generations.AsNoTracking().SingleAsync(job => job.Id == id, cancellation);
    }

    private TestContexts Contexts() => new(fixture.Database.ConnectionString);
    private GenerationApiFactory Api(GenerationTestEnvironment files, ControlledGenerationProvider provider,
        bool useDefaultPolling = false) => new(fixture.Database.ConnectionString, files.StorageRoot, provider, useDefaultPolling);

    private static HttpClient ExplicitCookieClient(GenerationApiFactory api, string? cookie)
    {
        var client = api.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        if (cookie is not null) client.DefaultRequestHeaders.Add("Cookie", cookie);
        return client;
    }

    private static async Task AssertUnavailableAsync(HttpClient client, Guid id)
    {
        foreach (var suffix in new[] { "", "/image", "/image?download=true" })
        {
            using var denied = await client.GetAsync($"{Endpoint}/{id}{suffix}");
            var problem = await AssertProblemAsync(denied, HttpStatusCode.NotFound, "generation_unavailable");
            Assert.Equal("The generation is unavailable.", problem.GetProperty("title").GetString());
            Assert.Equal("no-store", denied.Headers.CacheControl?.ToString());
        }
    }

    private static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        return problem;
    }

    private static async Task<(Guid Id, string Cookie)> SubmitAsync(HttpClient client, byte[] audio, bool expectNewCookie = true)
    {
        using var response = await PostAsync(client, audio);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JobStatus>();
        Assert.NotNull(body);
        Assert.Equal("Pending", body.Status);
        Assert.Equal($"{Endpoint}/{body.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        if (!expectNewCookie) return (body.Id, "");
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
        return (body.Id, cookie.Split(';')[0]);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, byte[] bytes, string? origin = Origin, bool duplicate = false)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes), "audio", "label-ignored.wav");
        if (duplicate) form.Add(new ByteArrayContent(bytes), "audio", "extra.webm");
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = form };
        if (origin is not null) request.Headers.Add("Origin", origin);
        return await client.SendAsync(request);
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T?>> getResult, TimeSpan? maxWait = null) where T : class
    {
        using var timeout = new CancellationTokenSource(maxWait ?? TimeSpan.FromSeconds(18));
        while (!timeout.IsCancellationRequested)
        {
            if (await getResult() is { } result) return result;
            await Task.Delay(80, timeout.Token);
        }
        throw new TimeoutException();
    }

    private sealed record JobStatus(Guid Id, string Status, string? ImageUrl, string? ErrorCode);

    private sealed class ProcessorLog : ILogger<GenerationProcessor>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }

    private sealed class TestContexts(string connectionString) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString).Options);
    }

    private sealed class GenerationApiFactory(string connectionString, string root, IGenerationProvider? provider,
        bool useDefaultPolling = false) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
            builder.UseSetting("GenerationStorage:Root", root);
            builder.UseSetting("OpenAI:ApiKey", "");
            builder.ConfigureTestServices(services =>
            {
                if (!useDefaultPolling)
                {
                    services.AddSingleton(GenerationTestEnvironment.Polling);
                }
                if (provider is not null)
                {
                    services.AddSingleton(provider);
                }
            });
        }
    }
}
