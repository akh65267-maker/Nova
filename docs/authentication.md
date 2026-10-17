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

Registration requires a password of at least 8 characters; there are no other complexity rules.

**Brute-force protection on `POST /users/login`** has two layers, and they guard different things:
- **Account lockout (per account).** Five wrong passwords within 15 minutes lock the account for 15 minutes (`Login:Lockout`: `MaxFailures`, `Window`, `Duration`). While locked, even the correct password is refused, and the real password hash is never consulted. An address that does not exist, a wrong password and a locked account all give the same `401` with the same message and cost the same time (a dummy hash is verified), so neither the answer nor the timing says which accounts exist. A correct password clears the count. The count is one atomic SQL statement, so parallel guesses cannot slip past it. The lock is temporary on purpose: a permanent one would let anybody lock anybody else out for good. The cost is that anyone who knows an address can lock that account for 15 minutes by guessing wrongly five times; that is the trade for stopping unlimited guessing, and the admin account is no exception.
- **Rate limit (per client address).** `Login:IpPermitPerMinute` (default 60) per address, a guard against one address burning CPU on password hashing and against spraying many accounts. It only works per client if UserService can see the client's address: behind the gateway every request otherwise comes from the gateway, which made the old limit (10 a minute) **one bucket for everybody**, so a few failed logins a minute from anyone blocked every login (reproduced on the local stack on 2026-10-17: 12 failed logins for addresses that do not exist, then the real admin's correct password got `429`). UserService now believes the gateway's `X-Forwarded-For`, only from the networks in `ForwardedHeaders:KnownNetworks` and only its last entry (`TrustedForwardedHeaders`); with nothing configured the header is ignored. Docker Compose trusts `172.16.0.0/12`. **Not done:** the gateway itself still sees only whatever connects to it, so on the local stack every outside client reaches it from the Docker network's address and is still one bucket (clients inside the Docker network are told apart). On Azure the gateway needs the same setting for the Container Apps ingress's network before the per-client limit means anything there; not tested, because nothing is deployed.

## Secrets handling

- Runtime secrets come from configuration: `Jwt:Key`, `RabbitMq:*`, `Admin:*`, connection strings. In compose they are all `${…}` env-var references sourced from `deploy/.env`, which is git-ignored (`deploy/.env.example` is the committed template).
- The placeholder development `Jwt:Key` (and UserService's placeholder admin credentials) live in each service's `appsettings.Development.json`, not the base file, so outside Development a service has no key unless one is supplied and refuses to start (`Jwt:Key configuration is missing`). Separately, two tracked Development files contain the real local database and broker passwords; see [TODO.md](TODO.md).
- No secret values are reproduced in this documentation.
