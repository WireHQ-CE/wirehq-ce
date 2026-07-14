using WireHQ.Shared.Results;

namespace WireHQ.Modules.WireGuard.Domain;

/// <summary>Stable, machine-readable errors for the WireGuard module (mapped centrally to HTTP).</summary>
public static class WireGuardErrors
{
    public static class Network
    {
        public static readonly Error InvalidName = Error.Validation("wg.network.invalid_name", "Network name is required and must be 96 characters or fewer.");
        public static readonly Error InvalidCidr = Error.Validation("wg.network.invalid_cidr", "A valid network CIDR is required (e.g. 10.8.0.0/24).");
        public static readonly Error NotFound = Error.NotFound("wg.network.not_found", "Network was not found.");
        public static readonly Error Exhausted = Error.Conflict("wg.network.exhausted", "No free addresses remain in the network.");
        public static readonly Error HasInstances = Error.Conflict("wg.network.has_instances", "This network still has instances; delete them first.");
    }

    public static class Instance
    {
        public static readonly Error InvalidName = Error.Validation("wg.instance.invalid_name", "Instance name is required and must be 96 characters or fewer.");
        public static readonly Error InvalidPort = Error.Validation("wg.instance.invalid_port", "Listen port must be between 1 and 65535.");
        public static readonly Error InvalidAddress = Error.Validation("wg.instance.invalid_address", "A valid interface address is required (e.g. 10.8.0.1/24).");
        public static readonly Error NotFound = Error.NotFound("wg.instance.not_found", "Instance was not found.");
        public static readonly Error SlugTaken = Error.Conflict("wg.instance.slug_taken", "An instance with that URL already exists.");
    }

    public static class Peer
    {
        public static readonly Error InvalidName = Error.Validation("wg.peer.invalid_name", "Peer name is required and must be 128 characters or fewer.");
        public static readonly Error NotFound = Error.NotFound("wg.peer.not_found", "Peer was not found.");
        public static readonly Error AddressTaken = Error.Conflict("wg.peer.address_taken", "That address is already assigned in this instance.");
        public static readonly Error PublicKeyTaken = Error.Conflict("wg.peer.public_key_taken", "A peer with that public key already exists in this instance.");
        public static readonly Error Revoked = Error.Conflict("wg.peer.revoked", "This peer has been revoked.");
    }

    public static class Key
    {
        public static readonly Error NotFound = Error.NotFound("wg.key.not_found", "Key material was not found.");
        public static readonly Error PrivateKeyUnavailable = Error.Conflict("wg.key.private_unavailable", "The private key is held by the client and cannot be exported.");
        public static readonly Error ServerKeyAgentManaged = Error.Conflict("wg.key.server_agent_managed", "This instance's interface key is agent-managed — WireHQ does not hold it, so the full server config cannot be exported.");
    }

    public static class Config
    {
        public static readonly Error VersionNotFound = Error.NotFound("wg.config.version_not_found", "That configuration version was not found.");
    }

    public static class Enrollment
    {
        public static readonly Error EmptyFile = Error.Validation("wg.enrollment.empty_file", "The uploaded CSV is empty.");
        public static readonly Error FileTooLarge = Error.Validation("wg.enrollment.file_too_large", "The CSV exceeds the maximum allowed size.");
        public static readonly Error TooManyRows = Error.Validation("wg.enrollment.too_many_rows", "The CSV exceeds the maximum number of rows.");
        public static readonly Error MissingColumns = Error.Validation("wg.enrollment.missing_columns", "The CSV is missing the required 'Name' and 'Email' columns.");
        public static readonly Error NoRows = Error.Validation("wg.enrollment.no_rows", "The CSV contained no data rows.");
        public static readonly Error NothingToImport = Error.Validation("wg.enrollment.nothing_to_import", "There are no valid, non-duplicate rows to import.");
        public static readonly Error BatchNotFound = Error.NotFound("wg.enrollment.batch_not_found", "Enrollment batch was not found.");
    }
}
