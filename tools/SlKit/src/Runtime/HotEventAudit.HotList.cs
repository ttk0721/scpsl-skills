namespace SlKit;

public static partial class HotEventAudit
{
    /// <summary>LabAPI version the built-in hot-event list is maintained for.</summary>
    public const string CatalogLabApiVersion = "1.1.7";

    /// <summary>Game version the built-in hot-event list is maintained for.</summary>
    public const string CatalogGameVersion = "14.2.7";

    // Maintained by hand for LabAPI 1.1.7 / game 14.2.7: 14 of 377 events. "Hot" means
    // per-frame, per-frame-per-pair or high-rate network traffic. Per-shot / per-hit events and
    // events that are only sometimes hot are NOT in this list: their rate follows a firefight or a
    // rare situation, not the frame.
    // The list mirrors the entries marked "hot": true in tools/catalog/labapi-events.json of the
    // scpsl-skills repository (meta.labapi 1.1.7, meta.game 14.2.7), in catalog order. To compare:
    //   jq -r '.events[] | select(.hot==true) | .event' tools/catalog/labapi-events.json
    private static readonly string[] HotEventNames =
    {
        "PlayerEvents.Escaping",
        "PlayerEvents.IdlingTesla",
        "PlayerEvents.ReceivingVoiceMessage",
        "PlayerEvents.SendingVoiceMessage",
        "PlayerEvents.StayingInHazard",
        "PlayerEvents.TriggeringTesla",
        "PlayerEvents.UsedRadio",
        "PlayerEvents.UsingIntercom",
        "PlayerEvents.UsingRadio",
        "PlayerEvents.ValidatedVisibility",
        "Scp106Events.ChangedVigor",
        "Scp106Events.ChangingVigor",
        "Scp173Events.AddingObserver",
        "Scp173Events.RemovingObserver",
    };
}
