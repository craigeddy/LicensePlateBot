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
        var state = new TripState
        {
            PartitionKey = chatId.ToString(),
            TripName = tripName,
            SeenStatesJson = "[]",
            StartedAt = DateTimeOffset.UtcNow
        };
        await _tableClient.UpsertEntityAsync(state, TableUpdateMode.Replace);
    }

    public List<string> DeserializeStates(string json) =>
        JsonSerializer.Deserialize<List<string>>(json) ?? [];

    public string SerializeStates(List<string> states) =>
        JsonSerializer.Serialize(states);
}
