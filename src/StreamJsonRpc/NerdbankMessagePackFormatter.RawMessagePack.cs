// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using Nerdbank.MessagePack;
using PolyType.Abstractions;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
/// <remarks>
/// The MessagePack implementation used here comes from https://github.com/AArnott/Nerdbank.MessagePack.
/// </remarks>
public partial class NerdbankMessagePackFormatter
{
    private struct RawMessagePack
    {
        private readonly ReadOnlySequence<byte> rawSequence;

        private readonly ReadOnlyMemory<byte> rawMemory;

        private RawMessagePack(ReadOnlySequence<byte> raw)
        {
            this.rawSequence = raw;
            this.rawMemory = default;
        }

        private RawMessagePack(ReadOnlyMemory<byte> raw)
        {
            this.rawSequence = default;
            this.rawMemory = raw;
        }

        internal readonly bool IsDefault => this.rawMemory.IsEmpty && this.rawSequence.IsEmpty;

        public override readonly string ToString() => "<raw msgpack>";

        /// <summary>
        /// Reads one raw messagepack token.
        /// </summary>
        /// <param name="reader">The reader to use.</param>
        /// <param name="copy"><see langword="true"/> if the token must outlive the lifetime of the reader's underlying buffer; <see langword="false"/> otherwise.</param>
        /// <param name="context">The serialization context to use.</param>
        /// <returns>The raw messagepack slice.</returns>
        internal static RawMessagePack ReadRaw(ref MessagePackReader reader, bool copy, Nerdbank.MessagePack.SerializationContext context)
        {
            SequencePosition initialPosition = reader.Position;
            reader.Skip(context);
            ReadOnlySequence<byte> slice = reader.Sequence.Slice(initialPosition, reader.Position);
            return copy ? new RawMessagePack(slice.ToArray()) : new RawMessagePack(slice);
        }

        internal readonly void WriteRaw(ref MessagePackWriter writer)
        {
            if (this.rawSequence.IsEmpty)
            {
                writer.WriteRaw(this.rawMemory.Span);
            }
            else
            {
                writer.WriteRaw(this.rawSequence);
            }
        }

        internal readonly object? Deserialize(Type type, FormatterContext options)
        {
            MessagePackReader reader = this.rawSequence.IsEmpty
                ? new MessagePackReader(this.rawMemory)
                : new MessagePackReader(this.rawSequence);

            return options.Serializer.DeserializeObject(
                ref reader,
                options.ShapeProvider.Resolve(type));
        }

        internal readonly T Deserialize<T>(FormatterContext options)
        {
            MessagePackReader reader = this.rawSequence.IsEmpty
                ? new MessagePackReader(this.rawMemory)
                : new MessagePackReader(this.rawSequence);

            return options.Serializer.Deserialize<T>(ref reader, options.ShapeProvider)
                ?? throw new MessagePackSerializationException(Resources.FailureDeserializingJsonRpc);
        }
    }
}
