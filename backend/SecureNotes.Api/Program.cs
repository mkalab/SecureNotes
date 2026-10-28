using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.WebUtilities;
using SecureNotes.Api.Data;
using SecureNotes.Api.Security;

var builder = WebApplication.CreateBuilder(args);
var isDevelopment = builder.Environment.IsDevelopment();
const int maxRequestBodySize = 1_048_576;
const int sessionIdleTimeoutMinutes = 20;
const int absoluteSessionTimeoutHours = 8;
var allowedOrigins = builder.Configuration.GetSection("Security:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddScoped<SecureNotes.Api.Data.IUserRepository, SecureNotes.Api.Data.SqlUserRepository>();
builder.Services.AddScoped<SecureNotes.Api.Data.INotesRepository, SecureNotes.Api.Data.SqlNotesRepository>();
builder.Services.AddScoped<IAuditRepository, SqlAuditRepository>();
builder.Services.AddScoped<ISessionRepository, SqlSessionRepository>();
builder.Services.AddScoped<P4EncryptionMigrationService>();
builder.Services.AddScoped<AppSessionService>();
builder.Services.AddScoped<GoogleOidcEvents>();
builder.Services.AddSingleton<SecureNotes.Api.Security.PasswordHasher>();
builder.Services.AddSingleton<CryptoService>();
builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
    options.Limits.MaxRequestBodySize = maxRequestBodySize;
});
var authentication = builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
});
authentication.AddCookie(options =>
{
    options.Cookie.Name = isDevelopment ? "SecureNotes.Dev" : "__Host-SecureNotes";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = isDevelopment
        ? Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest
        : Microsoft.AspNetCore.Http.CookieSecurePolicy.Always;
    options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict;
    options.Cookie.Path = "/";
    options.ExpireTimeSpan = TimeSpan.FromHours(absoluteSessionTimeoutHours);
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
    options.Events.OnValidatePrincipal = async context =>
    {
        var token = context.Principal?.FindFirstValue("session_id");
        if (string.IsNullOrEmpty(token))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        byte[] tokenBytes;
        try
        {
            tokenBytes = WebEncoders.Base64UrlDecode(token);
        }
        catch (FormatException)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        if (tokenBytes.Length != 32)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        var sessions = context.HttpContext.RequestServices.GetRequiredService<ISessionRepository>();
        var isValid = await sessions.ValidateAndTouchAsync(
            System.Security.Cryptography.SHA256.HashData(tokenBytes),
            DateTime.UtcNow,
            sessionIdleTimeoutMinutes,
            context.HttpContext.RequestAborted);
        if (!isValid)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    };
});

if (GoogleOidcOptions.IsConfigured(builder.Configuration))
{
    authentication.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
    {
        options.Authority = "https://accounts.google.com";
        options.ClientId = builder.Configuration["Authentication:Google:ClientId"]!;
        options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"]!;
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.CallbackPath = "/signin-oidc";
        options.ResponseType = "code";
        options.UsePkce = true;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.MapInboundClaims = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("email");
        options.Scope.Add("profile");
        options.Events.OnRedirectToIdentityProvider = context =>
        {
            if (GoogleOidcOptions.TryGetRedirectUri(builder.Configuration, out var redirectUri))
            {
                context.ProtocolMessage.RedirectUri = redirectUri!.AbsoluteUri;
            }
            else
            {
                context.HandleResponse();
                context.Response.Redirect("/?authError=google_unavailable");
            }

            return Task.CompletedTask;
        };
        options.Events.OnTokenValidated = context =>
            context.HttpContext.RequestServices.GetRequiredService<GoogleOidcEvents>().OnTokenValidatedAsync(context);
        options.Events.OnTicketReceived = context =>
            context.HttpContext.RequestServices.GetRequiredService<GoogleOidcEvents>().OnTicketReceivedAsync(context);
        options.Events.OnRemoteFailure = context =>
            context.HttpContext.RequestServices.GetRequiredService<GoogleOidcEvents>().OnRemoteFailureAsync(context);
    });
}

var trustedProxyAddresses = builder.Configuration
    .GetSection("ForwardedHeaders:KnownProxies")
    .Get<string[]>() ?? [];
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost;
    options.ForwardLimit = 1;
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();
    foreach (var address in trustedProxyAddresses.Where(address => !string.IsNullOrWhiteSpace(address)))
    {
        if (!System.Net.IPAddress.TryParse(address, out var proxyAddress))
        {
            throw new InvalidOperationException($"Invalid trusted proxy IP address: '{address}'.");
        }

        options.KnownProxies.Add(proxyAddress);
    }
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(5),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = isDevelopment ? "SecureNotes.CSRF.Dev" : "__Host-SecureNotes-CSRF";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = isDevelopment
        ? Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest
        : Microsoft.AspNetCore.Http.CookieSecurePolicy.Always;
    options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict;
    options.Cookie.Path = "/";
});
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();
app.UseForwardedHeaders();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider
        .GetRequiredService<P4EncryptionMigrationService>()
        .MigrateAsync(app.Lifetime.ApplicationStopping);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred." });
    }));
}

app.Use(async (context, next) =>
{
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; form-action 'self'; " +
        "img-src 'self' data:; script-src 'self'; style-src 'self' 'unsafe-inline'";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers.Remove("Server");
    context.Response.Headers.Remove("X-Powered-By");
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["Pragma"] = "no-cache";
    }

    if (!isDevelopment)
    {
        context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000";
    }

    var maxBodySizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (maxBodySizeFeature is { IsReadOnly: false })
    {
        maxBodySizeFeature.MaxRequestBodySize = maxRequestBodySize;
    }

    await next();
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (builder.Configuration.GetValue("HttpsRedirection:Enabled", true))
{
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api")
        && (HttpMethods.IsPost(context.Request.Method)
            || HttpMethods.IsPut(context.Request.Method)
            || HttpMethods.IsPatch(context.Request.Method)
            || HttpMethods.IsDelete(context.Request.Method)))
    {
        if (!IsAllowedOrigin(context, allowedOrigins))
        {
            await RecordSessionSecurityViolationAsync(context, "origin_violation");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "A valid same-origin request is required." });
            return;
        }

        try
        {
            await context.RequestServices.GetRequiredService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>()
                .ValidateRequestAsync(context);
        }
        catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException)
        {
            await RecordSessionSecurityViolationAsync(context, "csrf_violation");
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "A valid CSRF token is required." });
            return;
        }
    }

    await next();

    if (context.Response.StatusCode == StatusCodes.Status404NotFound)
    {
        await RecordSessionSecurityViolationAsync(context, "ownership_violation");
    }
});

app.MapControllers();

app.Run();

static bool IsAllowedOrigin(HttpContext context, IEnumerable<string> allowedOrigins)
{
    var origin = context.Request.Headers["Origin"].ToString();
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri)
        || originUri.UserInfo.Length != 0
        || originUri.AbsolutePath != "/"
        || originUri.Query.Length != 0
        || originUri.Fragment.Length != 0)
    {
        return false;
    }

    var requestOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
    return string.Equals(originUri.GetLeftPart(UriPartial.Authority), requestOrigin, StringComparison.OrdinalIgnoreCase)
        || allowedOrigins.Any(allowed => string.Equals(
            originUri.GetLeftPart(UriPartial.Authority),
            allowed.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase));
}

static async Task RecordSessionSecurityViolationAsync(HttpContext context, string action)
{
    if (context.User.Identity?.IsAuthenticated != true)
    {
        return;
    }

    var token = context.User.FindFirstValue("session_id");
    if (string.IsNullOrEmpty(token))
    {
        return;
    }

    var userAgent = context.Request.Headers.UserAgent.ToString();
    if (userAgent.Length > 512)
    {
        userAgent = userAgent[..512];
    }

    var repository = context.RequestServices.GetRequiredService<ISessionRepository>();
    var revoked = await repository.RecordSecurityViolationAsync(
        System.Security.Cryptography.SHA256.HashData(WebEncoders.Base64UrlDecode(token)),
        DateTime.UtcNow,
        action,
        context.Connection.RemoteIpAddress?.ToString(),
        string.IsNullOrEmpty(userAgent) ? null : userAgent,
        context.RequestAborted);
    if (revoked)
    {
        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("SecureNotes.SessionSecurity")
            .LogWarning("Revoked a session after repeated security violations. Action: {Action}", action);
    }
}
