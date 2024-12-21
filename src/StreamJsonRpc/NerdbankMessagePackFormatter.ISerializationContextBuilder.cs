// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
/// <remarks>
/// The MessagePack implementation used here comes from https://github.com/AArnott/Nerdbank.MessagePack.
/// </remarks>
public sealed partial class NerdbankMessagePackFormatter
{
    /// <summary>
    /// Provides a builder interface for configuring the serialization context.
    /// </summary>
    public interface IFormatterContextBuilder
    {
        /// <summary>
        /// Gets the type shape provider builder.
        /// </summary>
        ICompositeTypeShapeProviderBuilder TypeShapeProviderBuilder { get; }

        /// <summary>
        /// Registers a type converter for asynchronous enumerable types.
        /// </summary>
        /// <typeparam name="TElement">The element type of the asynchronous enumerable.</typeparam>
        void RegisterAsyncEnumerableTypeConverter<TElement>();

        /// <summary>
        /// Registers a custom converter for a specific type.
        /// </summary>
        /// <typeparam name="T">The type for which the converter is registered.</typeparam>
        /// <param name="converter">The converter to register.</param>
        void RegisterConverter<T>(MessagePackConverter<T> converter);

        /// <summary>
        /// Registers known subtypes for a base type.
        /// </summary>
        /// <typeparam name="TBase">The base type for which the subtypes are registered.</typeparam>
        /// <param name="mapping">The mapping of known subtypes.</param>
        void RegisterKnownSubTypes<TBase>(KnownSubTypeMapping<TBase> mapping);

        /// <summary>
        /// Registers a type converter for progress types.
        /// </summary>
        /// <typeparam name="TProgress">The type of the progress to register.</typeparam>
        void RegisterProgressTypeConverter<TProgress>();
    }
}
