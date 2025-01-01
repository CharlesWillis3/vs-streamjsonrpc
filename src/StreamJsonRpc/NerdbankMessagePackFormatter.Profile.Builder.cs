﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using Nerdbank.MessagePack;
using PolyType;
using StreamJsonRpc.Reflection;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
public partial class NerdbankMessagePackFormatter
{
    /// <summary>
    /// A serialization profile for the <see cref="NerdbankMessagePackFormatter"/>.
    /// </summary>
    public partial class Profile
    {
        /// <summary>
        /// Provides methods to build a serialization profile for the <see cref="NerdbankMessagePackFormatter"/>.
        /// </summary>
        public class Builder
        {
            private readonly Profile baseProfile;

            private ImmutableArray<ITypeShapeProvider>.Builder? typeShapeProvidersBuilder = null;

            /// <summary>
            /// Initializes a new instance of the <see cref="Builder"/> class.
            /// </summary>
            /// <param name="baseProfile">The base profile to build upon.</param>
            internal Builder(Profile baseProfile)
            {
                this.baseProfile = baseProfile;
            }

            /// <summary>
            /// Adds a type shape provider to the profile.
            /// </summary>
            /// <param name="provider">The type shape provider to add.</param>
            public void AddTypeShapeProvider(ITypeShapeProvider provider)
            {
                this.typeShapeProvidersBuilder ??= ImmutableArray.CreateBuilder<ITypeShapeProvider>(initialCapacity: 3);
                this.typeShapeProvidersBuilder.Add(provider);
            }

            /// <summary>
            /// Registers an async enumerable type with the profile.
            /// </summary>
            /// <typeparam name="TEnumerable">The type of the async enumerable.</typeparam>
            /// <typeparam name="TElement">The type of the elements in the async enumerable.</typeparam>
            public void RegisterAsyncEnumerableType<TEnumerable, TElement>()
                where TEnumerable : IAsyncEnumerable<TElement>
            {
                MessagePackConverter<TEnumerable> converter = AsyncEnumerableConverterResolver.GetConverter<TEnumerable>();
                this.baseProfile.Serializer.RegisterConverter(converter);

                MessagePackConverter<MessageFormatterEnumerableTracker.EnumeratorResults<TElement>> resultConverter = EnumeratorResultsConverterResolver.GetConverter<TElement>();
                this.baseProfile.Serializer.RegisterConverter(resultConverter);
            }

            /// <summary>
            /// Registers an async enumerable type with the profile.
            /// </summary>
            /// <remarks>
            ///     To avoid the cost of reflection, ensure <see cref="IReadOnlyList{T}"/> is
            ///     registered with your type shape provider.
            /// </remarks>
            /// <typeparam name="TElement">The type of the elements in the async enumerable.</typeparam>
            public void RegisterAsyncEnumerableType<TElement>()
            {
                MessagePackConverter<IAsyncEnumerable<TElement>> converter = AsyncEnumerableConverterResolver.GetConverter<IAsyncEnumerable<TElement>>();
                this.baseProfile.Serializer.RegisterConverter(converter);

                MessagePackConverter<MessageFormatterEnumerableTracker.EnumeratorResults<TElement>> resultConverter = EnumeratorResultsConverterResolver.GetConverter<TElement>();
                this.baseProfile.Serializer.RegisterConverter(resultConverter);
            }

            /// <summary>
            /// Registers a converter with the profile.
            /// </summary>
            /// <typeparam name="T">The type the converter handles.</typeparam>
            /// <param name="converter">The converter to register.</param>
            public void RegisterConverter<T>(MessagePackConverter<T> converter)
            {
                this.baseProfile.Serializer.RegisterConverter(converter);
            }

            /// <summary>
            /// Registers known subtypes for a base type with the profile.
            /// </summary>
            /// <typeparam name="TBase">The base type.</typeparam>
            /// <param name="mapping">The mapping of known subtypes.</param>
            public void RegisterKnownSubTypes<TBase>(KnownSubTypeMapping<TBase> mapping)
            {
                this.baseProfile.Serializer.RegisterKnownSubTypes(mapping);
            }

            /// <summary>
            /// Registers a progress type with the profile.
            /// </summary>
            /// <typeparam name="TProgress">The type of the progress.</typeparam>
            /// <typeparam name="TReport">The type of the report.</typeparam>
            public void RegisterProgressType<TProgress, TReport>()
                where TProgress : IProgress<TReport>
            {
                MessagePackConverter<TProgress> converter = ProgressConverterResolver.GetConverter<TProgress>();
                this.baseProfile.Serializer.RegisterConverter(converter);
            }

            /// <summary>
            /// Registers a progress type with the profile.
            /// </summary>
            /// <typeparam name="TReport">The type of the report.</typeparam>
            public void RegisterProgressType<TReport>()
            {
                MessagePackConverter<IProgress<TReport>> converter = ProgressConverterResolver.GetConverter<IProgress<TReport>>();
                this.baseProfile.Serializer.RegisterConverter(converter);
            }

            /// <summary>
            /// Registers an exception type with the profile.
            /// </summary>
            /// <typeparam name="TException">The type of the exception.</typeparam>
            public void RegisterExceptionType<TException>()
                where TException : Exception
            {
                MessagePackConverter<TException> converter = MessagePackExceptionConverterResolver.GetConverter<TException>();
                this.baseProfile.Serializer.RegisterConverter(converter);
            }

            /// <summary>
            /// Registers an RPC marshalable type with the profile.
            /// </summary>
            /// <typeparam name="T">The type to register.</typeparam>
            public void RegisterRpcMarshalableType<T>()
                where T : class
            {
                MessagePackConverter<T> converter = GetRpcMarshalableConverter<T>();
                this.baseProfile.Serializer.RegisterConverter(converter);
            }

            /// <summary>
            /// Registers an observer type with the profile.
            /// </summary>
            /// <remarks>
            ///     <para>
            ///         To register <see cref="IObserver{T}"/> with the profile,
            ///         call this method with the type parameter <typeparamref name="T"/>.
            ///     </para>
            ///     <code>
            ///         RegisterObserver&lt;int&gt;();
            ///     </code>
            /// </remarks>
            /// <typeparam name="T">The type of the observer.</typeparam>
            public void RegisterObserver<T>()
            {
                MessagePackConverter<IObserver<T>> converter = GetRpcMarshalableConverter<IObserver<T>>();
                this.baseProfile.Serializer.RegisterConverter(converter);
            }

            /// <summary>
            /// Builds the formatter profile.
            /// </summary>
            /// <returns>The built formatter profile.</returns>
            public Profile Build()
            {
                if (this.typeShapeProvidersBuilder is null || this.typeShapeProvidersBuilder.Count < 1)
                {
                    return this.baseProfile;
                }

                // ExoticTypeShapeProvider is always first and cannot be overridden.
                return new Profile(
                    this.baseProfile.Source,
                    this.baseProfile.Serializer,
                    [ExoticTypeShapeProvider.Instance, .. this.typeShapeProvidersBuilder]);
            }
        }
    }
}
