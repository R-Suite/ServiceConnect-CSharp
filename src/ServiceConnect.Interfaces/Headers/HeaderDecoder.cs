using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace ServiceConnect.Interfaces;

/// <summary>
/// Converts transport header values into their string representation.
/// </summary>
public static class HeaderDecoder
{
    /// <summary>
    /// Decodes a header value to its string representation. Handles the two canonical
    /// wire shapes (UTF-8 <see cref="byte"/>[] from RabbitMQ clients; <see cref="string"/>
    /// from in-process paths). Nested <see cref="IDictionary{TKey,TValue}"/> and
    /// <see cref="IEnumerable"/> values (such as AMQP x-table headers) are rendered as
    /// JSON-shaped strings so every nested value is preserved. Falls back to the type's
    /// FullName if rendering throws, which prevents a bad header from taking down the
    /// consumer host via an infinite nack-requeue cycle.
    /// </summary>
    /// <remarks>
    /// <b>String-input identity contract.</b> When <paramref name="value"/> is already
    /// a <see cref="string"/>, this method returns the same instance unchanged
    /// (no copy, no normalization). The consumer-side eager-decode optimisation in
    /// <c>RabbitMqConsumerHost.CopyInboundHeaders</c> and
    /// <c>InboundMessageProcessor.ProcessAsync</c> relies on this: by storing the
    /// UTF-8-decoded string back into the headers dictionary on copy, every subsequent
    /// <see cref="Decode"/> call short-circuits to the same string instance instead of
    /// re-running <see cref="System.Text.Encoding.UTF8"/>.GetString on each read.
    /// Future changes that wrap the string fast-path (e.g. <see cref="string.Intern"/>,
    /// case normalisation) would break that optimisation and the regression test at
    /// <c>InboundHeaderDecodeCachingTests.HeaderDecoder_Decode_ReturnsCachedString...</c>.
    /// </remarks>
    /// <param name="value">The raw header value.</param>
    /// <returns>The decoded string, or <see langword="null"/> when the value is <see langword="null"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string? Decode(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is byte[] bytes)
        {
            return Encoding.UTF8.GetString(bytes);
        }

        if (value is string str)
        {
            return str;
        }

        try
        {
            return Render(value);
        }
        catch
        {
            // Defensive fallback: a custom IEnumerable that throws on iteration must not
            // bring the trace pipeline down. Type name is enough to identify the slot.
            return value.GetType().FullName;
        }
    }

    private const int MaxDepth = 32;

    private static string Render(object value, int depth = 0)
    {
        // Guard against pathologically nested input (e.g. crafted AMQP x-table
        // headers). Without the limit a 1000-level chain would StackOverflow the
        // consumer thread; with it Decode's catch produces a graceful type-name
        // fallback instead.
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException(
                $"Header value exceeds nesting depth {MaxDepth}.");
        }

        return value switch
        {
            null => "null",
            byte[] bytes => "\"" + EscapeJsonString(Encoding.UTF8.GetString(bytes)) + "\"",
            string s => "\"" + EscapeJsonString(s) + "\"",
            IDictionary<string, object> dict => RenderDictionary(dict, depth + 1),
            IDictionary nonGeneric => RenderNonGenericDictionary(nonGeneric, depth + 1),
            IEnumerable seq => RenderEnumerable(seq, depth + 1),
            _ => RenderScalar(value),
        };
    }

    private static string RenderDictionary(IDictionary<string, object> dict, int depth)
    {
        var sb = new StringBuilder("{");
        bool first = true;
        foreach (var kv in dict)
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            sb.Append('"').Append(EscapeJsonString(kv.Key)).Append("\":").Append(Render(kv.Value, depth));
        }
        return sb.Append('}').ToString();
    }

    private static string RenderNonGenericDictionary(IDictionary dict, int depth)
    {
        var sb = new StringBuilder("{");
        bool first = true;
        foreach (DictionaryEntry kv in dict)
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            var keyStr = kv.Key?.ToString() ?? "null";
            sb.Append('"').Append(EscapeJsonString(keyStr)).Append("\":").Append(Render(kv.Value!, depth));
        }
        return sb.Append('}').ToString();
    }

    private static string RenderEnumerable(IEnumerable seq, int depth)
    {
        var sb = new StringBuilder("[");
        bool first = true;
        foreach (var item in seq)
        {
            if (!first)
            {
                sb.Append(',');
            }

            first = false;
            sb.Append(Render(item!, depth));
        }
        return sb.Append(']').ToString();
    }

    private static string EscapeJsonString(string s)
    {
        // Full RFC 8259 escape table. Escaping only " is insufficient — raw control
        // characters inside a JSON string literal cause parse failures downstream.
        var sb = new StringBuilder(s.Length + 2);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
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
        return sb.ToString();
    }

    private static string RenderScalar(object value)
    {
        return value switch
        {
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "null",
        };
    }
}
