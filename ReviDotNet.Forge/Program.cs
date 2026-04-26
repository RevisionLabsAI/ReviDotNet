// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

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

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true)
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

// Revi logging
builder.Services.AddSingleton<IRlogEventPublisher, NullRlogEventPublisher>();
builder.Services.AddSingleton<IReviLogger, ReviLogger>();
builder.Services.AddSingleton(typeof(IReviLogger<>), typeof(ReviLogger<>));

// Revi registry init (loads providers, models, prompts from embedded resources)
builder.Services.AddHostedService(sp => new RegistryInitService(
    sp.GetRequiredService<IReviLogger<RegistryInitService>>(),
    typeof(Program).Assembly));

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
builder.Services.AddSingleton<PromptRegistryService>();
builder.Services.AddSingleton<TestRunnerService>();
builder.Services.AddSingleton<PromptGeneratorService>();
builder.Services.AddSingleton<OptimizerService>();

// Gateway services
builder.Services.AddSingleton<IForgeRateLimiterService, ForgeRateLimiterService>();
builder.Services.AddSingleton<GatewayRouterService>();
builder.Services.AddSingleton<UsageDashboardService>();

// API key services
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IForgeApiKeyService, ForgeApiKeyService>();

WebApplication app = builder.Build();

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
