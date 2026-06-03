using DeltaLinq;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeltaLinq.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddDeltaLake_registers_factory_and_options()
    {
        var services = new ServiceCollection();
        services.AddDeltaLake(o => o.OnSql = _ => { });

        using var provider = services.BuildServiceProvider();

        var factory = provider.GetRequiredService<IDeltaTableFactory>();
        var query = factory.Open<User>("dummy");

        Assert.IsAssignableFrom<IQueryable<User>>(query);
        Assert.NotNull(provider.GetRequiredService<DeltaOptions>().OnSql);
    }
}
