// Project : Api.Web.FinanceIA.Tests
// Created : 2026-09-24

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Api.Web.FinanceIA.Models.Financia;
using Api.Web.FinanceIA.Servicios;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly.Timeout;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios;

/// <summary>
/// Guards the Deudas/Historicas leg of <c>BcraService.GetDeudaAsync</c>.
/// <para>
/// THE BUG THIS PINS: Deudas/{cuit} answers with the latest período only, so the three history series
/// — historialSituaciones, historialMontos, historialMontosPorSituacion — always held one point. The
/// portal showed "Trayectoria 1 m", never drew the debt-evolution chart (it needs two períodos), and
/// the scoring trend adjustment never had two períodos to compare. The history now comes from
/// Deudas/Historicas/{cuit}, fetched once Deudas has given a real answer — debt found OR no debt
/// reported; Deudas alone still decides Estado and every current-período field, and a failed history leg
/// falls back to the Deudas períodos (DebtFound) or to empty series (NoDebtReported).
/// </para>
/// <para>
/// NoDebtReported WITH a history is the CUATROVIENTOS case: Deudas 404s (nothing in the latest período)
/// while Historicas answers 200 with months of past debt. The analyst must see "no debt today, situación 3
/// four months ago", not "Sin deuda registrada" and nothing else — while scoring stays exactly as before.
/// </para>
/// <para>
/// The fixtures are REAL responses (captured 2026-09 for a PyME with debt), anonymised: denominacion
/// and identificacion replaced, entidad names and montos kept verbatim. Historicas is trimmed to its
/// eight newest of 24 períodos. Two shapes matter and are kept exactly as BCRA sends them: Historicas
/// lists newest-first, and it keeps an entidad the CUIT owes nothing that month as
/// <c>situacion 0, monto 0</c> — where Deudas omits it. Fixtures built by hand are marked SYNTHETIC.
/// </para>
/// <para>
/// The same harness pins the Deudas 404: BCRA answers "No se encontró datos para la identificación
/// ingresada." with HTTP 404 for a CUIT with no reported debt. That is NoDebtReported and cached — it
/// was LookupFailed, which excluded BCRA from the score and failed again on every refresh.
/// </para>
/// Hand-rolled stubs, no mock library. The handler routes BY PATH and matches Historicas before Deudas,
/// because the Deudas path is a prefix of the Historicas one.
/// </summary>
public sealed class BcraDeudaHistoricasTests
{
    private const string Cuit = "30716327279";

    // ── Fixtures: real, anonymised ────────────────────────────────────────────

    // Deudas/{cuit}: the one período, 202607, nine entidades, all situación 1. Total 11572449.
    private const string DeudaRealJson = """
        {"status":200,"results":{"identificacion":30716327279,"denominacion":"EMPRESA DE PRUEBA SA",
         "periodos":[{"periodo":"202607","entidades":[
           {"entidad":"BANCO DE LA NACION ARGENTINA","situacion":1,"fechaSit1":"2004-09-30","monto":918265.0,"diasAtrasoPago":0,"refinanciaciones":false,"recategorizacionOblig":false,"situacionJuridica":false,"irrecDisposicionTecnica":false,"enRevision":false,"procesoJud":false},
           {"entidad":"AMERICAN EXPRESS ARGENTINA S.A.","situacion":1,"fechaSit1":"2004-09-30","monto":218502.0,"diasAtrasoPago":0,"refinanciaciones":false,"recategorizacionOblig":false,"situacionJuridica":false,"irrecDisposicionTecnica":false,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO BBVA ARGENTINA S.A.","situacion":1,"fechaSit1":"2004-09-30","monto":984.0,"diasAtrasoPago":0,"refinanciaciones":false,"recategorizacionOblig":false,"situacionJuridica":false,"irrecDisposicionTecnica":false,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO SANTANDER ARGENTINA S.A.","situacion":1,"fechaSit1":"2004-09-30","monto":6934564.0,"diasAtrasoPago":0,"refinanciaciones":false,"recategorizacionOblig":false,"situacionJuridica":false,"irrecDisposicionTecnica":false,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE GALICIA Y BUENOS AIRES S.A.","situacion":1,"fechaSit1":"2004-09-30","monto":16895.0,"diasAtrasoPago":0,"refinanciaciones":false,"recategorizacionOblig":false,"situacionJuridica":false,"irrecDisposicionTecnica":false,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO MACRO S.A.","situacion":1,"fechaSit1":"2004-09-30","monto":942249.0,"diasAtrasoPago":0,"refinanciaciones":false,"recategorizacionOblig":false,"situacionJuridica":false,"irrecDisposicionTecnica":false,"enRevision":false,"procesoJud":false},
           {"entidad":"YPF S.A.","situacion":1,"fechaSit1":"2004-09-30","monto":67834.0,"diasAtrasoPago":0,"refinanciaciones":false,"recategorizacionOblig":false,"situacionJuridica":false,"irrecDisposicionTecnica":false,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO INDUSTRIAL S.A.","situacion":1,"fechaSit1":"2004-09-30","monto":197601.0,"diasAtrasoPago":0,"refinanciaciones":false,"recategorizacionOblig":false,"situacionJuridica":false,"irrecDisposicionTecnica":false,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO PATAGONIA S.A.","situacion":1,"fechaSit1":"2004-09-30","monto":2275555.0,"diasAtrasoPago":0,"refinanciaciones":false,"recategorizacionOblig":false,"situacionJuridica":false,"irrecDisposicionTecnica":false,"enRevision":false,"procesoJud":false}]}]}}
        """;

    // Deudas/Historicas/{cuit}: newest first, 202607 down to 202512 (of 24 in the real answer). Each
    // período lists the same 14 entidades; those at situación 0 / monto 0 owe nothing that month.
    // 202607 is entidad-for-entidad the Deudas período once its five situación-0 rows are dropped.
    private const string HistoricasRealJson = """
        {"status":200,"results":{"identificacion":30716327279,"denominacion":"EMPRESA DE PRUEBA SA",
         "periodos":[
         {"periodo":"202607","entidades":[
           {"entidad":"BANCO DE LA NACION ARGENTINA","situacion":1,"monto":918265.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE LA PROVINCIA DE BUENOS AIRES","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"AMERICAN EXPRESS ARGENTINA S.A.","situacion":1,"monto":218502.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE LA CIUDAD DE BUENOS AIRES","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO BBVA ARGENTINA S.A.","situacion":1,"monto":984.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO SANTANDER ARGENTINA S.A.","situacion":1,"monto":6934564.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE GALICIA Y BUENOS AIRES S.A.","situacion":1,"monto":16895.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO MACRO S.A.","situacion":1,"monto":942249.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE INVERSION Y COMERCIO EXTERIOR S.A.","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO CREDICOOP COOPERATIVO LIMITADO","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"YPF S.A.","situacion":1,"monto":67834.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BIBANK S.A.","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO INDUSTRIAL S.A.","situacion":1,"monto":197601.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO PATAGONIA S.A.","situacion":1,"monto":2275555.0,"enRevision":false,"procesoJud":false}]},
         {"periodo":"202606","entidades":[
           {"entidad":"BANCO DE LA NACION ARGENTINA","situacion":1,"monto":936622.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE LA PROVINCIA DE BUENOS AIRES","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"AMERICAN EXPRESS ARGENTINA S.A.","situacion":1,"monto":141864.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE LA CIUDAD DE BUENOS AIRES","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO BBVA ARGENTINA S.A.","situacion":1,"monto":388.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO SANTANDER ARGENTINA S.A.","situacion":1,"monto":4620117.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE GALICIA Y BUENOS AIRES S.A.","situacion":1,"monto":43838.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO MACRO S.A.","situacion":1,"monto":2784693.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE INVERSION Y COMERCIO EXTERIOR S.A.","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO CREDICOOP COOPERATIVO LIMITADO","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"YPF S.A.","situacion":1,"monto":82380.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BIBANK S.A.","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO INDUSTRIAL S.A.","situacion":1,"monto":209761.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO PATAGONIA S.A.","situacion":1,"monto":2948617.0,"enRevision":false,"procesoJud":false}]},
         {"periodo":"202605","entidades":[
           {"entidad":"BANCO DE LA NACION ARGENTINA","situacion":1,"monto":972639.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE LA PROVINCIA DE BUENOS AIRES","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"AMERICAN EXPRESS ARGENTINA S.A.","situacion":1,"monto":136008.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE LA CIUDAD DE BUENOS AIRES","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO BBVA ARGENTINA S.A.","situacion":1,"monto":311646.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO SANTANDER ARGENTINA S.A.","situacion":1,"monto":4070924.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE GALICIA Y BUENOS AIRES S.A.","situacion":1,"monto":351566.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO MACRO S.A.","situacion":1,"monto":2527237.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE INVERSION Y COMERCIO EXTERIOR S.A.","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO CREDICOOP COOPERATIVO LIMITADO","situacion":1,"monto":25808.0,"enRevision":false,"procesoJud":false},
           {"entidad":"YPF S.A.","situacion":1,"monto":85855.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BIBANK S.A.","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO INDUSTRIAL S.A.","situacion":1,"monto":221695.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO PATAGONIA S.A.","situacion":1,"monto":2946094.0,"enRevision":false,"procesoJud":false}]},
         {"periodo":"202604","entidades":[
           {"entidad":"BANCO DE LA NACION ARGENTINA","situacion":1,"monto":1007688.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE LA PROVINCIA DE BUENOS AIRES","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"AMERICAN EXPRESS ARGENTINA S.A.","situacion":1,"monto":189653.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE LA CIUDAD DE BUENOS AIRES","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO BBVA ARGENTINA S.A.","situacion":1,"monto":612810.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO SANTANDER ARGENTINA S.A.","situacion":1,"monto":4061055.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE GALICIA Y BUENOS AIRES S.A.","situacion":1,"monto":307400.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO MACRO S.A.","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE INVERSION Y COMERCIO EXTERIOR S.A.","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO CREDICOOP COOPERATIVO LIMITADO","situacion":1,"monto":50750.0,"enRevision":false,"procesoJud":false},
           {"entidad":"YPF S.A.","situacion":1,"monto":103646.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BIBANK S.A.","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO INDUSTRIAL S.A.","situacion":1,"monto":235692.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO PATAGONIA S.A.","situacion":1,"monto":2822539.0,"enRevision":false,"procesoJud":false}]},
         {"periodo":"202603","entidades":[
           {"entidad":"BANCO DE LA NACION ARGENTINA","situacion":1,"monto":1043235.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE LA PROVINCIA DE BUENOS AIRES","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"AMERICAN EXPRESS ARGENTINA S.A.","situacion":1,"monto":132244.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE LA CIUDAD DE BUENOS AIRES","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO BBVA ARGENTINA S.A.","situacion":1,"monto":905996.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO SANTANDER ARGENTINA S.A.","situacion":1,"monto":4063524.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE GALICIA Y BUENOS AIRES S.A.","situacion":1,"monto":86546.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO MACRO S.A.","situacion":1,"monto":1356696.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE INVERSION Y COMERCIO EXTERIOR S.A.","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO CREDICOOP COOPERATIVO LIMITADO","situacion":1,"monto":101926.0,"enRevision":false,"procesoJud":false},
           {"entidad":"YPF S.A.","situacion":1,"monto":2925.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BIBANK S.A.","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO INDUSTRIAL S.A.","situacion":1,"monto":250407.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO PATAGONIA S.A.","situacion":1,"monto":917777.0,"enRevision":false,"procesoJud":false}]},
         {"periodo":"202602","entidades":[
           {"entidad":"BANCO DE LA NACION ARGENTINA","situacion":1,"monto":1167338.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE LA PROVINCIA DE BUENOS AIRES","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"AMERICAN EXPRESS ARGENTINA S.A.","situacion":1,"monto":377146.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE LA CIUDAD DE BUENOS AIRES","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO BBVA ARGENTINA S.A.","situacion":1,"monto":1233855.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO SANTANDER ARGENTINA S.A.","situacion":1,"monto":963912.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE GALICIA Y BUENOS AIRES S.A.","situacion":1,"monto":2554280.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO MACRO S.A.","situacion":1,"monto":1522849.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE INVERSION Y COMERCIO EXTERIOR S.A.","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO CREDICOOP COOPERATIVO LIMITADO","situacion":1,"monto":735569.0,"enRevision":false,"procesoJud":false},
           {"entidad":"YPF S.A.","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BIBANK S.A.","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO INDUSTRIAL S.A.","situacion":1,"monto":263847.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO PATAGONIA S.A.","situacion":1,"monto":956139.0,"enRevision":false,"procesoJud":false}]},
         {"periodo":"202601","entidades":[
           {"entidad":"BANCO DE LA NACION ARGENTINA","situacion":1,"monto":1172973.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE LA PROVINCIA DE BUENOS AIRES","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"AMERICAN EXPRESS ARGENTINA S.A.","situacion":1,"monto":115392.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE LA CIUDAD DE BUENOS AIRES","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO BBVA ARGENTINA S.A.","situacion":1,"monto":1556629.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO SANTANDER ARGENTINA S.A.","situacion":1,"monto":2073062.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE GALICIA Y BUENOS AIRES S.A.","situacion":1,"monto":579246.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO MACRO S.A.","situacion":1,"monto":1468266.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE INVERSION Y COMERCIO EXTERIOR S.A.","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO CREDICOOP COOPERATIVO LIMITADO","situacion":1,"monto":2450591.0,"enRevision":false,"procesoJud":false},
           {"entidad":"YPF S.A.","situacion":1,"monto":139495.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BIBANK S.A.","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO INDUSTRIAL S.A.","situacion":1,"monto":277360.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO PATAGONIA S.A.","situacion":1,"monto":107446.0,"enRevision":false,"procesoJud":false}]},
         {"periodo":"202512","entidades":[
           {"entidad":"BANCO DE LA NACION ARGENTINA","situacion":1,"monto":1267847.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE LA PROVINCIA DE BUENOS AIRES","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"AMERICAN EXPRESS ARGENTINA S.A.","situacion":1,"monto":181155.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE LA CIUDAD DE BUENOS AIRES","situacion":1,"monto":3171.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO BBVA ARGENTINA S.A.","situacion":1,"monto":1878945.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO SANTANDER ARGENTINA S.A.","situacion":1,"monto":3837834.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE GALICIA Y BUENOS AIRES S.A.","situacion":1,"monto":138482.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO MACRO S.A.","situacion":1,"monto":261011.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO DE INVERSION Y COMERCIO EXTERIOR S.A.","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO CREDICOOP COOPERATIVO LIMITADO","situacion":1,"monto":1254041.0,"enRevision":false,"procesoJud":false},
           {"entidad":"YPF S.A.","situacion":1,"monto":79564.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BIBANK S.A.","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO INDUSTRIAL S.A.","situacion":1,"monto":289972.0,"enRevision":false,"procesoJud":false},
           {"entidad":"BANCO PATAGONIA S.A.","situacion":1,"monto":123178.0,"enRevision":false,"procesoJud":false}]}]}}
        """;

    // Real totals of the eight períodos (situación-0 entidades contribute their 0.0).
    private static readonly (string Periodo, decimal Total)[] RealTotals =
    [
        ("202607", 11572449m), ("202606", 11768280m), ("202605", 11649472m), ("202604", 9391233m),
        ("202603",  8861276m), ("202602",  9774935m), ("202601",  9940460m), ("202512", 9315200m),
    ];

    // ── Fixtures: SYNTHETIC ───────────────────────────────────────────────────

    // SYNTHETIC. 202606 has only situación-0 entidades — no debt at all that month — and 202605 mixes
    // a situación-0 entidad with a situación 1 and a situación 3.
    private const string HistoricasZeroPeriodoJson = """
        {"status":200,"results":{"identificacion":30716327279,"denominacion":"EMPRESA DE PRUEBA SA",
         "periodos":[
           {"periodo":"202607","entidades":[
             {"entidad":"BANCO DE LA NACION ARGENTINA","situacion":1,"monto":918265.0,"enRevision":false,"procesoJud":false}]},
           {"periodo":"202606","entidades":[
             {"entidad":"BANCO DE LA NACION ARGENTINA","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
             {"entidad":"BANCO MACRO S.A.","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false}]},
           {"periodo":"202605","entidades":[
             {"entidad":"BANCO DE LA NACION ARGENTINA","situacion":1,"monto":10.0,"enRevision":false,"procesoJud":false},
             {"entidad":"BANCO MACRO S.A.","situacion":0,"monto":0.0,"enRevision":false,"procesoJud":false},
             {"entidad":"BANCO PATAGONIA S.A.","situacion":3,"monto":30.0,"enRevision":false,"procesoJud":false}]}]}}
        """;

    // SYNTHETIC. Deudas answering 202608 while Historicas' newest is still 202607 — the publication lag
    // the merge must survive without dropping or duplicating the current month.
    private const string DeudaNewerJson = """
        {"status":200,"results":{"identificacion":30716327279,"denominacion":"EMPRESA DE PRUEBA SA",
         "periodos":[{"periodo":"202608","entidades":[
           {"entidad":"BANCO DE LA NACION ARGENTINA","situacion":2,"monto":500.0,"diasAtrasoPago":40,
            "refinanciaciones":false,"situacionJuridica":false,"enRevision":false,"procesoJud":false}]}]}}
        """;

    // SYNTHETIC. Historicas disagreeing with Deudas on the SAME período (202607): one row must survive,
    // carrying the Deudas values.
    private const string HistoricasDisagreeingJson = """
        {"status":200,"results":{"identificacion":30716327279,"denominacion":"EMPRESA DE PRUEBA SA",
         "periodos":[
           {"periodo":"202607","entidades":[
             {"entidad":"BANCO DE LA NACION ARGENTINA","situacion":4,"monto":1.0,"enRevision":false,"procesoJud":false}]},
           {"periodo":"202606","entidades":[
             {"entidad":"BANCO DE LA NACION ARGENTINA","situacion":1,"monto":2.0,"enRevision":false,"procesoJud":false}]}]}}
        """;

    private const string NoPeriodosJson = """
        {"status":200,"results":{"identificacion":30716327279,"denominacion":"EMPRESA DE PRUEBA SA","periodos":[]}}
        """;

    // SYNTHETIC. The other shape of the 200 NoDebtReported exit: the latest período is listed, with no
    // entidades. It is the same month as Historicas' newest in HistoricasZeroPeriodoJson, where that
    // month DOES carry an entidad — Deudas must win it, so no series shows debt in a month Deudas
    // reports as clean.
    private const string DeudaNoEntidadesJson = """
        {"status":200,"results":{"identificacion":30716327279,"denominacion":"EMPRESA DE PRUEBA SA",
         "periodos":[{"periodo":"202607","entidades":[]}]}}
        """;

    private const string NotFoundJson =
        """{"status":404,"errorMessages":["No se encontró datos para la identificación ingresada."]}""";

    /// <summary>
    /// SYNTHETIC. <paramref name="count"/> consecutive months ending 202606, NEWEST FIRST like the real
    /// Historicas, one entidad per month whose monto is its position counted from the oldest — so an
    /// ordering slip surfaces as a wrong number, not merely a wrong count.
    /// </summary>
    private static string BuildHistoricasWithConsecutivePeriodos(int count)
    {
        var newest = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var periodos = Enumerable.Range(0, count).Select(i =>
        {
            var periodo = newest.AddMonths(-i).ToString("yyyyMM", CultureInfo.InvariantCulture);
            return $$"""
                {"periodo":"{{periodo}}",
                 "entidades":[{"entidad":"BANCO DE LA NACION ARGENTINA","situacion":1,"monto":{{count - i}}.0}]}
                """;
        });

        return $$$"""
            {"status":200,"results":{"identificacion":30716327279,"denominacion":"EMPRESA DE PRUEBA SA",
             "periodos":[{{{string.Join(",", periodos)}}}]}}
            """;
    }

    // SYNTHETIC. The Deudas side of the 30-month case: its one período is the newest Historicas month.
    private const string Deuda202606Json = """
        {"status":200,"results":{"identificacion":30716327279,"denominacion":"EMPRESA DE PRUEBA SA",
         "periodos":[{"periodo":"202606","entidades":[
           {"entidad":"BANCO DE LA NACION ARGENTINA","situacion":1,"monto":30.0,"diasAtrasoPago":0,
            "refinanciaciones":false,"situacionJuridica":false,"enRevision":false,"procesoJud":false}]}]}}
        """;

    // ── Stubs ─────────────────────────────────────────────────────────────────

    /// <summary>One leg's answer: a status and body, or an exception to throw.</summary>
    private sealed record Leg(HttpStatusCode Status, string Body, Exception? Throws = null)
    {
        public static Leg Ok(string body)             => new(HttpStatusCode.OK, body);
        public static Leg Fails(HttpStatusCode s)     => new(s, NotFoundJson);
        public static Leg Throwing(Exception ex)      => new(HttpStatusCode.OK, string.Empty, ex);
    }

    /// <summary>
    /// Answers the Deudas and Historicas legs independently and counts the requests each receives.
    /// <see cref="Historicas"/> is settable so a test can change the answer between two calls.
    /// </summary>
    private sealed class DeudaLegsHandler(Leg deuda, Leg historicas) : HttpMessageHandler
    {
        public Leg Historicas { get; set; } = historicas;

        public int DeudaCalls      { get; private set; }
        public int HistoricasCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken  cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            // Historicas BEFORE Deudas: the Deudas path is a prefix of this one.
            if (path.Contains("/Deudas/Historicas/", StringComparison.Ordinal))
            {
                HistoricasCalls++;
                return Answer(Historicas);
            }

            if (path.Contains("/Deudas/", StringComparison.Ordinal))
            {
                DeudaCalls++;
                return Answer(deuda);
            }

            return Answer(Leg.Fails(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Answer(Leg leg)
        {
            if (leg.Throws is not null)
                throw leg.Throws;

            return Task.FromResult(new HttpResponseMessage(leg.Status)
            {
                Content = new StringContent(leg.Body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.bcra.gob.ar") };
    }

    private static BcraService BuildSut(DeudaLegsHandler handler) =>
        BcraServiceTestFactory.Create(new StubHttpClientFactory(handler),
            new MemoryCache(Options.Create(new MemoryCacheOptions())));

    private static Task<BcraDeudaData> GetDeudaAsync(Leg deuda, Leg historicas) =>
        BuildSut(new DeudaLegsHandler(deuda, historicas)).GetDeudaAsync(Cuit, CancellationToken.None);

    // ── Real response: the history reaches the three series ──────────────────

    [Fact]
    public async Task RealHistoricas_EverySeriesCarriesAllEightPeriodosNewestFirst()
    {
        var result = await GetDeudaAsync(Leg.Ok(DeudaRealJson), Leg.Ok(HistoricasRealJson));

        var expected = RealTotals.Select(t => t.Periodo).ToArray();

        Assert.Equal(expected, result.HistorialSituaciones!.Select(p => p.Periodo).ToArray());
        Assert.Equal(expected, result.HistorialMontos!.Select(p => p.Periodo).ToArray());
        Assert.Equal(expected, result.HistorialMontosPorSituacion!.Select(p => p.Periodo).ToArray());
    }

    [Fact]
    public async Task RealHistoricas_EachPeriodoTotalsItsEntidades_AndTheNewestEqualsTheMontoTotalScalar()
    {
        var result = await GetDeudaAsync(Leg.Ok(DeudaRealJson), Leg.Ok(HistoricasRealJson));

        Assert.Equal(RealTotals, result.HistorialMontos!.Select(p => (p.Periodo, p.MontoTotal)).ToArray());
        Assert.Equal(11572449m, result.MontoTotal);
        Assert.Equal(result.MontoTotal, result.HistorialMontos![0].MontoTotal);
    }

    [Fact]
    public async Task RealHistoricas_EveryBandRowSumsToItsPeriodosTotal()
    {
        var result = await GetDeudaAsync(Leg.Ok(DeudaRealJson), Leg.Ok(HistoricasRealJson));

        foreach (var total in result.HistorialMontos!)
        {
            var fila = Assert.Single(result.HistorialMontosPorSituacion!, f => f.Periodo == total.Periodo);

            // Every real entidad is situación 1 (or 0, owing nothing): it is all "normal" debt.
            Assert.Equal(total.MontoTotal, fila.MontoSituacion1);
            Assert.Equal(0m, fila.MontoSituacion23);
            Assert.Equal(0m, fila.MontoSituacion46);
        }
    }

    [Fact]
    public async Task RealHistoricas_SituacionZeroEntidadesNeverLowerAPeriodosSituacionMaxima()
    {
        var result = await GetDeudaAsync(Leg.Ok(DeudaRealJson), Leg.Ok(HistoricasRealJson));

        // Five to six entidades per real período sit at situación 0. Read as debt, they would make every
        // período's maximum 1 anyway here — but they must not be the reason; see the synthetic cases.
        Assert.All(result.HistorialSituaciones!, p => Assert.Equal(1, p.SituacionMaxima));
    }

    [Fact]
    public async Task RealHistoricas_CurrentPeriodoFieldsStillComeFromDeudasAlone()
    {
        var result = await GetDeudaAsync(Leg.Ok(DeudaRealJson), Leg.Ok(HistoricasRealJson));

        Assert.Equal(BcraDeudaConsultaEstado.DebtFound, result.Estado);
        Assert.Equal("202607", result.PeriodoConsultado);
        Assert.Equal(1, result.SituacionMaxima);

        // Nine — the Deudas entidades. Historicas lists fourteen for the same month.
        Assert.Equal(9, result.EntidadesDetalle!.Count);
        Assert.Equal(9, result.EntidadesInformantes!.Count);
        Assert.Equal(0, result.MaxDiasAtraso);
    }

    // ── SYNTHETIC: situación 0, the merge, the caps ───────────────────────────

    [Fact]
    public async Task APeriodoWithOnlySituacionZeroEntidades_AddsNoPointToAnySeries()
    {
        var result = await GetDeudaAsync(Leg.Ok(DeudaRealJson), Leg.Ok(HistoricasZeroPeriodoJson));

        string[] expected = ["202607", "202605"];

        // Kept as a point it would carry situaciónMaxima 0, and the trend adjustment would read any debt
        // today as a one-step worsening from a month that simply had none.
        Assert.Equal(expected, result.HistorialSituaciones!.Select(p => p.Periodo).ToArray());
        Assert.Equal(expected, result.HistorialMontos!.Select(p => p.Periodo).ToArray());
        Assert.Equal(expected, result.HistorialMontosPorSituacion!.Select(p => p.Periodo).ToArray());
    }

    [Fact]
    public async Task AMixedPeriodo_IgnoresItsSituacionZeroEntidadInEverySeries()
    {
        var result = await GetDeudaAsync(Leg.Ok(DeudaRealJson), Leg.Ok(HistoricasZeroPeriodoJson));

        Assert.Equal(3, Assert.Single(result.HistorialSituaciones!, p => p.Periodo == "202605").SituacionMaxima);
        Assert.Equal(40m, Assert.Single(result.HistorialMontos!, p => p.Periodo == "202605").MontoTotal);

        var fila = Assert.Single(result.HistorialMontosPorSituacion!, p => p.Periodo == "202605");
        Assert.Equal(10m, fila.MontoSituacion1);
        Assert.Equal(30m, fila.MontoSituacion23);
        Assert.Equal( 0m, fila.MontoSituacion46);
    }

    [Fact]
    public async Task ADeudaPeriodoNewerThanHistoricasFirst_IsAddedOnceAtTheHead()
    {
        var result = await GetDeudaAsync(Leg.Ok(DeudaNewerJson), Leg.Ok(HistoricasRealJson));

        string[] expected = ["202608", .. RealTotals.Select(t => t.Periodo)];

        Assert.Equal(expected, result.HistorialMontos!.Select(p => p.Periodo).ToArray());
        Assert.Equal(expected, result.HistorialSituaciones!.Select(p => p.Periodo).ToArray());
        Assert.Equal(500m, result.HistorialMontos![0].MontoTotal);
        Assert.Equal(2, result.HistorialSituaciones![0].SituacionMaxima);
    }

    [Fact]
    public async Task APeriodoInBothEndpoints_AppearsOnce_WithTheDeudasValues()
    {
        var result = await GetDeudaAsync(Leg.Ok(DeudaRealJson), Leg.Ok(HistoricasDisagreeingJson));

        Assert.Equal(["202607", "202606"], result.HistorialMontos!.Select(p => p.Periodo).ToArray());
        Assert.Equal(11572449m, result.HistorialMontos![0].MontoTotal);
        Assert.Equal(1, result.HistorialSituaciones![0].SituacionMaxima);
        Assert.Equal(2m, result.HistorialMontos![1].MontoTotal);
    }

    [Fact]
    public async Task ThirtyHistoricasPeriodos_AreCappedAt24And12_KeepingTheNewest()
    {
        var result = await GetDeudaAsync(
            Leg.Ok(Deuda202606Json), Leg.Ok(BuildHistoricasWithConsecutivePeriodos(30)));

        Assert.Equal(24, result.HistorialMontos!.Count);
        Assert.Equal(24, result.HistorialMontosPorSituacion!.Count);
        Assert.Equal(12, result.HistorialSituaciones!.Count);

        Assert.Equal("202606", result.HistorialMontos![0].Periodo);
        Assert.Equal("202407", result.HistorialMontos![^1].Periodo);
        Assert.Equal("202507", result.HistorialSituaciones![^1].Periodo);

        // Newest month's monto is 30 (the Deudas período, identical here); the 24th newest is 7.
        Assert.Equal(30m, result.HistorialMontos![0].MontoTotal);
        Assert.Equal( 7m, result.HistorialMontos![^1].MontoTotal);
    }

    // ── The history leg failing never touches the Deudas answer ──────────────

    public static TheoryData<string> HistoricasFailures =>
        ["404", "500", "throws", "timeout", "malformed", "empty"];

    private static Leg HistoricasFailure(string kind) => kind switch
    {
        "404"       => Leg.Fails(HttpStatusCode.NotFound),
        "500"       => Leg.Fails(HttpStatusCode.InternalServerError),
        // Not HttpRequestException: only FetchWithStatusAsync's backstop can absorb this one.
        "throws"    => Leg.Throwing(new InvalidOperationException("historicas leg exploded")),
        // The real timeout signal on BcraClient: the resilience pipeline's 40 s total cap.
        "timeout"   => Leg.Throwing(new TimeoutRejectedException("historicas timed out")),
        "malformed" => Leg.Ok("{\"status\":200,\"results\":{\"periodos\":[{\"periodo\":"),
        "empty"     => Leg.Ok(NoPeriodosJson),
        _           => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    [Theory]
    [MemberData(nameof(HistoricasFailures))]
    public async Task HistoricasFailing_LeavesEstadoAndCurrentPeriodoUnchanged(string kind)
    {
        var baseline = await GetDeudaAsync(Leg.Ok(DeudaRealJson), Leg.Ok(HistoricasRealJson));
        var result   = await GetDeudaAsync(Leg.Ok(DeudaRealJson), HistoricasFailure(kind));

        Assert.Equal(BcraDeudaConsultaEstado.DebtFound, result.Estado);
        Assert.Equal(baseline.SituacionMaxima,        result.SituacionMaxima);
        Assert.Equal(baseline.MontoTotal,             result.MontoTotal);
        Assert.Equal(baseline.PeriodoConsultado,      result.PeriodoConsultado);
        Assert.Equal(baseline.MaxDiasAtraso,          result.MaxDiasAtraso);
        Assert.Equal(baseline.TieneProcesoJudicial,   result.TieneProcesoJudicial);
        Assert.Equal(baseline.TieneSituacionJuridica, result.TieneSituacionJuridica);
        Assert.Equal(baseline.EnRevision,             result.EnRevision);
        Assert.Equal(baseline.EntidadesInformantes,   result.EntidadesInformantes);
        Assert.Equal(
            baseline.EntidadesDetalle!.Select(e => (e.Entidad, e.Situacion, e.Monto)).ToArray(),
            result.EntidadesDetalle!.Select(e => (e.Entidad, e.Situacion, e.Monto)).ToArray());
    }

    [Theory]
    [MemberData(nameof(HistoricasFailures))]
    public async Task HistoricasFailing_SeriesFallBackToTheDeudasPeriodo(string kind)
    {
        var result = await GetDeudaAsync(Leg.Ok(DeudaRealJson), HistoricasFailure(kind));

        Assert.Equal("202607", Assert.Single(result.HistorialSituaciones!).Periodo);
        Assert.Equal(11572449m, Assert.Single(result.HistorialMontos!).MontoTotal);
        Assert.Equal(11572449m, Assert.Single(result.HistorialMontosPorSituacion!).MontoSituacion1);
    }

    // ── Caching ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ATransientHistoricasFailure_IsNotCached_SoTheNextCallRetriesAndGetsTheHistory()
    {
        var handler = new DeudaLegsHandler(Leg.Ok(DeudaRealJson), Leg.Fails(HttpStatusCode.ServiceUnavailable));
        var sut     = BuildSut(handler);

        var first = await sut.GetDeudaAsync(Cuit, CancellationToken.None);
        Assert.Single(first.HistorialMontos!);

        handler.Historicas = Leg.Ok(HistoricasRealJson);
        var second = await sut.GetDeudaAsync(Cuit, CancellationToken.None);

        Assert.Equal(2, handler.HistoricasCalls);
        Assert.Equal(2, handler.DeudaCalls);
        Assert.Equal(8, second.HistorialMontos!.Count);
    }

    [Fact]
    public async Task ANotFoundHistoricas_IsADeterministicAnswer_AndTheResultIsCached()
    {
        var handler = new DeudaLegsHandler(Leg.Ok(DeudaRealJson), Leg.Fails(HttpStatusCode.NotFound));
        var sut     = BuildSut(handler);

        await sut.GetDeudaAsync(Cuit, CancellationToken.None);
        await sut.GetDeudaAsync(Cuit, CancellationToken.None);

        Assert.Equal(1, handler.HistoricasCalls);
        Assert.Equal(1, handler.DeudaCalls);
    }

    [Fact]
    public async Task ASuccessfulHistory_IsCached()
    {
        var handler = new DeudaLegsHandler(Leg.Ok(DeudaRealJson), Leg.Ok(HistoricasRealJson));
        var sut     = BuildSut(handler);

        await sut.GetDeudaAsync(Cuit, CancellationToken.None);
        var second = await sut.GetDeudaAsync(Cuit, CancellationToken.None);

        Assert.Equal(1, handler.HistoricasCalls);
        Assert.Equal(8, second.HistorialMontos!.Count);
    }

    // ── Deudas still decides the outcome ─────────────────────────────────────

    [Fact]
    public async Task DeudasFailing_IsLookupFailed_EvenWhenHistoricasAnswers()
    {
        var result = await GetDeudaAsync(Leg.Fails(HttpStatusCode.InternalServerError), Leg.Ok(HistoricasRealJson));

        Assert.Equal(BcraDeudaConsultaEstado.LookupFailed, result.Estado);
        Assert.Empty(result.HistorialMontos!);
        Assert.Null(result.SituacionMaxima);
    }

    // ── Deudas 404: no debt, not an outage ────────────────────────────────────

    [Fact]
    public async Task ADeudas404_IsNoDebtReported_WithEmptySeries()
    {
        var result = await GetDeudaAsync(Leg.Fails(HttpStatusCode.NotFound), Leg.Fails(HttpStatusCode.NotFound));

        Assert.Equal(BcraDeudaConsultaEstado.NoDebtReported, result.Estado);
        Assert.Equal(30716327279L, result.Identificacion);
        Assert.Null(result.Denominacion);
        Assert.Null(result.SituacionMaxima);
        Assert.NotEqual(default, result.FetchedAt);

        // Empty, never null — the same shape the 200-with-no-períodos path has always produced.
        Assert.Empty(result.EntidadesInformantes!);
        Assert.Empty(result.EntidadesDetalle!);
        Assert.Empty(result.HistorialSituaciones!);
        Assert.Empty(result.HistorialMontos!);
        Assert.Empty(result.HistorialMontosPorSituacion!);
    }

    [Fact]
    public async Task ADeudas404_IsCached()
    {
        var handler = new DeudaLegsHandler(Leg.Fails(HttpStatusCode.NotFound), Leg.Fails(HttpStatusCode.NotFound));
        var sut     = BuildSut(handler);

        await sut.GetDeudaAsync(Cuit, CancellationToken.None);
        var second = await sut.GetDeudaAsync(Cuit, CancellationToken.None);

        Assert.Equal(BcraDeudaConsultaEstado.NoDebtReported, second.Estado);
        Assert.Equal(1, handler.DeudaCalls);

        // Historicas IS asked once on this path (a CUIT with no debt today may have had some before),
        // and its 404 is a deterministic answer, so the second call is served from the cache.
        Assert.Equal(1, handler.HistoricasCalls);
    }

    // ── NoDebtReported WITH a history (the CUATROVIENTOS case) ───────────────

    public static TheoryData<string> NoDebtExits => ["404", "noPeriodos"];

    private static Leg NoDebtDeudas(string exit) => exit switch
    {
        "404"        => Leg.Fails(HttpStatusCode.NotFound),
        "noPeriodos" => Leg.Ok(NoPeriodosJson),
        _            => throw new ArgumentOutOfRangeException(nameof(exit), exit, null)
    };

    [Theory]
    [MemberData(nameof(NoDebtExits))]
    public async Task NoDebtReported_WithAHistory_FillsTheThreeSeries_AndKeepsTheCurrentPeriodoEmpty(string exit)
    {
        var result = await GetDeudaAsync(NoDebtDeudas(exit), Leg.Ok(HistoricasRealJson));

        // Deudas decided: no debt now. Nothing describing the current período may change.
        Assert.Equal(BcraDeudaConsultaEstado.NoDebtReported, result.Estado);
        Assert.Equal(30716327279L, result.Identificacion);
        Assert.Null(result.SituacionMaxima);
        Assert.Null(result.MontoTotal);
        Assert.Null(result.PeriodoConsultado);
        Assert.Empty(result.EntidadesInformantes!);
        Assert.Empty(result.EntidadesDetalle!);
        Assert.False(result.TieneProcesoJudicial);
        Assert.False(result.TieneSituacionJuridica);
        Assert.False(result.EnRevision);
        Assert.Equal(0, result.MaxDiasAtraso);

        // The past 24 months, built exactly as on DebtFound: same períodos, newest first, same totals.
        var expected = RealTotals.Select(t => t.Periodo).ToArray();
        Assert.Equal(expected, result.HistorialSituaciones!.Select(p => p.Periodo).ToArray());
        Assert.Equal(expected, result.HistorialMontosPorSituacion!.Select(p => p.Periodo).ToArray());
        Assert.Equal(RealTotals, result.HistorialMontos!.Select(p => (p.Periodo, p.MontoTotal)).ToArray());
    }

    [Fact]
    public async Task ADeudas404_WithAPastSituacion3_CarriesItInEverySeries()
    {
        var result = await GetDeudaAsync(Leg.Fails(HttpStatusCode.NotFound), Leg.Ok(HistoricasZeroPeriodoJson));

        Assert.Equal(BcraDeudaConsultaEstado.NoDebtReported, result.Estado);
        Assert.Null(result.SituacionMaxima);

        // 202606 owed nothing (situación 0 only) and adds no point, exactly as on DebtFound.
        string[] expected = ["202607", "202605"];
        Assert.Equal(expected, result.HistorialSituaciones!.Select(p => p.Periodo).ToArray());
        Assert.Equal(expected, result.HistorialMontos!.Select(p => p.Periodo).ToArray());

        Assert.Equal(3, Assert.Single(result.HistorialSituaciones!, p => p.Periodo == "202605").SituacionMaxima);
        Assert.Equal(40m, Assert.Single(result.HistorialMontos!, p => p.Periodo == "202605").MontoTotal);

        var fila = Assert.Single(result.HistorialMontosPorSituacion!, p => p.Periodo == "202605");
        Assert.Equal(10m, fila.MontoSituacion1);
        Assert.Equal(30m, fila.MontoSituacion23);
        Assert.Equal( 0m, fila.MontoSituacion46);
    }

    [Fact]
    public async Task ADeudas200WithAnEmptyLatestPeriodo_WinsThatMonth_SoNoSeriesShowsDebtInIt()
    {
        var result = await GetDeudaAsync(Leg.Ok(DeudaNoEntidadesJson), Leg.Ok(HistoricasZeroPeriodoJson));

        Assert.Equal(BcraDeudaConsultaEstado.NoDebtReported, result.Estado);
        Assert.Equal("EMPRESA DE PRUEBA SA", result.Denominacion);
        Assert.Null(result.PeriodoConsultado);

        // Historicas lists debt in 202607, but Deudas reports that very período with no entidades.
        Assert.Equal("202605", Assert.Single(result.HistorialSituaciones!).Periodo);
        Assert.Equal("202605", Assert.Single(result.HistorialMontos!).Periodo);
        Assert.Equal("202605", Assert.Single(result.HistorialMontosPorSituacion!).Periodo);
    }

    [Fact]
    public async Task ADeudas404_WithThirtyHistoricasPeriodos_IsCappedAt24And12_KeepingTheNewest()
    {
        var result = await GetDeudaAsync(
            Leg.Fails(HttpStatusCode.NotFound), Leg.Ok(BuildHistoricasWithConsecutivePeriodos(30)));

        Assert.Equal(24, result.HistorialMontos!.Count);
        Assert.Equal(24, result.HistorialMontosPorSituacion!.Count);
        Assert.Equal(12, result.HistorialSituaciones!.Count);

        Assert.Equal("202606", result.HistorialMontos![0].Periodo);
        Assert.Equal("202407", result.HistorialMontos![^1].Periodo);
        Assert.Equal("202507", result.HistorialSituaciones![^1].Periodo);
        Assert.Equal(30m, result.HistorialMontos![0].MontoTotal);
        Assert.Equal( 7m, result.HistorialMontos![^1].MontoTotal);
    }

    // A deterministic Historicas answer would come back the same on a retry: plain NoDebtReported, cached.
    public static TheoryData<string, string> NoDebtExitsByDeterministicHistoricas => new()
    {
        { "404", "404" }, { "404", "malformed" }, { "404", "empty" },
        { "noPeriodos", "404" }, { "noPeriodos", "malformed" }, { "noPeriodos", "empty" },
    };

    [Theory]
    [MemberData(nameof(NoDebtExitsByDeterministicHistoricas))]
    public async Task NoDebtReported_WithNoUsableHistory_IsThePlainBlock_AndCached(string exit, string historicas)
    {
        var handler = new DeudaLegsHandler(NoDebtDeudas(exit), HistoricasFailure(historicas));
        var sut     = BuildSut(handler);

        var first  = await sut.GetDeudaAsync(Cuit, CancellationToken.None);
        var second = await sut.GetDeudaAsync(Cuit, CancellationToken.None);

        Assert.Equal(BcraDeudaConsultaEstado.NoDebtReported, first.Estado);
        Assert.Empty(first.HistorialSituaciones!);
        Assert.Empty(first.HistorialMontos!);
        Assert.Empty(first.HistorialMontosPorSituacion!);

        Assert.Same(first, second);
        Assert.Equal(1, handler.DeudaCalls);
        Assert.Equal(1, handler.HistoricasCalls);
    }

    // A transient Historicas failure: the current-debt answer was real, so never LookupFailed — but the
    // block is not cached for 24 h, so the next refresh asks again and fills the history.
    public static TheoryData<string, string> NoDebtExitsByTransientHistoricas => new()
    {
        { "404", "500" }, { "404", "503" }, { "404", "429" }, { "404", "timeout" }, { "404", "reset" },
        { "noPeriodos", "500" }, { "noPeriodos", "503" }, { "noPeriodos", "429" },
        { "noPeriodos", "timeout" }, { "noPeriodos", "reset" },
    };

    private static Leg TransientHistoricas(string kind) => kind switch
    {
        "500"     => Leg.Fails(HttpStatusCode.InternalServerError),
        "503"     => Leg.Fails(HttpStatusCode.ServiceUnavailable),
        "429"     => Leg.Fails(HttpStatusCode.TooManyRequests),
        "timeout" => Leg.Throwing(new TimeoutRejectedException("historicas timed out")),
        // The 2026-10-19 signature: HttpRequestException → IOException → SocketException(reset).
        "reset"   => Leg.Throwing(new HttpRequestException("An error occurred while sending the request.",
                         new IOException("Connection reset by peer.",
                             new SocketException((int)SocketError.ConnectionReset)))),
        _         => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    [Theory]
    [MemberData(nameof(NoDebtExitsByTransientHistoricas))]
    public async Task NoDebtReported_WithATransientHistoricasFailure_IsThePlainBlock_NotCached(string exit, string historicas)
    {
        var handler = new DeudaLegsHandler(NoDebtDeudas(exit), TransientHistoricas(historicas));
        var sut     = BuildSut(handler);

        var first = await sut.GetDeudaAsync(Cuit, CancellationToken.None);

        Assert.Equal(BcraDeudaConsultaEstado.NoDebtReported, first.Estado);
        Assert.Empty(first.HistorialSituaciones!);
        Assert.Empty(first.HistorialMontos!);
        Assert.Empty(first.HistorialMontosPorSituacion!);

        handler.Historicas = Leg.Ok(HistoricasRealJson);
        var second = await sut.GetDeudaAsync(Cuit, CancellationToken.None);

        Assert.Equal(2, handler.DeudaCalls);
        Assert.Equal(2, handler.HistoricasCalls);
        Assert.Equal(BcraDeudaConsultaEstado.NoDebtReported, second.Estado);
        Assert.Equal(8, second.HistorialMontos!.Count);
    }

    [Fact]
    public async Task NoDebtReported_WithAHistory_IsCached()
    {
        var handler = new DeudaLegsHandler(Leg.Fails(HttpStatusCode.NotFound), Leg.Ok(HistoricasRealJson));
        var sut     = BuildSut(handler);

        await sut.GetDeudaAsync(Cuit, CancellationToken.None);
        var second = await sut.GetDeudaAsync(Cuit, CancellationToken.None);

        Assert.Equal(1, handler.DeudaCalls);
        Assert.Equal(1, handler.HistoricasCalls);
        Assert.Equal(8, second.HistorialMontos!.Count);
    }

    public static TheoryData<string> DeudasFailures =>
        ["500", "429", "400", "throws", "timeout"];

    private static Leg DeudasFailure(string kind) => kind switch
    {
        "500"     => Leg.Fails(HttpStatusCode.InternalServerError),
        "429"     => Leg.Fails(HttpStatusCode.TooManyRequests),
        "400"     => Leg.Fails(HttpStatusCode.BadRequest),
        "throws"  => Leg.Throwing(new HttpRequestException("deudas leg unreachable")),
        // The real timeout signal on BcraClient: the resilience pipeline's 40 s total cap.
        "timeout" => Leg.Throwing(new TimeoutRejectedException("deudas timed out")),
        _         => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    [Theory]
    [MemberData(nameof(DeudasFailures))]
    public async Task ADeudasFailureOtherThan404_IsLookupFailed_AndCachedOnlyForTheFailureWindow(string kind)
    {
        // The 404 body rides every Leg.Fails; only the status may decide the outcome.
        var handler = new DeudaLegsHandler(DeudasFailure(kind), Leg.Fails(HttpStatusCode.NotFound));
        var clock   = new BcraTestClock();
        var sut     = BcraServiceTestFactory.Create(new StubHttpClientFactory(handler), clock.CreateCache());

        var first  = await sut.GetDeudaAsync(Cuit, CancellationToken.None);
        var second = await sut.GetDeudaAsync(Cuit, CancellationToken.None);

        // Within the window the honest failure is served from the cache: a repeated click during a
        // BCRA penalty must not send another request and extend it.
        Assert.Equal(BcraDeudaConsultaEstado.LookupFailed, first.Estado);
        Assert.Equal(BcraDeudaConsultaEstado.LookupFailed, second.Estado);
        Assert.Equal(1, handler.DeudaCalls);

        // Past it, the failure is NOT the 24 h answer a real result gets: the next call asks again.
        clock.Advance(TimeSpan.FromSeconds(61));
        var third = await sut.GetDeudaAsync(Cuit, CancellationToken.None);

        Assert.Equal(BcraDeudaConsultaEstado.LookupFailed, third.Estado);
        Assert.Equal(2, handler.DeudaCalls);
        Assert.Equal(0, handler.HistoricasCalls);
    }

    [Fact]
    public async Task ADeudas200WithNoPeriodos_IsStillNoDebtReported_AndCached()
    {
        var handler = new DeudaLegsHandler(Leg.Ok(NoPeriodosJson), Leg.Fails(HttpStatusCode.NotFound));
        var sut     = BuildSut(handler);

        var first = await sut.GetDeudaAsync(Cuit, CancellationToken.None);
        await sut.GetDeudaAsync(Cuit, CancellationToken.None);

        Assert.Equal(BcraDeudaConsultaEstado.NoDebtReported, first.Estado);
        Assert.Equal(30716327279L, first.Identificacion);
        Assert.Equal("EMPRESA DE PRUEBA SA", first.Denominacion);
        Assert.Empty(first.HistorialMontos!);
        Assert.Equal(1, handler.DeudaCalls);
    }

    // The point of the fix, end to end: the block a Deudas 404 produces must reach the engine as a
    // real "no debt" answer — BCRA kept in the composite at its optimal base — not as an outage that
    // excludes it and reweights the rest.
    [Fact]
    public async Task ADeudas404_ScoresLikeNoDebt_NotAsAnExcludedBcra()
    {
        var deuda = await GetDeudaAsync(Leg.Fails(HttpStatusCode.NotFound), Leg.Fails(HttpStatusCode.NotFound));

        var scoring  = await ScoringTestHarness.BuildAsync();
        var result   = await ScoringTestHarness.Score(
            scoring, ScoringTestHarness.BaselineRatios(), new BcraData { Deuda = deuda }, ScoringTestHarness.MacroBenign());
        var expected = await ScoringTestHarness.Score(
            scoring, ScoringTestHarness.BaselineRatios(), ScoringTestHarness.BcraNoDebt(), ScoringTestHarness.MacroBenign());

        Assert.False(result.BcraExcluded);
        Assert.Equal(0m, result.ScoreBcra);
        Assert.Equal(expected.Score, result.Score);
    }

    // The history this change adds must not move the score: a 404 whose Historicas carries a past
    // situación 3 scores exactly as the plain no-debt block — no trend, no penalty, same composite.
    // Whether past situaciones SHOULD weigh on a company with no current debt is a separate product
    // decision this does not take.
    [Fact]
    public async Task ADeudas404_WithAPastSituacion3_ScoresExactlyLikeNoDebt()
    {
        var deuda = await GetDeudaAsync(Leg.Fails(HttpStatusCode.NotFound), Leg.Ok(HistoricasZeroPeriodoJson));
        Assert.NotEmpty(deuda.HistorialSituaciones!);

        var scoring  = await ScoringTestHarness.BuildAsync();
        var result   = await ScoringTestHarness.Score(
            scoring, ScoringTestHarness.BaselineRatios(), new BcraData { Deuda = deuda }, ScoringTestHarness.MacroBenign());
        var expected = await ScoringTestHarness.Score(
            scoring, ScoringTestHarness.BaselineRatios(), ScoringTestHarness.BcraNoDebt(), ScoringTestHarness.MacroBenign());

        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(result));
        Assert.Equal(0,  result.BcraTrendDelta);
        Assert.Equal(0m, result.BcraTrendAdj);
    }
}
