using FluentAssertions;
using WireHQ.Modules.Orchestration.Domain;
using Xunit;

namespace WireHQ.Orchestration.UnitTests;

/// <summary>
/// The rotation grace on <see cref="Agent"/>: rotating keeps the presented cert accepted (as the previous
/// fingerprint) until the agent first authenticates on the new one, so a lost rotate response is retryable rather
/// than a permanent lockout — and a retry on the old cert discards the orphaned cert instead of keeping it alive.
/// </summary>
public sealed class AgentCertificateRotationTests
{
    private static Agent Enrolled(string fingerprint = "A") =>
        Agent.Enroll(Guid.NewGuid(), Guid.NewGuid(), "edge", fingerprint, "pem-a", "linux-amd64",
            DateTimeOffset.Parse("2026-07-02T10:00:00Z")).Value;

    [Fact]
    public void A_fresh_agent_has_no_rotation_in_flight()
    {
        Enrolled().PreviousCertificateFingerprint.Should().BeNull();
    }

    [Fact]
    public void Rotating_on_the_current_cert_keeps_it_as_the_previous_one()
    {
        var agent = Enrolled();

        agent.RotateCertificate("B", "pem-b", presentedFingerprint: "A");

        agent.CertificateFingerprint.Should().Be("B");
        agent.CertificatePem.Should().Be("pem-b");
        agent.PreviousCertificateFingerprint.Should().Be("A");
    }

    [Fact]
    public void Retrying_on_the_previous_cert_discards_the_orphan_and_keeps_the_grace()
    {
        var agent = Enrolled();
        agent.RotateCertificate("B", "pem-b", presentedFingerprint: "A"); // response lost; B never used

        agent.RotateCertificate("C", "pem-c", presentedFingerprint: "A");

        agent.CertificateFingerprint.Should().Be("C");
        agent.PreviousCertificateFingerprint.Should().Be("A", "B was never held by the agent, so it must not stay accepted");
    }

    [Fact]
    public void Authenticating_on_the_new_cert_ends_the_grace()
    {
        var agent = Enrolled();
        agent.RotateCertificate("B", "pem-b", presentedFingerprint: "A");

        agent.ConfirmCertificate("B");

        agent.PreviousCertificateFingerprint.Should().BeNull();
    }

    [Fact]
    public void Authenticating_on_the_old_cert_does_not_end_the_grace()
    {
        var agent = Enrolled();
        agent.RotateCertificate("B", "pem-b", presentedFingerprint: "A");

        agent.ConfirmCertificate("A");

        agent.PreviousCertificateFingerprint.Should().Be("A");
    }
}
