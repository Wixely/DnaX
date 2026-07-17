using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;

namespace DnaX.Data;

public sealed class DnaXDataBuilder
{
    internal DnaXDataBuilder(IServiceCollection services) => Services = services;

    internal IServiceCollection Services { get; }

    public DnaXDataBuilder AddDatabase(string name, Func<IServiceProvider, DbConnection> connectionFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(connectionFactory);

        Services.AddSingleton(new DnaXDatabaseRegistration(name, connectionFactory));
        return this;
    }

    public DnaXDataBuilder AddDatabase(string name, Func<DbConnection> connectionFactory)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        return AddDatabase(name, _ => connectionFactory());
    }
}

internal sealed record DnaXDatabaseRegistration(
    string Name,
    Func<IServiceProvider, DbConnection> ConnectionFactory);
