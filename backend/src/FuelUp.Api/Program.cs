using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FuelUp.Api;
using FuelUp.Api.Security;
using FuelUp.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
var env = builder.Environment;

// ── Secrets: only from environment variables / secret store, validated at start-up ─────────────────────────────
if (string.IsNullOrWhiteSpace(cfg["Jwt:Key"]))
{
    if (!env.IsDevelopment()) throw new InvalidOperationException("Jwt__Key is not set. Provide a random secret of at least 32 bytes through the environment or a secret store.");
    cfg["Jwt:Key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));   // development only: a fresh key per run
}
if (Encoding.UTF8.GetByteCount(cfg["Jwt:Key"]!) < 32 || cfg["Jwt:Key"]!.Contains("CHANGE-ME", StringComparison.OrdinalIgnoreCase) || cfg["Jwt:Key"]!.Contains("replace-this", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Jwt__Key is too short or still the placeholder.");
if (!env.IsDevelopment() && cfg.GetValue<bool>("Seed:Demo") && !cfg.GetValue<bool>("Seed:AllowInProduction"))
    throw new InvalidOperationException("Demo data (with a published password) is refused outside Development. Set Seed__Demo=false.");

string? Cs(string name) => cfg.GetConnectionString(name) is { Length: > 0 } v ? v : null;
// Managed hosting (e.g. Render) hands over the database as separate host/port/name/user/password variables.
string? FromParts()
{
    if (cfg["Database:Host"] is not { Length: > 0 } host) return null;
    return new Npgsql.NpgsqlConnectionStringBuilder
    {
        Host = host, Port = cfg.GetValue("Database:Port", 5432), Database = cfg["Database:Name"], Username = cfg["Database:User"], Password = cfg["Database:Password"],
        SslMode = Enum.TryParse<Npgsql.SslMode>(cfg["Database:SslMode"], true, out var ssl) ? ssl : Npgsql.SslMode.Prefer,
        MaxPoolSize = cfg.GetValue("Database:PoolMax", 40), Timeout = 15,
    }.ConnectionString;
}
var runtimeCs = Cs("Default") ?? FromParts() ?? throw new InvalidOperationException("ConnectionStrings__Default (or Database__Host/Name/User/Password) is not set.");

// ── Database: runtime role (least privilege, row-level security applies) + owner role for migrations ───────────
builder.Services.AddScoped<TenantContext>();
var interceptor = new TenantConnectionInterceptor();
builder.Services.AddDbContext<FuelUpDb>(o => o.UseNpgsql(runtimeCs).UseSnakeCaseNamingConvention().AddInterceptors(interceptor));
builder.Services.AddScoped<AuditWriter>();
builder.Services.AddScoped<MeterLedger>();
builder.Services.AddScoped<AssignmentService>();
builder.Services.AddScoped<TripService>();
builder.Services.AddScoped<CustodyService>();
builder.Services.AddScoped<ActorAccessor>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<MfaService>();
builder.Services.AddSingleton<SecurityEvents>();
builder.Services.AddSingleton<FileStore>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient("alerts");
builder.Services.AddSingleton<ErrorReporter>();
// Production logs as one JSON object per line, ready for a log collector (Loki, CloudWatch, Azure Monitor...).
if (!env.IsDevelopment()) { builder.Logging.ClearProviders(); builder.Logging.AddJsonConsole(o => { o.IncludeScopes = false; o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ "; o.UseUtcTimestamp = true; }); }

// ── Data Protection keys (encrypt TOTP secrets). Kept outside the database; optionally wrapped by a certificate ──
var dp = builder.Services.AddDataProtection().SetApplicationName("FuelUp")
    .PersistKeysToFileSystem(new DirectoryInfo(cfg["DataProtection:KeysPath"] ?? Path.Combine(AppContext.BaseDirectory, "keys")));
if (cfg["DataProtection:CertificatePath"] is { Length: > 0 } certPath)
    dp.ProtectKeysWithCertificate(new System.Security.Cryptography.X509Certificates.X509Certificate2(certPath, cfg["DataProtection:CertificatePassword"]));

// ── Authentication: signed JWT (HS256 only), checked against the session table on every request ────────────────
var tokenService = new TokenService(cfg);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.RequireHttpsMetadata = !env.IsDevelopment();
    o.TokenValidationParameters = tokenService.Parameters();
});
// Every endpoint requires a signed-in user unless it explicitly says [AllowAnonymous].
builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// ── Abuse protection: per-IP limits on sign-in, per-user limits on the rest ─────────────────────────────────────
static string Partition(HttpContext c) => c.User.FindFirstValue("sub") ?? c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = "60";
        await ctx.HttpContext.Response.WriteAsJsonAsync(new { error = "طلبات كثيرة خلال وقت قصير. انتظر دقيقة ثم حاول مجددًا." }, ct);
    };
    o.AddPolicy("auth", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = cfg.GetValue("RateLimits:AuthPerMinute", 20), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("upload", c => RateLimitPartition.GetFixedWindowLimiter(Partition(c),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = cfg.GetValue("RateLimits:UploadsPerMinute", 30), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c => RateLimitPartition.GetTokenBucketLimiter(Partition(c),
        _ => new TokenBucketRateLimiterOptions { TokenLimit = cfg.GetValue("RateLimits:ApiBurst", 120), TokensPerPeriod = cfg.GetValue("RateLimits:ApiPerMinute", 300), ReplenishmentPeriod = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// X-Forwarded-For is honoured ONLY from configured proxy networks. With an empty list ASP.NET would trust every client,
// letting an attacker rotate fake IPs to escape the per-IP sign-in limit (found by tests/security.mjs).
// Platforms such as Render put their own proxy in front and their address ranges are not published: Proxy__TrustedNetworks
// "0.0.0.0/0,::/0" then trusts that proxy hop, and Proxy__ForwardLimit takes the right-most address(es) the platform added
// (a client-sent value stays further left and is ignored). Verified after deployment with tests/proxy-ip-check.mjs.
var trustedNetworks = (cfg["Proxy:TrustedNetworks"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = cfg.GetValue("Proxy:ForwardLimit", 1);   // only the proxy hops in front of us; a client cannot spoof its IP through extra headers
    o.KnownNetworks.Clear(); o.KnownProxies.Clear();   // trust X-Forwarded-For only from the configured proxy networks (not even loopback by default)
    foreach (var n in trustedNetworks)
        o.KnownNetworks.Add(Microsoft.AspNetCore.HttpOverrides.IPNetwork.Parse(n));
});
builder.WebHost.ConfigureKestrel(k => { k.Limits.MaxRequestBodySize = 1 * 1024 * 1024; k.AddServerHeader = false; });
builder.Services.AddHsts(o => { o.MaxAge = TimeSpan.FromDays(365); o.IncludeSubDomains = true; o.ExcludedHosts.Clear(); });   // same behaviour on every host name

builder.Services.AddControllers(o => o.Filters.Add<ErrorFilter>())
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(cfg.GetSection("Cors:Origins").Get<string[]>() ?? [])
    .WithHeaders("Authorization", "Content-Type", "X-FuelUp-Client").WithMethods("GET", "POST", "PUT").AllowCredentials()));
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "FuelUp API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement { [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [] });
});

var app = builder.Build();

// ── Start-up: migrate and harden with the owner account, then serve with the restricted runtime account ────────
{
    var ownerCs = Cs("Migrations") ?? runtimeCs;
    var opts = new DbContextOptionsBuilder<FuelUpDb>().UseNpgsql(ownerCs).UseSnakeCaseNamingConvention().AddInterceptors(interceptor).Options;
    var tenant = new TenantContext();
    await using var owner = new FuelUpDb(opts, tenant);
    await owner.Database.MigrateAsync();
    await DbHardening.ApplyAsync(owner, cfg["Database:AppRole"]);
    if (cfg.GetValue<bool>("Seed:Demo")) await DemoSeeder.RunAsync(owner, tenant);
    await Bootstrap.RunAsync(owner, tenant, cfg, app.Logger);
    await PresetSync.RunAsync(owner, tenant, app.Logger);
    if (Cs("Migrations") is null)
        app.Logger.LogWarning("ConnectionStrings:Migrations is not set: the API runs with the owner role. Use a separate restricted role in production.");
}

// Sample data for trying the system (Seed__Sample=true): loaded once in the background after start-up, so deploys stay fast.
if (cfg.GetValue<bool>("Seed:Sample"))
    app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
    {
        try
        {
            var opts = new DbContextOptionsBuilder<FuelUpDb>().UseNpgsql(Cs("Migrations") ?? runtimeCs).UseSnakeCaseNamingConvention().AddInterceptors(interceptor).Options;
            var tenant = new TenantContext();
            await using var db = new FuelUpDb(opts, tenant);
            await SampleData.RunAsync(db, tenant, cfg, app.Logger);
        }
        catch (Exception e) { app.Logger.LogError(e, "Sample data failed"); }
    }));

if (trustedNetworks.Length > 0) app.UseForwardedHeaders();
app.UseExceptionHandler(e => e.Run(async ctx =>
{
    var error = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    if (error is BadHttpRequestException bad)   // e.g. body over the size limit → 413, not 500
    {
        ctx.Response.StatusCode = bad.StatusCode;
        await ctx.Response.WriteAsJsonAsync(new { error = bad.StatusCode == 413 ? "حجم الطلب أكبر من المسموح." : "طلب غير صالح." });
        return;
    }
    if (error != null) await ctx.RequestServices.GetRequiredService<ErrorReporter>().ReportAsync(error, ctx);
    ctx.Response.StatusCode = 500;
    await ctx.Response.WriteAsJsonAsync(new { error = "حدث خطأ غير متوقع. سُجّل للمراجعة.", trace = ctx.TraceIdentifier });   // details stay in the server log
}));
if (!env.IsDevelopment()) app.UseHsts();

// The dashboard can be served by the API itself (single service on platforms such as Render): wwwroot holds the React build.
var spa = File.Exists(Path.Combine(app.Environment.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"), "index.html"));
static bool IsApi(PathString p) => p.StartsWithSegments("/api") || p.StartsWithSegments("/health") || p.StartsWithSegments("/swagger");
// The driver app's browser build (Flutter) lives under /driver: it needs WebAssembly, the camera and the location for «تقرير التعبئة».
static bool IsDriverApp(PathString p) => p.StartsWithSegments("/driver");
const string DriverCsp = "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; font-src 'self' https://fonts.gstatic.com; img-src 'self' data: blob:; connect-src 'self' https://fonts.gstatic.com; worker-src 'self' blob:; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
const string SpaCsp = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; font-src 'self' https://fonts.gstatic.com; img-src 'self' data: blob:; connect-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

// Security headers for every answer: strict for the API, a page policy (same as the nginx setup) for the dashboard.
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h.XContentTypeOptions = "nosniff";
    h.XFrameOptions = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Permissions-Policy"] = IsDriverApp(ctx.Request.Path) ? "camera=(self), microphone=(), geolocation=(self)" : "camera=(), microphone=(), geolocation=()";
    h["Cross-Origin-Resource-Policy"] = "same-origin";
    if (IsApi(ctx.Request.Path))
    {
        if (!ctx.Request.Path.StartsWithSegments("/swagger")) { h.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'"; h.CacheControl = "no-store"; }
    }
    else
    {
        h.ContentSecurityPolicy = IsDriverApp(ctx.Request.Path) ? DriverCsp : SpaCsp;
        h["Cross-Origin-Opener-Policy"] = "same-origin";
    }
    await next();
});
if (spa)
{
    app.UseDefaultFiles();
    app.UseStaticFiles(new StaticFileOptions
    {
        // hashed assets can be cached for a year; index.html must always be revalidated so a new deploy is picked up
        OnPrepareResponse = c => c.Context.Response.Headers.CacheControl =
            c.Context.Request.Path.StartsWithSegments("/assets") ? "public, max-age=31536000, immutable" : "no-cache",
    });
}

if (env.IsDevelopment() || cfg.GetValue<bool>("Swagger:Enabled")) { app.UseSwagger(); app.UseSwaggerUI(); }
app.UseCors();
app.UseAuthentication();
// A token whose session was revoked, or whose user was disabled, stops working immediately (not at expiry).
app.Use(async (ctx, next) =>
{
    if (ctx.User.Identity?.IsAuthenticated == true)
    {
        try { await ctx.RequestServices.GetRequiredService<ActorAccessor>().GetAsync(); }
        catch (UnauthorizedAccessException)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "انتهت الجلسة. سجّل الدخول من جديد." });
            return;
        }
    }
    await next();
});
app.UseRateLimiter();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous().DisableRateLimiting();                     // liveness
app.MapGet("/health/ready", (FuelUpDb db) => Readiness.CheckAsync(db, cfg)).AllowAnonymous().DisableRateLimiting();      // readiness (deploy smoke test, monitoring)
if (spa)   // client-side routes (/trips, /users…) get the dashboard page; unknown /api paths stay a JSON 404
    app.MapFallback(async ctx =>
    {
        if (IsApi(ctx.Request.Path)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsJsonAsync(new { error = "غير موجود." }); return; }
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        var driverPage = Path.Combine(app.Environment.WebRootPath!, "driver", "index.html");
        await ctx.Response.SendFileAsync(IsDriverApp(ctx.Request.Path) && File.Exists(driverPage) ? driverPage : Path.Combine(app.Environment.WebRootPath!, "index.html"));
    }).AllowAnonymous().DisableRateLimiting();
app.Run();

public partial class Program;
