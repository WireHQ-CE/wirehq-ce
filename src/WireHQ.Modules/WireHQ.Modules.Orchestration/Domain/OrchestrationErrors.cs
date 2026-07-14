using WireHQ.Shared.Results;

namespace WireHQ.Modules.Orchestration.Domain;

/// <summary>Stable, machine-readable errors for the orchestration module (mapped centrally to HTTP).</summary>
public static class OrchestrationErrors
{
    public static class Deployment
    {
        public static readonly Error InstanceNotFound = Error.NotFound("orch.deployment.instance_not_found", "The instance to deploy was not found.");
        public static readonly Error JobNotFound = Error.NotFound("orch.deployment.job_not_found", "Deployment job was not found.");
    }

    public static class Target
    {
        public static readonly Error SshTargetRequired = Error.Validation("orch.target.ssh_target_required", "This instance is bound to SSH but its target is missing.");
    }

    public static class Ssh
    {
        public static readonly Error HostKeyMismatch = Error.Failure("orch.ssh.host_key_mismatch", "The host key did not match the pinned fingerprint.");
        public static readonly Error WireGuardNotPresent = Error.Failure("orch.ssh.wireguard_missing", "wireguard-tools (wg/wg-quick) were not found on the host.");

        public static Error ConnectFailed(string detail) => Error.Failure("orch.ssh.connect_failed", $"Could not connect over SSH: {detail}");

        public static Error CommandFailed(string detail) => Error.Failure("orch.ssh.command_failed", detail);
    }

    public static class Agent
    {
        public static readonly Error InvalidName = Error.Validation("orch.agent.invalid_name", "Agent name is required and must be 96 characters or fewer.");
        public static readonly Error InvalidCertificate = Error.Validation("orch.agent.invalid_certificate", "A valid agent certificate is required.");
        public static readonly Error InvalidToken = Error.Validation("orch.agent.invalid_token", "An enrollment token is required.");
        public static readonly Error NotFound = Error.NotFound("orch.agent.not_found", "Agent was not found.");

        /// <summary>Returned by the gateway enrol endpoint — kept generic so it leaks nothing about which check failed.</summary>
        public static readonly Error EnrollmentRejected = Error.Unauthorized("orch.agent.enrollment_rejected", "Enrollment was rejected: the token is invalid, expired, or already used.");
        public static readonly Error InvalidCsr = Error.Validation("orch.agent.invalid_csr", "The certificate signing request could not be read or its signature was invalid.");
        public static readonly Error WeakKey = Error.Validation("orch.agent.weak_key", "The certificate signing request must carry an EC P-256 or P-384 public key.");
        public static readonly Error CaUnavailable = Error.Failure("orch.agent.ca_unavailable", "The organization's certificate authority could not be established.");
    }

    public static class SshTarget
    {
        public static readonly Error InvalidName = Error.Validation("orch.ssh_target.invalid_name", "Target name is required and must be 96 characters or fewer.");
        public static readonly Error InvalidHost = Error.Validation("orch.ssh_target.invalid_host", "A host is required.");
        public static readonly Error InvalidPort = Error.Validation("orch.ssh_target.invalid_port", "Port must be between 1 and 65535.");
        public static readonly Error InvalidUsername = Error.Validation("orch.ssh_target.invalid_username", "A username is required.");
        public static readonly Error CredentialRequired = Error.Validation("orch.ssh_target.credential_required", "A private key or password is required.");
        public static readonly Error NotFound = Error.NotFound("orch.ssh_target.not_found", "SSH target was not found.");
    }
}
