using System.Net.Http.Json;
using System.Text.Json;
using LeakOps.Contracts;

namespace LeakOps.Agents;

/// <summary>Publishes incidents to the (fake) Waterstoring.nl ingest API. Posting the same incident id again replaces the incident, so retries are safe.</summary>
public class WaterstoringClient(HttpClient http, AgentSettings settings)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<WaterstoringIncident> PublishAsync(WaterstoringIncidentRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.WaterstoringApiKey))
            throw new InvalidOperationException("WATERSTORING_API_KEY is not configured.");

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.WaterstoringBaseUrl, "/api/incidents"))
        {
            Content = JsonContent.Create(request, options: Json),
        };
        message.Headers.Add("X-Api-Key", settings.WaterstoringApiKey);

        using var response = await http.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Waterstoring returned {(int)response.StatusCode}: {body}");
        }
        return (await response.Content.ReadFromJsonAsync<WaterstoringIncident>(Json, cancellationToken))!;
    }
}
