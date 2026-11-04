using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.EntityFrameworkCore;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddAuthorization();
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
        options.Events.OnCreatingTicket = async context =>
        {
            if (!context.User.TryGetProperty("email_verified", out JsonElement verified) ||
                verified.ValueKind != JsonValueKind.True)
            {
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
        };
    });
}
var connectionString = builder.Configuration.GetConnectionString("Watcher");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddDbContext<WatcherDbContext>(options => options.UseNpgsql(connectionString));
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
