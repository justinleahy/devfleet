# Research: Native iOS API surface (ASP.NET Core 10)

**Date researched:** 2026-09-05
**Target framework:** .NET 10 / ASP.NET Core 10 (`PiCommandCenter.ControlPlane.csproj` → `net10.0`)
**Purpose:** Decision record for a native iOS JSON API (`/api/v1`) beside the existing Identity cookie Blazor host. Primary sources only.

**Primary sources:**

- [Use Identity to secure a Web API backend for SPAs](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity-api-authorization?view=aspnetcore-10.0) (ASP.NET Core 10)
- [BearerTokenOptions](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.authentication.bearertoken.bearertokenoptions?view=aspnetcore-10.0)
- [AccessTokenResponse](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.authentication.bearertoken.accesstokenresponse?view=aspnetcore-10.0)
- [BearerTokenExtensions.AddBearerToken](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.dependencyinjection.bearertokenextensions.addbearertoken?view=aspnetcore-10.0)
- [IdentityConstants.BearerScheme](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.identity.identityconstants?view=aspnetcore-10.0)
- [Configure JWT bearer authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-10.0)
- [Generate OpenAPI documents](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/aspnetcore-openapi?view=aspnetcore-10.0)
- [Include OpenAPI metadata](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/include-metadata?view=aspnetcore-10.0)
- [Handle errors in ASP.NET Core APIs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling-api?view=aspnetcore-10.0)
- [Prevent CSRF](https://learn.microsoft.com/en-us/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0)
- [Enforce HTTPS](https://learn.microsoft.com/en-us/aspnet/core/security/enforcing-ssl?view=aspnetcore-10.0)
- Apple [URLSession](https://developer.apple.com/documentation/foundation/urlsession)
- Apple [NSAppTransportSecurity](https://developer.apple.com/documentation/bundleresources/information-property-list/nsapptransportsecurity)
- Apple [NSAllowsLocalNetworking](https://developer.apple.com/documentation/bundleresources/information-property-list/nsapptransportsecurity/nsallowslocalnetworking)
- Apple [Preventing Insecure Network Connections](https://developer.apple.com/documentation/security/preventing-insecure-network-connections)
- Apple [Keychain services](https://developer.apple.com/documentation/security/keychain-services)
- Apple [Adding a password to the keychain](https://developer.apple.com/documentation/security/adding-a-password-to-the-keychain)
- [RFC 7807](https://tools.ietf.org/html/rfc7807)

---

## 1. Chosen design

Native clients authenticate with ASP.NET Core Identity opaque bearer tokens, not JWTs and not the admin cookie.

- Keep cookie plus antiforgery for Interactive Server Blazor (IdentityConstants.ApplicationScheme, cookie pcc.admin, antiforgery pcc.af) as in ControlPlaneAuthExtensions.
- Add AddBearerToken(IdentityConstants.BearerScheme) for iOS. Tokens are Data Protection protected opaque tickets. BearerTokenOptions documents opaque bearer tokens. They are not JWTs. Implemented lifetimes: BearerTokenExpiration 1h, RefreshTokenExpiration 14d.
- Do not call MapIdentityApi. It maps /register, /forgotPassword, /manage. Single-user private LAN must not expose public registration. Small JSON auth using SignInManager and HttpContext.SignInAsync(IdentityConstants.BearerScheme, principal): login and refresh only. There is no native logout endpoint; opaque tokens expire or die with the security stamp.
- Version native JSON under /api/v1 with MapGroup. OpenAPI document v1 at /openapi/v1.json. OpenAPI 3.1 is the .NET 10 default.
- Surface lives in class library `PiCommandCenter.Api`. ControlPlane mounts it with `AddPiCommandCenterApi()` / `MapPiCommandCenterApi()`.
- Errors: application/problem+json via AddProblemDetails, UseExceptionHandler plus Results.Problem, UseStatusCodePages, ProducesProblem.
- HTTPS only for native API. iOS stores accessToken and refreshToken in Keychain (kSecClassInternetPassword, kSecAttrServer), never UserDefaults.

Boring fit: same IdentityUser store and DataProtection:KeysDirectory. No JWT issuer.

---

## 2. Identity bearer vs JWT vs cookie

Identity SPA docs (ASP.NET Core 10): custom token proprietary to Identity. Quote: The tokens are not standard JSON Web Tokens (JWTs). Token option is for clients that cannot use cookies. AccessTokenResponse.AccessToken is the opaque bearer token. TokenType is always Bearer. Login with useCookies false or omitted returns tokenType, accessToken, expiresIn, refreshToken. POST /refresh takes refreshToken. Lifetimes: BearerTokenOptions.BearerTokenExpiration and RefreshTokenExpiration. Implemented: 1h access, 14d refresh. Security stamp does not immediately kill access tokens; BearerTokenExpiration bounds the window.

JWT docs: AddJwtBearer validates signature, iss, aud, exp. Do not mint production JWTs from username/password; use OIDC/OAuth.

Cookie plus antiforgery: browsers auto-send cookies (CSRF). Authorization Bearer is not auto-attached. ASP.NET Core 10 CSRF middleware allows non-browser clients that omit Sec-Fetch-Site and Origin. AccountEndpoints form login redirects; unusable as JSON.

Do not reuse NodeTokenAuthenticationHandler (AuthPolicies.Node) for the phone.

Chosen: Identity opaque bearer for iOS. Rejected: JWT, cookie for native.

---

## 3. Endpoint and auth contract

Prefix: /api/v1. JSON application/json. Failures: application/problem+json (RFC 7807 type, title, status, detail, instance).

Auth anonymous custom routes (do not expose MapIdentityApi /register). Implemented native auth is login+refresh only; no logout:

- POST /api/v1/auth/login body {username, password} -> 200 AccessTokenResponse {tokenType Bearer, accessToken, expiresIn, refreshToken}
- POST /api/v1/auth/refresh body {refreshToken} -> same DTO

There is no POST /api/v1/auth/logout. Cookie SignOut does not revoke opaque access tokens; they die at expiresIn (1h) or when the refresh ticket expires (14d) / security stamp fails. The iOS client deletes Keychain items locally when the user signs out of the app.

Validate username against AdminOptions.Username like AccountEndpoints. PasswordSignInAsync with lockoutOnFailure true (MaxFailedAccessAttempts 10).

Native resources require IdentityConstants.BearerScheme (ApiAuthorizationPolicies.NativeApi on the `/api/v1` group):

- GET/POST /api/v1/projects and /api/v1/projects/{id} plus validate
- GET/POST /api/v1/requests and result, events, messages, reply, guidance
- POST /api/v1/sessions/{id}/message and cancel
- POST /api/v1/messages/{id}/acknowledge
- POST /api/v1/reservations/{leaseId}/force-release

Unversioned `/api` stays cookie fallback for Blazor. The legacy JSON group explicitly enforces antiforgery (`RequireAntiforgeryTokenAttribute` plus a filter that turns validation failure into 400). Native `/api/v1` is bearer-only and calls `DisableAntiforgery()`. Leave /account/login form and /nodeHub node token unchanged.

OpenAPI GET /openapi/v1.json. Document HTTP Bearer, not JWT. 401 WWW-Authenticate; 403 policy fail.

---

## 4. Native client: URLSession, ATS, Keychain

URLSession uses ATS for HTTP(S). ATS requires HTTPS (RFC 2818). Console: App Transport Security has blocked a cleartext HTTP resource load since it is insecure.

Minimum server: TLS 1.2+, SHA-256+, RSA 2048 or ECC 256, AES-128/256, PFS/ECDHE, name matching a trusted CA or user-installed cert. ATS does not allow loosening trust evaluation for self-signed certs.

LAN: current host default http://127.0.0.1:5000 is unusable from a phone without ATS exceptions. Fix the server (Kestrel HTTPS plus LAN hostname), not NSAllowsArbitraryLoads.

If HTTP to a raw IP is unavoidable: iOS 17+ does not allow IP connections by default; NSAllowsLocalNetworking plus NSExceptionDomains for IP/CIDR. Apple: prefer server HTTPS; exceptions reduce security; some need App Store justification.

Do not UseHttpsRedirection as the API only protection. Microsoft: API clients may ignore redirects and leak on HTTP; APIs should not listen on HTTP or close with 400. HSTS is browser-oriented; iOS will not honor it.

Keychain: store secrets, not app files. Use kSecClassInternetPassword with kSecAttrServer = API host, kSecAttrAccount = username, kSecValueData = UTF-8 token, SecItemAdd. kSecClassGenericPassword if server attributes unused. Store access and refresh as separate items. On 401 or near expiresIn, POST /api/v1/auth/refresh; local sign-out is SecItemDelete only (no server logout).

URLSession: default or ephemeral config; set Authorization Bearer; application/json; await data(for:); decode RFC 7807 on errors.

---

## 5. OpenAPI and versioning

.NET 10 package Microsoft.AspNetCore.OpenApi. AddOpenApi default document v1, spec OpenAPI 3.1. MapOpenApi serves /openapi/{documentName}.json. Microsoft.OpenApi is centrally pinned at 2.7.5 (`Directory.Packages.props`, `CentralPackageTransitivePinningEnabled`) as a transitive security pin.

builder.Services.AddOpenApi("v1"); builder.Services.AddProblemDetails(); app.MapOpenApi(); var v1 = app.MapGroup("/api/v1").RequireAuthorization(ApiAuthorizationPolicies.NativeApi).DisableAntiforgery();

Metadata: WithTags, ProducesProblem, typed Results already used in ProjectsEndpoints.

URL path /api/v1 is enough. Second document: AddOpenApi v2 plus ShouldInclude. No Asp.Versioning package for v1. Unversioned /api stays cookie-authenticated with explicit antiforgery; native must use /api/v1.

---

## 6. Host and project wiring

`PiCommandCenter.Api` (class library, `net10.0`) owns endpoint mappers and host registration. `PiCommandCenter.ControlPlane` mounts it:

1. Package Microsoft.AspNetCore.OpenApi on the Api project. Transitive Microsoft.OpenApi 2.7.5 via Directory.Packages.props.
2. After host Identity cookies: `AddPiCommandCenterApi` calls authentication.AddBearerToken(IdentityConstants.BearerScheme) with BearerTokenExpiration 1h and RefreshTokenExpiration 14d.
3. ApiAuthorizationPolicies.NativeApi: AddAuthenticationSchemes(IdentityConstants.BearerScheme) plus RequireAuthenticatedUser. Keep Admin on cookies, Node on node token, fallback cookie-only.
4. AddOpenApi v1, AddProblemDetails.
5. UseExceptionHandler Results.Problem outside Development; UseStatusCodePages Results.Problem.
6. MapGroup /api: RequireAntiforgeryTokenAttribute plus RejectInvalidAntiforgeryAsync. MapGroup /api/v1: RequireAuthorization NativeApi and DisableAntiforgery (bearer-only policy; no CSRF cookie).
7. Do not MapIdentityApi. Native JSON MapNativeAuthEndpoints: login and refresh only.
8. Kestrel HTTPS for LAN. Stop advertising HTTP 127.0.0.1:5000 as native base.
9. PersistKeysToFileSystem must stay stable; opaque tokens bind to those keys.

iOS client out of repo: Keychain plus URLSession; optional OpenAPI 3.1 codegen from /openapi/v1.json.

Tests later; skip in this research file.

---

## 7. Rejected alternatives

1. JWT / AddJwtBearer / homemade HS256. Microsoft: do not mint production JWTs from username/password; needs OIDC.
2. Full MapIdentityApi. Public /register, email confirm, password reset. Wrong for private single-user Control Plane.
3. Cookie session on iOS. CSRF, SameSite, 302 login. Unusable JSON contract.
4. Node shared token as phone auth. Fleet credential, different threat model.
5. NSAllowsArbitraryLoads YES plus HTTP. Apple last resort; App Store justification.
6. Header versioning / Asp.Versioning NuGet. Unnecessary for first native document.
7. Swashbuckle-only. .NET 10 built-in OpenAPI is the supported generator.
8. Fake native `/auth/logout`. Opaque tickets are not individually revocable; client Keychain delete is the sign-out.

---

## 8. Lifecycle and security constraints

- Access token short-lived: BearerTokenExpiration = 1h. After security-sensitive change, session lasts until access token expiry.
- Refresh token longer: RefreshTokenExpiration = 14d; Keychain only; password-equivalent.
- Password change: cookies revalidate via security stamp; bearer access tokens remain valid until expiry.
- Tokens are not individually revocable without key rotation or a denylist this stack does not ship. There is no native logout; client discard only.
- Never log tokens or passwords (AccountEndpoints never echo password).
- HTTPS or no native API. First HTTP request can still leak (Microsoft).
- Lock OpenAPI outside Development.
- Identity lockout still applies to login.

---

## 9. Repository impact (exact)

- src/PiCommandCenter.Api/PiCommandCenter.Api.csproj: class library; PackageReference Microsoft.AspNetCore.OpenApi
- Directory.Packages.props: Microsoft.AspNetCore.OpenApi 10.0.10; Microsoft.OpenApi 2.7.5 transitive pin
- src/PiCommandCenter.ControlPlane/Program.cs: AddPiCommandCenterApi, MapPiCommandCenterApi
- src/PiCommandCenter.Api/PiCommandCenterApiExtensions.cs: AddBearerToken, NativeApi policy, AddOpenApi, MapGroup /api (antiforgery) and /api/v1 (DisableAntiforgery)
- src/PiCommandCenter.Api/NativeAuthEndpoints.cs: JSON login and refresh only (no logout)
- src/PiCommandCenter.ControlPlane/Security/ControlPlaneAuthExtensions.cs: cookie Identity, Admin policy
- src/PiCommandCenter.Infrastructure/Security/AuthPolicies.cs: Admin/Node constants
- src/PiCommandCenter.Api/*Endpoints.cs: dual-map JSON under /api and /api/v1
- Host/Kestrel: HTTPS listen; do not use HTTP-only ControlPlane:BaseUrl for native
- iOS client separate: Keychain plus URLSession; consume /openapi/v1.json
- Tests later: bearer /api/v1 (not this file)

No Identity schema migration.

---

## 10. Unresolved / operator choices

1. Dev cert vs private CA vs hostname for ATS (user-trust on device).
2. Whether unversioned /api remains cookie-only long term. Do not put cookie and bearer on the same native-only routes. Implemented: unversioned /api keeps cookie fallback plus explicit antiforgery; /api/v1 is bearer-only.
3. OpenAPI YAML vs JSON. MapOpenApi yaml suffix is supported; JSON is enough.

---

## 11. Security-review alignment (2026-09-05)

Verified from ASP.NET Core 10.0 BearerTokenOptions.cs: BearerTokenExpiration defaults to TimeSpan.FromHours(1); RefreshTokenExpiration defaults to TimeSpan.FromDays(14). Expiration is stored in the protected token. Implementation sets those same values explicitly.

Do not MapIdentityApi wholesale. Distinct NativeApi opaque-bearer policy; preserve cookie default (Admin) and node-only policy. Native /api/v1 is bearer-only, JSON-only, DisableAntiforgery. Legacy /api JSON group explicitly enforces antiforgery. Keep antiforgery on cookie form routes. No native logout: client Keychain delete only; access tokens remain valid until the 1h expiry unless a denylist is added. Require trusted HTTPS/ATS and Keychain; no HTTP LAN or NSAllowsArbitraryLoads bypass.

Source: https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Security/Authentication/BearerToken/src/BearerTokenOptions.cs

---

## 12. Cookie login antiforgery gap (verified in-repo)

AccountEndpoints.cs maps POST /account/login and POST /account/logout with AllowAnonymous only. Both call Request.ReadFormAsync. There is no RequireAntiforgery metadata and no IAntiforgery.ValidateRequestAsync. The class comment and docs/security.md say antiforgery protects login/logout; UseAntiforgery middleware alone is not endpoint opt-in for these handlers.

Recommend explicit antiforgery validation on cookie form login/logout. Native JSON bearer login and refresh must not use antiforgery; there is no native logout endpoint.

---

## 13. DisableCookieRedirect for /api/v1 (ASP.NET Core 10)

Official API: Microsoft.AspNetCore.Builder.CookieRedirectEndpointConventionBuilderExtensions.DisableCookieRedirect<TBuilder>(TBuilder) where TBuilder : IEndpointConventionBuilder.

Adds IDisableCookieRedirectMetadata. When present and not overridden by AllowCookieRedirect, the cookie authentication handler prefers 401 and 403 over redirecting to login or access-denied paths.

Call DisableCookieRedirect() on the /api/v1 MapGroup (safer/explicit than relying on automatic known-API metadata). Native clients must receive 401 JSON/problem, never a 302 to /login.

Docs: https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.builder.cookieredirectendpointconventionbuilderextensions.disablecookieredirect?view=aspnetcore-10.0
