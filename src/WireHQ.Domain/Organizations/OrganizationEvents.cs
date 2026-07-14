using WireHQ.Domain.Common;

namespace WireHQ.Domain.Organizations;

public sealed record OrganizationCreated(Guid OrganizationId, string Slug, string Name) : IDomainEvent;

public sealed record OrganizationRenamed(Guid OrganizationId, string Name) : IDomainEvent;

public sealed record OrganizationSuspended(Guid OrganizationId, string Reason) : IDomainEvent;

public sealed record OrganizationReactivated(Guid OrganizationId) : IDomainEvent;
