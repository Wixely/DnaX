using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using DnaX.Uploads;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DnaX.Uploads.Tests;

public sealed class EndpointTests
{
    [Fact]
    public async Task Authenticated_antiforgery_protected_http_roundtrip_and_owner_isolation()
    {
        var root = Path.Combine(Path.GetTempPath(), "dnax-upload-http-" + Guid.NewGuid().ToString("N"));
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, TestAuth>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddDnaXUploads(o => { o.Root = root; o.Profiles["whole"] = new UploadProfile { Chunking = false }; });
        await using (var app = builder.Build())
        {
            app.UseAuthentication(); app.UseAuthorization(); app.UseAntiforgery(); app.MapDnaXUploads(); await app.StartAsync();
            using var client = app.GetTestClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/uploads/capabilities")).StatusCode);
            client.DefaultRequestHeaders.Add("Test-Owner", "owner");
            var caps = await client.GetAsync("/uploads/capabilities"); caps.EnsureSuccessStatusCode();
            var json = await caps.Content.ReadFromJsonAsync<JsonElement>();
            var request = new UploadRequest(Guid.NewGuid(), "whole", "http.bin", 3);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/uploads/sessions", request)).StatusCode);
            client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", caps.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0])));
            client.DefaultRequestHeaders.Add("X-DnaX-Antiforgery", json.GetProperty("token").GetString());
            (await client.PostAsJsonAsync("/uploads/sessions", request)).EnsureSuccessStatusCode();
            var path = "/uploads/sessions/" + request.Id;
            var bytes = new ByteArrayContent([1, 2, 3]); bytes.Headers.ContentType = new("application/octet-stream");
            using var put = new HttpRequestMessage(HttpMethod.Put, path + "/bytes") { Content = bytes };
            put.Headers.Add("Upload-Offset", "0");
            (await client.SendAsync(put)).EnsureSuccessStatusCode();
            var complete = await client.PostAsync(path + "/complete", null); complete.EnsureSuccessStatusCode();
            Assert.Equal("complete", (await complete.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
            client.DefaultRequestHeaders.Remove("Test-Owner"); client.DefaultRequestHeaders.Add("Test-Owner", "other");
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
            await app.StopAsync();
        }
        Directory.Delete(root, true);
    }

    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var owner = Request.Headers["Test-Owner"].FirstOrDefault();
            return Task.FromResult(owner is null ? AuthenticateResult.NoResult() : AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, owner)], "test")), "test")));
        }
    }
}
