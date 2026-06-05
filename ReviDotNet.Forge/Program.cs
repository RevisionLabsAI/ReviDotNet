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
using ReviDotNet.Forge.Api;
using ReviDotNet.Forge.Components;
using ReviDotNet.Forge.Services;
using ReviDotNet.Forge.Services.ApiKeys;
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
builder.Services.AddReviDotNet(typeof(Program).Assembly);

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
builder.Services.AddSingleton<PromptGeneratorService>();
builder.Services.AddSingleton<OptimizerService>();

// Agent Workshop
builder.Services.AddSingleton<IAgentWorkshopService, AgentWorkshopService>();
builder.Services.AddSingleton<IWorkshopStore, WorkshopStore>();
builder.Services.AddSingleton<AgentGeneratorService>();

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
