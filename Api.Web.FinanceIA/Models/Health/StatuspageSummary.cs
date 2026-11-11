// Project : Api.Web.FinanceIA
// Created : 2026-06-07

using System.Text.Json.Serialization;

namespace Api.Web.FinanceIA.Models.Health;

internal sealed class StatuspageSummary
{
    [JsonPropertyName("status")]
    public StatuspageStatus Status { get; set; } = new();

    [JsonPropertyName("components")]
    public List<StatuspageComponent> Components { get; set; } = [];

    [JsonPropertyName("incidents")]
    public List<StatuspageIncident> Incidents { get; set; } = [];
}

internal sealed class StatuspageStatus
{
    [JsonPropertyName("indicator")]
    public string Indicator { get; set; } = "none";

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

internal sealed class StatuspageComponent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

internal sealed class StatuspageIncident
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("impact")]
    public string Impact { get; set; } = string.Empty;

    [JsonPropertyName("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    [JsonPropertyName("incident_updates")]
    public List<StatuspageIncidentUpdate> IncidentUpdates { get; set; } = [];
}

internal sealed class StatuspageIncidentUpdate
{
    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTime? CreatedAt { get; set; }
}
