/** The shared audit filter set (mirrors the API query params on both the tenant + platform reads). */
export interface AuditFilterValues {
  from?: string;
  to?: string;
  action?: string;
  category?: string;
  actor?: string;
  target?: string;
  outcome?: string;
  q?: string;
}

export const emptyAuditFilters: AuditFilterValues = {};
