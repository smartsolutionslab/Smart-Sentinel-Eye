import { fetchBaseQuery } from '@reduxjs/toolkit/query/react';
import { logResilienceEvent } from '../observability/resilienceLog.js';

// ADR-0106 (#1005): the browser apps reach every context REST API through the
// single API gateway, cross-origin. The gateway's CORS policy (#1003) allows the
// app origins, and it routes on `/<context>/...` — stripping that prefix before
// forwarding — so `${origin}/<context>/<group>` lands on the service's `<group>`
// route (e.g. camera-catalog exposes `/cameras`). The gateway origin is injected
// by the host: Aspire sets VITE_API_GATEWAY_URL in dev; the deploy layer supplies
// the public URL in prod. An empty origin falls back to same-origin outside a
// production build, which keeps unit tests and previews working; a production
// build with no origin throws at load instead of falling back (see below).
//
// Realtime (ADR-0076 WebSocket) and WebRTC media do NOT go through here — they
// stay direct, off the gateway and off the §IV latency budget.
const gatewayOrigin: string = (import.meta.env.VITE_API_GATEWAY_URL ?? '').replace(/\/+$/, '');

// Spec 011 FR-010: a prod bundle without the gateway origin would silently
// fire API calls at the static-file origin — fail loudly at load instead.
if (import.meta.env.PROD && gatewayOrigin === '') {
  throw new Error('VITE_API_GATEWAY_URL must be set in production builds (see docs/deployment-frontend-env.md).');
}

export const gatewayApiUrl = (route: string): string => `${gatewayOrigin}/${route}`;

// ADR-0113 Layer 1: a mutating request carries the aggregate version it was
// read at, so the server can refuse an edit built on a stale view instead of
// silently overwriting whoever wrote in between.
//
// The version is threaded explicitly through each mutation's arguments rather
// than cached here and injected centrally. A central store would have to map a
// request URL back to the resource whose ETag it needs -- `POST
// /layouts/{id}/revisions/2/publish` is guarded by the ETag from `GET
// /layouts/{id}` -- and any miss degrades to a request with no version, which
// is the silent fallback this whole mechanism exists to remove. Passing it as
// an argument makes TypeScript reject a call site that forgets.
export const ifMatch = (version: number): Record<string, string> => ({ 'If-Match': `"${version}"` });

// Every context API requires a Keycloak-minted JWT (ADR-0007/0008; the gateway
// forwards Authorization unmodified, ADR-0106). The RTK Query clients live in
// this shared package and are app-agnostic, so each app registers a getter that
// sources the current access token from its OIDC user; prepareHeaders attaches
// it as a bearer on every gateway request.
type AccessTokenGetter = () => string | undefined;

let accessTokenProvider: AccessTokenGetter = () => undefined;

export const setAccessTokenProvider = (provider: AccessTokenGetter): void => {
  accessTokenProvider = provider;
};

// Spec 011 FR-011/012: a 401 gets exactly one silent renewal and one retry
// before the session counts as expired. Both hooks are app-registered module
// singletons for the same reason as setAccessTokenProvider: the shared clients
// are app-agnostic, and registration must happen during render, before the
// first query dispatches.
type SessionRenewer = () => Promise<boolean>;

let sessionRenewer: SessionRenewer = () => Promise.resolve(false);
let onSessionExpired: () => void = () => undefined;

export const setSessionRenewer = (renew: SessionRenewer): void => {
  sessionRenewer = renew;
};

export const setOnSessionExpired = (handler: () => void): void => {
  onSessionExpired = handler;
};

// A burst of queries after token death must not stampede the identity
// provider: every concurrent 401 awaits the single in-flight renewal.
let renewalInFlight: Promise<boolean> | null = null;

const renewSessionOnce = (): Promise<boolean> => {
  if (renewalInFlight === null) {
    logResilienceEvent('session', 'renew-start');
    renewalInFlight = sessionRenewer()
      .catch(() => false)
      .then((renewed) => {
        renewalInFlight = null;
        logResilienceEvent('session', renewed ? 'renew-success' : 'renew-failure');
        return renewed;
      });
  }
  return renewalInFlight;
};

export const gatewayBaseQuery = (route: string): ReturnType<typeof fetchBaseQuery> => {
  const baseQuery = fetchBaseQuery({
    baseUrl: gatewayApiUrl(route),
    prepareHeaders: (headers) => {
      const token = accessTokenProvider();
      if (token !== undefined && token !== '') {
        headers.set('Authorization', `Bearer ${token}`);
      }
      return headers;
    },
  });

  return async (args, queryApi, extraOptions) => {
    let result = await baseQuery(args, queryApi, extraOptions);
    if (result.error === undefined || result.error.status !== 401) {
      return result;
    }

    if (await renewSessionOnce()) {
      result = await baseQuery(args, queryApi, extraOptions);
      if (result.error === undefined || result.error.status !== 401) {
        return result;
      }
    }

    logResilienceEvent('session', 'expired');
    onSessionExpired();
    return result;
  };
};
