using System;
using System.Threading;
using System.Threading.Tasks;
using ApiHelpers;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace PortalREST;

public class WooCommerceOrderPollingService : BackgroundService
{
    private const int PollingPeriodSeconds = 10;

    public WooCommerceOrderPollingService(string domain, string key, string secret)
    {
        WCHelper.Initialize(domain, key, secret);
        CustomerOrderAccess.Initialize();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("WooCommerce Polling Service started.");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await WCHelper.AddMetaForNewOrders();
                    await CustomerOrderAccess.UnassignExpiredOrders();
                }
                catch (Exception ex)
                {
                    Log.Error($"Error in polling service: {ex.Message}");
                }

                await Task.Delay(TimeSpan.FromSeconds(PollingPeriodSeconds), stoppingToken);
            }
        }
        catch (TaskCanceledException)
        {
            Log.Error("WooCommerce Polling Service was cancelled.");
        }
        finally
        {
            Log.Error("WooCommerce Polling Service is stopping gracefully.");
        }
    }
}