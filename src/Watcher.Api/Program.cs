using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.AlertSources;
using Watcher.Api.Commands;
using Watcher.Api.Delivery;
using Watcher.Api.Scheduling;
using Watcher.Api.Security;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddAuthorization();
builder.Services.AddAlertSources(builder.Configuration);
var dataProtectionPath = builder.Configuration["DataProtection:KeysPath"];
X509Certificate2? dataProtectionCertificate = null;
if (builder.Environment.IsProduction())
{
    if (string.IsNullOrWhiteSpace(dataProtectionPath))
    {
        throw new InvalidOperationException("Для production потрібно налаштувати постійне сховище ключів Data Protection.");
    }

    var certificatePath = builder.Configuration["DataProtection:CertificatePath"];
    var certificatePassword = builder.Configuration["DataProtection:CertificatePassword"];
    if (string.IsNullOrWhiteSpace(certificatePath) || string.IsNullOrWhiteSpace(certificatePassword))
    {
        throw new InvalidOperationException(
            "Для production потрібно налаштувати сертифікат шифрування ключів Data Protection.");
    }

    dataProtectionCertificate = X509CertificateLoader.LoadPkcs12FromFile(
        certificatePath, certificatePassword, X509KeyStorageFlags.EphemeralKeySet);
    if (!dataProtectionCertificate.HasPrivateKey)
    {
        dataProtectionCertificate.Dispose();
        throw new InvalidOperationException("Сертифікат Data Protection має містити приватний ключ.");
    }

    builder.Services.AddSingleton(dataProtectionCertificate);
}

IDataProtectionBuilder dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("KGTA.Alert");
if (!string.IsNullOrWhiteSpace(dataProtectionPath))
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));
}
if (builder.Environment.IsProduction())
{
    dataProtection.ProtectKeysWithCertificate(dataProtectionCertificate!);
}

DeviceListenerCertificates? deviceListenerCertificates = DeviceListenerConfiguration.ValidateAndLoad(
    builder.Configuration, builder.Environment);
X509Certificate2? deviceClientCaCertificate = deviceListenerCertificates?.ClientCaCertificate;
if (deviceListenerCertificates is not null)
{
    builder.Services.AddSingleton(deviceListenerCertificates);
    X509Certificate2 trustedRoot = deviceListenerCertificates.ClientCaCertificate;
    var httpPort = builder.Configuration.GetValue<int>("DeviceListener:HttpPort");
    var devicePort = builder.Configuration.GetValue<int>("DeviceListener:Port");
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(httpPort);
        options.ListenAnyIP(devicePort, listenOptions => listenOptions.UseHttps(httpsOptions =>
            DeviceClientCertificateTrust.ConfigureHttps(
                httpsOptions, deviceListenerCertificates.ServerCertificate, trustedRoot)));
    });
}

builder.Services.AddDeviceCertificateAuthentication(deviceClientCaCertificate);

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "__Host-KgtaAlert.AntiForgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.Path = "/";
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "__Host-KgtaAlert.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.Path = "/";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.LoginPath = "/api/v1/auth/login";
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnValidatePrincipal = async context =>
        {
            var userIdValue = context.Principal?.FindFirstValue("kgta_user_id");
            if (!Guid.TryParse(userIdValue, out Guid userId))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }

            WatcherDbContext db = context.HttpContext.RequestServices.GetRequiredService<WatcherDbContext>();
            var isActive = await db.Users.AsNoTracking()
                .AnyAsync(user => user.Id == userId && user.IsActive, context.HttpContext.RequestAborted);
            if (!isActive)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }

            var currentRoles = await db.UserRoles.AsNoTracking()
                .Where(assignment => assignment.UserId == userId && assignment.LocationId == null)
                .Join(db.Roles.AsNoTracking(), assignment => assignment.RoleId, role => role.Id,
                    (_, role) => role.Code)
                .Distinct()
                .ToArrayAsync(context.HttpContext.RequestAborted);
            var ticketRoles = context.Principal!.FindAll(ClaimTypes.Role)
                .Select(claim => claim.Value).Distinct(StringComparer.Ordinal).ToArray();
            if (!ticketRoles.Order(StringComparer.Ordinal).SequenceEqual(currentRoles.Order(StringComparer.Ordinal),
                    StringComparer.Ordinal))
            {
                var identity = (ClaimsIdentity)context.Principal.Identity!;
                foreach (Claim claim in identity.FindAll(ClaimTypes.Role).ToArray())
                {
                    identity.RemoveClaim(claim);
                }

                foreach (var role in currentRoles)
                {
                    identity.AddClaim(new Claim(ClaimTypes.Role, role));
                }

                context.ReplacePrincipal(context.Principal);
                context.ShouldRenew = true;
            }
        };
    });

IConfigurationSection google = builder.Configuration.GetSection("Authentication:Google");
if (google.GetValue<bool>("Enabled"))
{
    var clientId = google["ClientId"];
    var clientSecret = google["ClientSecret"];
    if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
    {
        throw new InvalidOperationException("Для входу Google потрібно налаштувати ClientId і ClientSecret.");
    }

    builder.Services.AddAuthentication().AddGoogle(options =>
    {
        options.ClientId = clientId;
        options.ClientSecret = clientSecret;
        options.Scope.Add("email");
        options.Scope.Add("profile");
        options.Events.OnRemoteFailure = async context =>
        {
            WatcherDbContext db = context.HttpContext.RequestServices.GetRequiredService<WatcherDbContext>();
            TimeProvider clock = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
            await AuthenticationAuditWriter.RecordAsync(
                db, clock, "auth.login.rejected", null, "google", "provider_failure",
                context.HttpContext.RequestAborted);
        };
        options.Events.OnCreatingTicket = async context =>
        {
            if (!context.User.TryGetProperty("email_verified", out JsonElement verified) ||
                verified.ValueKind != JsonValueKind.True)
            {
                WatcherDbContext auditDb = context.HttpContext.RequestServices.GetRequiredService<WatcherDbContext>();
                TimeProvider auditClock = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
                await AuthenticationAuditWriter.RecordAsync(
                    auditDb, auditClock, "auth.login.rejected", null, "google", "email_unverified",
                    context.HttpContext.RequestAborted);
                context.Fail("Google підтвердив неперевірену адресу електронної пошти.");
                return;
            }

            var subject = context.Identity?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            WatcherDbContext db = context.HttpContext.RequestServices.GetRequiredService<WatcherDbContext>();
            UserAccount? account = await db.Users.AsNoTracking().SingleOrDefaultAsync(
                user => user.ExternalSubject == subject && user.IsActive,
                context.HttpContext.RequestAborted);
            if (account is null)
            {
                TimeProvider auditClock = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
                await AuthenticationAuditWriter.RecordAsync(
                    db, auditClock, "auth.login.rejected", null, "google", "account_not_allowed",
                    context.HttpContext.RequestAborted);
                context.Fail("Обліковий запис не зареєстрований у системі.");
                return;
            }

            context.Identity!.AddClaim(new Claim("kgta_user_id", account.Id.ToString()));
            List<string> roles = await (from assignment in db.UserRoles
                                        join role in db.Roles on assignment.RoleId equals role.Id
                                        where assignment.UserId == account.Id && assignment.LocationId == null
                                        select role.Code).ToListAsync(context.HttpContext.RequestAborted);
            foreach (var role in roles)
            {
                context.Identity.AddClaim(new Claim(ClaimTypes.Role, role));
            }

            TimeProvider clock = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
            await AuthenticationAuditWriter.RecordAsync(
                db, clock, "auth.login.succeeded", account.Id, "google",
                cancellationToken: context.HttpContext.RequestAborted);
        };
    });
}
var connectionString = builder.Configuration.GetConnectionString("Watcher");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddDbContext<WatcherDbContext>(options => options.UseNpgsql(connectionString));
    builder.Services.AddScoped<DeviceCertificateAuthenticator>();
    builder.Services.AddScoped<ILocationCommandSequenceAllocator, PostgresLocationCommandSequenceAllocator>();
    builder.Services.AddScoped<CommandReceiptProcessor>();
    builder.Services.AddCommandOutboxWorker(builder.Configuration);
    builder.Services.AddMinuteOfSilenceScheduler();
}

WebApplication app = builder.Build();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();

app.MapGet("/", () => Results.Redirect("/index.html"));

app.Run();

public partial class Program;
