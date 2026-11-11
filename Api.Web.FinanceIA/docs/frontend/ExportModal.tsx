// Frontend file — copy to the Next.js frontend repo.
// Path suggestion: src/components/analysis/ExportModal.tsx
//
// Dependencies expected in the frontend repo:
//   - useToast hook (toast notifications)
//   - logger utility (structured logger, no console.log)
//   - analysisService.exportPdf(resultId, sections) → Blob
//   - Design tokens: var(--color-accent), var(--color-surface), etc.
//
// FIX 1: Modal is centered via fixed inset-0 flex overlay.
// STEP 2A: 2-step wizard (format selector → confirmation).
// STEP 2B: Calls BFF GET /api/bff/analyses/{id}/export/pdf and downloads the Blob.

"use client";

import { useEffect, useRef, useState } from "react";

// ── Types ─────────────────────────────────────────────────────────────────────

type ExportFormat = "pdf" | "excel" | "csv";

interface Section {
  key: "balance" | "ratios" | "risk" | "footer" | "charts";
  label: string;
}

interface ExportModalProps {
  analysisResultId: string;
  fiscalYear: number;
  onClose: () => void;
}

// ── Constants ─────────────────────────────────────────────────────────────────

const SECTIONS: Section[] = [
  { key: "balance", label: "Estado patrimonial" },
  { key: "ratios",  label: "Ratios financieros" },
  { key: "risk",    label: "Análisis de riesgo" },
  { key: "charts",  label: "Gráficos" },
  { key: "footer",  label: "Notas al pie" },
];

const SHORT_ID_LENGTH = 8;

// ── Component ─────────────────────────────────────────────────────────────────

export function ExportModal({ analysisResultId, fiscalYear, onClose }: ExportModalProps) {
  // ── State ──────────────────────────────────────────────────────────────────

  const [step, setStep]                 = useState<1 | 2>(1);
  const [format, setFormat]             = useState<ExportFormat>("pdf");
  const [selectedSections, setSelected] = useState<Set<Section["key"]>>(
    new Set(SECTIONS.map((s) => s.key))
  );
  const [isLoading, setIsLoading]       = useState(false);

  // ── Toast & logger injected via context — replace with your hook ───────────
  // const { toast } = useToast();
  // const logger = useLogger();
  // For illustration, placeholders are used below; replace with real hooks.

  // ── ESC key closes modal ───────────────────────────────────────────────────

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [onClose]);

  // ── Focus trap: keep focus inside modal ───────────────────────────────────

  const modalRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    modalRef.current?.focus();
  }, []);

  // ── Derived values ─────────────────────────────────────────────────────────

  const canContinue = format === "pdf" ? selectedSections.size > 0 : true;
  const shortId     = analysisResultId.slice(0, SHORT_ID_LENGTH).toUpperCase();

  // ── Handlers ───────────────────────────────────────────────────────────────

  function toggleSection(key: Section["key"]) {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }

  async function handleDownload() {
    setIsLoading(true);

    const sectionsParam = format === "pdf"
      ? [...selectedSections].join(",")
      : "";

    const url = `/api/bff/analyses/${analysisResultId}/export/pdf` +
      (sectionsParam ? `?sections=${encodeURIComponent(sectionsParam)}` : "");

    try {
      const response = await fetch(url, {
        method: "GET",
        credentials: "include", // send bff_session cookie
      });

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      const blob     = await response.blob();
      const objectUrl = URL.createObjectURL(blob);
      const anchor   = document.createElement("a");
      anchor.href     = objectUrl;
      anchor.download = `analisis-${shortId}-${fiscalYear}.pdf`;
      anchor.click();
      URL.revokeObjectURL(objectUrl);

      onClose();
      // toast({ variant: "success", title: "Informe exportado correctamente" });
    } catch (err) {
      // logger.error("PDF export failed", { analysisResultId, err });
      // toast({ variant: "error", title: "Error al generar el PDF. Intente nuevamente." });
    } finally {
      setIsLoading(false);
    }
  }

  // ── Render ─────────────────────────────────────────────────────────────────

  return (
    <>
      {/* Backdrop — click to close */}
      <div
        aria-hidden="true"
        onClick={onClose}
        style={{
          position: "fixed",
          inset: 0,
          background: "rgba(0,0,0,0.5)",
          zIndex: 1000,
        }}
      />

      {/* Modal panel — centered via flex on the backdrop */}
      <div
        role="dialog"
        aria-modal="true"
        aria-labelledby="export-modal-title"
        ref={modalRef}
        tabIndex={-1}
        onClick={(e) => e.stopPropagation()}
        style={{
          position: "fixed",
          inset: 0,
          display: "flex",
          alignItems: "center",
          justifyContent: "center",
          zIndex: 1001,
          pointerEvents: "none",
        }}
      >
        <div
          style={{
            pointerEvents: "auto",
            width: "90vw",
            maxWidth: 480,
            maxHeight: "85vh",
            display: "flex",
            flexDirection: "column",
            borderRadius: 12,
            background: "var(--color-surface, #fff)",
            boxShadow: "0 20px 60px rgba(0,0,0,0.25)",
            overflow: "hidden",
          }}
        >
          {/* ── Header ───────────────────────────────────────────────────── */}
          <ModalHeader step={step} onClose={onClose} />

          {/* ── Scrollable content ────────────────────────────────────────── */}
          <div style={{ overflowY: "auto", flex: 1, padding: "20px 24px" }}>
            {step === 1 ? (
              <StepOne
                format={format}
                onFormatChange={setFormat}
                selectedSections={selectedSections}
                onToggleSection={toggleSection}
              />
            ) : (
              <StepTwo
                format={format}
                selectedSections={selectedSections}
                shortId={shortId}
                fiscalYear={fiscalYear}
              />
            )}
          </div>

          {/* ── Footer buttons ────────────────────────────────────────────── */}
          <ModalFooter
            step={step}
            canContinue={canContinue}
            isLoading={isLoading}
            onBack={() => setStep(1)}
            onContinue={() => setStep(2)}
            onCancel={onClose}
            onDownload={handleDownload}
          />
        </div>
      </div>
    </>
  );
}

// ── Sub-components ────────────────────────────────────────────────────────────

function ModalHeader({
  step,
  onClose,
}: {
  step: 1 | 2;
  onClose: () => void;
}) {
  return (
    <div
      style={{
        display: "flex",
        alignItems: "center",
        justifyContent: "space-between",
        padding: "16px 24px",
        borderBottom: "1px solid var(--color-border, #e2e8f0)",
      }}
    >
      <div>
        <h2
          id="export-modal-title"
          style={{ margin: 0, fontSize: 16, fontWeight: 600 }}
        >
          Exportar informe
        </h2>
        {/* Step dots */}
        <div style={{ display: "flex", gap: 6, marginTop: 6 }}>
          {([1, 2] as const).map((s) => (
            <div
              key={s}
              style={{
                width: 8,
                height: 8,
                borderRadius: "50%",
                background:
                  s === step
                    ? "var(--color-accent, #3b82f6)"
                    : "var(--color-border, #e2e8f0)",
                transition: "background 0.2s",
              }}
            />
          ))}
        </div>
      </div>
      <button
        onClick={onClose}
        aria-label="Cerrar"
        style={{
          background: "none",
          border: "none",
          cursor: "pointer",
          fontSize: 20,
          color: "var(--color-muted, #64748b)",
          padding: 4,
        }}
      >
        ✕
      </button>
    </div>
  );
}

function StepOne({
  format,
  onFormatChange,
  selectedSections,
  onToggleSection,
}: {
  format: ExportFormat;
  onFormatChange: (f: ExportFormat) => void;
  selectedSections: Set<Section["key"]>;
  onToggleSection: (key: Section["key"]) => void;
}) {
  return (
    <>
      {/* Format selector */}
      <p style={{ margin: "0 0 12px", fontSize: 13, fontWeight: 600, color: "var(--color-muted, #64748b)" }}>
        Formato
      </p>
      <div style={{ display: "flex", gap: 10, marginBottom: 20 }}>
        {(["pdf", "excel", "csv"] as ExportFormat[]).map((f) => {
          const isDisabled = f !== "pdf";
          const isSelected = format === f;
          return (
            <button
              key={f}
              onClick={() => !isDisabled && onFormatChange(f)}
              disabled={isDisabled}
              aria-pressed={isSelected}
              style={{
                flex: 1,
                padding: "12px 8px",
                borderRadius: 8,
                border: isSelected
                  ? "2px solid var(--color-accent, #3b82f6)"
                  : "2px solid var(--color-border, #e2e8f0)",
                background: isSelected
                  ? "var(--color-accent-tint, #eff6ff)"
                  : "var(--color-surface, #fff)",
                cursor: isDisabled ? "not-allowed" : "pointer",
                opacity: isDisabled ? 0.45 : 1,
                position: "relative",
                transition: "border-color 0.15s, background 0.15s",
                textAlign: "center",
              }}
            >
              <div style={{ fontSize: 13, fontWeight: 600, textTransform: "uppercase" }}>
                {f}
              </div>
              {isDisabled && (
                <span
                  style={{
                    position: "absolute",
                    top: -8,
                    right: -8,
                    fontSize: 9,
                    fontWeight: 700,
                    background: "var(--color-muted, #64748b)",
                    color: "#fff",
                    borderRadius: 4,
                    padding: "2px 5px",
                    textTransform: "uppercase",
                  }}
                >
                  Próximamente
                </span>
              )}
            </button>
          );
        })}
      </div>

      {/* Section checkboxes (PDF only) */}
      {format === "pdf" && (
        <>
          <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between", marginBottom: 10 }}>
            <p style={{ margin: 0, fontSize: 13, fontWeight: 600, color: "var(--color-muted, #64748b)" }}>
              Secciones
            </p>
            <span
              style={{
                fontSize: 11,
                fontWeight: 700,
                background: "var(--color-accent, #3b82f6)",
                color: "#fff",
                borderRadius: 12,
                padding: "2px 8px",
              }}
            >
              {selectedSections.size} seleccionadas
            </span>
          </div>
          <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
            {SECTIONS.map((section) => {
              const checked = selectedSections.has(section.key);
              return (
                <label
                  key={section.key}
                  style={{
                    display: "flex",
                    alignItems: "center",
                    gap: 10,
                    padding: "10px 12px",
                    borderRadius: 8,
                    border: "1px solid var(--color-border, #e2e8f0)",
                    cursor: "pointer",
                    background: checked ? "var(--color-accent-tint, #eff6ff)" : "transparent",
                    transition: "background 0.15s",
                  }}
                >
                  <input
                    type="checkbox"
                    checked={checked}
                    onChange={() => onToggleSection(section.key)}
                    style={{ accentColor: "var(--color-accent, #3b82f6)", width: 16, height: 16 }}
                  />
                  <span style={{ fontSize: 14 }}>{section.label}</span>
                </label>
              );
            })}
          </div>
        </>
      )}
    </>
  );
}

function StepTwo({
  format,
  selectedSections,
  shortId,
  fiscalYear,
}: {
  format: ExportFormat;
  selectedSections: Set<Section["key"]>;
  shortId: string;
  fiscalYear: number;
}) {
  const sectionLabels = SECTIONS
    .filter((s) => selectedSections.has(s.key))
    .map((s) => s.label);

  return (
    <>
      <p style={{ margin: "0 0 16px", fontSize: 13, color: "var(--color-muted, #64748b)" }}>
        Revisá los detalles antes de descargar.
      </p>

      <div
        style={{
          background: "var(--color-surface-alt, #f8fafc)",
          borderRadius: 8,
          padding: 16,
          marginBottom: 16,
          border: "1px solid var(--color-border, #e2e8f0)",
        }}
      >
        <Row label="Formato"    value={format.toUpperCase()} />
        <Row label="Análisis"   value={shortId} />
        <Row label="Ejercicio"  value={String(fiscalYear)} />
        <Row label="Tamaño est." value="~2–4 páginas" />
      </div>

      <p style={{ margin: "0 0 8px", fontSize: 13, fontWeight: 600 }}>Secciones incluidas</p>
      <ul style={{ margin: 0, paddingLeft: 20 }}>
        {sectionLabels.map((label) => (
          <li key={label} style={{ fontSize: 13, marginBottom: 4 }}>
            {label}
          </li>
        ))}
      </ul>
    </>
  );
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <div style={{ display: "flex", justifyContent: "space-between", marginBottom: 8 }}>
      <span style={{ fontSize: 13, color: "var(--color-muted, #64748b)" }}>{label}</span>
      <span style={{ fontSize: 13, fontWeight: 600 }}>{value}</span>
    </div>
  );
}

function ModalFooter({
  step,
  canContinue,
  isLoading,
  onBack,
  onContinue,
  onCancel,
  onDownload,
}: {
  step: 1 | 2;
  canContinue: boolean;
  isLoading: boolean;
  onBack: () => void;
  onContinue: () => void;
  onCancel: () => void;
  onDownload: () => Promise<void>;
}) {
  const ghostStyle: React.CSSProperties = {
    background: "none",
    border: "1px solid var(--color-border, #e2e8f0)",
    borderRadius: 8,
    padding: "10px 20px",
    fontSize: 14,
    cursor: "pointer",
    color: "var(--color-muted, #64748b)",
  };

  const primaryStyle: React.CSSProperties = {
    background: "var(--color-accent, #3b82f6)",
    border: "none",
    borderRadius: 8,
    padding: "10px 20px",
    fontSize: 14,
    fontWeight: 600,
    cursor: canContinue && !isLoading ? "pointer" : "not-allowed",
    color: "#fff",
    opacity: canContinue && !isLoading ? 1 : 0.5,
    display: "flex",
    alignItems: "center",
    gap: 8,
  };

  return (
    <div
      style={{
        display: "flex",
        justifyContent: "space-between",
        padding: "16px 24px",
        borderTop: "1px solid var(--color-border, #e2e8f0)",
        gap: 8,
      }}
    >
      {step === 1 ? (
        <>
          <button style={ghostStyle} onClick={onCancel}>
            Cancelar
          </button>
          <button
            style={primaryStyle}
            onClick={onContinue}
            disabled={!canContinue}
          >
            Continuar →
          </button>
        </>
      ) : (
        <>
          <button style={ghostStyle} onClick={onBack} disabled={isLoading}>
            ← Volver
          </button>
          <button
            style={primaryStyle}
            onClick={onDownload}
            disabled={isLoading}
          >
            {isLoading ? (
              <>
                <Spinner /> Generando...
              </>
            ) : (
              <>
                ⬇ Descargar PDF
              </>
            )}
          </button>
        </>
      )}
    </div>
  );
}

function Spinner() {
  return (
    <span
      style={{
        display: "inline-block",
        width: 14,
        height: 14,
        border: "2px solid rgba(255,255,255,0.3)",
        borderTopColor: "#fff",
        borderRadius: "50%",
        animation: "spin 0.7s linear infinite",
      }}
    />
  );
}
