﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Headers;
using Microsoft.AspNetCore.ResponseCaching.Internal;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.ResponseCaching
{
    public class ResponseCachingMiddleware
    {
        private static readonly TimeSpan DefaultExpirationTimeSpan = TimeSpan.FromSeconds(10);

        private readonly RequestDelegate _next;
        private readonly IResponseCache _cache;
        private readonly ResponseCachingOptions _options;
        private readonly ICacheabilityValidator _cacheabilityValidator;
        private readonly ICacheKeyProvider _cacheKeyProvider;
        private readonly Func<object, Task> _onStartingCallback;

        public ResponseCachingMiddleware(
            RequestDelegate next,
            IResponseCache cache,
            IOptions<ResponseCachingOptions> options,
            ICacheabilityValidator cacheabilityValidator,
            ICacheKeyProvider cacheKeyProvider)
        {
            if (next == null)
            {
                throw new ArgumentNullException(nameof(next));
            }
            if (cache == null)
            {
                throw new ArgumentNullException(nameof(cache));
            }
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            if (cacheabilityValidator == null)
            {
                throw new ArgumentNullException(nameof(cacheabilityValidator));
            }
            if (cacheKeyProvider == null)
            {
                throw new ArgumentNullException(nameof(cacheKeyProvider));
            }

            _next = next;
            _cache = cache;
            _options = options.Value;
            _cacheabilityValidator = cacheabilityValidator;
            _cacheKeyProvider = cacheKeyProvider;
            _onStartingCallback = state =>
            {
                OnResponseStarting((ResponseCachingContext)state);
                return TaskCache.CompletedTask;
            };
        }

        public async Task Invoke(HttpContext httpContext)
        {
            var context = new ResponseCachingContext(httpContext);

            // Should we attempt any caching logic?
            if (_cacheabilityValidator.IsRequestCacheable(context))
            {
                // Can this request be served from cache?
                if (await TryServeFromCacheAsync(context))
                {
                    return;
                }

                // Hook up to listen to the response stream
                ShimResponseStream(context);

                try
                {
                    // Subscribe to OnStarting event
                    httpContext.Response.OnStarting(_onStartingCallback, context);

                    await _next(httpContext);

                    // If there was no response body, check the response headers now. We can cache things like redirects.
                    OnResponseStarting(context);

                    // Finalize the cache entry
                    FinalizeCachingBody(context);
                }
                finally
                {
                    UnshimResponseStream(context);
                }
            }
            else
            {
                // TODO: Invalidate resources for successful unsafe methods? Required by RFC
                await _next(httpContext);
            }
        }

        internal async Task<bool> TryServeCachedResponseAsync(ResponseCachingContext context, CachedResponse cachedResponse)
        {
            context.CachedResponse = cachedResponse;
            context.CachedResponseHeaders = new ResponseHeaders(cachedResponse.Headers);
            context.ResponseTime = _options.SystemClock.UtcNow;
            var cachedEntryAge = context.ResponseTime - context.CachedResponse.Created;
            context.CachedEntryAge = cachedEntryAge > TimeSpan.Zero ? cachedEntryAge : TimeSpan.Zero;

            if (_cacheabilityValidator.IsCachedEntryFresh(context))
            {
                // Check conditional request rules
                if (ConditionalRequestSatisfied(context))
                {
                    context.HttpContext.Response.StatusCode = StatusCodes.Status304NotModified;
                }
                else
                {
                    var response = context.HttpContext.Response;
                    // Copy the cached status code and response headers
                    response.StatusCode = context.CachedResponse.StatusCode;
                    foreach (var header in context.CachedResponse.Headers)
                    {
                        response.Headers.Add(header);
                    }

                    response.Headers[HeaderNames.Age] = context.CachedEntryAge.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture);

                    var body = context.CachedResponse.Body ??
                        ((CachedResponseBody)_cache.Get(context.CachedResponse.BodyKeyPrefix))?.Body;

                    // If the body is not found, something went wrong.
                    if (body == null)
                    {
                        return false;
                    }

                    // Copy the cached response body
                    if (body.Length > 0)
                    {
                        // Add a content-length if required
                        if (response.ContentLength == null && StringValues.IsNullOrEmpty(response.Headers[HeaderNames.TransferEncoding]))
                        {
                            response.ContentLength = body.Length;
                        }
                        await response.Body.WriteAsync(body, 0, body.Length);
                    }
                }

                return true;
            }
            else
            {
                // TODO: Validate with endpoint instead
            }

            return false;
        }

        internal async Task<bool> TryServeFromCacheAsync(ResponseCachingContext context)
        {
            foreach (var baseKey in _cacheKeyProvider.CreateLookupBaseKeys(context))
            {
                var cacheEntry = _cache.Get(baseKey);

                if (cacheEntry is CachedVaryRules)
                {
                    // Request contains vary rules, recompute key(s) and try again
                    context.CachedVaryRules = (CachedVaryRules)cacheEntry;

                    foreach (var varyKey in _cacheKeyProvider.CreateLookupVaryKeys(context))
                    {
                        cacheEntry = _cache.Get(varyKey);

                        if (cacheEntry is CachedResponse && await TryServeCachedResponseAsync(context, (CachedResponse)cacheEntry))
                        {
                            return true;
                        }
                    }
                }

                if (cacheEntry is CachedResponse && await TryServeCachedResponseAsync(context, (CachedResponse)cacheEntry))
                {
                    return true;
                }
            }


            if (context.RequestCacheControlHeaderValue.OnlyIfCached)
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                return true;
            }

            return false;
        }

        internal void FinalizeCachingHeaders(ResponseCachingContext context)
        {
            if (_cacheabilityValidator.IsResponseCacheable(context))
            {
                context.ShouldCacheResponse = true;
                context.StorageBaseKey = _cacheKeyProvider.CreateStorageBaseKey(context);

                // Create the cache entry now
                var response = context.HttpContext.Response;
                var varyHeaderValue = response.Headers[HeaderNames.Vary];
                var varyParamsValue = context.HttpContext.GetResponseCachingFeature()?.VaryParams ?? StringValues.Empty;
                context.CachedResponseValidFor = context.ResponseCacheControlHeaderValue.SharedMaxAge ??
                    context.ResponseCacheControlHeaderValue.MaxAge ??
                    (context.TypedResponseHeaders.Expires - context.ResponseTime) ??
                    DefaultExpirationTimeSpan;

                // Check if any vary rules exist
                if (!StringValues.IsNullOrEmpty(varyHeaderValue) || !StringValues.IsNullOrEmpty(varyParamsValue))
                {
                    // Normalize order and casing of vary by rules
                    var normalizedVaryHeaderValue = GetNormalizedHeaderStringValues(varyHeaderValue);
                    var normalizedVaryParamsValue = GetOrderCasingNormalizedStringValues(varyParamsValue);

                    // Update vary rules if they are different
                    if (context.CachedVaryRules == null ||
                        !StringValues.Equals(context.CachedVaryRules.Params, normalizedVaryParamsValue) ||
                        !StringValues.Equals(context.CachedVaryRules.Headers, normalizedVaryHeaderValue))
                    {
                        context.CachedVaryRules = new CachedVaryRules
                        {
                            VaryKeyPrefix = FastGuid.NewGuid().IdString,
                            Headers = normalizedVaryHeaderValue,
                            Params = normalizedVaryParamsValue
                        };

                        _cache.Set(context.StorageBaseKey, context.CachedVaryRules, context.CachedResponseValidFor);
                    }

                    context.StorageVaryKey = _cacheKeyProvider.CreateStorageVaryKey(context);
                }

                // Ensure date header is set
                if (context.TypedResponseHeaders.Date == null)
                {
                    context.TypedResponseHeaders.Date = context.ResponseTime;
                }

                // Store the response on the state
                context.CachedResponse = new CachedResponse
                {
                    BodyKeyPrefix = FastGuid.NewGuid().IdString,
                    Created = context.TypedResponseHeaders.Date.Value,
                    StatusCode = context.HttpContext.Response.StatusCode
                };

                foreach (var header in context.TypedResponseHeaders.Headers)
                {
                    if (!string.Equals(header.Key, HeaderNames.Age, StringComparison.OrdinalIgnoreCase))
                    {
                        context.CachedResponse.Headers.Add(header);
                    }
                }
            }
            else
            {
                context.ResponseCacheStream.DisableBuffering();
            }
        }

        internal void FinalizeCachingBody(ResponseCachingContext context)
        {
            if (context.ShouldCacheResponse &&
                context.ResponseCacheStream.BufferingEnabled &&
                (context.TypedResponseHeaders.ContentLength == null ||
                 context.TypedResponseHeaders.ContentLength == context.ResponseCacheStream.BufferedStream.Length))
            {
                if (context.ResponseCacheStream.BufferedStream.Length >= _options.MinimumSplitBodySize)
                {
                    // Store response and response body separately
                    _cache.Set(context.StorageVaryKey ?? context.StorageBaseKey, context.CachedResponse, context.CachedResponseValidFor);

                    var cachedResponseBody = new CachedResponseBody()
                    {
                        Body = context.ResponseCacheStream.BufferedStream.ToArray()
                    };

                    _cache.Set(context.CachedResponse.BodyKeyPrefix, cachedResponseBody, context.CachedResponseValidFor);
                }
                else
                {
                    // Store response and response body together
                    context.CachedResponse.Body = context.ResponseCacheStream.BufferedStream.ToArray();
                    _cache.Set(context.StorageVaryKey ?? context.StorageBaseKey, context.CachedResponse, context.CachedResponseValidFor);
                }
            }
        }

        internal void OnResponseStarting(ResponseCachingContext context)
        {
            if (!context.ResponseStarted)
            {
                context.ResponseStarted = true;
                context.ResponseTime = _options.SystemClock.UtcNow;

                FinalizeCachingHeaders(context);
            }
        }

        internal void ShimResponseStream(ResponseCachingContext context)
        {
            // TODO: Consider caching large responses on disk and serving them from there.

            // Shim response stream
            context.OriginalResponseStream = context.HttpContext.Response.Body;
            context.ResponseCacheStream = new ResponseCacheStream(context.OriginalResponseStream, _options.MaximumCachedBodySize);
            context.HttpContext.Response.Body = context.ResponseCacheStream;

            // Shim IHttpSendFileFeature
            context.OriginalSendFileFeature = context.HttpContext.Features.Get<IHttpSendFileFeature>();
            if (context.OriginalSendFileFeature != null)
            {
                context.HttpContext.Features.Set<IHttpSendFileFeature>(new SendFileFeatureWrapper(context.OriginalSendFileFeature, context.ResponseCacheStream));
            }

            // TODO: Move this temporary interface with endpoint to HttpAbstractions
            context.HttpContext.AddResponseCachingFeature();
        }

        internal static void UnshimResponseStream(ResponseCachingContext context)
        {
            // Unshim response stream
            context.HttpContext.Response.Body = context.OriginalResponseStream;

            // Unshim IHttpSendFileFeature
            context.HttpContext.Features.Set(context.OriginalSendFileFeature);

            // TODO: Move this temporary interface with endpoint to HttpAbstractions
            context.HttpContext.RemoveResponseCachingFeature();
        }

        internal static bool ConditionalRequestSatisfied(ResponseCachingContext context)
        {
            var cachedResponseHeaders = context.CachedResponseHeaders;
            var ifNoneMatchHeader = context.TypedRequestHeaders.IfNoneMatch;

            if (ifNoneMatchHeader != null)
            {
                if (ifNoneMatchHeader.Count == 1 && ifNoneMatchHeader[0].Equals(EntityTagHeaderValue.Any))
                {
                    return true;
                }

                if (cachedResponseHeaders.ETag != null)
                {
                    foreach (var tag in ifNoneMatchHeader)
                    {
                        if (cachedResponseHeaders.ETag.Compare(tag, useStrongComparison: true))
                        {
                            return true;
                        }
                    }
                }
            }
            else if ((cachedResponseHeaders.LastModified ?? cachedResponseHeaders.Date) <= context.TypedRequestHeaders.IfUnmodifiedSince)
            {
                return true;
            }

            return false;
        }

        // Split by commas and normalize order and casing
        internal static StringValues GetNormalizedHeaderStringValues(StringValues stringValues)
        {
            var commaFound = false;

            foreach (var value in stringValues)
            {
                if (value.Contains(","))
                {
                    commaFound = true;
                    break;
                }
            }

            if (!commaFound)
            {
                return GetOrderCasingNormalizedStringValues(stringValues);
            }
            else
            {
                var headers = new List<string>(stringValues.Count);
                foreach (var value in stringValues)
                {
                    foreach (var header in value.Split(','))
                    {
                        headers.Add(header.Trim().ToUpperInvariant());
                    }
                }

                // Since the casing has already been normalized, use Ordinal comparison
                headers.Sort(StringComparer.Ordinal);

                return new StringValues(headers.ToArray());
            }
        }

        // Normalize order and casing
        internal static StringValues GetOrderCasingNormalizedStringValues(StringValues stringValues)
        {
            if (stringValues.Count == 1)
            {
                return new StringValues(stringValues.ToString().ToUpperInvariant());
            }
            else
            {
                var originalArray = stringValues.ToArray();
                var newArray = new string[originalArray.Length];

                for (int i = 0; i < originalArray.Length; i++)
                {
                    newArray[i] = originalArray[i].ToUpperInvariant();
                }

                // Since the casing has already been normalized, use Ordinal comparison
                Array.Sort(newArray, StringComparer.Ordinal);

                return new StringValues(newArray);
            }
        }
    }
}
