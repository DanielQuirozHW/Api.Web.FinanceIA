// Project : Api.Web.FinanceIA
// Created : 2026-11-10

using System.Text.RegularExpressions;
using Api.Web.FinanceIA.Models.Nosis;

namespace Api.Web.FinanceIA.Servicios.NosisReport;

/// <summary>
/// Defence in depth after parsing. The parser never reads the personal-data sections, but a string it
/// does keep (an entity, a source, a type) comes from free text. Any kept string that looks like a
/// CUIT/CUIL, DNI, phone or CBU (seven or more digits run together, spaces, dots, dashes and brackets
/// allowed between them), an e-mail, or that names one of those identifiers, is dropped: a nullable
/// field becomes null, a list item or row is removed. Strings are capped at <see cref="MaxStringLength"/>
/// characters and lists at fixed sizes (newest kept). Returns how many strings it dropped — the only
/// thing about them that is ever logged.
/// </summary>
internal static class NosisReportPrivacyGuard
{
    public const int MaxStringLength = 200;
    public const int MaxMonths = 60;
    public const int MaxEntities = 100;
    public const int MaxChequeMonths = 240;
    public const int MaxItems = 200;

    private static readonly Regex DigitRun = new(
        @"\d(?:[\s.\-()]*\d){6,}", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    private static readonly Regex Email = new(
        @"[^\s@]+@[^\s@]+", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    private static readonly Regex IdentifierWord = new(
        @"\b(CUIT|CUIL|DNI|CBU|CVU|TEL|TELEFONO|CELULAR|E-?MAIL|DOMICILIO)\b", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    /// <summary>True when <paramref name="value"/> must not be stored.</summary>
    public static bool LooksPersonal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return DigitRun.IsMatch(value) || Email.IsMatch(value) || IdentifierWord.IsMatch(NosisReportText.Normalise(value));
    }

    public static (NosisManagerReport Report, int Dropped) Apply(NosisManagerReport report)
    {
        var dropped = 0;

        string? Keep(string? value)
        {
            if (value is null)
                return null;
            if (LooksPersonal(value))
            {
                dropped++;
                return null;
            }

            var trimmed = value.Trim();
            return trimmed.Length > MaxStringLength ? trimmed[..MaxStringLength] : trimmed;
        }

        List<string> KeepAll(IEnumerable<string> values, int max)
            => values.Select(Keep).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).Take(max).ToList();

        static IReadOnlyList<T> Newest<T>(IReadOnlyList<T> items, int max) => items.Count <= max ? items : items.Skip(items.Count - max).ToList();

        var summary = report.Summary is null ? null : report.Summary with
        {
            EmpleadosRango     = Keep(report.Summary.EmpleadosRango),
            ActividadPrincipal = Keep(report.Summary.ActividadPrincipal),
            Arca = report.Summary.Arca is null ? null : report.Summary.Arca with
            {
                Ganancias = Keep(report.Summary.Arca.Ganancias),
                Iva       = Keep(report.Summary.Arca.Iva),
            },
        };

        var deuda = report.DeudaPorEntidad?
            .Select(e => (Entity: e, Name: Keep(e.Entidad)))
            .Where(e => e.Name is not null)
            .Take(MaxEntities)
            .Select(e => e.Entity with { Entidad = e.Name!, Periodos = Newest(e.Entity.Periodos, MaxMonths) })
            .ToList();

        var aportes = report.AportesPatronales is null ? null : Newest(report.AportesPatronales, MaxMonths)
            .Select(a => a with
            {
                SeguridadSocial        = Keep(a.SeguridadSocial),
                ObraSocial             = Keep(a.ObraSocial),
                ContribucionObraSocial = Keep(a.ContribucionObraSocial),
            })
            .ToList();

        var criterios = report.CriteriosAceptacion is null ? null : report.CriteriosAceptacion with
        {
            Nombre    = Keep(report.CriteriosAceptacion.Nombre),
            Resultado = Keep(report.CriteriosAceptacion.Resultado),
            Criterios = report.CriteriosAceptacion.Criterios
                .Select(c => (Nombre: Keep(c.Nombre), Resultado: Keep(c.Resultado)))
                .Where(c => c.Nombre is not null && c.Resultado is not null)
                .Take(MaxItems)
                .Select(c => new NosisReportCriterio { Nombre = c.Nombre!, Resultado = c.Resultado! })
                .ToList(),
        };

        var juicios = report.Juicios is null ? null : report.Juicios with
        {
            Items = report.Juicios.Items
                .Take(MaxItems)
                .Select(j => j with { Tipo = Keep(j.Tipo), Jurisdiccion = Keep(j.Jurisdiccion) })
                .ToList(),
        };

        var sisa = report.Sisa is null ? null : report.Sisa with
        {
            Estado     = Keep(report.Sisa.Estado),
            Categorias = KeepAll(report.Sisa.Categorias, MaxItems),
        };

        var srt = report.Srt is null ? null : report.Srt with { Aseguradora = Keep(report.Srt.Aseguradora) };

        var referencias = report.ReferenciasComerciales is null ? null : report.ReferenciasComerciales with
        {
            Fuentes = KeepAll(report.ReferenciasComerciales.Fuentes, MaxItems),
        };

        var mav = report.Mav is null ? null : report.Mav with
        {
            PorInstrumento = report.Mav.PorInstrumento
                .Select(i => (Item: i, Name: Keep(i.Instrumento))).Where(i => i.Name is not null).Take(MaxItems)
                .Select(i => i.Item with { Instrumento = i.Name! }).ToList(),
            PorMoneda = report.Mav.PorMoneda
                .Select(m => (Item: m, Name: Keep(m.Moneda))).Where(m => m.Name is not null).Take(MaxItems)
                .Select(m => m.Item with { Moneda = m.Name! }).ToList(),
        };

        var fiscales = report.AntecedentesFiscales?
            .Take(MaxItems)
            .Select(a => a with { Organismo = Keep(a.Organismo), Tipo = Keep(a.Tipo) })
            .ToList();

        var scrubbed = report with
        {
            BalancesReducidos       = report.BalancesReducidos is null ? null : Newest(report.BalancesReducidos, 3),
            Mav                     = mav,
            AntecedentesFiscales    = fiscales,
            ComunicadosBcra         = report.ComunicadosBcra is null ? null : report.ComunicadosBcra with { Fechas = Newest(report.ComunicadosBcra.Fechas, MaxItems) },
            ComercioExterior        = report.ComercioExterior is null ? null : report.ComercioExterior with { Anios = Newest(report.ComercioExterior.Anios, MaxMonths) },
            Summary                 = summary,
            Evolucion24m            = report.Evolucion24m is null ? null : Newest(report.Evolucion24m, MaxMonths),
            DeudaPorEntidad         = deuda,
            Atrasos                 = report.Atrasos is null ? null : Newest(report.Atrasos, MaxMonths),
            AportesPatronales       = aportes,
            ChequesRechazadosPorMes = report.ChequesRechazadosPorMes is null ? null : Newest(report.ChequesRechazadosPorMes, MaxChequeMonths),
            CriteriosAceptacion     = criterios,
            Juicios                 = juicios,
            Sisa                    = sisa,
            Srt                     = srt,
            ReferenciasComerciales  = referencias,
        };

        return (scrubbed, dropped);
    }
}
