using System.Text;
using Watcher.Api.AlertSources;

namespace Watcher.UnitTests.AlertSources;

public sealed class NeptunAlertSnapshotParserTests
{
    [Fact]
    public void Rest_snapshot_maps_only_known_oblast_and_raion_keys()
    {
        const string body = """
            {
              "raions": [
                { "key": "poltavska:kremenchutskyi", "name": "Кременчуцький район", "oblast": "Полтавська область" },
                { "key": "odeska:odeskyi", "name": "Одеський район", "oblast": "Одеська область" }
              ],
              "oblasts": [
                { "key": "poltavska", "name": "Полтавська область", "oblast": "" }
              ]
            }
            """;

        var parsed = NeptunAlertSnapshotParser.TryParseRest(body, out NeptunAlertSnapshot snapshot);

        Assert.True(parsed);
        Assert.Equal(AlertState.Active, snapshot.OblastState);
        Assert.Equal(AlertState.Active, snapshot.RaionState);
    }

    [Fact]
    public void Valid_empty_arrays_mean_no_alerts_for_supported_scopes()
    {
        const string body = "{ \"raions\": [], \"oblasts\": [] }";

        var parsed = NeptunAlertSnapshotParser.TryParseRest(body, out NeptunAlertSnapshot snapshot);

        Assert.True(parsed);
        Assert.Equal(AlertState.Inactive, snapshot.OblastState);
        Assert.Equal(AlertState.Inactive, snapshot.RaionState);
    }

    [Theory]
    [InlineData("{\"raions\":[],\"oblasts\":null}")]
    [InlineData("{\"raions\":[]}")]
    [InlineData("{\"raions\":[{\"key\":\"poltavska:kremenchutskyi\",\"name\":\"Кременчуцький район\"}],\"oblasts\":[]}")]
    [InlineData("{\"raions\":[{\"key\":\"poltavska:kremenchutskyi\",\"name\":\"Кременчуцький район\",\"oblast\":\"Полтавська область\"},{\"key\":\"poltavska:kremenchutskyi\",\"name\":\"Дублікат\",\"oblast\":\"Полтавська область\"}],\"oblasts\":[]}")]
    [InlineData("{invalid-json")]
    public void Malformed_rest_data_is_not_an_inactive_snapshot(string body)
    {
        Assert.False(NeptunAlertSnapshotParser.TryParseRest(body, out _));
    }

    [Fact]
    public void Familiar_location_name_with_an_unknown_key_invalidates_the_snapshot()
    {
        const string body = """
            {
              "raions": [
                { "key": "poltavska:unknown", "name": "Кременчуцький район", "oblast": "Полтавська область" }
              ],
              "oblasts": []
            }
            """;

        Assert.False(NeptunAlertSnapshotParser.TryParseRest(body, out _));
    }

    [Fact]
    public void Alerts_envelope_replaces_both_normalized_statuses()
    {
        const string envelope = """
            {
              "type": "alerts",
              "ts": "2026-11-05T12:00:00.000Z",
              "data": {
                "raions": [],
                "oblasts": [
                  { "key": "poltavska", "name": "Полтавська область", "oblast": "" }
                ]
              }
            }
            """;

        NeptunRealtimeFrame frame = NeptunAlertSnapshotParser.ParseRealtimeFrame(Encoding.UTF8.GetBytes(envelope));

        Assert.Equal(NeptunRealtimeFrameKind.Alerts, frame.Kind);
        Assert.Equal(AlertState.Active, frame.AlertSnapshot?.OblastState);
        Assert.Equal(AlertState.Inactive, frame.AlertSnapshot?.RaionState);
    }

    [Fact]
    public void Threat_snapshot_is_valid_but_does_not_change_alert_status()
    {
        const string envelope = "{ \"type\": \"snapshot\", \"ts\": \"2026-11-05T12:00:00Z\", \"data\": { \"threats\": [] } }";

        NeptunRealtimeFrame frame = NeptunAlertSnapshotParser.ParseRealtimeFrame(Encoding.UTF8.GetBytes(envelope));

        Assert.Equal(NeptunRealtimeFrameKind.Snapshot, frame.Kind);
        Assert.Null(frame.AlertSnapshot);
    }

    [Fact]
    public void Heartbeat_does_not_replace_alert_snapshot()
    {
        NeptunRealtimeFrame frame = NeptunAlertSnapshotParser.ParseRealtimeFrame(
            Encoding.UTF8.GetBytes("{ \"type\": \"heartbeat\", \"ts\": \"2026-11-05T12:00:00Z\" }"));

        Assert.Equal(NeptunRealtimeFrameKind.Heartbeat, frame.Kind);
        Assert.Null(frame.AlertSnapshot);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"type\":\"alerts\",\"data\":{\"raions\":[]}}")]
    [InlineData("{\"type\":\"future-event\",\"data\":{}}")]
    public void Invalid_or_unknown_frame_cannot_clear_current_alerts(string envelope)
    {
        NeptunRealtimeFrame frame = NeptunAlertSnapshotParser.ParseRealtimeFrame(Encoding.UTF8.GetBytes(envelope));

        Assert.Equal(NeptunRealtimeFrameKind.Invalid, frame.Kind);
        Assert.Null(frame.AlertSnapshot);
    }
}
