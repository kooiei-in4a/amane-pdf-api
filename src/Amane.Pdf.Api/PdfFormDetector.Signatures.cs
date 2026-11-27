using System.Buffers;
using System.Buffers.Text;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Amane.Pdf.Api;

internal static partial class PdfFormDetector
{
    // A negative byte scan is sufficient only when qpdf also reports no compressed objects.
    // A positive scan may come from any part of the file and requires structural inspection.
    internal static async Task<bool> MayHaveSignatureNameAsync(string path, CancellationToken token)
    {
        // A name byte may take three bytes when escaped as #xx.
        const int overlap = 1 + 3 * 12; // /DocTimeStamp
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024 + overlap];
        var remaining = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(remaining), token);
            var available = remaining + read;
            var searchLength = read == 0 ? available : Math.Max(0, available - overlap);
            if (MayHaveSignatureName(buffer.AsSpan(0, available), searchLength)) return true;
            if (read == 0) return false;
            buffer.AsSpan(searchLength, available - searchLength).CopyTo(buffer);
            remaining = available - searchLength;
        }
    }

    private static bool MayHaveSignatureName(ReadOnlySpan<byte> bytes, int searchLength)
    {
        while (bytes[..searchLength].IndexOf((byte)'/') is var slash && slash >= 0)
        {
            bytes = bytes[(slash + 1)..];
            searchLength -= slash + 1;
            if (MayStartWithName(bytes, "Sig"u8) || MayStartWithName(bytes, "DocTimeStamp"u8) ||
                MayStartWithName(bytes, "ByteRange"u8)) return true;
        }
        return false;
    }

    private static bool MayStartWithName(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> name)
    {
        foreach (var expected in name)
        {
            if (bytes.IsEmpty) return false;
            var actual = bytes[0];
            bytes = bytes[1..];
            if (actual == (byte)'#')
            {
                if (bytes.Length < 2 || !Utf8Parser.TryParse(bytes[..2], out actual, out var consumed, 'X') ||
                    consumed != 2) return false;
                // The JSON name decoder can see an escaped hash after qpdf decodes it.
                if (actual == (byte)'#') return true;
                bytes = bytes[2..];
            }
            if (actual != expected) return false;
        }
        return true;
    }

    internal static async Task<bool> HasOnlyUncompressedObjectsAsync(Stream stdout, CancellationToken token)
    {
        using var reader = new StreamReader(stdout, leaveOpen: true);
        var hasObjects = false;
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (!line.Contains(": uncompressed; offset = ", StringComparison.Ordinal)) return false;
            hasObjects = true;
        }
        return hasObjects;
    }

    internal static async Task<bool?> ContainsAppliedSignatureAsync(Stream stdout, CancellationToken token)
    {
        // Retain only the current JSON token and nesting state as output is consumed.
        var pipe = PipeReader.Create(stdout, new StreamPipeReaderOptions(bufferSize: 32 * 1024, leaveOpen: true));
        var state = new JsonReaderState(new JsonReaderOptions { MaxDepth = int.MaxValue });
        var scan = new SignatureScan();
        var minimum = 1;
        try
        {
            while (true)
            {
                var result = await pipe.ReadAtLeastAsync(minimum, token);
                var reader = new Utf8JsonReader(result.Buffer, result.IsCompleted, state);
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (scan.Read(ref reader)) return true;
                }
                state = reader.CurrentState;
                var buffered = result.Buffer.Length;
                pipe.AdvanceTo(result.Buffer.GetPosition(reader.BytesConsumed), result.Buffer.End);
                if (result.IsCompleted) return scan.SawIndirectObject ? false : null;
                // If a token spans several buffers, avoid scanning its whole prefix after every read.
                minimum = reader.BytesConsumed == 0 ? (int)Math.Min(int.MaxValue, 2 * buffered) : 1;
            }
        }
        catch (JsonException) { return null; }
        finally { await pipe.CompleteAsync(); }
    }

    private enum SignatureKey { Other, Type, Filter, Contents, ByteRange }

    [Flags]
    private enum SignatureMembers { None = 0, Filter = 1, Contents = 2, ByteRange = 4, All = 7 }

    private struct SignatureFrame
    {
        internal SignatureMembers Members;
        internal bool IntegerByteRange;
    }

    private sealed class SignatureScan
    {
        private readonly List<SignatureFrame> frames = [];
        private bool inQpdf;
        private bool inIndirectObject;
        private SignatureKey property;
        internal bool SawIndirectObject { get; private set; }

        internal bool Read(ref Utf8JsonReader reader)
        {
            var kind = reader.TokenType;
            if (reader.CurrentDepth == 1 && kind == JsonTokenType.PropertyName)
                inQpdf = reader.ValueTextEquals("qpdf"u8);
            // The keys of qpdf[1] identify indirect objects. Its trailer entry is separate.
            if (inQpdf && reader.CurrentDepth == 3 && kind == JsonTokenType.PropertyName)
            {
                inIndirectObject = StartsWith(ref reader, "obj:"u8);
                SawIndirectObject |= inIndirectObject;
                return false;
            }
            if (!inIndirectObject) return false;

            if (kind == JsonTokenType.PropertyName)
            {
                property = ReadKey(ref reader);
                return false;
            }
            if (kind is JsonTokenType.EndObject or JsonTokenType.EndArray)
            {
                var finished = frames[^1];
                frames.RemoveAt(frames.Count - 1);
                if (frames.Count == 0) inIndirectObject = false;
                if (kind == JsonTokenType.EndObject && finished.Members == SignatureMembers.All) return true;
                if (kind == JsonTokenType.EndArray && finished.IntegerByteRange)
                    CollectionsMarshal.AsSpan(frames)[^1].Members |= SignatureMembers.ByteRange;
                return false;
            }

            if (frames.Count > 0)
            {
                ref var parent = ref CollectionsMarshal.AsSpan(frames)[^1];
                if (parent.IntegerByteRange && (kind != JsonTokenType.Number || !reader.TryGetInt64(out _)))
                    parent.IntegerByteRange = false;
                if (kind == JsonTokenType.String)
                {
                    if (property == SignatureKey.Type &&
                        (PdfNameEquals(ref reader, "/Sig"u8) || PdfNameEquals(ref reader, "/DocTimeStamp"u8))) return true;
                    if (property == SignatureKey.Filter &&
                        (StartsWith(ref reader, "/"u8) || StartsWith(ref reader, "n:"u8)))
                        parent.Members |= SignatureMembers.Filter;
                    if (property == SignatureKey.Contents &&
                        (StartsWith(ref reader, "b:"u8) || StartsWith(ref reader, "u:"u8)))
                        parent.Members |= SignatureMembers.Contents;
                }
            }
            if (kind is JsonTokenType.StartObject or JsonTokenType.StartArray)
                frames.Add(new() { IntegerByteRange = kind == JsonTokenType.StartArray && property == SignatureKey.ByteRange });
            property = SignatureKey.Other;
            return false;
        }

        private static SignatureKey ReadKey(ref Utf8JsonReader reader)
        {
            if (PdfNameEquals(ref reader, "/Type"u8)) return SignatureKey.Type;
            if (PdfNameEquals(ref reader, "/Filter"u8)) return SignatureKey.Filter;
            if (PdfNameEquals(ref reader, "/Contents"u8)) return SignatureKey.Contents;
            if (PdfNameEquals(ref reader, "/ByteRange"u8)) return SignatureKey.ByteRange;
            return SignatureKey.Other;
        }

        private static bool PdfNameEquals(ref Utf8JsonReader reader, ReadOnlySpan<byte> expected)
        {
            if (reader.ValueTextEquals(expected)) return true;
            var length = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
            if (length > expected.Length * (reader.ValueIsEscaped ? 18 : 3)) return false;
            if (!reader.HasValueSequence && !reader.ValueIsEscaped && !reader.ValueSpan.Contains((byte)'#')) return false;
            var name = Name(reader.GetString()!);
            if (name.Length != expected.Length) return false;
            for (var i = 0; i < name.Length; i++)
                if (name[i] != expected[i]) return false;
            return true;
        }

        private static bool StartsWith(ref Utf8JsonReader reader, ReadOnlySpan<byte> prefix)
        {
            if (!reader.HasValueSequence) return reader.ValueSpan.StartsWith(prefix);
            if (reader.ValueSequence.Length < prefix.Length) return false;
            Span<byte> first = stackalloc byte[prefix.Length];
            reader.ValueSequence.Slice(0, first.Length).CopyTo(first);
            return first.SequenceEqual(prefix);
        }
    }
}
