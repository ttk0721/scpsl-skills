using System;
using System.Collections.Generic;
using System.Net.Http;

namespace SlKit;

/// <summary>The default request headers of one <see cref="SlHttpClient"/>: copied and checked once at creation, added to each request that lacks them. Immutable.</summary>
internal sealed class HttpDefaultHeaders
{
    private readonly KeyValuePair<string, string>[] _headers;

    private HttpDefaultHeaders(KeyValuePair<string, string>[] headers)
    {
        _headers = headers;
    }

    /// <summary>Copies <paramref name="headers"/> and checks that each can be a default request header.</summary>
    /// <param name="headers">The client's <see cref="SlHttpOptions.DefaultHeaders"/>.</param>
    /// <returns>An immutable copy.</returns>
    /// <exception cref="ArgumentException">A header name is empty or belongs on the content.</exception>
    internal static HttpDefaultHeaders Copy(IDictionary<string, string> headers)
    {
        return new HttpDefaultHeaders(CopyHeaders(headers));
    }

    /// <summary>Adds each default header the request does not have yet.</summary>
    /// <param name="request">The request about to be sent.</param>
    internal void AddTo(HttpRequestMessage request)
    {
        for (int i = 0; i < _headers.Length; i++)
        {
            if (!request.Headers.Contains(_headers[i].Key))
            {
                request.Headers.TryAddWithoutValidation(_headers[i].Key, _headers[i].Value);
            }
        }
    }

    private static KeyValuePair<string, string>[] CopyHeaders(IDictionary<string, string> headers)
    {
        if (headers.Count == 0)
        {
            return Array.Empty<KeyValuePair<string, string>>();
        }

        List<KeyValuePair<string, string>> copy = new List<KeyValuePair<string, string>>(headers.Count);
        using (HttpRequestMessage probe = new HttpRequestMessage())
        {
            foreach (KeyValuePair<string, string> header in headers)
            {
                // Fail at creation, on the plugin's own thread, rather than on every request later.
                if (string.IsNullOrEmpty(header.Key) || !probe.Headers.TryAddWithoutValidation(header.Key, header.Value ?? string.Empty))
                {
                    throw new ArgumentException("'" + header.Key + "' cannot be a default request header (content headers belong on the request's content).", nameof(headers));
                }

                copy.Add(new KeyValuePair<string, string>(header.Key, header.Value ?? string.Empty));
            }
        }

        return copy.ToArray();
    }
}
