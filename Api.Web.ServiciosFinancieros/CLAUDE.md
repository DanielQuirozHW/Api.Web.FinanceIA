# Api.Web.ServiciosFinancieros — legacy, frozen, not deployed

This project is **not** the FinancIA business API and is **not** what gets published.
`.github/workflows/deploy.yml` publishes only `Api.Web.FinanceIA/Api.Web.FinanceIA.csproj`.

It remains in `Api.Web.ServiciosFinancieros.slnx` and still compiles, so CI builds it — but every
file here has been frozen since 2026-06-28. Its `Data/AppDbContext.cs` is a separate, unrelated
context; do not confuse it with `Api.Web.FinanceIA/Data/AppDbContext.cs`.

**The canonical guidance is the CLAUDE.md at the repo root.** This file used to be a near-identical
copy of it and had drifted into contradicting it — notably on where business data lives (Azure SQL,
not Dynamics 365) and on the schema (23 tables, not two).

Do not treat anything under this directory, including `docs/architecture/README.md`, as a current
description of the system.
