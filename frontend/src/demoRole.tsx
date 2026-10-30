import { createContext, useContext, useState } from 'react';
import type { ReactNode } from 'react';
import { useNavigate } from 'react-router-dom';
import { isAdminUiEnabled } from './adminEnvironment';

export type DemoRole = 'Shopper' | 'Admin';
export const demoRoleStorageKey = 'ipl.demo-role.v1';
interface DemoRoleContextValue {
  role: DemoRole; enabled: boolean; changeRole: (role: DemoRole) => void; error: string;
}
const DemoRoleContext = createContext<DemoRoleContextValue | null>(null);
export function useDemoRole() {
  const value = useContext(DemoRoleContext);
  if (!value) throw new Error('DemoRoleProvider is required.');
  return value;
}
export function DemoRoleProvider({ children }: { children: ReactNode }) {
  const enabled = isAdminUiEnabled();
  const [selected, setSelected] = useState<DemoRole>(() => {
    if (!enabled) return 'Shopper';
    try { return sessionStorage.getItem(demoRoleStorageKey) === 'Admin' ? 'Admin' : 'Shopper'; }
    catch { return 'Shopper'; }
  });
  const [error, setError] = useState('');
  const navigate = useNavigate();
  // Production ignores even a manually stored Admin value. Roles never come from demo user identity.
  const role = enabled ? selected : 'Shopper';
  const changeRole = (next: DemoRole) => {
    if (!enabled) return;
    try { sessionStorage.setItem(demoRoleStorageKey, next); }
    catch { setError('Cannot save the demo role. Enable session storage before switching experiences.'); return; }
    setError(''); setSelected(next); navigate(next === 'Admin' ? '/admin' : '/products');
  };
  return <DemoRoleContext.Provider value={{ role, enabled, changeRole, error }}>{children}</DemoRoleContext.Provider>;
}
export function DemoRoleSelector() {
  const { role, enabled, changeRole, error } = useDemoRole();
  if (!enabled) return null;
  return <section className="demo-role" aria-label="Development role simulation">
    <label htmlFor="demo-role">Development-only demo role<select id="demo-role" value={role} onChange={event => changeRole(event.target.value === 'Admin' ? 'Admin' : 'Shopper')}><option value="Shopper">Shopper</option><option value="Admin">Development Demo Admin</option></select></label>
    <p>This selector simulates a UI role. It is NOT real authentication or server authorization.</p>
    {error && <p role="alert" aria-live="assertive">{error}</p>}
  </section>;
}
