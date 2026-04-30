using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using LicensePlateBot.Models;
using Microsoft.Extensions.Configuration;

public class TripStateService
{
    private const string TableName = "TripStates";
    private readonly TableClient _tableClient;

    public TripStateService(IConfiguration config)
    {
        var connString = config["StorageConnectionString"]
            ?? throw new InvalidOperationException("StorageConnectionString is not configured.");

        var serviceClient = new TableServiceClient(connString);
        serviceClient.CreateTableIfNotExists(TableName);
        _tableClient = serviceClient.GetTableClient(TableName);
    }

    public async Task<TripState> GetOrCreateAsync(long chatId)
    {
        try
        {
            var response = await _tableClient.GetEntityAsync<TripState>(
                chatId.ToString(), "currentTrip");
            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return new TripState { PartitionKey = chatId.ToString() };
        }
    }

    public async Task SaveAsync(TripState state)
    {
        await _tableClient.UpsertEntityAsync(state, TableUpdateMode.Replace);
    }

    public async Task ResetAsync(long chatId, string tripName)
    {
        // Archive current trip if it has any states logged or any states skipped
        try
        {
            var response = await _tableClient.GetEntityAsync<TripState>(chatId.ToString(), "currentTrip");
            var existing = response.Value;
            if (DeserializeSightings(existing.SeenStatesJson).Count > 0 ||
                DeserializeSkippedStates(existing.SkippedStatesJson).Count > 0)
            {
                var archived = new TripState
                {
                    PartitionKey = chatId.ToString(),
                    RowKey = $"trip_{existing.StartedAt:yyyyMMddHHmmss}",
                    TripName = existing.TripName,
                    SeenStatesJson = existing.SeenStatesJson,
                    SkippedStatesJson = existing.SkippedStatesJson,
                    StartedAt = existing.StartedAt,
                    EndedAt = DateTimeOffset.UtcNow
                };
                await _tableClient.UpsertEntityAsync(archived, TableUpdateMode.Replace);
            }
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }

        var state = new TripState
        {
            PartitionKey = chatId.ToString(),
            TripName = tripName,
            SeenStatesJson = "[]",
            StartedAt = DateTimeOffset.UtcNow
        };
        await _tableClient.UpsertEntityAsync(state, TableUpdateMode.Replace);
    }

    public async Task<List<TripState>> GetHistoryAsync(long chatId)
    {
        var partitionKey = chatId.ToString();
        var history = new List<TripState>();

        var query = _tableClient.QueryAsync<TripState>(
            filter: $"PartitionKey eq '{partitionKey}' and RowKey ne 'currentTrip'");

        await foreach (var entity in query)
            history.Add(entity);

        return history.OrderByDescending(t => t.StartedAt).ToList();
    }

    public List<SightingRecord> DeserializeSightings(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<SightingRecord>>(json) ?? [];
        }
        catch
        {
            // Legacy format: plain JSON array of state abbreviation strings
            var states = JsonSerializer.Deserialize<List<string>>(json) ?? [];
            return states.Select(s => new SightingRecord(s, 0, string.Empty)).ToList();
        }
    }

    public string SerializeSightings(List<SightingRecord> sightings) =>
        JsonSerializer.Serialize(sightings);

    public List<string> DeserializeSkippedStates(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "[]") return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public string SerializeSkippedStates(List<string> states) =>
        JsonSerializer.Serialize(states);
}
