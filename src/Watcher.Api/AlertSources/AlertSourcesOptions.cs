namespace Watcher.Api.AlertSources;

public sealed record AlertsInUaOptions(string? Token);

public static class AlertSourceCatalog
{
    public const string UkraineAlarmCode = "ukrainealarm";
    public const string UkraineAlarmName = "UkraineAlarm";
    public const string UkraineAlarmAttributionUrl = "https://api.ukrainealarm.com/";
    public const string UkraineAlarmBaseUrl = "https://api.ukrainealarm.com/";
    public const string AlertsInUaCode = "alerts.in.ua";
    public const string AlertsInUaName = "alerts.in.ua";
    public const string AlertsInUaAttributionUrl = "https://alerts.in.ua/";
    public const string AlertsInUaBaseUrl = "https://api.alerts.in.ua/";
    public const string NeptunCode = "neptun";
    public const string NeptunName = "NEPTUN";
    public const string NeptunAttributionUrl = "https://neptun.in.ua/";
    public const string NeptunBaseUrl = "https://neptun.in.ua/";

    public const string PoltavaOblastId = "19";
    public const string PoltavaOblastName = "Полтавська область";
    public const string PoltavaOblastNeptunKey = "poltavska";
    public const string KremenchukHromadaId = "1088";
    public const string KremenchukHromadaName = "Кременчуцька громада";
    public const string KremenchukRaionId = "poltavska:kremenchutskyi";
    public const string KremenchukRaionName = "Кременчуцький район";
}
