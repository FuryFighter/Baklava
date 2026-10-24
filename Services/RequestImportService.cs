#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Baklava.Api;
using Baklava.Helpers;
using MediaBrowser.Controller.Library;

namespace Baklava.Services;

/// <summary>Imports approved requests with the requester's Gelato configuration.</summary>
public sealed class RequestImportService(IServiceProvider services, IUserManager users)
{
    public async Task<string?> ImportAsync(MediaRequest request, CancellationToken cancellationToken)
    {
        var assembly = GelatoCompatibility.GetAssembly()
            ?? throw new InvalidOperationException("Gelato must be installed and enabled before approving requests.");
        var manager = GelatoCompatibility.GetManager(services, assembly)
            ?? throw new InvalidOperationException("Gelato's library manager is unavailable.");
        var requester = Guid.TryParse(request.UserId, out var requesterId) && requesterId != Guid.Empty
            ? users.GetUserById(requesterId) : null;
        if (requester is null)
            throw new InvalidOperationException("The requester no longer has a Jellyfin account.");
        var config = GelatoCompatibility.GetConfig(assembly, requester.Id)
            ?? throw new InvalidOperationException("The requester has no Gelato configuration.");
        var provider = GelatoCompatibility.GetStremioProvider(config)
            ?? throw new InvalidOperationException("Configure an AIOStreams addon in Gelato before approving requests.");
        var series = request.ItemType is "series" or "tv" || request.TmdbMediaType == "tv";
        var mediaTypeClass = GelatoCompatibility.GetMediaType(assembly)
            ?? throw new InvalidOperationException("This Gelato release is incompatible with Baklava.");
        var mediaType = GelatoCompatibility.ParseMediaType(mediaTypeClass, series);
        var id = !string.IsNullOrWhiteSpace(request.ImdbId) ? request.ImdbId
            : !string.IsNullOrWhiteSpace(request.TmdbId) ? $"tmdb:{request.TmdbId}"
            : throw new InvalidOperationException("The request needs an IMDb or TMDB identifier.");
        cancellationToken.ThrowIfCancellationRequested();
        var meta = await GelatoCompatibility.GetMetaAsync(provider, id, mediaType).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The configured addon returned no metadata for this request.");
        var folder = GelatoCompatibility.GetRootFolder(manager, series, requester.Id)
            ?? throw new InvalidOperationException("Configure a Gelato library for this media type before approving requests.");
        var item = await GelatoCompatibility.InsertMetaAsync(
            manager, folder, meta, requester, false, true, false, cancellationToken).ConfigureAwait(false);
        return GelatoCompatibility.GetItemId(item)
            ?? throw new InvalidOperationException("Gelato could not import this title for the requester.");
    }
}
