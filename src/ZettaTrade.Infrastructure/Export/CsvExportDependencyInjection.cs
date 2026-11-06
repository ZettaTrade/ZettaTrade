namespace ZettaTrade.Infrastructure.Export;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI wiring for the CSV export module. The composition root calls the
/// single aggregate extension, so module-level registration details stay
/// local. The service is stateless (pure format work), so a singleton is
/// safe and allocation-free.
/// </summary>
public static class CsvExportDependencyInjection
{
    /// <summary>Register the CSV export service.</summary>
    public static IServiceCollection AddCsvExportServices(this IServiceCollection services) => services
        .AddSingleton<ICsvExportService, CsvExportService>();
}
