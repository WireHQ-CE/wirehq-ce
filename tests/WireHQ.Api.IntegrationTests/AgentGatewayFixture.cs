using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace WireHQ.Api.IntegrationTests;

/// <summary>
/// Runs the REAL compiled API as a child process (real Kestrel, the real <c>ConfigureAgentGateway</c>
/// listener, real TLS) against a disposable Postgres, so the agent mTLS gateway is proven end-to-end —
/// the <c>AgentCertificate</c> scheme over an actual handshake + client certificate, which the in-memory
/// TestServer cannot do. The app runs in Development (auto-migrates + applies RLS on boot) as the RLS
/// <c>wirehq_app</c> role. Tests drive the JWT API (:MainPort, http) and the agent listener (:AgentPort,
/// https) with real <see cref="HttpClient"/>s. (ADR-028)
/// </summary>
public sealed class AgentGatewayFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("wirehq")
        .WithUsername("wirehq")
        .WithPassword("wirehq")
        .Build();

    private Process? _process;

    public int MainPort { get; } = FreeTcpPort();
    public int AgentPort { get; } = FreeTcpPort();

    public HttpClient CreateApiClient() => new() { BaseAddress = new Uri($"http://localhost:{MainPort}") };

    public HttpClient CreateAgentClient(X509Certificate2? clientCertificate)
    {
        var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true; // trust the dev server cert
        if (clientCertificate is not null)
        {
            handler.SslOptions.ClientCertificates = [clientCertificate];
        }

        return new HttpClient(handler) { BaseAddress = new Uri($"https://localhost:{AgentPort}") };
    }

    /// <summary>
    /// Marks a user's email verified out-of-band (direct SQL on the owner connection) so the verified-email
    /// gate lets it create instances — the API + the gateway run in a separate process, so there's no DI to reach.
    /// </summary>
    public async Task VerifyEmailAsync(string email)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE identity.users SET email_verified = TRUE WHERE email = @e", connection);
        command.Parameters.AddWithValue("e", email);
        await command.ExecuteNonQueryAsync();
    }

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var adminConnectionString = _postgres.GetConnectionString();
        var appConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Username = "wirehq_app",
            Password = "wirehq_app",
        }.ConnectionString;

        var (apiDirectory, apiDll) = LocateApi();
        var startInfo = new ProcessStartInfo("dotnet", $"\"{apiDll}\"")
        {
            WorkingDirectory = apiDirectory, // so appsettings.json resolves
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["ASPNETCORE_HTTP_PORTS"] = MainPort.ToString();
        startInfo.Environment["ConnectionStrings__Admin"] = adminConnectionString;
        startInfo.Environment["ConnectionStrings__Default"] = appConnectionString;
        startInfo.Environment["Jwt__SigningKey"] = "integration-test-signing-key-at-least-32-bytes-long!!";
        startInfo.Environment["SecretProtection__Key"] = "ZGV2LW9ubHktMzItYnl0ZS1hZXMta2V5LWNoYW5nZSE=";
        startInfo.Environment["Seed__DemoData"] = "false";
        startInfo.Environment["Turnstile__EnabledByDefault"] = "false";
        startInfo.Environment["RateLimiting__AuthPermitPerMinute"] = "100000";
        startInfo.Environment["RateLimiting__GlobalPermitPerMinute"] = "100000";
        startInfo.Environment["AgentGateway__Enabled"] = "true";
        startInfo.Environment["AgentGateway__Port"] = AgentPort.ToString();
        // New orgs default to Enterprise so the gateway tests run unconstrained by plan caps/feature gates
        // (e.g. binding with auto-re-converge). Production defaults to Community. (docs/commercial.md)
        startInfo.Environment["Entitlements__DefaultEdition"] = "Enterprise";

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the API process.");
        var output = new System.Text.StringBuilder();
        _process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) { output.AppendLine(e.Data); } };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) { output.AppendLine(e.Data); } };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitForReadyAsync(output);
    }

    private async Task WaitForReadyAsync(System.Text.StringBuilder output)
    {
        using var probe = new HttpClient { BaseAddress = new Uri($"http://localhost:{MainPort}") };
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (_process!.HasExited)
            {
                throw new InvalidOperationException($"API process exited early (code {_process.ExitCode}):\n{output}");
            }

            try
            {
                var response = await probe.GetAsync("/health/ready");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
                // not up yet
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"API did not become ready on :{MainPort}.\n{output}");
    }

    public async Task DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process?.Dispose();
        await _postgres.DisposeAsync();
    }

    /// <summary>Finds the compiled API (built transitively by the test) and its content root.</summary>
    private static (string Directory, string Dll) LocateApi()
    {
        var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}")
            ? "Debug"
            : "Release";

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WireHQ.sln")))
        {
            root = root.Parent;
        }

        if (root is null)
        {
            throw new InvalidOperationException("Could not locate the repository root (WireHQ.sln).");
        }

        var apiDirectory = Path.Combine(root.FullName, "src", "WireHQ.Api", "bin", configuration, "net9.0");
        return (apiDirectory, Path.Combine(apiDirectory, "WireHQ.Api.dll"));
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
