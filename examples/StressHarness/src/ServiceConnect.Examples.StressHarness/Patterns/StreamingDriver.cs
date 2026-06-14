using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ServiceConnect.Examples.StressHarness.Assertions;
using ServiceConnect.Examples.StressHarness.Contracts.Messages;
using ServiceConnect.Examples.StressHarness.Patterns.Handlers;
using ServiceConnect.Interfaces;

namespace ServiceConnect.Examples.StressHarness.Patterns;

/// <summary>
/// Drives a single chunked stream from <c>sender</c> to <c>receiver</c> per
/// direction and asserts the byte sequence the receiver reassembles is identical
/// to what was written, by SHA-256 digest. The payload is a deterministic
/// JSON-serialised <see cref="DocumentUploaded"/> sized to comfortably exceed a
/// single transport packet so the chunked write path is exercised end-to-end.
/// </summary>
/// <remarks>
/// <para>
/// The driver serialises the message itself rather than relying on the framework's
/// serializer because <see cref="IBus.CreateStream{T}"/> writes raw bytes — the
/// caller is responsible for producing a byte sequence the framework's stream
/// processor can deserialise on receive. The serialiser options here mirror the
/// framework's defaults (relaxed Unicode escaping, emit nulls, case-sensitive
/// property names) so the framework's <c>StreamProcessor.Deserialize</c> consumes
/// the same bytes the driver hashed.
/// </para>
/// <para>
/// Flow correlation rides on <see cref="Message.CorrelationId"/> because the
/// stream API exposes no caller-header pathway. The receiver-side handler reads
/// the correlation id from the deserialised message body and indexes its
/// observation under it.
/// </para>
/// </remarks>
public sealed class StreamingDriver(FlowAccounting accounting, StreamObservations observations) : IPatternDriver
{
    public string Name => "streaming";
    public bool RequiresPersistence => false;

    // Mirror the framework's SystemTextJsonMessageSerializer wire-compat settings so
    // the bytes the driver sends round-trip cleanly through StreamProcessor.Deserialize.
    // Mismatched options would produce JSON the framework can't deserialise, surfacing
    // as a stream-completion warning rather than a usable integrity check.
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = false,
        IncludeFields = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        MaxDepth = 32,
    };

    // Payload size and chunk boundaries: roughly 48 KiB of binary payload inflates
    // (under base64 in JSON) to a serialised message of ~65 KiB, which is split into
    // three writes so the chunked send path runs at least three SendBytesAsync round
    // trips against the broker. The exact byte counts are not load-bearing — the
    // assertion compares end-to-end SHA-256 digests — but the magnitude is chosen so
    // the test is meaningful (the single-packet fast path would not exercise the
    // multi-packet reassembly machinery).
    private const int PayloadSeedBytes = 48 * 1024;

    [SuppressMessage("Style", "IDE0060", Justification = "Threaded through to satisfy IPatternDriver contract; the stream targets the receiver bus's queue regardless of which IBus reference the harness passes.")]
    public async Task<FlowResult> RunFlowAsync(IBus sender, IBus receiver, StressFlowContext context, CancellationToken cancellationToken)
    {
        _ = receiver;
        var sw = Stopwatch.StartNew();
        var failures = new List<string>();

        var receiverEndpoint = context.ExpectedReceiver == BusIdentity.Alpha ? "stress-a.work" : "stress-b.work";
        var receiverTag = context.ExpectedReceiver.ToHeaderValue();

        // ExecuteAsync runs exactly once per fully-reassembled stream — the
        // close-packet → dispatch handshake is single-shot inside the framework's
        // StreamProcessor. One booked invocation aligns with the reconciliation
        // pass at end-of-run.
        accounting.RecordSend(context.FlowId, expectedHandlerInvocations: 1);

        // Deterministic payload from a flow-id-derived seed so the bytes are
        // reproducible across the two directional flows; the SHA digest is what's
        // compared, but determinism makes a failure log easier to interpret.
        var payload = new byte[PayloadSeedBytes];
        new Random(unchecked(context.FlowId.GetHashCode())).NextBytes(payload);

        var message = new DocumentUploaded(context.FlowId)
        {
            FileName = string.Create(CultureInfo.InvariantCulture, $"flow-{context.FlowId:N}.bin"),
            Payload = payload,
        };

        // Serialise to a byte[] so the driver can hash the wire bytes once and feed
        // the same buffer to the chunk loop without re-encoding. UTF-8 is the wire
        // format used by SystemTextJsonMessageSerializer.
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(message, WireOptions);
        var sentDigest = Convert.ToHexStringLower(SHA256.HashData(jsonBytes));

        // Three chunks: length/3, length/3, remainder. The exact split isn't load-
        // bearing — the receiver concatenates packets in PacketNumber order — but
        // a deterministic split keeps failures reproducible.
        var third = jsonBytes.Length / 3;

        try
        {
            await using (var stream = sender.CreateStream<DocumentUploaded>(receiverEndpoint))
            {
                await stream.WriteAsync(jsonBytes.AsMemory(0, third), cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(jsonBytes.AsMemory(third, third), cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(jsonBytes.AsMemory(2 * third, jsonBytes.Length - (2 * third)), cancellationToken).ConfigureAwait(false);
                await stream.CloseAsync(cancellationToken).ConfigureAwait(false);
            }

            var observation = await observations.AwaitAsync(context.FlowId, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(observation.Sha256, sentDigest, StringComparison.Ordinal))
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"streaming {context.Origin.ToHeaderValue()}->{receiverTag}: expected SHA-256 {sentDigest} but observed {observation.Sha256}"));
            }
            if (observation.Bytes != jsonBytes.Length)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"streaming {context.Origin.ToHeaderValue()}->{receiverTag}: expected {jsonBytes.Length} bytes but observed {observation.Bytes}"));
            }
            if (!string.Equals(observation.BusTag, receiverTag, StringComparison.Ordinal))
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture,
                    $"streaming {context.Origin.ToHeaderValue()}->{receiverTag}: expected dispatch on '{receiverTag}' but observed '{observation.BusTag}'"));
            }
        }
        catch (OperationCanceledException)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture,
                $"streaming {context.Origin.ToHeaderValue()}->{receiverTag}: stream did not reassemble within {context.FlowTimeout}"));
        }

        sw.Stop();
        return failures.Count == 0
            ? FlowResult.Pass(sw.Elapsed, sent: 1, handled: 1)
            : FlowResult.Fail(sw.Elapsed, sent: 1, handled: 0, [.. failures]);
    }
}
