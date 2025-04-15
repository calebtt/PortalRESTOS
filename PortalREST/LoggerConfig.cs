using Serilog;
using System;
namespace PortalREST;

public static class LoggerConfig
{
    public static void InitializeLogger()
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.File("logs/app_log.txt", rollingInterval: RollingInterval.Day)
            .WriteTo.Console()
            .MinimumLevel.Debug()
            .CreateLogger();

        Log.Information("Logging system initialized.");
    }
}
