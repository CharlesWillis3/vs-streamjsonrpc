// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.IO.Pipelines;
using Nerdbank.MessagePack;
using PolyType;
using PolyType.Abstractions;
using PolyType.ReflectionProvider;
using StreamJsonRpc.Reflection;

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
        /// <typeparam name="TReport">The type of the progress report.</typeparam>
        void RegisterProgressType<TProgress, TReport>()
            where TProgress : IProgress<TReport>;

        /// <summary>
        /// Registers a type converter for asynchronous enumerable types.
        /// </summary>
        /// <typeparam name="TEnumerable">The type of the asynchronous enumerable to register.</typeparam>
        /// <typeparam name="TElement">The element type of the asynchronous enumerable.</typeparam>
        void RegisterAsyncEnumerableType<TEnumerable, TElement>()
            where TEnumerable : IAsyncEnumerable<TElement>;

        /// <summary>
        /// Registers a type converter for duplex pipe types.
        /// </summary>
        /// <typeparam name="TPipe">The type of the duplex pipe to register.</typeparam>
        void RegisterDuplexPipeType<TPipe>()
            where TPipe : IDuplexPipe;

        /// <summary>
        /// Registers a type converter for pipe reader types.
        /// </summary>
        /// <typeparam name="TReader">The type of the pipe reader to register.</typeparam>
        void RegisterPipeReaderType<TReader>()
            where TReader : PipeReader;

        /// <summary>
        /// Registers a type converter for pipe writer types.
        /// </summary>
        /// <typeparam name="TWriter">The type of the pipe writer to register.</typeparam>
        void RegisterPipeWriterType<TWriter>()
            where TWriter : PipeWriter;

        /// <summary>
        /// Registers a type converter for stream types.
        /// </summary>
        /// <typeparam name="TStream">The type of the stream to register.</typeparam>
        void RegisterStreamType<TStream>()
            where TStream : Stream;

        /// <summary>
        /// Registers a type that can be marshaled over RPC.
        /// </summary>
        /// <typeparam name="T">The type to register.</typeparam>
        void RegisterRpcMarshalableType<T>()
            where T : class;

        /// <summary>
        /// Registers a custom exception type for serialization.
        /// </summary>
        /// <typeparam name="TException">The type of the exception to register.</typeparam>
        void RegisterExceptionType<TException>()
            where TException : Exception;

        /// <summary>
        /// Adds a type shape provider to the formatter context.
        /// </summary>
        /// <param name="provider">The type shape provider to add.</param>
        void AddTypeShapeProvider(ITypeShapeProvider provider);

        /// <summary>
        /// Enable the reflection fallback for dynamic type generation.
        /// </summary>
        /// <param name="useReflectionEmit">A value indicating whether to use Reflection.Emit for dynamic type generation.</param>
        void EnableReflectionFallback(bool useReflectionEmit);
    }

    private class FormatterContextBuilder(
        NerdbankMessagePackFormatter formatter,
        FormatterContext baseContext)
        : IFormatterContextBuilder
    {
        private ImmutableArray<ITypeShapeProvider>.Builder? typeShapeProvidersBuilder = null;
        private ReflectionTypeShapeProvider? reflectionTypeShapeProvider = null;

        public void AddTypeShapeProvider(ITypeShapeProvider provider)
        {
            this.typeShapeProvidersBuilder ??= ImmutableArray.CreateBuilder<ITypeShapeProvider>();
            this.typeShapeProvidersBuilder.Add(provider);
        }

        public void EnableReflectionFallback(bool useReflectionEmit)
        {
            ReflectionTypeShapeProviderOptions options = new()
            {
                UseReflectionEmit = useReflectionEmit,
            };

            this.reflectionTypeShapeProvider = ReflectionTypeShapeProvider.Create(options);
        }

        public void RegisterAsyncEnumerableType<TEnumerable, TElement>()
            where TEnumerable : IAsyncEnumerable<TElement>
        {
            MessagePackConverter<TEnumerable> converter = formatter.asyncEnumerableConverterResolver.GetConverter<TEnumerable>();
            baseContext.Serializer.RegisterConverter(converter);
        }

        public void RegisterConverter<T>(MessagePackConverter<T> converter)
        {
            baseContext.Serializer.RegisterConverter(converter);
        }

        public void RegisterKnownSubTypes<TBase>(KnownSubTypeMapping<TBase> mapping)
        {
            baseContext.Serializer.RegisterKnownSubTypes(mapping);
        }

        public void RegisterProgressType<TProgress, TReport>()
            where TProgress : IProgress<TReport>
        {
            MessagePackConverter<TProgress> converter = formatter.progressConverterResolver.GetConverter<TProgress>();
            baseContext.Serializer.RegisterConverter(converter);
        }

        public void RegisterDuplexPipeType<TPipe>()
            where TPipe : IDuplexPipe
        {
            MessagePackConverter<TPipe> converter = formatter.pipeConverterResolver.GetConverter<TPipe>();
            baseContext.Serializer.RegisterConverter(converter);
        }

        public void RegisterPipeReaderType<TReader>()
            where TReader : PipeReader
        {
            MessagePackConverter<TReader> converter = formatter.pipeConverterResolver.GetConverter<TReader>();
            baseContext.Serializer.RegisterConverter(converter);
        }

        public void RegisterPipeWriterType<TWriter>()
            where TWriter : PipeWriter
        {
            MessagePackConverter<TWriter> converter = formatter.pipeConverterResolver.GetConverter<TWriter>();
            baseContext.Serializer.RegisterConverter(converter);
        }

        public void RegisterStreamType<TStream>()
            where TStream : Stream
        {
            MessagePackConverter<TStream> converter = formatter.pipeConverterResolver.GetConverter<TStream>();
            baseContext.Serializer.RegisterConverter(converter);
        }

        public void RegisterExceptionType<TException>()
            where TException : Exception
        {
            MessagePackConverter<TException> converter = formatter.exceptionResolver.GetConverter<TException>();
            baseContext.Serializer.RegisterConverter(converter);
        }

        public void RegisterRpcMarshalableType<T>()
            where T : class
        {
            if (MessageFormatterRpcMarshaledContextTracker.TryGetMarshalOptionsForType(
                typeof(T),
                out JsonRpcProxyOptions? proxyOptions,
                out JsonRpcTargetOptions? targetOptions,
                out RpcMarshalableAttribute? attribute))
            {
                var converter = (RpcMarshalableConverter<T>)Activator.CreateInstance(
                    typeof(RpcMarshalableConverter<>).MakeGenericType(typeof(T)),
                    formatter,
                    proxyOptions,
                    targetOptions,
                    attribute)!;

                baseContext.Serializer.RegisterConverter(converter);
            }

            // TODO: Throw?
        }

        internal FormatterContext Build()
        {
            if (this.reflectionTypeShapeProvider is not null)
            {
                this.AddTypeShapeProvider(this.reflectionTypeShapeProvider);
            }

            if (this.typeShapeProvidersBuilder is null || this.typeShapeProvidersBuilder.Count < 1)
            {
                return baseContext;
            }

            ITypeShapeProvider provider = this.typeShapeProvidersBuilder.Count == 1
                ? this.typeShapeProvidersBuilder[0]
                : new CompositeTypeShapeProvider(this.typeShapeProvidersBuilder.ToImmutable());

            return new FormatterContext(baseContext.Serializer, provider);
        }
    }

    private class CompositeTypeShapeProvider : ITypeShapeProvider
    {
        private readonly ImmutableArray<ITypeShapeProvider> providers;

        internal CompositeTypeShapeProvider(ImmutableArray<ITypeShapeProvider> providers)
        {
            this.providers = providers;
        }

        public ITypeShape? GetShape(Type type)
        {
            foreach (ITypeShapeProvider provider in this.providers)
            {
                ITypeShape? shape = provider.GetShape(type);
                if (shape is not null)
                {
                    return shape;
                }
            }

            return null;
        }
    }
}
