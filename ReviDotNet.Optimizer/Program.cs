// ===================================================================
//  Copyright © 2026 Revision Labs and contributors
//  SPDX-License-Identifier: MIT
//  See LICENSE.txt in the project root for full license information.
// ===================================================================

using MudBlazor.Services;
using Revi;
using ReviDotNet.Optimizer.Components;
using ReviDotNet.Optimizer.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);

// Blazor / MudBlazor
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

// Revi logging
builder.Services.AddSingleton<IRlogEventPublisher, NullRlogEventPublisher>();
builder.Services.AddSingleton<IReviLogger, ReviLogger>();
builder.Services.AddSingleton(typeof(IReviLogger<>), typeof(ReviLogger<>));

// Revi registry init (loads providers, models, prompts from embedded resources)
builder.Services.AddHostedService(sp => new RegistryInitService(
    sp.GetRequiredService<IReviLogger<RegistryInitService>>(),
    typeof(Program).Assembly));

// Optimizer app services
builder.Services.AddSingleton<PromptRegistryService>();
builder.Services.AddSingleton<TestRunnerService>();
builder.Services.AddSingleton<PromptGeneratorService>();
builder.Services.AddSingleton<OptimizerService>();

WebApplication app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAntiforgery();

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
