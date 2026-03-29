using Azure;
using Azure.Data.Tables;

namespace LicensePlateBot.Models;

public class TripState : ITableEntity
{
    // PartitionKey = chat ID, RowKey = "currentTrip"
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = "currentTrip";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string TripName { get; set; } = "Road Trip";
    public string SeenStatesJson { get; set; } = "[]";  // JSON array of state abbreviations
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? PendingCommand { get; set; }  // conversational state, e.g. "saw"
}
