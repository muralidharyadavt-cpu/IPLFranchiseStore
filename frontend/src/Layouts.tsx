import type { ReactNode } from 'react';
import { Link, NavLink } from 'react-router-dom';
import { CustomerStatus } from './customerSession';
import { DemoRoleSelector, useDemoRole } from './demoRole';

function Frame({ admin, children }: { admin: boolean; children: ReactNode }) {
  return <><a className="skip-link" href="#main">Skip to content</a>
    <header className="site-header"><Link className="brand" to={admin ? '/admin' : '/products'}>{admin ? 'Development Demo Admin' : 'IPL Franchise Store'}</Link>
      {admin ? <nav aria-label="Admin navigation"><NavLink to="/admin">Inventory management</NavLink><Link to="/admin#stock-adjustments">Stock adjustments</Link><Link to="/admin#price-updates">Price updates</Link><Link to="/admin#audit-history">Audit history</Link></nav> :
        <nav aria-label="Shopper navigation">{['Products', 'Cart', 'Orders'].map(name => <NavLink key={name} to={`/${name.toLowerCase()}`}>{name}</NavLink>)}</nav>}
    </header>
    <main id="main"><DemoRoleSelector /><CustomerStatus />{children}</main>
    <footer>IPL Franchise Merchandise Store · Prices in INR, inclusive of tax</footer></>;
}
export function ShopperLayout({ children }: { children: ReactNode }) { return <Frame admin={false}>{children}</Frame>; }
export function AdminLayout({ children }: { children: ReactNode }) { return <Frame admin>{children}</Frame>; }
export function RoleLayout({ children }: { children: ReactNode }) {
  return useDemoRole().role === 'Admin' ? <AdminLayout>{children}</AdminLayout> : <ShopperLayout>{children}</ShopperLayout>;
}
