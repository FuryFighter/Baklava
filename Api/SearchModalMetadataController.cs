#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Baklava.Helpers;
using Baklava.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;

namespace Baklava.Api
{
    [ApiController]
    [Authorize]
    [Route("api/baklava/metadata")]
    [Produces("application/json")]
    public class SearchModalMetadataController : ControllerBase
    {
        private readonly ILogger<SearchModalMetadataController> _logger;
        private readonly ILibraryManager _libraryManager;

        public SearchModalMetadataController(
            ILogger<SearchModalMetadataController> logger,
            ILibraryManager libraryManager)
        {
            _logger = logger;
            _libraryManager = libraryManager;
        }

        [HttpGet("search")]
        public async Task<ActionResult> GetSearchMetadata(
            [FromQuery] string? tmdbId,
            [FromQuery] string? imdbId,
            [FromQuery] string? itemType) // "movie" or "series"
        {
            _logger.LogInformation("[Baklava.SearchMetadata] GetSearchMetadata: tmdbId={TmdbId}, imdbId={ImdbId}, itemType={ItemType}", 
                tmdbId ?? "null", imdbId ?? "null", itemType ?? "null");
            
            try
            {
                // 1. Determine Stremio ID
                string? stremioId = null;
                if (!string.IsNullOrEmpty(imdbId))
                {
                    stremioId = imdbId;
                }
                else if (!string.IsNullOrEmpty(tmdbId))
                {
                    stremioId = $"tmdb:{tmdbId}";
                }

                if (string.IsNullOrEmpty(stremioId))
                {
                    return BadRequest(new { error = "Either imdbId or tmdbId is required" });
                }

                // 2. Validate the media type before mapping it to Gelato's enum by name.
                var isMovie = string.Equals(itemType, "movie", StringComparison.OrdinalIgnoreCase);
                var isSeries = string.Equals(itemType, "series", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(itemType, "tv", StringComparison.OrdinalIgnoreCase);
                if (!isMovie && !isSeries)
                {
                    return BadRequest(new { error = "Invalid or missing itemType (must be 'movie' or 'series')" });
                }

                // 3. Reflect into Gelato to get metadata
                var gelatoData = await FetchMetadataFromGelato(stremioId, isSeries);
                
                if (gelatoData != null)
                {
                     // Debug: Log the properties of the result for troubleshooting
                     try 
                     {
                         var json = JsonSerializer.Serialize(gelatoData);
                         _logger.LogInformation("[Baklava.SearchMetadata] Gelato response: {Json}", json);

                         // Robust Mapping
                         using var doc = JsonDocument.Parse(json);
                         var root = doc.RootElement;
                         
                         var mapped = new Dictionary<string, object?>();
                         
                         // Helper to find property case-insensitive
                         JsonElement? FindProp(string[] names) 
                         {
                             foreach(var name in names) 
                             {
                                 if (root.TryGetProperty(name, out var prop)) return prop;
                                 if (root.TryGetProperty(name.ToLowerInvariant(), out var propLower)) return propLower;
                             }
                             return null;
                         }

                         var nameProp = FindProp(new[] { "Name", "Title" });
                         mapped["name"] = nameProp?.GetString();

                         var descProp = FindProp(new[] { "Description", "Overview", "Plot" });
                         mapped["description"] = descProp?.GetString();

                         var yearProp = FindProp(new[] { "Year", "ProductionYear", "ReleaseInfo" });
                         mapped["year"] = yearProp?.ToString(); // Could be int or string

                         var backProp = FindProp(new[] { "Background", "Backdrop", "BackdropImage", "Banner" });
                         mapped["background"] = backProp?.GetString();

                         var logoProp = FindProp(new[] { "Logo", "LogoImage" });
                         mapped["logo"] = logoProp?.GetString();

                         var ratingProp = FindProp(new[] { "ImdbRating", "Rating", "CommunityRating" });
                         mapped["imdbRating"] = ratingProp?.ToString();

                         var genresProp = FindProp(new[] { "Genres", "Genre" });
                         if (genresProp.HasValue && genresProp.Value.ValueKind == JsonValueKind.Array)
                         {
                             mapped["genres"] = genresProp.Value.EnumerateArray().Select(x => x.GetString()).ToList();
                         }

                         var runtimeProp = FindProp(new[] { "Runtime", "Duration" });
                         mapped["runtime"] = runtimeProp?.ToString();

                         return Ok(mapped);
                     }
                     catch(Exception ex)
                     {
                         _logger.LogError(ex, "[Baklava.SearchMetadata] Error mapping Gelato response");
                         return Ok(gelatoData); // Fallback
                     }
                }

                return NotFound(new { error = "No metadata found in Gelato" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava.SearchMetadata] Error getting metadata from Gelato");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        private async Task<object?> FetchMetadataFromGelato(string id, bool isSeries)
        {
            try
            {
                var gelatoPlugin = GelatoCompatibility.GetAssembly();

                if (gelatoPlugin == null)
                {
                    _logger.LogWarning("[Baklava.SearchMetadata] Gelato plugin assembly not found");
                    return null;
                }

                var actorId = User.FindFirst("Jellyfin-UserId")?.Value ?? User.FindFirst("UserId")?.Value;
                Guid.TryParse(actorId, out var userId);
                var config = GelatoCompatibility.GetConfig(gelatoPlugin, userId);

                if (config == null) return null;

                var stremioProvider = GelatoCompatibility.GetStremioProvider(config);

                if (stremioProvider == null) return null;

                var mediaType = GelatoCompatibility.GetMediaType(gelatoPlugin);
                if (mediaType == null)
                {
                    return null;
                }

                var mediaTypeEnumValue = GelatoCompatibility.ParseMediaType(mediaType, isSeries);
                return await GelatoCompatibility
                    .GetMetaAsync(stremioProvider, id, mediaTypeEnumValue)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Baklava.SearchMetadata] Reflection error fetching from Gelato");
                return null;
            }
        }
    }
}
