// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack;
using PolyType;
using PolyType.Abstractions;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
public sealed partial class NerdbankMessagePackFormatter
{
    internal class FormatterProfile(MessagePackSerializer serializer, ITypeShapeProvider shapeProvider)
    {
        internal MessagePackSerializer Serializer => serializer;

        internal ITypeShapeProvider ShapeProvider => shapeProvider;

        public T? Deserialize<T>(ref MessagePackReader reader, CancellationToken cancellationToken = default)
        {
            return serializer.Deserialize<T>(ref reader, shapeProvider, cancellationToken);
        }

        public T Deserialize<T>(in RawMessagePack pack, CancellationToken cancellationToken = default)
        {
            // TODO: Improve the exception
            return serializer.Deserialize<T>(pack, shapeProvider, cancellationToken)
                ?? throw new MessagePackSerializationException(Resources.UnexpectedErrorProcessingJsonRpc);
        }

        public object? DeserializeObject(in RawMessagePack pack, Type objectType, CancellationToken cancellationToken = default)
        {
            MessagePackReader reader = new(pack);
            return serializer.DeserializeObject(
                ref reader,
                shapeProvider.Resolve(objectType),
                cancellationToken);
        }

        public void Serialize<T>(ref MessagePackWriter writer, T? value, CancellationToken cancellationToken = default)
        {
            serializer.Serialize(ref writer, value, shapeProvider, cancellationToken);
        }

        public void SerializeObject(ref MessagePackWriter writer, object? value, Type objectType, CancellationToken cancellationToken = default)
        {
            if (value is null)
            {
                writer.WriteNil();
                return;
            }

            serializer.SerializeObject(ref writer, value, shapeProvider.Resolve(objectType), cancellationToken);
        }

        public void SerializeObject(ref MessagePackWriter writer, object? value, CancellationToken cancellationToken = default)
        {
            if (value is null)
            {
                writer.WriteNil();
                return;
            }

            serializer.SerializeObject(ref writer, value, shapeProvider.Resolve(value.GetType()), cancellationToken);
        }
    }
}
