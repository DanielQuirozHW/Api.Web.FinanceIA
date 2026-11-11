// Project : Api.Web.FinanceIA.Tests
// Created : 2026-11-05

using System.Text;
using Api.Web.FinanceIA.Servicios.Privacy;
using Xunit;

namespace Api.Web.FinanceIA.Tests.Servicios.Privacy;

/// <summary>
/// K5: <see cref="EntityIdentifierRedactor"/> strips the evaluated company's identity out of what the chat
/// sends Claude. Every name and CUIT here is SYNTHETIC — never a real company or person.
/// </summary>
public sealed class EntityIdentifierRedactorTests
{
    private const string Name = "Ejemplo Agro S.R.L.";
    private const string Cuit = "30-99887766-5";

    private static EntityIdentifierRedactor Redactor(
        string[]? companies = null,
        string[]? related   = null,
        string[]? persons   = null,
        string[]? cuits     = null)
    {
        var identifiers = new EntityIdentifiers(
            companies ?? [Name],
            related   ?? [],
            persons   ?? [],
            cuits     ?? [Cuit]);

        Assert.True(EntityIdentifierRedactor.TryCreate(identifiers, out var redactor));
        return redactor!;
    }

    private static (string Text, RedactionCounts Counts) Redact(EntityIdentifierRedactor redactor, string text)
    {
        Assert.True(redactor.TryRedact(text, out var redacted, out var counts));
        return (redacted, counts);
    }

    // ── Company name ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Ejemplo Agro S.R.L.")]
    [InlineData("Ejemplo Agro SRL")]
    [InlineData("Ejemplo Agro S. R. L.")]
    [InlineData("Ejemplo Agro S.R.L")]
    [InlineData("Ejemplo Agro, S.R.L.")]
    [InlineData("Ejemplo Agro")]
    [InlineData("EJEMPLO AGRO SRL")]
    [InlineData("ejemplo agro s.r.l.")]
    [InlineData("Ejemplo   Agro  S.R.L.")]
    [InlineData("Ejemplo-Agro S.R.L.")]
    [InlineData("Ejemplo Agro S.A.")]
    public void Replaces_the_name_in_every_spelling_of_its_legal_form_case_and_spacing(string spelling)
    {
        var (text, counts) = Redact(Redactor(), $"El balance de {spelling} muestra liquidez.");

        Assert.Equal("El balance de la empresa muestra liquidez.", text);
        Assert.Equal(1, counts.Names);
        Assert.Equal(0, counts.Cuits);
    }

    [Theory]
    [InlineData("Éjemplo Agró S.R.L.")]
    [InlineData("EJÉMPLO ÁGRO")]
    public void Is_accent_insensitive_when_the_text_carries_accents(string spelling)
    {
        var (text, _) = Redact(Redactor(), $"Datos de {spelling} al cierre.");

        Assert.Equal("Datos de la empresa al cierre.", text);
    }

    [Fact]
    public void Is_accent_insensitive_when_the_stored_name_carries_them()
    {
        var redactor = Redactor(companies: ["Agropecuaria Peña Ñandú S.A."]);

        var (text, counts) = Redact(redactor, "Informe de AGROPECUARIA PENA NANDU SA.");

        Assert.Equal("Informe de la empresa.", text);
        Assert.Equal(1, counts.Names);
    }

    [Fact]
    public void Accepts_a_decomposed_accent_in_the_text_and_keeps_the_rest_byte_identical()
    {
        // "Ágro" written as A + combining acute.
        var input = "Antes — Ejemplo Ágro S.R.L. — después ✓";

        var (text, _) = Redact(Redactor(), input);

        Assert.Equal("Antes — la empresa — después ✓", text);
    }

    [Fact]
    public void Capitalises_the_placeholder_at_the_start_of_a_sentence_and_keeps_the_period()
    {
        var input = "Ejemplo Agro S.R.L. es una sociedad agropecuaria. Opera con Ejemplo Agro S.R.L. La deuda es baja.\n" +
                    "Ejemplo Agro tiene dos plantas.";

        var (text, counts) = Redact(Redactor(), input);

        Assert.Equal(
            "La empresa es una sociedad agropecuaria. Opera con la empresa. La deuda es baja.\n" +
            "La empresa tiene dos plantas.",
            text);
        Assert.Equal(3, counts.Names);
    }

    [Fact]
    public void The_registry_name_and_the_trade_name_are_matched_alongside_the_business_name()
    {
        var redactor = Redactor(companies: [Name, "EJEMPLO AGROPECUARIA SOCIEDAD DE RESPONSABILIDAD LIMITADA", "Campo Ejemplo"]);

        var (text, counts) = Redact(redactor,
            "Ejemplo Agropecuaria Sociedad de Responsabilidad Limitada, también Ejemplo Agro SRL, marca Campo Ejemplo.");

        Assert.Equal("La empresa, también la empresa, marca la empresa.", text);
        Assert.Equal(3, counts.Names);
    }

    [Fact]
    public void A_name_that_contains_another_listed_name_is_consumed_whole_and_counted_once()
    {
        var redactor = Redactor(companies: ["Ejemplo Agro S.R.L.", "Ejemplo Agro Industrial S.R.L."]);

        var (text, counts) = Redact(redactor, "Con Ejemplo Agro Industrial S.R.L. y Ejemplo Agro.");

        Assert.Equal("Con la empresa y la empresa.", text);
        Assert.Equal(2, counts.Names);
    }

    [Theory]
    [InlineData("El sector Agroindustrial de Ejemplos crece.")]
    [InlineData("Ejemplo de cálculo: agro y ganadería.")]
    [InlineData("EjemploAgro es otra cosa.")]
    public void Never_replaces_a_fragment_or_a_word_that_merely_contains_the_name(string input)
    {
        var (text, counts) = Redact(Redactor(), input);

        Assert.Same(input, text);
        Assert.Equal(RedactionCounts.None, counts);
    }

    [Fact]
    public void A_pronoun_after_the_name_is_not_read_as_a_legal_form()
    {
        var (text, _) = Redact(Redactor(), "Ejemplo Agro se encuentra al día.");

        Assert.Equal("La empresa se encuentra al día.", text);
    }

    [Fact]
    public void A_core_shorter_than_three_characters_matches_only_with_its_legal_form()
    {
        var redactor = Redactor(companies: ["AB S.A."]);

        var (bare, bareCounts)       = Redact(redactor, "Rubro ab y otros.");
        var (suffixed, suffixCounts) = Redact(redactor, "Socio de AB SA desde 2020.");

        Assert.Equal("Rubro ab y otros.", bare);
        Assert.Equal(0, bareCounts.Names);
        Assert.Equal("Socio de la empresa desde 2020.", suffixed);
        Assert.Equal(1, suffixCounts.Names);
    }

    [Fact]
    public void An_ampersand_and_a_y_are_the_same_word()
    {
        var redactor = Redactor(companies: ["Pérez & Gómez S.H."]);

        var (text, counts) = Redact(redactor, "Contrato con Perez y Gomez SH y con PÉREZ & GÓMEZ.");

        Assert.Equal("Contrato con la empresa y con la empresa.", text);
        Assert.Equal(2, counts.Names);
    }

    // ── Partners / authorities ────────────────────────────────────────────────

    [Theory]
    [InlineData("Juan Carlos Pérez")]
    [InlineData("Pérez, Juan Carlos")]
    [InlineData("PEREZ JUAN CARLOS")]
    [InlineData("Juan Pérez")]
    [InlineData("Carlos Perez")]
    public void Replaces_a_person_in_any_order_of_at_least_two_of_their_names(string spelling)
    {
        var redactor = Redactor(persons: ["PEREZ JUAN CARLOS"]);

        var (text, counts) = Redact(redactor, $"El socio gerente {spelling} firmó el balance.");

        Assert.Equal("El socio gerente [persona vinculada] firmó el balance.", text);
        Assert.Equal(1, counts.Persons);
        Assert.Equal(0, counts.Names);
    }

    [Theory]
    [InlineData("El señor Pérez firmó.")]
    [InlineData("Juan llegó tarde.")]
    [InlineData("Juan Juan firmó.")]
    public void Never_replaces_one_name_of_a_person_alone(string input)
    {
        var redactor = Redactor(persons: ["PEREZ JUAN CARLOS"]);

        var (text, counts) = Redact(redactor, input);

        Assert.Same(input, text);
        Assert.Equal(0, counts.Persons);
    }

    [Fact]
    public void Two_people_side_by_side_are_not_merged_into_one()
    {
        var redactor = Redactor(persons: ["PEREZ JUAN", "GOMEZ ANA MARIA"]);

        var (text, counts) = Redact(redactor, "Directores: Juan Pérez, Ana María Gómez.");

        Assert.Equal("Directores: [persona vinculada], [persona vinculada].", text);
        Assert.Equal(2, counts.Persons);
    }

    [Fact]
    public void A_connector_inside_a_name_is_allowed_between_two_of_its_tokens()
    {
        var redactor = Redactor(persons: ["DE LA SERNA MARIA"]);

        var (text, counts) = Redact(redactor, "Presidenta: María de la Serna.");

        Assert.Equal("Presidenta: [persona vinculada].", text);
        Assert.Equal(1, counts.Persons);
    }

    [Fact]
    public void A_related_legal_person_follows_the_company_rules_with_the_person_placeholder()
    {
        var redactor = Redactor(related: ["Inversora Ejemplo S.A."]);

        var (text, counts) = Redact(redactor, "Su accionista Inversora Ejemplo SA tiene el 60%.");

        Assert.Equal("Su accionista [persona vinculada] tiene el 60%.", text);
        Assert.Equal(1, counts.Persons);
        Assert.Equal(0, counts.Names);
    }

    // ── CUIT ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("30-99887766-5")]
    [InlineData("30998877665")]
    [InlineData("30 99887766 5")]
    [InlineData("30.99887766.5")]
    [InlineData("30–99887766–5")]
    public void Replaces_the_company_cuit_with_or_without_separators(string spelling)
    {
        var (text, counts) = Redact(Redactor(), $"CUIT {spelling}, inscripta.");

        Assert.Equal("CUIT [CUIT], inscripta.", text);
        Assert.Equal(1, counts.Cuits);
    }

    [Fact]
    public void Replaces_any_other_cuit_written_with_dashes()
    {
        var (text, counts) = Redact(Redactor(), "Fiador 20-11223344-6 y proveedor 33-55667788-9.");

        Assert.Equal("Fiador [CUIT] y proveedor [CUIT].", text);
        Assert.Equal(2, counts.Cuits);
    }

    [Theory]
    [InlineData("{\"ventas\":20112233446,\"costo\":1250000.5}")]
    [InlineData("Activo total: 30998877665.42 pesos.")]
    [InlineData("Pasivo: 309988776650 pesos.")]
    [InlineData("| Ene | 1.250.000 |")]
    public void Leaves_amounts_alone_even_when_they_have_eleven_digits(string input)
    {
        var (text, counts) = Redact(Redactor(), input);

        Assert.Same(input, text);
        Assert.Equal(0, counts.Cuits);
    }

    [Fact]
    public void A_cuit_given_as_bare_digits_is_matched_in_its_dashed_form()
    {
        var redactor = Redactor(cuits: ["20112233446"]);

        var (text, counts) = Redact(redactor, "Socio CUIT 20-11223344-6 y también 20112233446.");

        Assert.Equal("Socio CUIT [CUIT] y también [CUIT].", text);
        Assert.Equal(2, counts.Cuits);
    }

    // ── Counts, identity, robustness ──────────────────────────────────────────

    [Fact]
    public void Counts_each_kind_separately()
    {
        var redactor = Redactor(persons: ["PEREZ JUAN"]);

        var (text, counts) = Redact(redactor,
            "Ejemplo Agro S.R.L. (CUIT 30-99887766-5), socio Juan Pérez; Ejemplo Agro informa.");

        Assert.Equal("La empresa (CUIT [CUIT]), socio [persona vinculada]; la empresa informa.", text);
        Assert.Equal(new RedactionCounts(2, 1, 1), counts);
        Assert.Equal(4, counts.Total);
    }

    [Fact]
    public void Text_without_identifiers_comes_back_as_the_same_instance()
    {
        const string input = "La liquidez corriente es 1,8 y el endeudamiento 0,45.";

        var (text, counts) = Redact(Redactor(), input);

        Assert.Same(input, text);
        Assert.Equal(RedactionCounts.None, counts);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Null_and_empty_text_redact_to_empty(string? input)
    {
        Assert.True(Redactor().TryRedact(input, out var text, out var counts));
        Assert.Equal(string.Empty, text);
        Assert.Equal(RedactionCounts.None, counts);
    }

    [Fact]
    public void Blank_and_unusable_identifiers_still_build_a_redactor_that_changes_nothing_but_dashed_cuits()
    {
        Assert.True(EntityIdentifierRedactor.TryCreate(
            new EntityIdentifiers(["", "   ", "S.A."], [""], ["Pérez"], ["", "123"]), out var redactor));

        const string input = "Pérez S.A. informa ventas por 123.";
        Assert.True(redactor!.TryRedact(input, out var text, out _));
        Assert.Same(input, text);
    }

    [Fact]
    public void Null_identifiers_answer_false_instead_of_throwing()
    {
        Assert.False(EntityIdentifierRedactor.TryCreate(null!, out var redactor));
        Assert.Null(redactor);
    }

    [Fact]
    public void Never_throws_on_odd_unicode_or_a_large_input()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 4000; i++)
        {
            sb.Append("Ejemplo Agro S.R.L. 😀 ́́ \uD83D ﬁ ß İ ı Ǆ ");
            sb.Append(i).Append(' ').Append("30-99887766-5 ");
        }

        var input = sb.ToString();

        Assert.True(Redactor(persons: ["PEREZ JUAN"]).TryRedact(input, out var text, out var counts));
        Assert.DoesNotContain("Ejemplo Agro", text, StringComparison.Ordinal);
        Assert.DoesNotContain("30-99887766-5", text, StringComparison.Ordinal);
        Assert.Equal(4000, counts.Names);
        Assert.Equal(4000, counts.Cuits);
    }
}
