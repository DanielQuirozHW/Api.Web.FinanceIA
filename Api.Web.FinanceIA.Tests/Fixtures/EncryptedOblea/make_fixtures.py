#!/usr/bin/env python3
"""
Generates the SYNTHETIC encrypted CPCE "legalización" wrappers used by
PdfAttachmentExtractorEncryptedTests. No client data: every name, CUIT-free label
and amount below is invented.

Structure reproduced (see README.md):
  * 1-page cover, PDF 1.6, linearized, cross-reference STREAM (object streams on)
  * /AcroForm with one signature field + widget (/V = /Sig dict, zero-filled /Contents)
  * document-level /Names /EmbeddedFiles name tree, keys "Balance" and "Informe"
  * each file spec: UNC-style /F and /UF; /EF /F stream with NO /Type,
    /Subtype /PDF, /Filter /FlateDecode
  * encrypted, EMPTY user password, owner password set, P = -1596

Usage (pikepdf pinned in README.md):
  python make_fixtures.py <output-dir>
"""
import sys
import zlib
from pathlib import Path

import pikepdf
from pikepdf import Array, Dictionary, Name, Pdf, Stream, String

OWNER_PASSWORD = "fake-owner"

# P = -1596 (0xFFFFF9C4): print (bit 3), fill forms (bit 9), high-res print (bit 12).
PERMISSIONS = pikepdf.Permissions(
    accessibility=False,
    extract=False,
    modify_annotation=False,
    modify_assembly=False,
    modify_form=True,
    modify_other=False,
    print_lowres=True,
    print_highres=True,
)

VARIANTS = {
    "cpce_aes128.pdf": dict(R=4, aes=True),    # V4 / R4 / AESV2, 128-bit (the production case)
    "cpce_aes256.pdf": dict(R=6, aes=True),    # V5 / R6 / AESV3, 256-bit
    "cpce_rc4128.pdf": dict(R=3, aes=False, metadata=False), # V2 / R3 / RC4, 128-bit
}


def _text_page(pdf: Pdf, lines: list[str]) -> pikepdf.Page:
    font = pdf.make_indirect(Dictionary(Type=Name.Font, Subtype=Name.Type1, BaseFont=Name.Helvetica))
    ops = ["BT", "/F1 9 Tf", "40 800 Td", "11 TL"]
    for line in lines:
        safe = line.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")
        ops.append(f"({safe}) Tj T*")
    ops.append("ET")
    content = Stream(pdf, "\n".join(ops).encode("latin-1"))
    page = Dictionary(
        Type=Name.Page,
        MediaBox=Array([0, 0, 595, 842]),
        Resources=Dictionary(Font=Dictionary(F1=font)),
        Contents=pdf.make_indirect(content),
    )
    return pdf.pages.append(pikepdf.Page(page)) or pdf.pages[-1]


def make_inner(title: str, pages: int, rows_per_page: int) -> bytes:
    """An uncompressed, unencrypted inner PDF with invented figures, well above 10 KB."""
    pdf = Pdf.new()
    pdf.docinfo["/Title"] = title
    n = 0
    for p in range(pages):
        lines = [f"{title} - EMPRESA FICTICIA S.A. - hoja {p + 1}"]
        for _ in range(rows_per_page):
            n += 1
            lines.append(f"Cuenta ficticia {n:04d} ............ $ {n * 1_234_567 % 98_765_432:>12,}".replace(",", "."))
        if p == 0:
            lines += ["Total del Activo  $ 1.000.000.000", "Total del Pasivo  $ 400.000.000",
                      "Patrimonio Neto  $ 600.000.000", "Resultado del ejercicio  $ 50.000.000"]
        _text_page(pdf, lines)
    from io import BytesIO
    buf = BytesIO()
    pdf.save(buf, compress_streams=False, deterministic_id=True,
             object_stream_mode=pikepdf.ObjectStreamMode.disable)
    return buf.getvalue()


def _filespec(pdf: Pdf, data: bytes, unc: str) -> pikepdf.Object:
    ef = Stream(pdf, zlib.compress(data, 9))
    ef.Filter = Name.FlateDecode          # data above is already Flate-encoded
    ef.Subtype = Name("/PDF")             # non-standard subtype, no /Type — as in the real wrapper
    ef.Params = Dictionary(Size=len(data))
    ef_ref = pdf.make_indirect(ef)
    return pdf.make_indirect(Dictionary(
        Type=Name.Filespec,
        F=String(unc),
        UF=String(unc),
        EF=Dictionary(F=ef_ref),
    ))


def make_outer(balance: bytes, informe: bytes, encryption) -> Pdf:
    pdf = Pdf.new()
    _text_page(pdf, ["CONSEJO PROFESIONAL FICTICIO", "Legalizacion de tramites con firma digital",
                     "Activo 1.000.000.000  Pasivo 400.000.000  P. Neto 600.000.000",
                     "Resultado 50.000.000  Var. E.F.E. 10.000.000"])
    page = pdf.pages[0]

    sig = pdf.make_indirect(Dictionary(
        Type=Name.Sig,
        Filter=Name("/Adobe.PPKLite"),
        SubFilter=Name("/adbe.pkcs7.detached"),
        ByteRange=Array([0, 0, 0, 0]),
        Contents=String(bytes(2048)),
    ))
    widget = pdf.make_indirect(Dictionary(
        Type=Name.Annot, Subtype=Name.Widget, FT=Name.Sig, T=String("FirmaFicticia"),
        Rect=Array([0, 0, 0, 0]), F=132, P=page.obj, V=sig,
    ))
    page.obj.Annots = Array([widget])
    pdf.Root.AcroForm = Dictionary(Fields=Array([widget]), SigFlags=3)

    pdf.Root.Names = Dictionary(EmbeddedFiles=Dictionary(Names=Array([
        String("Balance"), _filespec(pdf, balance, r"\\srv-ficticio\legal\2026\0000000_balance.pdf"),
        String("Informe"), _filespec(pdf, informe, r"\\srv-ficticio\legal\2026\0000000_informe.pdf"),
    ])))
    return pdf


def main(out_dir: str) -> None:
    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)

    # The report is kept within 20 % of the balance's size, as a real CPCE pair can be: the size
    # rule then gives up, so ONLY the name-tree keys can pick "Balance" — which is exactly what
    # an undecrypted key loses.
    balance = make_inner("ESTADOS CONTABLES FICTICIOS", pages=10, rows_per_page=70)
    informe = make_inner("INFORME DEL AUDITOR FICTICIO", pages=9, rows_per_page=70)
    (out / "balance.expected.pdf").write_bytes(balance)
    (out / "informe.expected.pdf").write_bytes(informe)

    # deterministic_id is refused by qpdf for encrypted output, and AES IVs are random anyway,
    # so the encrypted fixtures are NOT bit-reproducible — the committed files are the fixtures.
    common = dict(object_stream_mode=pikepdf.ObjectStreamMode.generate, linearize=True,
                  min_version="1.6")

    for file_name, enc in VARIANTS.items():
        pdf = make_outer(balance, informe, enc)
        pdf.save(out / file_name, encryption=pikepdf.Encryption(
            owner=OWNER_PASSWORD, user="", allow=PERMISSIONS, **enc), **common)

    make_outer(balance, informe, None).save(out / "cpce_plain.pdf", **common)

    # qpdf forces the accessibility bit on for R >= 4, so the two AES files above carry
    # P = -1084, not the production -1596. pypdf honours P exactly but writes a classic xref
    # TABLE (no xref stream, no linearization), so this variant covers the exact P while the
    # qpdf one covers the exact file layout.
    from pypdf import PdfReader, PdfWriter
    writer = PdfWriter(clone_from=PdfReader(out / "cpce_plain.pdf"))
    writer.encrypt(user_password="", owner_password=OWNER_PASSWORD,
                   permissions_flag=-1596, algorithm="AES-128")
    writer.pdf_header = b"%PDF-1.6"
    with open(out / "cpce_aes128_xreftable.pdf", "wb") as fh:
        writer.write(fh)
    print(f"balance {len(balance)} bytes, informe {len(informe)} bytes")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else str(Path(__file__).parent))
