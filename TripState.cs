using Azure;
using Azure.Data.Tables;

namespace LicensePlateBot.Models;

public record SightingRecord(string State, long UserId, string UserName);

public class TripState : ITableEntity
{
    // PartitionKey = chat ID, RowKey = "currentTrip" for active trip or "trip_<yyyyMMddHHmmss>" for archived trips
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = "currentTrip";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string TripName { get; set; } = "Road Trip";
    public string SeenStatesJson { get; set; } = "[]";  // JSON array of state abbreviations
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? PendingCommand { get; set; }  // conversational state, e.g. "saw"
    public DateTimeOffset? EndedAt { get; set; }  // set when trip is archived
    public string SkippedStatesJson { get; set; } = "[]";  // JSON array of skipped state abbreviations
}
