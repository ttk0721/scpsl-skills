using System;
using System.Globalization;
using System.Text;

namespace Examples.AddingExternalIntegrations
{
    /// <summary>
    /// Builds the JSON for a Discord webhook by hand (no JSON library is referenced). Pure code, no
    /// game types: sanitising happens when an entry is queued on the main thread, escaping when the
    /// body is built.
    /// </summary>
    internal static class DiscordPayload
    {
        /// <summary>Webhook content limit is 2000 characters; the rest is headroom for the footer.</summary>
        public const int MaxContentChars = 1900;

        /// <summary>Longest single line. Keeps one entry always able to fit in a message.</summary>
        public const int MaxLineChars = 300;

        /// <summary>
        /// Makes untrusted text (a nickname, a ban reason) safe to embed: one line, no control or
        /// format characters (bidi overrides, zero-width), no lone surrogates, bounded length.
        /// </summary>
        /// <param name="text">Raw text, may be null.</param>
        /// <param name="maxChars">Upper bound on the result length.</param>
        /// <returns>A sanitised string, never null.</returns>
        public static string OneLine(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || maxChars <= 0)
                return string.Empty;

            StringBuilder sb = new StringBuilder(Math.Min(text.Length, maxChars));
            for (int i = 0; i < text.Length && sb.Length < maxChars; i++)
            {
                char c = text[i];
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    if (sb.Length + 2 > maxChars)
                        break;

                    sb.Append(c).Append(text[i + 1]);
                    i++;
                    continue;
                }

                if (char.IsSurrogate(c))
                    continue;

                if (char.IsControl(c))
                {
                    sb.Append(' ');
                    continue;
                }

                if (char.GetUnicodeCategory(c) == UnicodeCategory.Format)
                    continue;

                sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Wraps untrusted text in an inline code span. Inside code Discord renders no markdown and
        /// no mention syntax, which is a second line of defence behind <c>allowed_mentions</c>.
        /// </summary>
        /// <param name="text">Raw text, may be null.</param>
        /// <param name="maxChars">Upper bound on the inner text.</param>
        /// <returns>The span, for example <c>`name`</c>.</returns>
        public static string Code(string text, int maxChars)
        {
            return "`" + OneLine(text, maxChars).Replace('`', '\'') + "`";
        }

        /// <summary>How many of the first entries fit in one message (at least 1 when any exist).</summary>
        /// <param name="entries">Candidates, oldest first.</param>
        /// <returns>The count to send.</returns>
        public static int CountFitting(OutboxEntry[] entries)
        {
            int used = 0;
            int count = 0;
            foreach (OutboxEntry entry in entries)
            {
                int length = LineOf(entry).Length + 1;
                if (count > 0 && used + length > MaxContentChars)
                    break;

                used += length;
                count++;
            }

            return count;
        }

        /// <summary>
        /// The request body. <c>allowed_mentions.parse = []</c> turns every mention into plain text, so a
        /// nickname such as <c>@everyone</c> pings nobody. The batch id is shown as the last line:
        /// Discord has no idempotency key, so a duplicate after an ambiguous timeout is at least recognisable.
        /// </summary>
        /// <param name="batchId">Stable id of this batch, the same on every retry.</param>
        /// <param name="entries">The pinned batch.</param>
        /// <returns>A JSON object.</returns>
        public static string BuildBody(string batchId, OutboxEntry[] entries)
        {
            StringBuilder sb = new StringBuilder(128 + (entries.Length * 96));
            sb.Append("{\"content\":\"");
            foreach (OutboxEntry entry in entries)
            {
                AppendEscaped(sb, LineOf(entry));
                sb.Append("\\n");
            }

            AppendEscaped(sb, "-# batch " + batchId);
            sb.Append("\",\"allowed_mentions\":{\"parse\":[]}}");
            return sb.ToString();
        }

        /// <summary>
        /// Escapes a string for the inside of a JSON string literal: quote, backslash, every control
        /// character below U+0020, and lone surrogates (invalid in UTF-8, so the receiver would answer 400).
        /// </summary>
        /// <param name="sb">Destination.</param>
        /// <param name="value">Text to escape, may be null.</param>
        public static void AppendEscaped(StringBuilder sb, string value)
        {
            if (value == null)
                return;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                        {
                            sb.Append(c).Append(value[i + 1]);
                            i++;
                        }
                        else if (char.IsSurrogate(c))
                        {
                            sb.Append("\\ufffd");
                        }
                        else if (c < ' ')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }
        }

        // The timestamp is the event time, not the send time: a batch that sat in the outbox during an
        // outage still shows when each thing happened (Discord renders <t:unix:T> in the reader's zone).
        private static string LineOf(OutboxEntry entry)
        {
            return "<t:" + entry.UnixSeconds.ToString(CultureInfo.InvariantCulture) + ":T> " + entry.Text;
        }
    }
}
