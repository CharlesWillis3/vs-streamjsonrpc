// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

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
    /// Provides a builder interface for adding type shape providers.
    /// </summary>
    public interface ICompositeTypeShapeProviderBuilder
    {
        /// <summary>
        /// Adds a single type shape provider to the builder.
        /// </summary>
        /// <param name="provider">The type shape provider to add.</param>
        /// <returns>The current builder instance.</returns>
        ICompositeTypeShapeProviderBuilder Add(ITypeShapeProvider provider);

        /// <summary>
        /// Adds a range of type shape providers to the builder.
        /// </summary>
        /// <param name="providers">The collection of type shape providers to add.</param>
        /// <returns>The current builder instance.</returns>
        ICompositeTypeShapeProviderBuilder AddRange(IEnumerable<ITypeShapeProvider> providers);

        /// <summary>
        /// Adds a reflection-based type shape provider to the builder.
        /// </summary>
        /// <param name="useReflectionEmit">A value indicating whether to use Reflection.Emit for dynamic type generation.</param>
        /// <returns>The current builder instance.</returns>
        ICompositeTypeShapeProviderBuilder AddReflectionTypeShapeProvider(bool useReflectionEmit);
    }
}
