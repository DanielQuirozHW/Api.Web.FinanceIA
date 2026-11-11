// Project : Api.Web.FinanceIA
// Created : 2026-11-02

using System.Text.Json;
using System.Text.Json.Nodes;
using Api.Web.FinanceIA.Clases.Exceptions;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.BalanceCorrections;

/// <summary>
/// Validates a balance correction, in the style of <c>LegajoCorrectionValidator</c>: pure, static, es-AR
/// messages that name the field and the balance path, and NEVER quote a value (a figure, or the reason).
/// Two halves:
/// <list type="bullet">
/// <item><see cref="ValidateRequest"/> — what the request alone can tell (reason, count, whitelist,
/// duplicates, value type and range); run by the controller before anything is loaded;</item>
/// <item><see cref="ValidateAgainstBalance"/> — what needs the stored balance (the line exists, the value
/// changes); run by the service on the loaded BalanceJson.</item>
/// </list>
/// </summary>
public static class BalanceCorrectionValidator
{
    /// <summary>Minimum length of the trimmed reason.</summary>
    public const int MinReasonLength = 10;

    /// <summary>Maximum length of the trimmed reason.</summary>
    public const int MaxReasonLength = 1000;

    /// <summary>Maximum corrections in one request.</summary>
    public const int MaxCorrections = 30;

    /// <summary>
    /// Largest absolute value accepted, in pesos: far above any SME balance, and well inside the range a
    /// double holds exactly enough for the integrity checks. The legajo correction uses the same cap.
    /// </summary>
    public const double MaxAbsoluteValue = 1_000_000_000_000_000d;

    // A path longer than this is not echoed back in a message: it cannot be a whitelisted one anyway.
    private const int MaxEchoedPathLength = 100;

    /// <summary>
    /// The request-only rules. Returns the command when every rule passes, otherwise null and the errors.
    /// </summary>
    public static (BalanceCorrectionCommand? Command, IReadOnlyList<ValidationError> Errors) ValidateRequest(
        BalanceCorrectionRequest? request)
    {
        var errors = new List<ValidationError>();

        var reason = request?.Reason?.Trim() ?? string.Empty;
        if (reason.Length is < MinReasonLength or > MaxReasonLength)
        {
            errors.Add(new ValidationError(
                "reason",
                $"El motivo es obligatorio y debe tener entre {MinReasonLength} y {MaxReasonLength} caracteres."));
        }

        // Closed vocabulary; absent or null is the default. The value sent is never echoed.
        var origin = request?.Origin ?? BalanceCorrectionOrigins.Manual;
        if (!BalanceCorrectionOrigins.All.Contains(origin, StringComparer.Ordinal))
        {
            errors.Add(new ValidationError(
                "origin", "El origen de la corrección debe ser \"manual\" o \"ai_suggestion\"."));
        }

        var items = request?.Corrections;
        if (items is null || items.Count == 0)
        {
            errors.Add(new ValidationError("corrections", "Indicá al menos una cifra a corregir."));
            return (null, errors);
        }

        if (items.Count > MaxCorrections)
        {
            errors.Add(new ValidationError(
                "corrections", $"Se pueden corregir como máximo {MaxCorrections} cifras por vez."));
            return (null, errors);
        }

        var entries = new List<BalanceCorrectionEntry>(items.Count);
        var seen    = new HashSet<string>(StringComparer.Ordinal);

        for (var k = 0; k < items.Count; k++)
        {
            var item      = items[k];
            var pathField = $"corrections[{k}].path";

            if (item is null)
            {
                errors.Add(new ValidationError($"corrections[{k}]", "La corrección está vacía."));
                continue;
            }

            var path = BalanceCorrectionPath.TryParse(item.Path);
            if (path is null)
            {
                errors.Add(new ValidationError(pathField, string.IsNullOrWhiteSpace(item.Path)
                    ? "Indicá la cifra a corregir."
                    : item.Path.Length <= MaxEchoedPathLength
                        ? $"La cifra \"{item.Path}\" no se puede corregir: solo se admiten las cifras principales del balance y los importes de los rubros del activo corriente, el pasivo corriente y el pasivo no corriente."
                        : "La cifra indicada no se puede corregir."));
                continue;
            }

            if (!seen.Add(path.Raw))
            {
                errors.Add(new ValidationError(pathField, $"La cifra {path.Raw} figura más de una vez en la misma corrección."));
                continue;
            }

            if (ReadValue(item.Value, path.Raw, $"corrections[{k}].value", errors, out var value))
                entries.Add(new BalanceCorrectionEntry(path, value));
        }

        return errors.Count == 0
            ? (new BalanceCorrectionCommand(reason, entries, origin), errors)
            : (null, errors);
    }

    /// <summary>
    /// The rules that need the stored balance: every item path's line must exist, and every correction must
    /// change the stored value. Empty when all pass. Field names keep the request index.
    /// </summary>
    public static IReadOnlyList<ValidationError> ValidateAgainstBalance(JsonObject balance, BalanceCorrectionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        return ValidateAgainstBalance(balance, command.Corrections);
    }

    /// <summary>The same rules over bare entries — what the suggestion's dry run validates.</summary>
    public static IReadOnlyList<ValidationError> ValidateAgainstBalance(
        JsonObject balance, IReadOnlyList<BalanceCorrectionEntry> corrections)
    {
        ArgumentNullException.ThrowIfNull(balance);
        ArgumentNullException.ThrowIfNull(corrections);

        var errors = new List<ValidationError>();

        for (var k = 0; k < corrections.Count; k++)
        {
            var entry = corrections[k];

            if (!TryReadStored(balance, entry.Path, out var stored))
            {
                errors.Add(new ValidationError($"corrections[{k}].path", entry.Path.IsItem
                    ? $"{entry.Path.Raw}: el balance extraído no tiene ese renglón."
                    : $"{entry.Path.Raw}: el balance extraído no tiene esa cifra en un formato corregible."));
                continue;
            }

            if (stored == entry.Value)
            {
                errors.Add(new ValidationError(
                    $"corrections[{k}].value",
                    $"{entry.Path.Raw}: sin cambios — el valor corregido es igual al extraído."));
            }
        }

        return errors;
    }

    /// <summary>
    /// The stored value at <paramref name="path"/>: true with the number (or null) when the target exists
    /// and holds a number or null; false when an item's line does not exist or the target holds something
    /// else. A missing top-level key reads as null: every extraction writes the 13 ratio figures, and
    /// result_before_tax / income_tax since prompt 2.18, so only an older blob lacks one — and its figure
    /// was never extracted.
    /// </summary>
    internal static bool TryReadStored(JsonObject balance, BalanceCorrectionPath path, out double? value)
    {
        value = null;

        JsonNode? target;
        if (path.Index is { } index)
        {
            if (balance[path.Key] is not JsonArray array || index >= array.Count || array[index] is not JsonObject line)
                return false;

            target = line["amount"];
        }
        else
        {
            target = balance[path.Key];
        }

        if (target is null)
            return true;

        if (target is JsonValue jsonValue && jsonValue.GetValueKind() == JsonValueKind.Number
            && jsonValue.TryGetValue<double>(out var number))
        {
            value = number;
            return true;
        }

        return false;
    }

    private static bool ReadValue(
        JsonElement value, string path, string field, List<ValidationError> errors, out double? result)
    {
        result = null;

        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return true;

            case JsonValueKind.Number:
                if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
                {
                    errors.Add(new ValidationError(field, $"{path} está fuera del rango admitido."));
                    return false;
                }

                if (Math.Abs(number) > MaxAbsoluteValue)
                {
                    errors.Add(new ValidationError(field, $"{path} está fuera del rango admitido."));
                    return false;
                }

                // -0 and 0 are the same figure; store the canonical zero.
                result = number == 0d ? 0d : number;
                return true;

            case JsonValueKind.Undefined:
                errors.Add(new ValidationError(field, $"Indicá el valor corregido de {path} (un número, o null para borrarlo)."));
                return false;

            default:
                errors.Add(new ValidationError(field, $"{path} debe ser un número (sin separadores de miles) o null."));
                return false;
        }
    }
}
