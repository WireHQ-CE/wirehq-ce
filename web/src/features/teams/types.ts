// Mirrors the Teams API DTOs (/api/v1/teams).

export interface TeamListItem {
  id: string;
  name: string;
  slug: string;
  description: string | null;
  memberCount: number;
  createdAtUtc: string;
}

export interface TeamMemberItem {
  membershipId: string;
  userId: string;
  name: string;
  email: string;
  status: string;
  addedAtUtc: string;
}

export interface TeamDetail {
  id: string;
  name: string;
  slug: string;
  description: string | null;
  createdAtUtc: string;
  members: TeamMemberItem[];
}

export interface CreateTeamInput {
  name: string;
  description?: string;
}

export interface UpdateTeamInput {
  name?: string;
  description?: string;
}

export interface AddTeamMemberInput {
  email: string;
  name?: string;
  roleId?: string;
}

export interface AddTeamMemberResult {
  teamId: string;
  membershipId: string;
  /** InvitedNewUser | AddedExistingUser | AlreadyMember */
  outcome: string;
}
