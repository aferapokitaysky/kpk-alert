using System.ComponentModel.DataAnnotations;

namespace Watcher.Api.Contracts;

public sealed record CreateSoundPresetRequest(
    [param: Required, RegularExpression("^[a-z0-9_]{1,80}$")]
    string Code,
    [param: Required, StringLength(160, MinimumLength = 1)]
    string Name,
    int Priority,
    [param: Required, RegularExpression("^[A-Fa-f0-9]{64}$")]
    string ManifestHash);

public sealed record UpdateSoundPresetRequest(
    [param: Required, StringLength(160, MinimumLength = 1)]
    string Name,
    int Priority,
    [param: Required, RegularExpression("^[A-Fa-f0-9]{64}$")]
    string ManifestHash);

public sealed record SetSoundPresetPublicationRequest(
    [param: Required]
    bool? IsPublished);

public sealed record SoundPresetResponse(
    string Code,
    string Name,
    int Priority,
    string ManifestHash,
    bool IsPublished);
