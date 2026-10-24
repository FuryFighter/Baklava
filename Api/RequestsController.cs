using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Baklava.Services;

namespace Baklava.Api
{
    [ApiController]
    [Route("api/baklava/requests")]
    [Produces("application/json")]
    [Authorize]
    public class RequestsController : ControllerBase
    {
        private static readonly object ConfigurationLock = new();

        private readonly ILogger<RequestsController> _logger;
        private readonly IUserManager _userManager;
        private readonly IAuthorizationService _authorizationService;
        private readonly RequestImportService _requestImport;

        public RequestsController(
            ILogger<RequestsController> logger,
            IUserManager userManager,
            IAuthorizationService authorizationService,
            RequestImportService requestImport)
        {
            _logger = logger;
            _userManager = userManager;
            _authorizationService = authorizationService;
            _requestImport = requestImport;
        }

        [HttpGet]
        public async Task<ActionResult<List<MediaRequest>>> GetRequests()
        {
            var currentUser = GetAuthenticatedUser();
            if (currentUser == null)
            {
                return Unauthorized();
            }

            var isElevated = await IsElevatedAsync().ConfigureAwait(false);
            lock (ConfigurationLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null)
                {
                    _logger.LogWarning("[RequestsController] Config is null");
                    return Ok(new List<MediaRequest>());
                }

                var requests = config.Requests ?? new List<MediaRequest>();
                var visibleRequests = isElevated
                    ? requests.ToList()
                    : requests.Where(request => IsOwnedBy(request, currentUser.Id)).ToList();

                _logger.LogInformation(
                    "[RequestsController] Returning {RequestCount} requests to user {UserId}",
                    visibleRequests.Count,
                    currentUser.Id);
                return Ok(visibleRequests);
            }
        }

        [HttpPost]
        public async Task<ActionResult<MediaRequest>> CreateRequest([FromBody] MediaRequest request)
        {
            if (request == null)
            {
                return BadRequest("Request data is required");
            }

            var currentUser = GetAuthenticatedUser();
            if (currentUser == null)
            {
                return Unauthorized();
            }

            var isElevated = await IsElevatedAsync().ConfigureAwait(false);
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                _logger.LogWarning("[RequestsController] Config is null");
                return BadRequest("Plugin configuration not available");
            }

            if (config.DisableNonAdminRequests && !isElevated)
            {
                return Forbid();
            }

            // Request identity and workflow state are always derived on the server.
            request.Username = currentUser.Username;
            request.UserId = currentUser.Id.ToString("N");
            request.Status = "pending";
            request.ApprovedBy = null;

            request.ItemType = (request.ItemType ?? string.Empty).ToLowerInvariant();
            if (string.IsNullOrEmpty(request.TmdbMediaType))
            {
                request.TmdbMediaType = request.ItemType == "series" ? "tv" : "movie";
            }

            // If an IMDb ID is present, prefer TMDB's canonical media-type mapping.
            // Authentication and request policy checks deliberately happen before this call.
            try
            {
                if (!string.IsNullOrEmpty(request.ImdbId) && !string.IsNullOrEmpty(config.TmdbApiKey))
                {
                    var tmdbFindUrl = $"https://api.themoviedb.org/3/find/{Uri.EscapeDataString(request.ImdbId)}?api_key={Uri.EscapeDataString(config.TmdbApiKey)}&external_source=imdb_id";
                    using var http = new HttpClient();
                    var response = await http.GetAsync(tmdbFindUrl).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        using var document = System.Text.Json.JsonDocument.Parse(content);
                        var root = document.RootElement;
                        if (root.TryGetProperty("tv_results", out var tvResults) && tvResults.GetArrayLength() > 0)
                        {
                            var first = tvResults[0];
                            if (first.TryGetProperty("id", out var idProperty))
                            {
                                request.TmdbId = idProperty.GetRawText().Trim('"');
                                request.TmdbMediaType = "tv";
                                request.ItemType = "series";
                            }
                        }
                        else if (root.TryGetProperty("movie_results", out var movieResults) && movieResults.GetArrayLength() > 0)
                        {
                            var first = movieResults[0];
                            if (first.TryGetProperty("id", out var idProperty))
                            {
                                request.TmdbId = idProperty.GetRawText().Trim('"');
                                request.TmdbMediaType = "movie";
                                request.ItemType = "movie";
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RequestsController] TMDB find check failed for imdbId={ImdbId}", request.ImdbId);
            }

            lock (ConfigurationLock)
            {
                config.Requests ??= new List<MediaRequest>();

                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string requestId;
                do
                {
                    requestId = $"{currentUser.Username}_{request.TmdbId}_{timestamp++}";
                }
                while (config.Requests.Any(existing => string.Equals(existing.Id, requestId, StringComparison.Ordinal)));

                request.Id = requestId;
                request.Timestamp = timestamp - 1;
                config.Requests.Add(request);
                Plugin.Instance.SaveConfiguration();
            }

            _logger.LogInformation("[RequestsController] Created request {RequestId} for user {UserId}", request.Id, currentUser.Id);
            return Ok(request);
        }

        [HttpPut("{id}")]
        [Authorize(Policy = Policies.RequiresElevation)]
        public async Task<ActionResult> UpdateRequest(string id, [FromBody] UpdateRequestDto update)
        {
            var currentUser = GetAuthenticatedUser();
            if (currentUser == null)
            {
                return Unauthorized();
            }

            if (update == null || string.IsNullOrWhiteSpace(update.Status))
            {
                return BadRequest("A status is required");
            }

            var status = update.Status.Trim().ToLowerInvariant();
            if (status != "approved" && status != "rejected")
            {
                return BadRequest("Status must be approved or rejected");
            }

            MediaRequest request;
            string importedId = null;

            if (status == "approved")
            {
                lock (ConfigurationLock)
                {
                    request = Plugin.Instance?.Configuration?.Requests?.FirstOrDefault(candidate => candidate.Id == id);
                    if (request == null) return NotFound($"Request {id} not found");
                }
                try
                {
                    importedId = await _requestImport.ImportAsync(request, HttpContext.RequestAborted).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Could not import approved request {RequestId}", id);
                    return StatusCode(502, new { error = "Gelato could not import this request. Check its addon and library configuration. The request has not been approved." });
                }
            }

            lock (ConfigurationLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null)
                {
                    _logger.LogWarning("[RequestsController] Config is null");
                    return BadRequest("Plugin configuration not available");
                }

                request = config.Requests?.FirstOrDefault(candidate => candidate.Id == id);
                if (request == null)
                {
                    return NotFound($"Request {id} not found");
                }

                request.Status = status;
                request.ApprovedBy = currentUser.Username;
                if (importedId != null) request.JellyfinId = importedId;

                // Preserve the existing admin-owned status copy used by the UI, but derive
                // both its owner and approver from the authenticated Jellyfin user.
                if (!IsOwnedBy(request, currentUser.Id))
                {
                    config.Requests ??= new List<MediaRequest>();
                    var existingAdminCopy = config.Requests.FirstOrDefault(candidate =>
                        IsOwnedBy(candidate, currentUser.Id)
                        && candidate.TmdbId == request.TmdbId
                        && string.Equals(candidate.Status, status, StringComparison.OrdinalIgnoreCase));

                    if (existingAdminCopy == null)
                    {
                        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        config.Requests.Add(new MediaRequest
                        {
                            Id = $"{currentUser.Username}_{request.TmdbId}_{timestamp}",
                            Username = currentUser.Username,
                            UserId = currentUser.Id.ToString("N"),
                            Timestamp = timestamp,
                            Title = request.Title,
                            Year = request.Year,
                            Img = request.Img,
                            ImdbId = request.ImdbId,
                            TmdbId = request.TmdbId,
                            JellyfinId = request.JellyfinId,
                            ItemType = request.ItemType,
                            TmdbMediaType = request.TmdbMediaType,
                            Status = status,
                            ApprovedBy = currentUser.Username
                        });
                    }
                }

                Plugin.Instance.SaveConfiguration();
            }

            _logger.LogInformation(
                "[RequestsController] Updated request {RequestId}: status={Status}, approver={ApproverId}",
                id,
                status,
                currentUser.Id);

            return Ok();
        }

        [HttpDelete("{id}")]
        public async Task<ActionResult> DeleteRequest(string id)
        {
            var currentUser = GetAuthenticatedUser();
            if (currentUser == null)
            {
                return Unauthorized();
            }

            var isElevated = await IsElevatedAsync().ConfigureAwait(false);
            lock (ConfigurationLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null)
                {
                    _logger.LogWarning("[RequestsController] Config is null");
                    return BadRequest("Plugin configuration not available");
                }

                var request = config.Requests?.FirstOrDefault(candidate => candidate.Id == id);
                if (request == null)
                {
                    return NotFound($"Request {id} not found");
                }

                if (!isElevated && !IsOwnedBy(request, currentUser.Id))
                {
                    return Forbid();
                }

                config.Requests.Remove(request);
                Plugin.Instance.SaveConfiguration();
            }

            _logger.LogInformation("[RequestsController] Deleted request {RequestId} for user {UserId}", id, currentUser.Id);
            return Ok();
        }

        /// <summary>
        /// Clean up invalid requests (undefined/empty IDs).
        /// </summary>
        [HttpPost("cleanup")]
        [Authorize(Policy = Policies.RequiresElevation)]
        public ActionResult CleanupRequests()
        {
            lock (ConfigurationLock)
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null)
                {
                    return BadRequest("Configuration not available");
                }

                var requests = config.Requests ?? new List<MediaRequest>();
                var validRequests = requests.Where(request =>
                    !string.IsNullOrEmpty(request.ImdbId) && request.ImdbId != "undefined"
                    && !string.IsNullOrEmpty(request.TmdbId) && request.TmdbId != "undefined").ToList();
                var removed = requests.Count - validRequests.Count;

                if (removed > 0)
                {
                    config.Requests = validRequests;
                    Plugin.Instance.SaveConfiguration();
                    _logger.LogInformation("[RequestsController] Cleaned up {RemovedCount} invalid requests", removed);
                }

                return Ok(new { removed, remaining = validRequests.Count });
            }
        }

        private RequestUser GetAuthenticatedUser()
        {
            var userIdValues = new[]
            {
                User.FindFirst("UserId")?.Value,
                User.FindFirst("Jellyfin-UserId")?.Value,
                User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            };
            var userId = Guid.Empty;
            if (!userIdValues.Any(value => Guid.TryParse(value, out userId)))
            {
                _logger.LogWarning("[RequestsController] Authenticated principal did not contain a valid Jellyfin user ID");
                return null;
            }

            var user = _userManager.GetUserById(userId);
            return user == null ? null : new RequestUser(user.Id, user.Username);
        }

        private async Task<bool> IsElevatedAsync()
        {
            var result = await _authorizationService.AuthorizeAsync(User, Policies.RequiresElevation).ConfigureAwait(false);
            return result.Succeeded;
        }

        private static bool IsOwnedBy(MediaRequest request, Guid userId)
        {
            return Guid.TryParse(request.UserId, out var requestUserId) && requestUserId == userId;
        }

        private static void ApplyGelatoAuthorization(HttpClient http, string authHeader)
        {
            if (string.IsNullOrEmpty(authHeader))
            {
                return;
            }

            var separatorIndex = authHeader.IndexOf(':');
            if (separatorIndex > -1)
            {
                var name = authHeader.Substring(0, separatorIndex).Trim();
                var value = authHeader.Substring(separatorIndex + 1).Trim();
                http.DefaultRequestHeaders.Remove(name);
                http.DefaultRequestHeaders.Add(name, value);
                return;
            }

            http.DefaultRequestHeaders.Remove("Authorization");
            http.DefaultRequestHeaders.Add("Authorization", authHeader);
        }

        private sealed class RequestUser
        {
            public RequestUser(Guid id, string username)
            {
                Id = id;
                Username = username;
            }

            public Guid Id { get; }

            public string Username { get; }
        }
    }

    public class MediaRequest
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("username")]
        public string Username { get; set; }

        [JsonPropertyName("userId")]
        public string UserId { get; set; }

        [JsonPropertyName("timestamp")]
        public long Timestamp { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("year")]
        public string Year { get; set; }

        [JsonPropertyName("img")]
        public string Img { get; set; }

        [JsonPropertyName("imdbId")]
        public string ImdbId { get; set; }

        [JsonPropertyName("tmdbId")]
        public string TmdbId { get; set; }

        [JsonPropertyName("jellyfinId")]
        public string JellyfinId { get; set; }

        [JsonPropertyName("itemType")]
        public string ItemType { get; set; }

        [JsonPropertyName("tmdbMediaType")]
        public string TmdbMediaType { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("approvedBy")]
        public string ApprovedBy { get; set; }
    }

    public class UpdateRequestDto
    {
        [JsonPropertyName("status")]
        public string Status { get; set; }

        [JsonPropertyName("approvedBy")]
        public string ApprovedBy { get; set; }
    }
}
