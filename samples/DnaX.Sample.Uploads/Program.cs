using System.Security.Claims;
using DnaX.Uploads;
using DnaX.Sample.Uploads;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5189");
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddDnaXUploads(options =>
{
    options.Root = Path.Combine(builder.Environment.ContentRootPath, "upload-data");
    options.Profiles["chunked"] = new UploadProfile();
    options.Profiles["whole"] = new UploadProfile { Chunking = false };
    options.Profiles["minimal"] = new UploadProfile
    {
        Multiple = false,
        Chunking = false,
        Resume = false,
        AutomaticRetry = false,
        PersistMetadata = false,
        PersistFileBytes = 0,
        ShowPause = false,
        DragAndDrop = false
    };
});
var app = builder.Build();
app.UseAuthentication();
// Local-only demonstration: issue a separate protected identity to each browser.
// Real applications use their existing login and stable subject/domain identifiers.
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated != true)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("N"))], CookieAuthenticationDefaults.AuthenticationScheme));
        await context.SignInAsync(principal); context.User = principal;
    }
    await next(context);
});
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapDnaXUploads();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
