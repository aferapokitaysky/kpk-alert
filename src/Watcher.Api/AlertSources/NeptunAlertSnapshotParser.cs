using System.Text.Json;

namespace Watcher.Api.AlertSources;

internal readonly record struct NeptunAlertSnapshot(
    AlertState OblastState,
    AlertState RaionState);

internal enum NeptunRealtimeFrameKind
{
    Alerts,
    Heartbeat,
    Snapshot,
    Ignored,
    Invalid
}

internal readonly record struct NeptunRealtimeFrame(
    NeptunRealtimeFrameKind Kind,
    NeptunAlertSnapshot? AlertSnapshot = null);

internal static class NeptunAlertSnapshotParser
{
    public static bool TryParseRest(string body, out NeptunAlertSnapshot snapshot)
    {
        snapshot = default;
        try
        {
            using var document = JsonDocument.Parse(body);
            return TryParseAlertData(document.RootElement, out snapshot);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static NeptunRealtimeFrame ParseRealtimeFrame(ReadOnlyMemory<byte> utf8Json)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8Json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out JsonElement typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                return new(NeptunRealtimeFrameKind.Invalid);
            }

            var type = typeElement.GetString();
            return type switch
            {
                "alerts" => root.TryGetProperty("data", out JsonElement alertsData) &&
                    TryParseAlertData(alertsData, out NeptunAlertSnapshot snapshot)
                    ? new(NeptunRealtimeFrameKind.Alerts, snapshot)
                    : new(NeptunRealtimeFrameKind.Invalid),
                "heartbeat" => new(NeptunRealtimeFrameKind.Heartbeat),
                "snapshot" => root.TryGetProperty("data", out JsonElement snapshotData) &&
                    IsThreatSnapshot(snapshotData)
                    ? new(NeptunRealtimeFrameKind.Snapshot)
                    : new(NeptunRealtimeFrameKind.Invalid),
                "upsert" or "remove" => root.TryGetProperty("data", out JsonElement threatData) &&
                    threatData.ValueKind == JsonValueKind.Object
                    ? new(NeptunRealtimeFrameKind.Ignored)
                    : new(NeptunRealtimeFrameKind.Invalid),
                _ => new(NeptunRealtimeFrameKind.Invalid)
            };
        }
        catch (JsonException)
        {
            return new(NeptunRealtimeFrameKind.Invalid);
        }
    }

    private static bool TryParseAlertData(JsonElement root, out NeptunAlertSnapshot snapshot)
    {
        snapshot = default;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("oblasts", out JsonElement oblasts) ||
            oblasts.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("raions", out JsonElement raions) ||
            raions.ValueKind != JsonValueKind.Array ||
            !TryReadLocations(oblasts, out IReadOnlyList<NeptunLocation> oblastEntries) ||
            !TryReadLocations(raions, out IReadOnlyList<NeptunLocation> raionEntries))
        {
            return false;
        }

        NeptunLocation? activeOblast = oblastEntries.FirstOrDefault(location =>
            string.Equals(location.Key, AlertSourceCatalog.PoltavaOblastNeptunKey, StringComparison.Ordinal));
        NeptunLocation? activeRaion = raionEntries.FirstOrDefault(location =>
            string.Equals(location.Key, AlertSourceCatalog.KremenchukRaionId, StringComparison.Ordinal));
        var oblastNameWithoutKnownKey = activeOblast is null && oblastEntries.Any(location =>
            string.Equals(location.Name, AlertSourceCatalog.PoltavaOblastName, StringComparison.Ordinal));
        var raionNameWithoutKnownKey = activeRaion is null && raionEntries.Any(location =>
            string.Equals(location.Name, AlertSourceCatalog.KremenchukRaionName, StringComparison.Ordinal) &&
            string.Equals(location.Oblast, AlertSourceCatalog.PoltavaOblastName, StringComparison.Ordinal));
        if (oblastNameWithoutKnownKey || raionNameWithoutKnownKey)
        {
            return false;
        }

        snapshot = new(
            activeOblast is null ? AlertState.Inactive : AlertState.Active,
            activeRaion is null ? AlertState.Inactive : AlertState.Active);
        return true;
    }

    private static bool IsThreatSnapshot(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty("threats", out JsonElement threats) &&
        threats.ValueKind == JsonValueKind.Array;

    private static bool TryReadLocations(JsonElement array, out IReadOnlyList<NeptunLocation> locations)
    {
        List<NeptunLocation> found = [];
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("key", out JsonElement key) || key.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("oblast", out JsonElement oblast) || oblast.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(key.GetString()) || string.IsNullOrWhiteSpace(name.GetString()) ||
                !keys.Add(key.GetString()!))
            {
                locations = [];
                return false;
            }

            found.Add(new(key.GetString()!, name.GetString()!, oblast.GetString()!));
        }

        locations = found;
        return true;
    }

    private sealed record NeptunLocation(string Key, string Name, string Oblast);
}
