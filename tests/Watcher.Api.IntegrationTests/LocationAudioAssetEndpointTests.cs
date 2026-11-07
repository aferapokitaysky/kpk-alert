using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.Controllers;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class LocationAudioAssetEndpointTests(PostgresApiFixture fixture)
    : IClassFixture<PostgresApiFixture>
{
    [PostgresFact]
    public async Task Administrator_uploads_a_valid_wav_as_an_opaque_location_asset()
    {
        SeedData seed = await fixture.ResetAndSeedAsync(includeGlobalAdministrator: true);
        await using (WatcherDbContext db = fixture.CreateDbContext())
        {
            SoundPreset preset = await db.SoundPresets.SingleAsync(item => item.Code == "evacuation");
            preset.IsPublished = false;
            await db.SaveChangesAsync();
        }

        var wav = CreatePcmWave();
        using MultipartFormDataContent form = new();
        using ByteArrayContent file = new(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "File", "../../outside.wav");
        using HttpRequestMessage request = new(HttpMethod.Post,
            $"/api/v1/admin/locations/{seed.FirstLocationId}/sound-presets/evacuation/asset")
        {
            Content = form
        };
        AddUserHeaders(request, seed.AdministratorId);
        request.Headers.Add("X-CSRF-TOKEN", await GetCsrfTokenAsync(fixture.Client, seed.AdministratorId));

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        LocationAudioAssetResponse? result = await response.Content.ReadFromJsonAsync<LocationAudioAssetResponse>();
        Assert.NotNull(result);
        Assert.Equal(seed.FirstLocationId, result.LocationId);
        Assert.Equal("evacuation", result.PresetCode);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(wav)), result.Sha256);
        Assert.Equal(wav.LongLength, result.SizeBytes);
        Assert.Equal(1, result.DurationSeconds);

        using WatcherDbContext verifyDb = fixture.CreateDbContext();
        LocationAudioAsset storedAsset = await verifyDb.LocationAudioAssets.SingleAsync();
        Assert.Equal(result.AssetId, storedAsset.Id);
        Assert.True(File.Exists(Path.Combine(fixture.AudioAssetStoragePath,
            seed.FirstLocationId.ToString("N"), $"{result.AssetId:N}.wav")));
        Assert.Contains(await verifyDb.AuditEvents.ToArrayAsync(), audit =>
            audit.EventType == "audio_asset.uploaded" && audit.ActorId == seed.AdministratorId &&
            audit.Payload.Contains(result.AssetId.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("outside.wav", result.AssetId.ToString());
    }

    [PostgresFact]
    public async Task Upload_rejects_invalid_audio_and_does_not_publish_or_store_it()
    {
        SeedData seed = await fixture.ResetAndSeedAsync(includeGlobalAdministrator: true);
        await using (WatcherDbContext db = fixture.CreateDbContext())
        {
            SoundPreset preset = await db.SoundPresets.SingleAsync(item => item.Code == "evacuation");
            preset.IsPublished = false;
            await db.SaveChangesAsync();
        }

        using MultipartFormDataContent form = new();
        using ByteArrayContent file = new("це не wav"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "File", "sound.wav");
        using HttpRequestMessage request = new(HttpMethod.Post,
            $"/api/v1/admin/locations/{seed.FirstLocationId}/sound-presets/evacuation/asset")
        {
            Content = form
        };
        AddUserHeaders(request, seed.AdministratorId);
        request.Headers.Add("X-CSRF-TOKEN", await GetCsrfTokenAsync(fixture.Client, seed.AdministratorId));

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using WatcherDbContext verifyDb = fixture.CreateDbContext();
        Assert.Empty(await verifyDb.LocationAudioAssets.ToArrayAsync());
        Assert.Empty(await verifyDb.AuditEvents.ToArrayAsync());
        Assert.False(Directory.Exists(fixture.AudioAssetStoragePath) &&
                     Directory.EnumerateFiles(fixture.AudioAssetStoragePath, "*", SearchOption.AllDirectories).Any());
    }

    private static void AddUserHeaders(HttpRequestMessage request, Guid userId)
    {
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        request.Headers.Add("X-Test-Role", "admin");
    }

    private static async Task<string> GetCsrfTokenAsync(HttpClient client, Guid userId)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/auth/csrf-token");
        AddUserHeaders(request, userId);
        using HttpResponseMessage response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("CSRF-токен не повернуто.");
    }

    private static byte[] CreatePcmWave()
    {
        const int sampleRate = 8_000;
        const short channels = 1;
        const short bitsPerSample = 16;
        const int dataSize = sampleRate * channels * bitsPerSample / 8;
        var bytes = new byte[44 + dataSize];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22, 2), channels);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28, 4), sampleRate * channels * bitsPerSample / 8);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32, 2), channels * bitsPerSample / 8);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34, 2), bitsPerSample);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40, 4), dataSize);
        return bytes;
    }
}
