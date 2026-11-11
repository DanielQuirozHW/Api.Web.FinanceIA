# Encrypted CPCE "legalización" wrappers — synthetic fixtures

**No client data.** Every company name, path and amount in these files is invented by
`make_fixtures.py`. Never add a real wrapper here; real files go through the local, env-gated
harness only, from a folder outside the repo.

## What they reproduce
A CPCE Córdoba "Legalización de trámites con firma digital" PDF:
- a 1-page cover, PDF 1.6, linearized, cross-reference **stream**, with an `/AcroForm` signature
  field and widget (`/V` = `/Sig` dict with a zero-filled `/Contents`);
- a document-level `/Names /EmbeddedFiles` name tree, held **directly** in the catalog, with
  keys `Balance` and `Informe`;
- each file spec has UNC-style `/F` and `/UF` (`\\srv-ficticio\legal\2026\0000000_balance.pdf`);
- each `/EF /F` stream has no `/Type`, has `/Subtype /PDF` and `/Filter /FlateDecode`;
- an empty user password and an owner password (`fake-owner`).

The report is within 20 % of the balance's size, so the extractor's size rule cannot decide. Only
the name-tree keys can pick `Balance`.

| File | Encryption | Notes |
|---|---|---|
| `cpce_plain.pdf` | none | control, same structure |
| `cpce_aes128.pdf` | V4 / R4 / AESV2, 128-bit | the production case; P = -1084 (qpdf forces the accessibility bit on for R ≥ 4) |
| `cpce_aes128_xreftable.pdf` | V4 / R4 / AESV2, 128-bit | exact P bits of -1596 (written unsigned, 4294965700) but a classic xref TABLE, not linearized — written by pypdf |
| `cpce_aes256.pdf` | V5 / R6 / AESV3, 256-bit | P = -1084 |
| `cpce_rc4128.pdf` | V2 / R3 / RC4, 128-bit | P = -1596 |
| `balance.expected.pdf` / `informe.expected.pdf` | — | the exact inner bytes embedded under each key |

## Regenerating
```bash
python3 -m venv .venv
.venv/bin/pip install pikepdf==10.17.0 pypdf==6.17.0 cryptography==46.0.3
.venv/bin/python -I make_fixtures.py .
```
The encrypted outputs are **not bit-reproducible**: AES IVs are random, and qpdf refuses a
deterministic /ID for encrypted output. The committed files are the fixtures. Regenerate only to
change the structure, and re-run `PdfAttachmentExtractorEncryptedTests` afterwards.

## Build action
The test project copies `*.pdf` from here next to the test assembly (`None Update ...
CopyToOutputDirectory`). The `.py` and `.md` stay Build Action None and are never compiled
(MISTAKES [001]).
