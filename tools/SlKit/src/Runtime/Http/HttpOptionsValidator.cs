using System;

namespace SlKit;

/// <summary>The limits an <see cref="SlHttpClient"/> accepts in its <see cref="SlHttpOptions"/>.</summary>
internal static class HttpOptionsValidator
{
    /// <summary>Throws when a value of <paramref name="options"/> is out of range; checks them in a fixed order, the first failure wins.</summary>
    /// <param name="options">The options a client is created from.</param>
    /// <exception cref="ArgumentOutOfRangeException">A timeout, concurrency, queue or size limit is out of range.</exception>
    internal static void Validate(SlHttpOptions options)
    {
        TimeSpan timeout = options.Timeout;
        if (timeout != System.Threading.Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SlHttpOptions.Timeout must be positive or Timeout.InfiniteTimeSpan.");
        }

        TimeSpan queueTimeout = options.QueueTimeout;
        if (queueTimeout != System.Threading.Timeout.InfiniteTimeSpan && queueTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SlHttpOptions.QueueTimeout must be zero, positive or Timeout.InfiniteTimeSpan.");
        }

        if (options.MaxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SlHttpOptions.MaxConcurrency must be at least 1.");
        }

        if (options.MaxQueued < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SlHttpOptions.MaxQueued must not be negative.");
        }

        if (options.MaxResponseBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SlHttpOptions.MaxResponseBytes must not be negative.");
        }
    }
}
