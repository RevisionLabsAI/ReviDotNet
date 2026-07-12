// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using DotNetEnv;
using DotNetEnv.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using MudBlazor.Services;
using Revi;
using Revi.Refinery;
using Revi.Refinery.Hosting;
using ReviDotNet.Forge.Api;
using ReviDotNet.Forge.Components;
using ReviDotNet.Forge.Services;
using ReviDotNet.Forge.Services.ApiKeys;
using ReviDotNet.Forge.Services.FileLog;
using ReviDotNet.Forge.Services.Gateway;
using ReviDotNet.Forge.Services.Mongo;
using ReviDotNet.Forge.Services.Observer;
using ReviDotNet.Forge.Services.Workshop;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Load static web assets manifest in all environments (not just Development) so that
// RCL assets like MudBlazor's `_content/MudBlazor/*.js|css` resolve when launching the
// build output directly (e.g. running the .exe), where ASPNETCORE_ENVIRONMENT defaults
// to Production and the manifest would otherwise be ignored.
builder.WebHost.UseStaticWebAssets();

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
    .AddDotNetEnv("forge.env", LoadOptions.TraversePath())
    .AddEnvironmentVariables();

bool useAuthentication = builder.Configuration.GetValue<bool>("Forge:UseAuthentication");

// Rolling file log (logs/forge-yyyyMMdd.log under the content root, or Forge:FileLog:Directory) so a
// crash or silent process death leaves evidence that outlives the console buffer. Disable with
// Forge:FileLog:Enabled=false. A once-a-minute MemoryStatsLogger line makes memory growth diagnosable
// after the fact.
if (builder.Configuration.GetValue("Forge:FileLog:Enabled", defaultValue: true))
{
    string logDir = builder.Configuration["Forge:FileLog:Directory"] is { Length: > 0 } configured
        ? (Path.IsPathRooted(configured) ? configured : Path.Combine(builder.Environment.ContentRootPath, configured))
        : Path.Combine(builder.Environment.ContentRootPath, "logs");
    builder.Logging.AddProvider(new FileLoggerProvider(logDir));
    builder.Services.AddHostedService<MemoryStatsLogger>();
    Console.WriteLine($"[Forge] File log: {logDir}");
}

// Blazor / MudBlazor
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

// FusionAuth OIDC authentication (opt-in via Forge:UseAuthentication)
if (useAuthentication)
{
    string? authority = builder.Configuration["FusionAuth:Authority"];

    builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie()
    .AddOpenIdConnect(options =>
    {
        string? redirectUri = builder.Configuration["FusionAuth:RedirectUri"];
        string? postLogoutRedirectUri = builder.Configuration["FusionAuth:PostLogoutRedirectUri"];

        options.Authority = authority;
        options.ClientId = builder.Configuration["FusionAuth:ClientId"];
        options.ClientSecret = builder.Configuration["FusionAuth:ClientSecret"];
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.CallbackPath = "/signin-oidc";
        options.SignedOutCallbackPath = "/signout-callback-oidc";
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");

        options.TokenValidationParameters = new TokenValidationParameters
        {
            RoleClaimType = "roles",
            NameClaimType = "name"
        };

        if (!string.IsNullOrEmpty(authority) && authority.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            options.RequireHttpsMetadata = false;

        options.Events = new OpenIdConnectEvents
        {
            OnRedirectToIdentityProvider = context =>
            {
                if (!string.IsNullOrEmpty(redirectUri))
                    context.ProtocolMessage.RedirectUri = redirectUri;
                return Task.CompletedTask;
            },
            OnRedirectToIdentityProviderForSignOut = context =>
            {
                if (!string.IsNullOrEmpty(postLogoutRedirectUri))
                    context.ProtocolMessage.PostLogoutRedirectUri = postLogoutRedirectUri;

                string? idToken = context.HttpContext.GetTokenAsync("id_token").Result;
                if (!string.IsNullOrEmpty(idToken))
                    context.ProtocolMessage.IdTokenHint = idToken;

                return Task.CompletedTask;
            }
        };
    });

    builder.Services.AddAuthorization();
    builder.Services.AddCascadingAuthenticationState();
}

// Workshop event bus — in-memory pub/sub for live agent run trace events.
// Registered before publishers so the BroadcastingRlogEventPublisher can resolve it.
builder.Services.AddSingleton<IWorkshopEventBus, WorkshopEventBus>();

// Revi logging — choose Mongo-backed or null inner publisher based on config,
// then wrap with BroadcastingRlogEventPublisher so Workshop UI gets live events.
if (!string.IsNullOrWhiteSpace(builder.Configuration["Observer:MongoDb:ConnectionString"]))
{
    builder.Services.AddSingleton<MongoRlogEventPublisher>();
    builder.Services.AddSingleton<IRlogEventPublisher>(sp =>
        new BroadcastingRlogEventPublisher(
            sp.GetRequiredService<MongoRlogEventPublisher>(),
            sp.GetRequiredService<IWorkshopEventBus>()));
}
else
{
    builder.Services.AddSingleton<IRlogEventPublisher>(sp =>
        new BroadcastingRlogEventPublisher(
            new NullRlogEventPublisher(),
            sp.GetRequiredService<IWorkshopEventBus>()));
}

// Refinery durable store (config-gated). Register the durable ICampaignStore BEFORE AddRefinery() so its
// guard ("only register InMemory if no ICampaignStore exists") skips the in-memory default and the durable
// store wins. Modes (Forge:CampaignStore):
//   "file"     (default) — JSON files under Forge:CampaignStoreDir (default data/refinery, content-root
//              relative); campaigns survive restarts with no external database.
//   "mongo"    — MongoCampaignStore, requires the Observer Mongo connection string.
//   "inmemory" — the pre-existing volatile behavior (campaigns are lost on restart).
string campaignStoreMode = builder.Configuration["Forge:CampaignStore"] ?? "file";
string? refineryMongoConn = builder.Configuration["Observer:MongoDb:ConnectionString"];
if (string.Equals(campaignStoreMode, "mongo", StringComparison.OrdinalIgnoreCase)
    && !string.IsNullOrWhiteSpace(refineryMongoConn))
{
    const string refineryDbName = "refinery";
    builder.Services.AddSingleton<ICampaignStore>(_ =>
        new MongoCampaignStore(refineryMongoConn!, refineryDbName));
    Console.WriteLine($"[Refinery] Campaign store: Mongo (db='{refineryDbName}').");
}
else if (string.Equals(campaignStoreMode, "file", StringComparison.OrdinalIgnoreCase))
{
    string storeDir = builder.Configuration["Forge:CampaignStoreDir"] is { Length: > 0 } configuredDir
        ? (Path.IsPathRooted(configuredDir) ? configuredDir : Path.Combine(builder.Environment.ContentRootPath, configuredDir))
        : Path.Combine(builder.Environment.ContentRootPath, "data", "refinery");
    builder.Services.AddSingleton<ICampaignStore>(_ => new FileCampaignStore(storeDir));
    Console.WriteLine($"[Refinery] Campaign store: file ({storeDir}).");
}
else
{
    Console.WriteLine("[Refinery] Campaign store: in-memory (campaigns are lost on restart).");
}

// Refinery: decorate the IRlogEventPublisher registered above with a per-run capture broker (must be
// AFTER it is registered so the host's logging/Observer UI is preserved), and register the plugin host +
// engine. Repos to build/load come from the "Refinery" config section (Refinery:Repos).
builder.Services.AddRefinery();
builder.Services.AddRefineryHosting(builder.Configuration);
// Forge-side campaign orchestration (validates spec, registers plugin tools, runs the baseline in the
// background while clients poll). Singleton so its run-serializing semaphore is process-wide.
builder.Services.AddSingleton<RefineryCampaignService>();
// Additional on-disk RConfig folders loaded INTO Forge IN ADDITION to its own embedded set ("also load":
// Forge's own configs load first and always win on a name clash; these folders only add what's new). Each
// is an RConfigs root (Providers/, Models/Inference/, Models/Embedding/, Prompts/, Agents/, Tools/) — handy
// for testing agents kept in a separate project. Give one or more folders either way (both are combined):
//   - Semicolon-separated on one line — ';' is a fixed delimiter, the same on every OS (NOT the OS path
//     separator):   REVI_RCONFIG_PATHS=C:/proj-a/RConfigs;C:/proj-b/RConfigs
//   - Or one folder per line with a numbered suffix:   REVI_RCONFIG_PATHS=C:/proj-a/RConfigs
//                                                       REVI_RCONFIG_PATHS_2=C:/proj-b/RConfigs
// (Also accepted: the .NET config-array form Revi__RConfigPaths__0 / __1, e.g. in appsettings.json.)
var extraRConfigPaths = ReadAdditionalRConfigPaths(builder.Configuration);

builder.Services.AddReviDotNet(
    typeof(Program).Assembly,
    options =>
    {
        options.AdditionalConfigDirectories.AddRange(extraRConfigPaths);
        // Load the Refinery toolkit's embedded RConfigs (the evaluator judge prompts) into Forge's registry.
        options.AdditionalAssemblies.Add(typeof(RefineryServiceCollectionExtensions).Assembly);
    });

static List<string> ReadAdditionalRConfigPaths(IConfiguration config)
{
    // Split one value into folders on ';' (a fixed, OS-agnostic delimiter) or embedded newlines.
    static string[] Split(string? raw) => string.IsNullOrWhiteSpace(raw)
        ? Array.Empty<string>()
        : raw.Split(new[] { ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var paths = new List<string>();

    // (1) .NET config: array (Revi:RConfigPaths:0/1 — e.g. Revi__RConfigPaths__0, or an appsettings.json
    //     array) or scalar (Revi:RConfigPaths).
    foreach (var child in config.GetSection("Revi:RConfigPaths").GetChildren())
        paths.AddRange(Split(child.Value));
    paths.AddRange(Split(config["Revi:RConfigPaths"]));

    // (2) Plain env vars, one folder per line: REVI_RCONFIG_PATHS plus optional REVI_RCONFIG_PATHS_1, _2, …
    //     ordered by the numeric suffix. Delimiter-free per line, so it survives any .env parser.
    var numbered = new List<(int Order, string? Value)>();
    foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
    {
        var key = (string)e.Key;
        if (key.Equals("REVI_RCONFIG_PATHS", StringComparison.OrdinalIgnoreCase))
            numbered.Add((0, e.Value as string));
        else if (key.StartsWith("REVI_RCONFIG_PATHS_", StringComparison.OrdinalIgnoreCase)
                 && int.TryParse(key["REVI_RCONFIG_PATHS_".Length..], out int n))
            numbered.Add((n, e.Value as string));
    }
    foreach (var entry in numbered.OrderBy(x => x.Order))
        paths.AddRange(Split(entry.Value));

    return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

// Observer services (log viewer)
if (!string.IsNullOrWhiteSpace(builder.Configuration["Observer:MongoDb:ConnectionString"]))
{
    builder.Services.AddSingleton<IForgeMongoConnectionService, ForgeMongoConnectionService>();
    builder.Services.AddSingleton<IReviLogViewerService, MongoReviLogViewerService>();
}
else
{
    builder.Services.AddSingleton<IReviLogViewerService, NullReviLogViewerService>();
}
builder.Services.AddSingleton<IReviLogLimiter, ReviLogLimiterService>();

// Forge services (prompt engineering tools)
builder.Services.AddSingleton<ArtifactHistoryService>();
builder.Services.AddSingleton<WorkbenchStateService>();
builder.Services.AddSingleton<DependencyAnalyzerService>();
builder.Services.AddSingleton<RunningJobsService>();
builder.Services.AddSingleton<SavedSuitesService>();
builder.Services.AddSingleton<PromptRegistryService>();
builder.Services.AddSingleton<TestRunnerService>();
builder.Services.AddSingleton<AgentTestRunnerService>();
builder.Services.AddSingleton<PromptGeneratorService>();
builder.Services.AddSingleton<OptimizerService>();

// Agent Workshop
builder.Services.AddSingleton<IAgentWorkshopService, AgentWorkshopService>();
builder.Services.AddSingleton<IWorkshopStore, WorkshopStore>();
builder.Services.AddSingleton<AgentGeneratorService>();
builder.Services.AddScoped<WorkshopHandoff>();

// Registry + export services for the new edit pages
builder.Services.AddSingleton<ModelRegistryService>();
builder.Services.AddSingleton<ProviderRegistryService>();
builder.Services.AddScoped<ExportService>();

// Gateway services
builder.Services.AddSingleton<IForgeRateLimiterService, ForgeRateLimiterService>();
builder.Services.AddSingleton<GatewayRouterService>();
builder.Services.AddSingleton<EmbeddingGatewayRouterService>();
builder.Services.AddSingleton<UsageDashboardService>();

// API key services
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IForgeApiKeyService, ForgeApiKeyService>();

WebApplication app = builder.Build();

// Bridge static logging (Util.Log, AgentReviLogger) to the DI container so any
// agent run anywhere in the process publishes structured events to ReviLog.
ReviServiceLocator.SetProvider(app.Services);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();

if (useAuthentication)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.UseAntiforgery();

// Auth endpoints
if (useAuthentication)
{
    app.MapGet("/auth/login", async (HttpContext context, string? redirectUrl) =>
        await context.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme,
            new AuthenticationProperties { RedirectUri = redirectUrl ?? "/" }));

    app.MapGet("/auth/logout", async (HttpContext context) =>
    {
        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await context.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme,
            new AuthenticationProperties { RedirectUri = "/" });
    });
}

app.MapForgeApi();
app.MapRefineryApi(builder.Configuration.GetValue<bool>("Forge:RefineryApi:RequireApiKey"));

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>
/// A no-op Rlog publisher for local dev use.
/// </summary>
public class NullRlogEventPublisher : IRlogEventPublisher
{
    public Task PublishLogEventAsync(RlogEvent rlogEvent) => Task.CompletedTask;
    public void PublishLogEvent(RlogEvent rlogEvent) { }
}
