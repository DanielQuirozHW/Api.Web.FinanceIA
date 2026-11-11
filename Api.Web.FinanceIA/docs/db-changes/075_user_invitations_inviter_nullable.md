# 075 — UserInvitations: inviter nullable (first-admin bootstrap)

## What changed and why

A new client gets its own empty Financia database. The only path that creates a `Users` row is
`POST /api/bff/auth/register-invited`, and it needs an invitation. Only an Admin can create one
(`POST /api/bff/settings/users/invite`). With zero users there is no Admin. The first invitation
could not even be inserted by hand, because `[UserInvitations].[InvitedByUserId]` was NOT NULL with a
foreign key to `[Users].[UserId]`.

| Change | Where |
|---|---|
| The inviter may be NULL | `[dbo].[UserInvitations].[InvitedByUserId]` UNIQUEIDENTIFIER **NULL** (was NOT NULL). Type and FK unchanged. |

NULL means "issued by the first-admin bootstrap", `POST /api/bff/setup/first-admin`. That endpoint is
off unless the `Setup:BootstrapKey` App Setting is set. Invitations from the normal invite still carry
a real inviter.

## What the API does with a NULL inviter

- `GET /api/bff/settings/invitations` returns `invitedByUserId: null` and
  `invitedByName: "Configuración inicial"`. The JSON keeps the same shape. The Portal never reads
  `invitedByUserId` at runtime, and it renders `invitedByName` as is, so no Portal change is needed.
- Resend works. The email uses the inviter name "Un administrador", the same fallback as an
  unresolvable inviter.
- Revoke works. A bootstrap re-call also revokes its own previous live invitation for the same
  email, with `RevokedByUserId` NULL.
- Registration ignores the inviter entirely. The invitee becomes an Admin because the invitation's
  `Role` is `Admin`.

## Deploy order

1. Run `075` in **every** Financia database. Confirm `is_nullable = 1` (query at the end of the
   script).
2. Publish the API.
3. Follow `docs/guides/first-admin.md` per client: set the key, make the call, then remove the key.

Publishing before step 1 breaks nothing that works today, because EF selects the same columns. Only
the bootstrap insert fails, with HTTP 500, and the API logs an Error naming this script.
`--verify-schema` cannot detect the miss, because it checks table and column names, not nullability.

No App Service restart is needed for the DDL itself.

## Rollback

Only possible while no row has a NULL inviter:

```sql
IF NOT EXISTS (SELECT 1 FROM [dbo].[UserInvitations] WHERE [InvitedByUserId] IS NULL)
    ALTER TABLE [dbo].[UserInvitations] ALTER COLUMN [InvitedByUserId] UNIQUEIDENTIFIER NOT NULL;
```

Once a bootstrap invitation exists, rolling back means deleting it or pointing it at a real user
first. Rolling the code back alone is safe in either direction while no NULL rows exist.
