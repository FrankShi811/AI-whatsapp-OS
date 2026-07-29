using WAFlow.Core;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;

namespace WAFlow.Mac;

internal static class MacSelfTest
{
    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ai-sales-os-mac-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var previousDataRoot = Environment.GetEnvironmentVariable("WAFLOW_LOCAL_APP_DATA_ROOT");
        Environment.SetEnvironmentVariable("WAFLOW_LOCAL_APP_DATA_ROOT", root);
        var database = PlatformDataPaths.DatabasePath;
        var secrets = new MemorySecretStore();
        var services = new AppServices(new LocalRepository(), _ => secrets);
        try
        {
            await services.InitializeAsync();
            var lead = new Lead
            {
                BuyerId = "mac-smoke-buyer",
                Name = "macOS Smoke Customer",
                Country = "US",
                PhoneE164 = "+14155552671",
                PhoneValid = true,
                ProductInterest = "Native macOS validation"
            };
            await services.Repository.UpsertLeadAsync(lead);
            var loaded = await services.Repository.GetLeadByBuyerIdAsync(lead.BuyerId);
            var dashboard = await services.Repository.GetDashboardAsync();
            var accounts = await services.Repository.GetWhatsAppAccountsAsync();
            if (accounts.Count == 0)
            {
                accounts.Add(new WhatsAppAccount { Id = "primary", Name = "macOS Smoke Account" });
                await services.Repository.SaveWhatsAppAccountsAsync(accounts);
                accounts = await services.Repository.GetWhatsAppAccountsAsync();
            }
            if (loaded is null || dashboard.TotalLeads < 1 || accounts.Count == 0)
                throw new InvalidOperationException("Core repository smoke assertions failed.");
            Console.WriteLine(
                $"PASS macOS runtime smoke version={typeof(App).Assembly.GetName().Version} " +
                $"database={Path.GetFileName(database)} modules=8");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"FAIL macOS runtime smoke: {error}");
            return 1;
        }
        finally
        {
            try { await services.MessagingSync.DisposeAsync(); } catch { }
            try { await services.LeadAutomation.DisposeAsync(); } catch { }
            try { await services.Campaigns.DisposeAsync(); } catch { }
            try { await services.Email.DisposeAsync(); } catch { }
            try { await services.WhatsApp.DisposeAsync(); } catch { }
            services.CustomerSuccessCoordinator.Dispose();
            try { Directory.Delete(root, true); } catch { }
            Environment.SetEnvironmentVariable("WAFLOW_LOCAL_APP_DATA_ROOT", previousDataRoot);
        }
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private string? _secret;
        public void Save(string secret) => _secret = secret;
        public string? Read() => _secret;
        public void Delete() => _secret = null;
    }
}
