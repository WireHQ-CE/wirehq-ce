export interface PagedList<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
  totalPages: number;
}

/** Keyset-paginated slice: pass `nextCursor` back to load the following page (null = last page). */
export interface CursorPage<T> {
  items: T[];
  nextCursor: string | null;
}

export interface Organization {
  id: string;
  slug: string;
  name: string;
  status: string;
  edition: string;
  legalName: string | null;
  website: string | null;
  industry: string | null;
  companySize: string | null;
  country: string | null;
  timezone: string | null;
  memberCount: number;
  teamCount: number;
  createdAtUtc: string;
}

export interface OrganizationUpdate {
  name: string;
  legalName: string | null;
  website: string | null;
  industry: string | null;
  companySize: string | null;
  country: string | null;
  timezone: string | null;
}

export interface UserListItem {
  userId: string;
  membershipId: string;
  email: string;
  name: string;
  status: string;
  joinedAtUtc: string | null;
}

export interface AuditLogItem {
  id: string;
  actorUserId: string | null;
  actorEmail: string | null;
  actorType: string;
  action: string;
  outcome: string;
  targetType: string | null;
  targetId: string | null;
  ipAddress: string | null;
  correlationId: string | null;
  /** Structured before/after diff (JSON string) for the changes viewer; null for non-mutating events. */
  changes: string | null;
  occurredAtUtc: string;
}

/** A cross-tenant audit row for the Super-Admin platform search — adds the owning tenant. */
export interface PlatformAuditLogItem extends AuditLogItem {
  organizationId: string | null;
  organizationName: string | null;
  impersonatorUserId: string | null;
}
