/** Curated option lists shared across profile + organization forms. Free strings are stored server-side. */

/** Common IANA timezones (label → value). Kept short and practical rather than exhaustive. */
export const TIMEZONES: { value: string; label: string }[] = [
  { value: 'Pacific/Honolulu', label: '(GMT-10:00) Hawaii' },
  { value: 'America/Anchorage', label: '(GMT-09:00) Alaska' },
  { value: 'America/Los_Angeles', label: '(GMT-08:00) Pacific Time' },
  { value: 'America/Denver', label: '(GMT-07:00) Mountain Time' },
  { value: 'America/Chicago', label: '(GMT-06:00) Central Time' },
  { value: 'America/New_York', label: '(GMT-05:00) Eastern Time' },
  { value: 'America/Sao_Paulo', label: '(GMT-03:00) São Paulo' },
  { value: 'Etc/UTC', label: '(GMT+00:00) UTC' },
  { value: 'Europe/London', label: '(GMT+00:00) London, Dublin' },
  { value: 'Europe/Paris', label: '(GMT+01:00) Paris, Berlin, Madrid' },
  { value: 'Europe/Athens', label: '(GMT+02:00) Athens, Cairo' },
  { value: 'Europe/Moscow', label: '(GMT+03:00) Moscow' },
  { value: 'Asia/Dubai', label: '(GMT+04:00) Dubai' },
  { value: 'Asia/Karachi', label: '(GMT+05:00) Karachi' },
  { value: 'Asia/Kolkata', label: '(GMT+05:30) India' },
  { value: 'Asia/Singapore', label: '(GMT+08:00) Singapore, Hong Kong' },
  { value: 'Asia/Shanghai', label: '(GMT+08:00) Beijing' },
  { value: 'Asia/Tokyo', label: '(GMT+09:00) Tokyo, Seoul' },
  { value: 'Australia/Sydney', label: '(GMT+10:00) Sydney' },
  { value: 'Pacific/Auckland', label: '(GMT+12:00) Auckland' },
];

/** Common UI languages (BCP-47). */
export const LANGUAGES: { value: string; label: string }[] = [
  { value: 'en', label: 'English' },
  { value: 'es', label: 'Español' },
  { value: 'fr', label: 'Français' },
  { value: 'de', label: 'Deutsch' },
  { value: 'pt', label: 'Português' },
  { value: 'nl', label: 'Nederlands' },
  { value: 'it', label: 'Italiano' },
  { value: 'ja', label: '日本語' },
  { value: 'zh', label: '中文' },
];

/** ISO-3166 countries (commonly used subset) for organization + billing addresses. */
export const COUNTRIES: { value: string; label: string }[] = [
  { value: 'US', label: 'United States' },
  { value: 'GB', label: 'United Kingdom' },
  { value: 'IE', label: 'Ireland' },
  { value: 'CA', label: 'Canada' },
  { value: 'AU', label: 'Australia' },
  { value: 'NZ', label: 'New Zealand' },
  { value: 'DE', label: 'Germany' },
  { value: 'FR', label: 'France' },
  { value: 'ES', label: 'Spain' },
  { value: 'IT', label: 'Italy' },
  { value: 'NL', label: 'Netherlands' },
  { value: 'SE', label: 'Sweden' },
  { value: 'NO', label: 'Norway' },
  { value: 'DK', label: 'Denmark' },
  { value: 'CH', label: 'Switzerland' },
  { value: 'AE', label: 'United Arab Emirates' },
  { value: 'IN', label: 'India' },
  { value: 'SG', label: 'Singapore' },
  { value: 'JP', label: 'Japan' },
  { value: 'BR', label: 'Brazil' },
  { value: 'ZA', label: 'South Africa' },
];

/** Company-size buckets, shared with the onboarding wizard's sizing options. */
export const COMPANY_SIZES = ['1', '2–10', '11–50', '51–200', '201–1000', '1000+'];
