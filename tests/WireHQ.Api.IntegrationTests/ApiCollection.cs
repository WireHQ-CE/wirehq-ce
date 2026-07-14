using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Shares a single <see cref="WireHqApiFactory"/> (one API host + one Postgres container) across all
/// integration test classes. xUnit runs a collection's tests sequentially, so there is never more
/// than one host alive — which avoids racing on Serilog's static logger ("already frozen") and on the
/// process-global connection-string environment variables the factory sets. Tests stay isolated by
/// creating their own organization per run.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<WireHqApiFactory>
{
    public const string Name = "Api";
}
