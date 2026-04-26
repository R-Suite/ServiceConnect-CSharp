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

    private static string Render(object value)
    {
        return value switch
        {
            null => "null",
            byte[] bytes => "\"" + Encoding.UTF8.GetString(bytes).Replace("\"", "\\\"") + "\"",
            string s => "\"" + s.Replace("\"", "\\\"") + "\"",
            IDictionary<string, object> dict => RenderDictionary(dict),
            IDictionary nonGeneric => RenderNonGenericDictionary(nonGeneric),
            IEnumerable seq => RenderEnumerable(seq),
            _ => RenderScalar(value),
        };
    }

    private static string RenderDictionary(IDictionary<string, object> dict)
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
            sb.Append('"').Append(kv.Key.Replace("\"", "\\\"")).Append("\":").Append(Render(kv.Value));
        }
        return sb.Append('}').ToString();
    }

    private static string RenderNonGenericDictionary(IDictionary dict)
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
            sb.Append('"').Append(keyStr.Replace("\"", "\\\"")).Append("\":").Append(Render(kv.Value!));
        }
        return sb.Append('}').ToString();
    }

    private static string RenderEnumerable(IEnumerable seq)
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
            sb.Append(Render(item!));
        }
        return sb.Append(']').ToString();
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
