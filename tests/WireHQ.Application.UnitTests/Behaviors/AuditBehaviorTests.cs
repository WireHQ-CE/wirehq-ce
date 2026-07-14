using FluentAssertions;
using MediatR;
using NSubstitute;
using WireHQ.Application.Abstractions;
using WireHQ.Application.Common.Behaviors;
using WireHQ.Application.Common.Messaging;
using WireHQ.Domain.Auditing;
using WireHQ.Shared.Results;
using Xunit;

namespace WireHQ.Application.UnitTests.Behaviors;

/// <summary>
/// The declarative <see cref="AuditBehavior{TRequest,TResponse}"/> (docs/15 §5, ADR-031): a successful
/// <see cref="IAuditableRequest"/> command is audited exactly once with the marker's action and the captured
/// EF diff; failures and non-auditable requests are left alone (no spurious or duplicate entries).
/// </summary>
public sealed class AuditBehaviorTests
{
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IAuditChangeCapture _capture = Substitute.For<IAuditChangeCapture>();

    [Fact]
    public async Task Records_the_action_and_captured_diff_when_an_auditable_command_succeeds()
    {
        var changeSet = new AuditChangeSet("WireGuardNetwork", "net-1", new { hello = "world" });
        _capture.Capture().Returns(changeSet);
        var behavior = new AuditBehavior<AuditableCommand, Result>(_audit, _capture);

        var result = await behavior.Handle(
            new AuditableCommand("wg.network.created"), Next(Result.Success()), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        _audit.Received(1).Record(
            "wg.network.created", AuditOutcome.Success, "WireGuardNetwork", "net-1", changeSet.Changes, null);
    }

    [Fact]
    public async Task Does_not_audit_when_the_command_fails()
    {
        var behavior = new AuditBehavior<AuditableCommand, Result>(_audit, _capture);

        await behavior.Handle(
            new AuditableCommand("wg.network.created"),
            Next(Result.Failure(Error.Validation("x", "y"))), CancellationToken.None);

        _audit.DidNotReceiveWithAnyArgs().Record(default!);
        _capture.DidNotReceiveWithAnyArgs().Capture();
    }

    [Fact]
    public async Task Does_not_audit_a_request_that_is_not_marked_auditable()
    {
        var behavior = new AuditBehavior<PlainCommand, Result>(_audit, _capture);

        await behavior.Handle(new PlainCommand(), Next(Result.Success()), CancellationToken.None);

        _audit.DidNotReceiveWithAnyArgs().Record(default!);
        _capture.DidNotReceiveWithAnyArgs().Capture();
    }

    private static RequestHandlerDelegate<Result> Next(Result result) => _ => Task.FromResult(result);

    private sealed record AuditableCommand(string AuditAction) : IBaseCommand, IAuditableRequest;

    private sealed record PlainCommand : IBaseCommand;
}
