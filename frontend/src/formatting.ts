export const formatPrice = (price: number) => new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR' }).format(price);
export const defaultTimeZone = 'Asia/Kolkata';
export const timeZoneStorageKey = 'ipl.display-time-zone.v1';
export function validTimeZone(value: string | null | undefined): string {
  if (value) { try { new Intl.DateTimeFormat('en-IN', { timeZone: value }); return value; } catch { /* Invalid preference falls back safely. */ } }
  return defaultTimeZone;
}
export function readTimeZone(): string {
  try { return validTimeZone(sessionStorage.getItem(timeZoneStorageKey)); } catch { return defaultTimeZone; }
}
export function browserTimeZone(): string {
  try { return validTimeZone(Intl.DateTimeFormat().resolvedOptions().timeZone); } catch { return defaultTimeZone; }
}
export function formatDate(utc: string, selectedZone = defaultTimeZone): string {
  const timeZone = validTimeZone(selectedZone);
  return new Intl.DateTimeFormat('en-IN', { dateStyle: 'medium', timeStyle: 'short', timeZone }).format(new Date(utc)) +
    (timeZone === defaultTimeZone ? ' IST' : ' ' + timeZone);
}
