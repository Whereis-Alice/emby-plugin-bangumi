using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace Emby.Plugins.Bangumi.Api
{
    /// <summary>
    /// Thin, self-throttling client for the Bangumi (bgm.tv) HTTP API.
    /// </summary>
    /// <remarks>
    /// Deliberately built on <see cref="HttpClient"/> instead of Emby's
    /// <c>IHttpClient</c>: <c>HttpRequestOptions</c> has no way to express a per-request
    /// proxy, and a user-selectable proxy is mandatory for Bangumi in a lot of networks.
    /// Emby Server 4.9 is a self-contained .NET 8 build and ships System.Net.Http and
    /// System.Text.Json in its system directory, so both are safe to use from a plugin.
    /// </remarks>
    public sealed class BangumiApiClient : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private static readonly JsonSerializerOptions WriteOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly ILogger _logger;
        private readonly Func<PluginOptions> _optionsFactory;
        private readonly SemaphoreSlim _throttle = new SemaphoreSlim(1, 1);
        private readonly TtlCache _cache = new TtlCache();
        private readonly object _transportLock = new object();

        private HttpClient _http;
        private string _transportKey;
        private long _nextAllowedTicks;
        private bool _disposed;

        public BangumiApiClient(ILogger logger, Func<PluginOptions> optionsFactory)
        {
            _logger = logger;
            _optionsFactory = optionsFactory;
        }

        private PluginOptions Options => _optionsFactory() ?? new PluginOptions();

        /// <summary>
        /// Called when the configuration page is saved. Proxy, user agent, timeout and
        /// token live on the handler / default headers and cannot be mutated afterwards.
        /// </summary>
        public void InvalidateTransport()
        {
            HttpClient stale;
            lock (_transportLock)
            {
                stale = _http;
                _http = null;
                _transportKey = null;
            }

            _cache.Clear();

            if (stale == null) return;

            // Do not dispose synchronously: a library scan may still be reading a response body.
            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                try { stale.Dispose(); } catch { /* nothing useful to do */ }
            });
        }

        private HttpClient GetTransport(PluginOptions options)
        {
            var key = string.Join(
                "\u0001",
                options.ProxyUrl ?? string.Empty,
                options.UserAgent ?? string.Empty,
                options.AccessToken ?? string.Empty,
                options.RequestTimeoutSeconds.ToString(CultureInfo.InvariantCulture));

            lock (_transportLock)
            {
                if (_http != null && string.Equals(_transportKey, key, StringComparison.Ordinal)) return _http;

                var handler = new HttpClientHandler
                {
                    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                    AllowAutoRedirect = true,
                    MaxAutomaticRedirections = 5
                };

                if (!string.IsNullOrWhiteSpace(options.ProxyUrl))
                {
                    if (Uri.TryCreate(options.ProxyUrl.Trim(), UriKind.Absolute, out var proxyUri))
                    {
                        // .NET 6+ WebProxy understands http, https, socks4, socks4a and socks5.
                        handler.Proxy = new WebProxy(proxyUri);
                        handler.UseProxy = true;
                    }
                    else
                    {
                        _logger.Warn("Bangumi: ignoring unparseable proxy url {0}", options.ProxyUrl);
                    }
                }

                var client = new HttpClient(handler, disposeHandler: true)
                {
                    Timeout = TimeSpan.FromSeconds(Math.Max(5, options.RequestTimeoutSeconds))
                };

                var userAgent = string.IsNullOrWhiteSpace(options.UserAgent)
                    ? BangumiConstants.DefaultUserAgent
                    : options.UserAgent.Trim();

                // Bangumi rejects generic user agents, and the recommended format contains a
                // bare URL that HttpHeaders' validator refuses, so bypass validation.
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
                client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                if (!string.IsNullOrWhiteSpace(options.AccessToken))
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation(
                        "Authorization", "Bearer " + options.AccessToken.Trim());
                }

                var previous = _http;
                _http = client;
                _transportKey = key;

                if (previous != null)
                {
                    try { previous.Dispose(); } catch { }
                }

                return client;
            }
        }

        private string BaseUrl(PluginOptions options)
        {
            var value = string.IsNullOrWhiteSpace(options.ApiBaseUrl)
                ? BangumiConstants.DefaultApiBaseUrl
                : options.ApiBaseUrl.Trim();
            return value.TrimEnd('/');
        }

        // ------------------------------------------------------------------ transport

        private async Task<string> SendAsync(
            HttpMethod method,
            string relativeUrl,
            string jsonBody,
            bool cacheable,
            CancellationToken cancellationToken)
        {
            if (_disposed) return null;

            var options = Options;
            var url = relativeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? relativeUrl
                : BaseUrl(options) + relativeUrl;

            var cacheKey = method.Method + " " + url + "\n" + (jsonBody ?? string.Empty);
            var ttl = TimeSpan.FromMinutes(Math.Max(0, options.CacheMinutes));

            if (cacheable && ttl > TimeSpan.Zero && _cache.TryGet(cacheKey, out var cached))
            {
                if (options.EnableVerboseLogging) _logger.Debug("Bangumi: cache hit {0}", url);
                return cached;
            }

            var attempts = Math.Max(0, options.MaxRetries) + 1;
            Exception lastError = null;

            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var body = await ExecuteOnceAsync(method, url, jsonBody, options, cancellationToken)
                        .ConfigureAwait(false);

                    // null means "definitively absent" (404) - do not retry, do not cache.
                    if (body == null) return null;

                    if (cacheable) _cache.Set(cacheKey, body, ttl);
                    return body;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (RetryableBangumiException ex)
                {
                    lastError = ex;
                    if (attempt >= attempts) break;

                    var delay = ex.RetryAfter ?? TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt - 1));
                    _logger.Warn(
                        "Bangumi: {0} on {1} (attempt {2}/{3}), retrying in {4:0.##}s",
                        ex.Message, url, attempt, attempts, delay.TotalSeconds);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Bangumi: request failed for " + url, ex);
                    return null;
                }
            }

            if (lastError != null)
            {
                _logger.Error("Bangumi: giving up on {0} after {1} attempts ({2})", url, attempts, lastError.Message);
            }

            return null;
        }

        private async Task<string> ExecuteOnceAsync(
            HttpMethod method,
            string url,
            string jsonBody,
            PluginOptions options,
            CancellationToken cancellationToken)
        {
            var client = GetTransport(options);

            await _throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var minIntervalMs = Math.Max(0, options.RequestIntervalMs);
                if (minIntervalMs > 0)
                {
                    var waitTicks = Interlocked.Read(ref _nextAllowedTicks) - DateTime.UtcNow.Ticks;
                    if (waitTicks > 0)
                    {
                        await Task.Delay(TimeSpan.FromTicks(waitTicks), cancellationToken).ConfigureAwait(false);
                    }
                }

                if (options.EnableVerboseLogging) _logger.Info("Bangumi: {0} {1}", method.Method, url);

                using (var request = new HttpRequestMessage(method, url))
                {
                    if (jsonBody != null)
                    {
                        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                    }

                    HttpResponseMessage response;
                    try
                    {
                        response = await client
                            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (HttpRequestException ex)
                    {
                        throw new RetryableBangumiException("network error: " + ex.Message, null);
                    }
                    catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new RetryableBangumiException(
                            "timeout after " + options.RequestTimeoutSeconds + "s", null);
                    }

                    using (response)
                    {
                        var status = (int)response.StatusCode;

                        if (status == 404) return null;

                        if (status == 429)
                        {
                            TimeSpan? retryAfter = null;
                            if (response.Headers.RetryAfter != null)
                            {
                                retryAfter = response.Headers.RetryAfter.Delta;
                                if (retryAfter == null && response.Headers.RetryAfter.Date != null)
                                {
                                    retryAfter = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                                }
                            }

                            throw new RetryableBangumiException("rate limited (429)", retryAfter);
                        }

                        if (status >= 500) throw new RetryableBangumiException("server error " + status, null);

                        if (status == 401 || status == 403)
                        {
                            _logger.Error(
                                "Bangumi: {0} {1} - the Access Token is missing, expired or lacks permission. " +
                                "Regenerate it at https://next.bgm.tv/demo/access-token .",
                                status, url);
                            return null;
                        }

                        if (status < 200 || status >= 300)
                        {
                            var detail = await SafeReadAsync(response).ConfigureAwait(false);
                            _logger.Error("Bangumi: HTTP {0} for {1} - {2}", status, url, Truncate(detail, 400));
                            return null;
                        }

                        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                var minIntervalMs = Math.Max(0, options.RequestIntervalMs);
                Interlocked.Exchange(
                    ref _nextAllowedTicks,
                    DateTime.UtcNow.Ticks + TimeSpan.FromMilliseconds(minIntervalMs).Ticks);
                _throttle.Release();
            }
        }

        private static async Task<string> SafeReadAsync(HttpResponseMessage response)
        {
            try { return await response.Content.ReadAsStringAsync().ConfigureAwait(false); }
            catch { return string.Empty; }
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            value = value.Replace("\r", " ").Replace("\n", " ");
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        private T Deserialize<T>(string payload, string context) where T : class
        {
            if (string.IsNullOrWhiteSpace(payload)) return null;

            try
            {
                return JsonSerializer.Deserialize<T>(payload, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.ErrorException(
                    "Bangumi: could not parse response for " + context + ": " + Truncate(payload, 300), ex);
                return null;
            }
        }

        private sealed class RetryableBangumiException : Exception
        {
            public RetryableBangumiException(string message, TimeSpan? retryAfter) : base(message)
            {
                RetryAfter = retryAfter;
            }

            public TimeSpan? RetryAfter { get; }
        }

        // ------------------------------------------------------------------ endpoints

        /// <summary>POST /v0/search/subjects, falling back to the legacy GET /search/subject/{keyword}.</summary>
        public async Task<List<BangumiSubject>> SearchSubjectsAsync(
            string keyword, int subjectType, int limit, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(keyword)) return new List<BangumiSubject>();

            var options = Options;
            limit = Math.Max(1, Math.Min(50, limit));

            var request = new BangumiSearchRequest
            {
                Keyword = keyword,
                Sort = "match",
                Filter = new BangumiSearchFilter
                {
                    Type = new List<int> { subjectType },
                    Nsfw = options.IncludeNsfw ? (bool?)null : false
                }
            };

            var payload = await SendAsync(
                HttpMethod.Post,
                "/v0/search/subjects?limit=" + limit.ToString(CultureInfo.InvariantCulture) + "&offset=0",
                JsonSerializer.Serialize(request, WriteOptions),
                cacheable: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var paged = Deserialize<BangumiPaged<BangumiSubject>>(payload, "search '" + keyword + "'");
            if (paged?.Data != null && paged.Data.Count > 0) return paged.Data;

            if (options.EnableVerboseLogging)
            {
                _logger.Info("Bangumi: v0 search returned nothing for '{0}', trying the legacy endpoint", keyword);
            }

            return await LegacySearchAsync(keyword, subjectType, limit, cancellationToken).ConfigureAwait(false);
        }

        private async Task<List<BangumiSubject>> LegacySearchAsync(
            string keyword, int subjectType, int limit, CancellationToken cancellationToken)
        {
            var url = "/search/subject/" + Uri.EscapeDataString(keyword)
                      + "?type=" + subjectType.ToString(CultureInfo.InvariantCulture)
                      + "&responseGroup=large&max_results=" + limit.ToString(CultureInfo.InvariantCulture);

            var payload = await SendAsync(HttpMethod.Get, url, null, true, cancellationToken).ConfigureAwait(false);
            var legacy = Deserialize<LegacySearchResponse>(payload, "legacy search '" + keyword + "'");
            var results = new List<BangumiSubject>();

            if (legacy?.List == null) return results;

            foreach (var item in legacy.List)
            {
                if (item == null) continue;
                results.Add(new BangumiSubject
                {
                    Id = item.Id,
                    Type = item.Type,
                    Name = item.Name,
                    NameCn = item.NameCn,
                    Summary = item.Summary,
                    Date = item.AirDate,
                    Images = item.Images,
                    Rating = item.Rating,
                    Eps = item.Eps,
                    TotalEpisodes = item.EpsCount
                });
            }

            return results;
        }

        private sealed class LegacySearchResponse
        {
            [JsonPropertyName("results")] public int Results { get; set; }
            [JsonPropertyName("list")] public List<LegacySearchItem> List { get; set; }
        }

        private sealed class LegacySearchItem
        {
            [JsonPropertyName("id")] public int Id { get; set; }
            [JsonPropertyName("type")] public int Type { get; set; }
            [JsonPropertyName("name")] public string Name { get; set; }
            [JsonPropertyName("name_cn")] public string NameCn { get; set; }
            [JsonPropertyName("summary")] public string Summary { get; set; }
            [JsonPropertyName("air_date")] public string AirDate { get; set; }
            [JsonPropertyName("eps")] public int Eps { get; set; }
            [JsonPropertyName("eps_count")] public int EpsCount { get; set; }
            [JsonPropertyName("images")] public BangumiImages Images { get; set; }
            [JsonPropertyName("rating")] public BangumiRating Rating { get; set; }
        }

        public async Task<BangumiSubject> GetSubjectAsync(int subjectId, CancellationToken cancellationToken)
        {
            if (subjectId <= 0) return null;

            var payload = await SendAsync(
                HttpMethod.Get,
                "/v0/subjects/" + subjectId.ToString(CultureInfo.InvariantCulture),
                null, true, cancellationToken).ConfigureAwait(false);

            return Deserialize<BangumiSubject>(payload, "subject " + subjectId);
        }

        /// <summary>
        /// GET /v0/episodes, following pagination until everything is collected.
        /// </summary>
        /// <param name="episodeType">
        /// Bangumi episode type, or <c>null</c> for every type. Note that the API treats an
        /// absent <c>type</c> as "all" but <c>type=0</c> as "main episodes only".
        /// </param>
        public async Task<List<BangumiEpisode>> GetEpisodesAsync(
            int subjectId, int? episodeType, CancellationToken cancellationToken)
        {
            var all = new List<BangumiEpisode>();
            if (subjectId <= 0) return all;

            const int PageSize = 100;
            var offset = 0;

            while (true)
            {
                var url = "/v0/episodes?subject_id=" + subjectId.ToString(CultureInfo.InvariantCulture)
                          + "&limit=" + PageSize.ToString(CultureInfo.InvariantCulture)
                          + "&offset=" + offset.ToString(CultureInfo.InvariantCulture);

                if (episodeType.HasValue)
                {
                    url += "&type=" + episodeType.Value.ToString(CultureInfo.InvariantCulture);
                }

                var payload = await SendAsync(HttpMethod.Get, url, null, true, cancellationToken)
                    .ConfigureAwait(false);

                var page = Deserialize<BangumiPaged<BangumiEpisode>>(payload, "episodes of " + subjectId);
                if (page?.Data == null || page.Data.Count == 0) break;

                all.AddRange(page.Data);
                offset += page.Data.Count;

                if (all.Count >= page.Total || page.Data.Count < PageSize) break;

                // Guard against a server that keeps returning data with a bogus total.
                if (offset > 5000) break;
            }

            return all;
        }

        public async Task<List<BangumiRelatedPerson>> GetSubjectPersonsAsync(
            int subjectId, CancellationToken cancellationToken)
        {
            var payload = await SendAsync(
                HttpMethod.Get,
                "/v0/subjects/" + subjectId.ToString(CultureInfo.InvariantCulture) + "/persons",
                null, true, cancellationToken).ConfigureAwait(false);

            return Deserialize<List<BangumiRelatedPerson>>(payload, "persons of " + subjectId)
                   ?? new List<BangumiRelatedPerson>();
        }

        public async Task<List<BangumiRelatedCharacter>> GetSubjectCharactersAsync(
            int subjectId, CancellationToken cancellationToken)
        {
            var payload = await SendAsync(
                HttpMethod.Get,
                "/v0/subjects/" + subjectId.ToString(CultureInfo.InvariantCulture) + "/characters",
                null, true, cancellationToken).ConfigureAwait(false);

            return Deserialize<List<BangumiRelatedCharacter>>(payload, "characters of " + subjectId)
                   ?? new List<BangumiRelatedCharacter>();
        }

        public async Task<List<BangumiRelatedSubject>> GetRelatedSubjectsAsync(
            int subjectId, CancellationToken cancellationToken)
        {
            var payload = await SendAsync(
                HttpMethod.Get,
                "/v0/subjects/" + subjectId.ToString(CultureInfo.InvariantCulture) + "/subjects",
                null, true, cancellationToken).ConfigureAwait(false);

            return Deserialize<List<BangumiRelatedSubject>>(payload, "relations of " + subjectId)
                   ?? new List<BangumiRelatedSubject>();
        }

        /// <summary>
        /// GET /v0/persons/{id}. Bangumi only returns a person's biography, portrait and birth
        /// date here; the subject-level /persons list carries nothing but name + relation.
        /// </summary>
        public async Task<BangumiPersonDetail> GetPersonAsync(int personId, CancellationToken cancellationToken)
        {
            if (personId <= 0) return null;

            var payload = await SendAsync(
                HttpMethod.Get,
                "/v0/persons/" + personId.ToString(CultureInfo.InvariantCulture),
                null, true, cancellationToken).ConfigureAwait(false);

            return Deserialize<BangumiPersonDetail>(payload, "person " + personId);
        }

        /// <summary>
        /// GET /v0/characters/{id}. The only place a Chinese rendering of a character name exists
        /// (infobox 简体中文名) - /v0/subjects/{id}/characters returns the Japanese name alone.
        /// </summary>
        public async Task<BangumiCharacterDetail> GetCharacterAsync(int characterId, CancellationToken cancellationToken)
        {
            if (characterId <= 0) return null;

            var payload = await SendAsync(
                HttpMethod.Get,
                "/v0/characters/" + characterId.ToString(CultureInfo.InvariantCulture),
                null, true, cancellationToken).ConfigureAwait(false);

            return Deserialize<BangumiCharacterDetail>(payload, "character " + characterId);
        }

        /// <summary>POST /v0/search/persons, used when Emby asks for a person it has no Bangumi id for.</summary>
        public async Task<List<BangumiPersonDetail>> SearchPersonsAsync(
            string keyword, int limit, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(keyword)) return new List<BangumiPersonDetail>();

            limit = Math.Max(1, Math.Min(20, limit));
            var request = new BangumiPersonSearchRequest { Keyword = keyword };

            var payload = await SendAsync(
                HttpMethod.Post,
                "/v0/search/persons?limit=" + limit.ToString(CultureInfo.InvariantCulture) + "&offset=0",
                JsonSerializer.Serialize(request, WriteOptions),
                cacheable: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var paged = Deserialize<BangumiPaged<BangumiPersonDetail>>(payload, "person search '" + keyword + "'");
            return paged != null && paged.Data != null ? paged.Data : new List<BangumiPersonDetail>();
        }

        /// <summary>
        /// Fetches an arbitrary URL (artwork on lain.bgm.tv) through the same proxy and
        /// user agent, bypassing the API rate limiter.
        /// </summary>
        public async Task<HttpResponseMessage> GetRawAsync(string url, CancellationToken cancellationToken)
        {
            var client = GetTransport(Options);
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            return await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            HttpClient stale;
            lock (_transportLock)
            {
                stale = _http;
                _http = null;
            }

            try { stale?.Dispose(); } catch { }
            try { _throttle.Dispose(); } catch { }
            _cache.Clear();
        }
    }
}