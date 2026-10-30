import { browserTimeZone, formatDate, readTimeZone, timeZoneStorageKey } from './formatting';

beforeEach(() => sessionStorage.clear());
afterEach(() => jest.restoreAllMocks());
test('unavailable browser timezone falls back to IST', () => {
  jest.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockImplementation(() => { throw new Error('Unavailable'); });
  expect(browserTimeZone()).toBe('Asia/Kolkata');
});
test('default is IST with the correct UTC day rollover', () => {
  expect(formatDate('2026-10-30T20:00:00Z')).toContain('31 Oct 2026');
  expect(formatDate('2026-10-30T20:00:00Z')).toContain('IST');
});
test('New York display crosses to the previous date without changing the timestamp', () => {
  const timestamp = '2026-10-30T00:00:00Z';
  expect(formatDate(timestamp, 'America/New_York')).toContain('29 Oct 2026');
  expect(formatDate(timestamp, 'America/New_York')).toContain('America/New_York');
  expect(timestamp).toBe('2026-10-30T00:00:00Z');
});
test('UTC display retains the UTC date at the IST boundary', () => {
  expect(formatDate('2026-10-30T20:00:00Z', 'UTC')).toContain('30 Oct 2026');
  expect(formatDate('2026-10-30T20:00:00Z', 'UTC')).toContain('UTC');
});
test.each(['Invalid/Zone', '', 'Mars/Olympus'])('invalid zone %s safely falls back to IST', zone => {
  expect(formatDate('2026-10-30T00:00:00Z', zone)).toContain('IST');
});
test('saved invalid setting falls back and a valid non-IST setting is restored', () => {
  sessionStorage.setItem(timeZoneStorageKey, 'Invalid/Zone');
  expect(readTimeZone()).toBe('Asia/Kolkata');
  sessionStorage.setItem(timeZoneStorageKey, 'America/New_York');
  expect(readTimeZone()).toBe('America/New_York');
});
