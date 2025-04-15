using System.Collections.Concurrent;
using System.Linq;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text.Json;
using ApiHelpers;
using PortalREST;
using Serilog;



// Initialize logging...
LoggerConfig.InitializeLogger();

var builder = WebApplication.CreateBuilder(args);

// Explicitly load appsettings.json
builder.Configuration
    .SetBasePath(Directory.GetCurrentDirectory()) // Ensure correct path
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables();

var sslPass = builder.Configuration["SSLPassword"]!;

builder.WebHost.UseKestrel(options =>
{
    options.ListenAnyIP(5000); // HTTP

    options.ListenAnyIP(5001, listenOptions =>
    {
        var env = builder.Environment.EnvironmentName;

        if (builder.Environment.IsDevelopment())
        {
            // Local debug SSL cert
            listenOptions.UseHttps(@"C:\local_ssl\localhost.pfx", sslPass);
        }
        else
        {
            // Production Let's Encrypt cert
            listenOptions.UseHttps("/etc/letsencrypt/live/slowcasting.com/kestrel-cert.pfx", sslPass);
        }
    });
});

builder.Services.AddCors();

var googleApiKey = builder.Configuration["GoogleApiKey"]!;
var ticketMasterApiKey = builder.Configuration["TicketMasterApiKey"]!;
// Load EmployeePasswords from appsettings.json
var employeeList = builder.Configuration.GetSection("EmployeePasswords").Get<List<EmployeeLogin>>();
LoginAuth.SetPasswords(employeeList!);

var wcKey = builder.Configuration["WooCommerceKey"]!;
var wcSecret = builder.Configuration["WooCommerceSecret"]!;
var emailServer = builder.Configuration["EmailSMTPAddress"]!;
var emailAddress = builder.Configuration["EmailSMTPEmail"]!;
var emailPass = builder.Configuration["EmailSMTPPassword"]!;

// Add WooCommerce polling service to DI container
builder.Services.AddHostedService(provider =>
    new WooCommerceOrderPollingService(
        "https://slowdialing.com/wp-json/wc/v2/", // Replace with actual WooCommerce domain
        wcKey,
        wcSecret
    )
);

// Builder build
var app = builder.Build();

// Enable CORS for frontend calls
app.UseCors(policy =>
    policy.AllowAnyOrigin()
        .AllowAnyMethod()
        .AllowAnyHeader());


// Enforce HTTPS redirection
app.UseHttpsRedirection();

// Root endpoint for API health check
app.MapGet("/", () =>
{
    return Results.Ok("API is running.");
});

Log.Information($"Starting backend...");

// **************************************************
// Google routes for place info.
Routing.MapGoogleRoutes(app, googleApiKey, ticketMasterApiKey);

// **************************************************
// Place saving/updating/etc.
Routing.MapSavePlaceRoutes(app, emailServer, emailAddress, emailPass, googleApiKey);

// **************************************************
// Order IO, accepting, retrieving, completing, etc.
Routing.MapOrderRoutes(app);

// **************************************************
// PBX credentials, etc.
Routing.MapPbxRoutes(app);

app.Run();