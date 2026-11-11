// Project : Api.Web.FinanceIA
// Created : 2026-09-28

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// Everything that defines the extraction of ONE legajo document type (M10 Phase 2b): the versions
/// stored on every run, the prompt, the tool, the schema tree and the required fields. Instances live
/// in <see cref="LegajoExtractionPrompts"/>; a fingerprint test pins each prompt + schema to its
/// <see cref="PromptVersion"/>.
/// </summary>
public sealed class LegajoExtractionSchema
{
    private string? _toolSchemaJson;

    /// <summary>The Documents.DocumentType this schema extracts.</summary>
    public required string DocumentType { get; init; }

    /// <summary>Version of the output shape, stored on every run.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>Version of the prompt + schema text, stored on every run and fingerprinted.</summary>
    public required string PromptVersion { get; init; }

    /// <summary>The one tool the request forces.</summary>
    public required string ToolName { get; init; }

    /// <summary>The tool description sent beside the schema.</summary>
    public required string ToolDescription { get; init; }

    /// <summary>The system prompt.</summary>
    public required string SystemPrompt { get; init; }

    /// <summary>The output shape.</summary>
    public required LegajoSchemaObject Root { get; init; }

    /// <summary>JSON Pointers of the <see cref="LegajoSchemaField"/> leaves that must hold a value.</summary>
    public required IReadOnlyList<string> RequiredFieldPaths { get; init; }

    /// <summary>
    /// Array pointers of which at least ONE must be non-empty (BP: items or rubroTotals). Reported
    /// missing under the first pointer.
    /// </summary>
    public IReadOnlyList<string> RequiredAnyArrayPaths { get; init; } = [];

    /// <summary>
    /// Requirements a path list cannot express (Phase 3a), each reported missing under its own path
    /// when unmet. Empty for MB.
    /// </summary>
    public IReadOnlyList<LegajoRequirement> Requirements { get; init; } = [];

    /// <summary>
    /// The output ceiling of this type's extraction call, overriding <c>Legajo:ExtractionMaxTokens</c> when
    /// set (Phase 3a: a 12-month IVA or IIBB PDF has far more rows than a BP). Null: the option. A request
    /// parameter, not prompt text — outside the fingerprint.
    /// </summary>
    public int? MaxTokens { get; init; }

    /// <summary>The tool's input_schema, generated from <see cref="Root"/> (deterministic, so a race only computes it twice).</summary>
    public string ToolSchemaJson => _toolSchemaJson ??= LegajoSchemaJson.Generate(Root);
}
