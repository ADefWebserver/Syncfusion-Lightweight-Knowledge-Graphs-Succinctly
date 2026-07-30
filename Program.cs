using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Syncfusion.Blazor;
using Syncfusion.Blazor.SmartComponents;
using SyncfusionHelpDesk.Components;
using SyncfusionHelpDesk.Components.Account;
using SyncfusionHelpDesk.Data;
using SyncfusionHelpDesk.Graph;
using SyncfusionHelpDesk.Models;

namespace SyncfusionHelpDesk
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            builder.Services.AddCascadingAuthenticationState();
            builder.Services.AddScoped<IdentityUserAccessor>();
            builder.Services.AddScoped<IdentityRedirectManager>();
            builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

            // Add Identity with roles support
            builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
            {
                options.SignIn.RequireConfirmedAccount = true;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

            var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
            builder.Services.AddDbContext<ApplicationDbContext>(options =>
                options.UseSqlServer(connectionString));
            builder.Services.AddDatabaseDeveloperPageExceptionFilter();

            builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

            // Syncfusion Support
            builder.Services.AddSyncfusionBlazor();

            var openAIApiKey = builder.Configuration["AI:ApiKey"];
            if (!string.IsNullOrWhiteSpace(openAIApiKey))
            {
                var openAIModel = builder.Configuration["AI:Model"] ?? "gpt-4o-mini";
                IChatClient openAIChatClient = new OpenAI.Chat.ChatClient(openAIModel, openAIApiKey)
                    .AsIChatClient();

                builder.Services.AddChatClient(openAIChatClient);
                builder.Services.AddSyncfusionSmartComponents()
                    .InjectOpenAIInference();
            }

            // Get SYNCFUSION_APIKEY from appsettings.json
            var SyncfusionApiKey = builder.Configuration["SYNCFUSION_APIKEY"];

            if (SyncfusionApiKey != "")
            {
                // Register Syncfusion license
                Syncfusion.Licensing.SyncfusionLicenseProvider
                    .RegisterLicense(SyncfusionApiKey);
            }

            // To access HelpDesk tables
            builder.Services.AddDbContextFactory<SyncfusionHelpDeskContext>(options =>
                options.UseSqlServer(connectionString));

            builder.Services.AddScoped<SyncfusionHelpDeskService>();
            builder.Services.AddScoped<EmailSender>();

            // Knowledge-graph storage layer.
            builder.Services.Configure<GraphOptions>(
                builder.Configuration.GetSection("Graph"));
            builder.Services.AddScoped<HelpDeskGraphBuilder>();
            builder.Services.AddSingleton<GraphStore>();

            // Ensure the knowledge-graph output directory exists so the builder can
            // write graph files atomically without a first-run failure.
            var graphPath = builder.Configuration["Graph:OutputPath"] ?? "App_Data/graph";
            Directory.CreateDirectory(
                Path.Combine(builder.Environment.ContentRootPath, graphPath));

            var app = builder.Build();

            // Build graph.json on start-up when it does not already exist, then
            // load the snapshot into the in-memory GraphStore.
            var graphFilePath = GraphFile.ResolvePath(
                graphPath, app.Environment.ContentRootPath);
            if (!File.Exists(graphFilePath))
            {
                using var graphScope = app.Services.CreateScope();
                var graphBuilder = graphScope.ServiceProvider
                    .GetRequiredService<HelpDeskGraphBuilder>();
                await graphBuilder.BuildAsync();
            }

            await app.Services.GetRequiredService<GraphStore>().ReloadAsync();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseMigrationsEndPoint();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }

            app.UseHttpsRedirection();

            // Authentication and Authorization Middleware
            app.UseAuthentication();
            app.UseAuthorization();

            app.UseAntiforgery(); // Must be placed after UseAuthentication and UseAuthorization

            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.MapAdditionalIdentityEndpoints();

            // Ensure the Administrator role is created at startup
            await CreateRoles(app.Services);

            await app.RunAsync();
        }

        // Role creation logic
        private static async Task CreateRoles(IServiceProvider serviceProvider)
        {
            using var scope = serviceProvider.CreateScope();
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

            // Check if the Administrator role exists, if not, create it
            if (!await roleManager.RoleExistsAsync("Administrators"))
            {
                var adminRole = new IdentityRole("Administrators");
                await roleManager.CreateAsync(adminRole);
            }
        }
    }
}
