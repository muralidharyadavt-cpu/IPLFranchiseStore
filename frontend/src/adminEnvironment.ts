declare const __DEVELOPMENT_ADMIN_UI__: boolean;
export function isAdminUiEnabled(): boolean {
  return typeof __DEVELOPMENT_ADMIN_UI__ !== 'undefined' && __DEVELOPMENT_ADMIN_UI__;
}
