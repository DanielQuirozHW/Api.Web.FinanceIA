// Project : Api.Web.FinanceIA
// Created : 2026-10-29

using System.Text.Json;
using Api.Web.FinanceIA.Models.Arca;
using Api.Web.FinanceIA.Models.Financia;

namespace Api.Web.FinanceIA.Servicios.Legajo;

/// <summary>
/// The person-level BCRA and ARCA snapshot writes, shared by the entity refreshes
/// (POST api/entities/{id}/refresh-bcra|arca) and the legajo-scoped ones
/// (POST api/person-legajos/{legajoId}/refresh-bcra|arca), so the two can never disagree on what a
/// refresh stores. Pure: it mutates the rows it is given and saves nothing, calls nothing and logs
/// nothing (a snapshot is PII).
/// </summary>
/// <remarks>
/// Either row may be absent: an entity without a legajo (a company, or a persona humana created before
/// DDL 069) refreshes the entity only, and a fiador's legajo — linked to no entity — refreshes the legajo
/// only. When both are given they describe the same person and leave this class agreeing.
/// </remarks>
public static class PersonSnapshotWriter
{
    private static readonly JsonSerializerOptions SnapshotReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    /// Whether both BCRA queries of a snapshot gave a real answer — data found or nothing reported. A missing
    /// snapshot, a missing block or a LookupFailed is not usable. Shared by the solicitante's re-query at
    /// evaluation and the fiador's (<see cref="IPersonLegajoSnapshotRefreshService.RefreshUnusableBcraAsync"/>).
    /// </summary>
    public static bool IsBcraSnapshotUsable(BcraData? bcra)
        => bcra?.Deuda is { Estado: not BcraDeudaConsultaEstado.LookupFailed }
        && bcra.Cheques is { Estado: not BcraChequesConsultaEstado.LookupFailed };

    /// <summary>
    /// Whether the Central de Deudores part of a BCRA answer is a real answer — debt found or no debt
    /// reported. A null payload, a missing Deuda block or a LookupFailed is not. A refresh that would
    /// store such an answer stores nothing instead: the deuda drives the score, and overwriting a good
    /// stored block with an outage is how a transient failure became a "fresh" snapshot.
    /// </summary>
    public static bool IsDeudaAnswered(BcraData? bcra)
        => bcra?.Deuda is { Estado: BcraDeudaConsultaEstado.DebtFound or BcraDeudaConsultaEstado.NoDebtReported };

    /// <summary>
    /// When the fresh cheques lookup failed (block null or LookupFailed) and the stored snapshot holds a
    /// real cheques answer (ChequesFound or NoChequesReported), puts the STORED cheques block on
    /// <paramref name="fresh"/> — with its own FetchedAt, so its age stays visible — and returns true.
    /// Everything else in <paramref name="fresh"/> is stored fresh. Returns false, touching nothing, when
    /// the fresh cheques answered or there is no real stored answer to keep. Shared by the entity and the
    /// legajo refresh-bcra; never used by the analysis pipeline, which reads BCRA live.
    /// </summary>
    public static bool KeepStoredChequesOnFailure(BcraData fresh, string? storedBcraJson)
    {
        ArgumentNullException.ThrowIfNull(fresh);

        if (fresh.Cheques is { Estado: not BcraChequesConsultaEstado.LookupFailed })
            return false;

        var stored = ReadBcra(storedBcraJson)?.Cheques;
        if (stored is not { Estado: BcraChequesConsultaEstado.ChequesFound or BcraChequesConsultaEstado.NoChequesReported })
            return false;

        fresh.Cheques = stored;
        return true;
    }

    /// <summary>A stored BCRA snapshot, read defensively: null when absent or unreadable.</summary>
    public static BcraData? ReadBcra(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<BcraData>(json, SnapshotReadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stores one BCRA answer on the entity and/or its legajo, with the same payload and timestamp. A
    /// persona humana's BCRA snapshot is queried by CUIL, so it is the PERSON's: fetched once, copied.
    /// </summary>
    public static void ApplyBcra(Entity? entity, PersonLegajo? legajo, BcraData bcraData, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(bcraData);

        var json = JsonSerializer.Serialize(bcraData);

        if (entity is not null)
        {
            entity.BcraDataJson      = json;
            entity.BcraDataUpdatedAt = nowUtc;
        }

        if (legajo is not null)
        {
            legajo.BcraDataJson      = json;
            legajo.BcraDataUpdatedAt = nowUtc;
        }
    }

    /// <summary>
    /// Copies the entity's BCRA snapshot onto its legajo — payload and timestamp — after a write path
    /// stored it on the entity. Copied, never fetched a second time.
    /// </summary>
    public static void CopyBcraSnapshot(Entity entity, PersonLegajo legajo)
    {
        legajo.BcraDataJson      = entity.BcraDataJson;
        legajo.BcraDataUpdatedAt = entity.BcraDataUpdatedAt;
    }

    /// <summary>
    /// Stores an ARCA answer that carries a fiscal section (<see cref="ArcaPersonaData.FiscalData"/>
    /// non-null): the snapshot with the key status, its timestamp and the derived fiscal regime on the
    /// entity; and on the legajo also the monotributo category and the activity start. BusinessName,
    /// Sector, MipymeCategory and the person type are never touched.
    /// </summary>
    public static void ApplyArca(Entity? entity, PersonLegajo? legajo, ArcaPersonaData arcaData, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(arcaData);
        if (arcaData.FiscalData is null)
            throw new ArgumentException("ApplyArca needs an answer with a fiscal section.", nameof(arcaData));

        var fiscal = WithKeyStatus(arcaData.FiscalData, arcaData.EstadoClave, nowUtc)!;
        var json   = JsonSerializer.Serialize(fiscal);

        if (entity is not null)
        {
            entity.FiscalDataJson      = json;
            entity.FiscalDataUpdatedAt = nowUtc;

            // Fiscal regime (DDL 069): ARCA's answer is authoritative, except that a PARTIAL answer
            // (an error section beside datosGenerales) never downgrades a known regime.
            entity.FiscalRegime = ChooseRefreshedRegime(entity.FiscalRegime, fiscal.FiscalRegime, arcaData.IsIncomplete);
        }

        if (legajo is not null)
        {
            legajo.FiscalRegime = ChooseRefreshedRegime(legajo.FiscalRegime, fiscal.FiscalRegime, arcaData.IsIncomplete)
                ?? legajo.FiscalRegime;
            legajo.MonotributoCategory = ChooseRefreshedCategory(
                legajo.MonotributoCategory,
                legajo.FiscalRegime,
                FiscalRegimes.NormalizeMonotributoCategory(fiscal.MonotributoCategoria),
                arcaData.IsIncomplete);
            legajo.ArcaSnapshotJson  = json;
            legajo.ArcaDataUpdatedAt = nowUtc;
            legajo.ActivityStartDate = ChooseActivityStart(
                legajo.ActivityStartDate, ArcaActivityStart.From(fiscal), arcaData.IsIncomplete);

            // Entity and legajo describe the same person: they must not disagree.
            if (entity is not null)
                entity.FiscalRegime = legajo.FiscalRegime;
        }
    }

    /// <summary>
    /// An ARCA answer with datosGenerales and a key status but no fiscal section — the likely shape for
    /// a CUIT whose key is not active, i.e. exactly the case arca_cuit_activo exists to catch. The key
    /// status and its read date are merged into the entity's and the legajo's snapshots (a minimal one
    /// with empty lists when there is none). Lists, regime, category, the activity start and both
    /// snapshot stamps are left as they were, so older lists never look freshly read: the key status
    /// carries its own date. A snapshot that does not parse is left untouched.
    /// </summary>
    public static void ApplyKeyStatusOnly(Entity? entity, PersonLegajo? legajo, string estadoClave, DateTime nowUtc)
    {
        string? MergeKeyStatus(string? json)
        {
            ArcaFiscalData? current;
            if (string.IsNullOrWhiteSpace(json))
            {
                current = new ArcaFiscalData(null, null, null, [], [], []);
            }
            else
            {
                try
                {
                    current = JsonSerializer.Deserialize<ArcaFiscalData>(json);
                }
                catch (JsonException)
                {
                    current = null;
                }
            }

            return current is null
                ? json
                : JsonSerializer.Serialize(WithKeyStatus(current, estadoClave, nowUtc));
        }

        if (entity is not null)
            entity.FiscalDataJson = MergeKeyStatus(entity.FiscalDataJson);

        if (legajo is not null)
            legajo.ArcaSnapshotJson = MergeKeyStatus(legajo.ArcaSnapshotJson);
    }

    /// <summary>
    /// The snapshot with ARCA's key status and the instant it was read. Null in, null out; a blank status
    /// is stored as null (the evaluation's SinDatos), never as an empty string.
    /// </summary>
    public static ArcaFiscalData? WithKeyStatus(ArcaFiscalData? fiscal, string? estadoClave, DateTime readAtUtc)
    {
        if (fiscal is null) return null;

        var status = string.IsNullOrWhiteSpace(estadoClave) ? null : estadoClave.Trim();
        return fiscal with
        {
            EstadoClave             = status,
            EstadoClaveConsultadoAt = status is null ? null : readAtUtc
        };
    }

    /// <summary>
    /// The regime refresh-arca stores: ARCA's answer, Desconocido included — except that a PARTIAL
    /// answer (<see cref="ArcaPersonaData.IsIncomplete"/>) never replaces a known regime with an
    /// undetermined one. A regime outside <see cref="FiscalRegimes.Known"/> is ignored.
    /// </summary>
    private static string? ChooseRefreshedRegime(string? current, string? fromArca, bool arcaIncomplete)
    {
        if (!FiscalRegimes.IsKnown(fromArca)) return current;

        if (arcaIncomplete && !FiscalRegimes.IsDeterminate(fromArca) && FiscalRegimes.IsDeterminate(current))
        {
            return current;
        }

        return fromArca;
    }

    /// <summary>
    /// The monotributo category refresh-arca stores beside <paramref name="storedRegime"/>: ARCA's
    /// letter for a monotributista, keeping the current one when ARCA sent none (or an unparsable
    /// one); nothing for any other known regime; and, while the regime is undetermined, the current
    /// category after a partial answer but nothing after a complete one.
    /// </summary>
    private static string? ChooseRefreshedCategory(
        string? currentCategory,
        string storedRegime,
        string? arcaCategory,
        bool arcaIncomplete)
    {
        if (storedRegime == FiscalRegimes.Monotributo) return arcaCategory ?? currentCategory;

        if (FiscalRegimes.IsDeterminate(storedRegime)) return null;

        return arcaIncomplete ? currentCategory : null;
    }

    /// <summary>
    /// The activity start a legajo ARCA write stores: ARCA's earliest registration period. ARCA reporting
    /// none never clears a stored date, and a PARTIAL answer (a section errored, so periods may be missing)
    /// never moves it later. ARCA is the column's only writer: no analyst or document path sets it.
    /// </summary>
    private static DateTime? ChooseActivityStart(DateTime? current, DateTime? fromArca, bool arcaIncomplete)
    {
        if (fromArca is not { } arca) return current;

        if (arcaIncomplete && current is { } stored && stored <= arca) return current;

        return arca;
    }
}
