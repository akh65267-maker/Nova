# Authentication & authorization

Symmetric-key JWT bearer tokens. UserService is the only issuer; every other service validates independently with the same shared secret. The gateway does not participate.

## Issuance

`UserService.Infrastructure.Security.JwtTokenGenerator` on successful `POST /users/login`:

| Property | Value |
|---|---|
| Algorithm | HMAC-SHA256, key = `Jwt:Key` (UTF-8 bytes of the configured string) |
| Issuer / Audience | `Jwt:Issuer` / `Jwt:Audience`, both defaulting to `TaskManager` |
| Lifetime | **1 hour** (`UtcNow.AddHours(1)`), returned to the client as `ExpiresAtUtc` |
| Claims | `sub` = user id, `email`, `jti` (fresh GUID), and `ClaimTypes.Role` = `Customer` \| `Admin` |

There is **no refresh token, no revocation and no `jti` tracking** — the `jti` claim is generated but never stored or checked, so a token is valid until it expires.

## Validation

Identical block in User, Catalog, Basket, Inventory, Order (and TaskService): `AddJwtBearer` with `ValidateIssuer`, `ValidateAudience`, `ValidateIssuerSigningKey`, `ValidateLifetime` all on, and — importantly — **`MapInboundClaims = false`**, so claims keep their raw JWT names. That is why user id is read as `sub`:

```csharp
user.FindFirstValue(JwtRegisteredClaimNames.Sub)  // throws if absent
```

`ClaimsPrincipalExtensions.GetUserId()` exists as a **separate copy** in BasketService, OrderService and TaskService. Catalog and Inventory have no per-user endpoints and no copy.

Each service reads `Jwt:Key` at startup and throws `InvalidOperationException` if it is missing — a misconfigured service fails fast rather than accepting unsigned traffic.

## Authorization

- **Ownership** is enforced in application code, not by policy. Basket keys everything by the caller's `sub`; OrderService filters `GET /orders` by user id and returns `null`/`404` for another user's order.
- **Roles** use inline policies only — `RequireAuthorization(policy => policy.RequireRole("Admin"))` on `POST /products`, `POST /inventory` and `POST /inventory/{productId}/restock`. There are no named policies, no `AddAuthorization(options => …)` configuration, and no claims transformation.
- The role string compared at the resource (`"Admin"`) and the enum written into the token (`UserRole.Admin.ToString()`) are coupled by convention only.
- `GET /inventory` and `GET /inventory/{productId}` have **no** `RequireAuthorization` — stock levels are public. `GET /products` is anonymous by design.
- `GET /users` (every user's id, email, display name and role) is **admin-only**. `GET /users/{id}` answers for your own id or for an admin; anyone else gets a 404, the same as for an id that does not exist (`UserAccess.CanView`).

## Admin accounts

The only path to an `Admin` user is the startup seeder in `UserService/Program.cs`, which creates one from `Admin:Email` / `Admin:Password` / `Admin:DisplayName` when that email doesn't already exist. `POST /users` always creates a `Customer` (the `role` parameter defaults on the domain constructor and is not bindable from the request). There is no promote, demote, invite or admin-management endpoint.

## Password storage

`Pbkdf2PasswordHasher`: PBKDF2-HMAC-SHA256, 100,000 iterations, 16-byte random salt (`RandomNumberGenerator`), 32-byte output, stored as `base64(salt).base64(hash)`. Verification re-derives with the stored salt and compares via `CryptographicOperations.FixedTimeEquals`. There is no per-hash algorithm/iteration marker, so changing the parameters would invalidate existing hashes.

Registration requires a password of at least 8 characters; there are no other complexity rules, **no login rate limiting, and no account lockout**.

## Secrets handling

- Runtime secrets come from configuration: `Jwt:Key`, `RabbitMq:*`, `Admin:*`, connection strings. In compose they are all `${…}` env-var references sourced from `deploy/.env`, which is git-ignored (`deploy/.env.example` is the committed template).
- The placeholder development `Jwt:Key` (and UserService's placeholder admin credentials) live in each service's `appsettings.Development.json`, not the base file, so outside Development a service has no key unless one is supplied and refuses to start (`Jwt:Key configuration is missing`). Separately, two tracked Development files contain the real local database and broker passwords; see [TODO.md](TODO.md).
- No secret values are reproduced in this documentation.
