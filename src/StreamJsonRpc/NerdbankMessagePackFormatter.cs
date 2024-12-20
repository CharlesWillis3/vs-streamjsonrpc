// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipelines;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json.Nodes;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using Nerdbank.MessagePack;
using Nerdbank.Streams;
using PolyType;
using PolyType.Abstractions;
using PolyType.ReflectionProvider;
using PolyType.SourceGenerator;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;
using NBMP = Nerdbank.MessagePack;

namespace StreamJsonRpc;

/// <summary>
/// Serializes JSON-RPC messages using MessagePack (a fast, compact binary format).
/// </summary>
/// <remarks>
/// The MessagePack implementation used here comes from https://github.com/AArnott/Nerdbank.MessagePack.
/// </remarks>
public sealed partial class NerdbankMessagePackFormatter : FormatterBase, IJsonRpcMessageFormatter, IJsonRpcFormatterTracingCallbacks, IJsonRpcMessageFactory
{
    /// <summary>
    /// The constant "jsonrpc", in its various forms.
    /// </summary>
    private static readonly CommonString VersionPropertyName = new(Constants.jsonrpc);

    /// <summary>
    /// The constant "id", in its various forms.
    /// </summary>
    private static readonly CommonString IdPropertyName = new(Constants.id);

    /// <summary>
    /// The constant "method", in its various forms.
    /// </summary>
    private static readonly CommonString MethodPropertyName = new(Constants.Request.method);

    /// <summary>
    /// The constant "result", in its various forms.
    /// </summary>
    private static readonly CommonString ResultPropertyName = new(Constants.Result.result);

    /// <summary>
    /// The constant "error", in its various forms.
    /// </summary>
    private static readonly CommonString ErrorPropertyName = new(Constants.Error.error);

    /// <summary>
    /// The constant "params", in its various forms.
    /// </summary>
    private static readonly CommonString ParamsPropertyName = new(Constants.Request.@params);

    /// <summary>
    /// The constant "traceparent", in its various forms.
    /// </summary>
    private static readonly CommonString TraceParentPropertyName = new(Constants.Request.traceparent);

    /// <summary>
    /// The constant "tracestate", in its various forms.
    /// </summary>
    private static readonly CommonString TraceStatePropertyName = new(Constants.Request.tracestate);

    /// <summary>
    /// The constant "2.0", in its various forms.
    /// </summary>
    private static readonly CommonString Version2 = new("2.0");

    /// <summary>
    /// A cache of property names to declared property types, indexed by their containing parameter object type.
    /// </summary>
    /// <remarks>
    /// All access to this field should be while holding a lock on this member's value.
    /// </remarks>
    private static readonly Dictionary<Type, IReadOnlyDictionary<string, Type>> ParameterObjectPropertyTypes = new Dictionary<Type, IReadOnlyDictionary<string, Type>>();

    /// <summary>
    /// The serializer context to use for top-level RPC messages.
    /// </summary>
    private readonly FormatterContext rpcContext;

    private readonly ProgressFormatterResolver progressFormatterResolver;

    private readonly AsyncEnumerableFormatterResolver asyncEnumerableFormatterResolver;

    private readonly PipeFormatterResolver pipeFormatterResolver;

    private readonly MessagePackExceptionResolver exceptionResolver;

    private readonly ToStringHelper serializationToStringHelper = new();

    private readonly ToStringHelper deserializationToStringHelper = new();

    /// <summary>
    /// The serializer to use for user data (e.g. arguments, return values and errors).
    /// </summary>
    private FormatterContext userDataContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="NerdbankMessagePackFormatter"/> class.
    /// </summary>
    public NerdbankMessagePackFormatter()
    {
        // Set up initial options for our own message types.
        NBMP::MessagePackSerializer serializer = new()
        {
            InternStrings = true,
            SerializeDefaultValues = false,
        };

        serializer.RegisterConverter(new RequestIdConverter());
        serializer.RegisterConverter(new JsonRpcMessageConverter(this));
        serializer.RegisterConverter(new JsonRpcRequestConverter(this));
        serializer.RegisterConverter(new JsonRpcResultConverter(this));
        serializer.RegisterConverter(new JsonRpcErrorConverter(this));
        serializer.RegisterConverter(new JsonRpcErrorDetailConverter(this));
        serializer.RegisterConverter(new TraceParentConverter());

        this.rpcContext = new FormatterContext(serializer, ShapeProvider_StreamJsonRpc.Default);

        // Create the specialized formatters/resolvers that we will inject into the chain for user data.
        this.progressFormatterResolver = new ProgressFormatterResolver(this);
        this.asyncEnumerableFormatterResolver = new AsyncEnumerableFormatterResolver(this);
        this.pipeFormatterResolver = new PipeFormatterResolver(this);
        this.exceptionResolver = new MessagePackExceptionResolver(this);

        FormatterContext userDataContext = new(
            new()
            {
                InternStrings = true,
                SerializeDefaultValues = false,
            },
            ReflectionTypeShapeProvider.Default);

        this.MassageUserDataContext(userDataContext);
        this.userDataContext = userDataContext;
    }

    private interface IJsonRpcMessagePackRetention
    {
        /// <summary>
        /// Gets the original msgpack sequence that was deserialized into this message.
        /// </summary>
        /// <remarks>
        /// The buffer is only retained for a short time. If it has already been cleared, the result of this property is an empty sequence.
        /// </remarks>
        ReadOnlySequence<byte> OriginalMessagePack { get; }
    }

    /// <inheritdoc cref="FormatterBase.MultiplexingStream"/>
    public new MultiplexingStream? MultiplexingStream
    {
        get => base.MultiplexingStream;
        set => base.MultiplexingStream = value;
    }

    /// <summary>
    /// Configures the serialization context for user data with the specified configuration action.
    /// </summary>
    /// <param name="configure">The action to configure the serialization context.</param>
    public void SetFormatterContext(Action<IFormatterContextBuilder> configure)
    {
        Requires.NotNull(configure, nameof(configure));

        var builder = new FormatterContextBuilder(this.userDataContext.Serializer);
        configure(builder);

        FormatterContext context = builder.Build();
        this.MassageUserDataContext(context);

        this.userDataContext = context;
    }

    /// <inheritdoc/>
    public JsonRpcMessage Deserialize(ReadOnlySequence<byte> contentBuffer)
    {
        JsonRpcMessage message = this.rpcContext.Serializer.Deserialize<JsonRpcMessage>(contentBuffer, ShapeProvider_StreamJsonRpc.Default)
            ?? throw new NBMP::MessagePackSerializationException(Resources.UnexpectedErrorProcessingJsonRpc);

        IJsonRpcTracingCallbacks? tracingCallbacks = this.JsonRpc;
        this.deserializationToStringHelper.Activate(contentBuffer);
        try
        {
            tracingCallbacks?.OnMessageDeserialized(message, this.deserializationToStringHelper);
        }
        finally
        {
            this.deserializationToStringHelper.Deactivate();
        }

        return message;
    }

    /// <inheritdoc/>
    public void Serialize(IBufferWriter<byte> contentBuffer, JsonRpcMessage message)
    {
        if (message is Protocol.JsonRpcRequest request
            && request.Arguments is not null
            && request.ArgumentsList is null
            && request.Arguments is not IReadOnlyDictionary<string, object?>)
        {
            // This request contains named arguments, but not using a standard dictionary. Convert it to a dictionary so that
            // the parameters can be matched to the method we're invoking.
            if (GetParamsObjectDictionary(request.Arguments) is { } namedArgs)
            {
                request.Arguments = namedArgs.ArgumentValues;
                request.NamedArgumentDeclaredTypes = namedArgs.ArgumentTypes;
            }
        }

        var writer = new NBMP::MessagePackWriter(contentBuffer);
        try
        {
            this.rpcContext.Serializer.Serialize(ref writer, message, this.rpcContext.ShapeProvider);
            writer.Flush();
        }
        catch (Exception ex)
        {
            throw new NBMP::MessagePackSerializationException(string.Format(CultureInfo.CurrentCulture, Resources.ErrorWritingJsonRpcMessage, ex.GetType().Name, ex.Message), ex);
        }
    }

    /// <inheritdoc/>
    public object GetJsonText(JsonRpcMessage message) => message is IJsonRpcMessagePackRetention retainedMsgPack
        ? NBMP::MessagePackSerializer.ConvertToJson(retainedMsgPack.OriginalMessagePack)
        : throw new NotSupportedException();

    /// <inheritdoc/>
    Protocol.JsonRpcRequest IJsonRpcMessageFactory.CreateRequestMessage() => new OutboundJsonRpcRequest(this);

    /// <inheritdoc/>
    Protocol.JsonRpcError IJsonRpcMessageFactory.CreateErrorMessage() => new JsonRpcError(this.userDataContext);

    /// <inheritdoc/>
    Protocol.JsonRpcResult IJsonRpcMessageFactory.CreateResultMessage() => new JsonRpcResult(this, this.rpcContext);

    void IJsonRpcFormatterTracingCallbacks.OnSerializationComplete(JsonRpcMessage message, ReadOnlySequence<byte> encodedMessage)
    {
        IJsonRpcTracingCallbacks? tracingCallbacks = this.JsonRpc;
        this.serializationToStringHelper.Activate(encodedMessage);
        try
        {
            tracingCallbacks?.OnMessageSerialized(message, this.serializationToStringHelper);
        }
        finally
        {
            this.serializationToStringHelper.Deactivate();
        }
    }

    /// <summary>
    /// Extracts a dictionary of property names and values from the specified params object.
    /// </summary>
    /// <param name="paramsObject">The params object.</param>
    /// <returns>A dictionary of argument values and another of declared argument types, or <see langword="null"/> if <paramref name="paramsObject"/> is null.</returns>
    /// <remarks>
    /// This method supports DataContractSerializer-compliant types. This includes C# anonymous types.
    /// </remarks>
    [return: NotNullIfNotNull(nameof(paramsObject))]
    private static (IReadOnlyDictionary<string, object?> ArgumentValues, IReadOnlyDictionary<string, Type> ArgumentTypes)? GetParamsObjectDictionary(object? paramsObject)
    {
        if (paramsObject is null)
        {
            return default;
        }

        // Look up the argument types dictionary if we saved it before.
        Type paramsObjectType = paramsObject.GetType();
        IReadOnlyDictionary<string, Type>? argumentTypes;
        lock (ParameterObjectPropertyTypes)
        {
            ParameterObjectPropertyTypes.TryGetValue(paramsObjectType, out argumentTypes);
        }

        // If we couldn't find a previously created argument types dictionary, create a mutable one that we'll build this time.
        Dictionary<string, Type>? mutableArgumentTypes = argumentTypes is null ? new Dictionary<string, Type>() : null;

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        TypeInfo paramsTypeInfo = paramsObject.GetType().GetTypeInfo();
        bool isDataContract = paramsTypeInfo.GetCustomAttribute<DataContractAttribute>() is not null;

        BindingFlags bindingFlags = BindingFlags.FlattenHierarchy | BindingFlags.Public | BindingFlags.Instance;
        if (isDataContract)
        {
            bindingFlags |= BindingFlags.NonPublic;
        }

        bool TryGetSerializationInfo(MemberInfo memberInfo, out string key)
        {
            key = memberInfo.Name;
            if (isDataContract)
            {
                DataMemberAttribute? dataMemberAttribute = memberInfo.GetCustomAttribute<DataMemberAttribute>();
                if (dataMemberAttribute is null)
                {
                    return false;
                }

                if (!dataMemberAttribute.EmitDefaultValue)
                {
                    throw new NotSupportedException($"(DataMemberAttribute.EmitDefaultValue == false) is not supported but was found on: {memberInfo.DeclaringType!.FullName}.{memberInfo.Name}.");
                }

                key = dataMemberAttribute.Name ?? memberInfo.Name;
                return true;
            }
            else
            {
                return memberInfo.GetCustomAttribute<IgnoreDataMemberAttribute>() is null;
            }
        }

        foreach (PropertyInfo property in paramsTypeInfo.GetProperties(bindingFlags))
        {
            if (property.GetMethod is not null)
            {
                if (TryGetSerializationInfo(property, out string key))
                {
                    result[key] = property.GetValue(paramsObject);
                    if (mutableArgumentTypes is object)
                    {
                        mutableArgumentTypes[key] = property.PropertyType;
                    }
                }
            }
        }

        foreach (FieldInfo field in paramsTypeInfo.GetFields(bindingFlags))
        {
            if (TryGetSerializationInfo(field, out string key))
            {
                result[key] = field.GetValue(paramsObject);
                if (mutableArgumentTypes is object)
                {
                    mutableArgumentTypes[key] = field.FieldType;
                }
            }
        }

        // If we assembled the argument types dictionary this time, save it for next time.
        if (mutableArgumentTypes is not null)
        {
            lock (ParameterObjectPropertyTypes)
            {
                if (ParameterObjectPropertyTypes.TryGetValue(paramsObjectType, out IReadOnlyDictionary<string, Type>? lostRace))
                {
                    // Of the two, pick the winner to use ourselves so we consolidate on one and allow the GC to collect the loser sooner.
                    argumentTypes = lostRace;
                }
                else
                {
                    ParameterObjectPropertyTypes.Add(paramsObjectType, argumentTypes = mutableArgumentTypes);
                }
            }
        }

        return (result, argumentTypes!);
    }

    private static ReadOnlySequence<byte> GetSliceForNextToken(ref NBMP::MessagePackReader reader, in NBMP::SerializationContext context)
    {
        SequencePosition startingPosition = reader.Position;
        reader.Skip(context);
        SequencePosition endingPosition = reader.Position;
        return reader.Sequence.Slice(startingPosition, endingPosition);
    }

    /// <summary>
    /// Reads a string with an optimized path for the value "2.0".
    /// </summary>
    /// <param name="reader">The reader to use.</param>
    /// <returns>The decoded string.</returns>
    private static unsafe string ReadProtocolVersion(ref NBMP::MessagePackReader reader)
    {
        if (!reader.TryReadStringSpan(out ReadOnlySpan<byte> valueBytes))
        {
            // TODO: More specific exception type
            throw new NBMP::MessagePackSerializationException(Resources.UnexpectedErrorProcessingJsonRpc);
        }

        // Recognize "2.0" since we expect it and can avoid decoding and allocating a new string for it.
        if (Version2.TryRead(valueBytes))
        {
            return Version2.Value;
        }
        else
        {
            // It wasn't the expected value, so decode it.
            fixed (byte* pValueBytes = valueBytes)
            {
                return Encoding.UTF8.GetString(pValueBytes, valueBytes.Length);
            }
        }
    }

    /// <summary>
    /// Writes the JSON-RPC version property name and value in a highly optimized way.
    /// </summary>
    private static void WriteProtocolVersionPropertyAndValue(ref NBMP::MessagePackWriter writer, string version)
    {
        VersionPropertyName.Write(ref writer);
        if (!Version2.TryWrite(ref writer, version))
        {
            writer.Write(version);
        }
    }

    private static void ReadUnknownProperty(ref NBMP::MessagePackReader reader, in NBMP::SerializationContext context, ref Dictionary<string, ReadOnlySequence<byte>>? topLevelProperties, ReadOnlySpan<byte> stringKey)
    {
        topLevelProperties ??= new Dictionary<string, ReadOnlySequence<byte>>(StringComparer.Ordinal);
#if NETSTANDARD2_1_OR_GREATER || NET6_0_OR_GREATER
        string name = Encoding.UTF8.GetString(stringKey);
#else
        string name = Encoding.UTF8.GetString(stringKey.ToArray());
#endif
        topLevelProperties.Add(name, GetSliceForNextToken(ref reader, context));
    }

    /// <summary>
    /// Takes the user-supplied resolver for their data types and prepares the wrapping options
    /// and the dynamic object wrapper for serialization.
    /// </summary>
    /// <param name="userDataContext">The options for user data that is supplied by the user (or the default).</param>
    private void MassageUserDataContext(FormatterContext userDataContext)
    {
        // Add our own resolvers to fill in specialized behavior if the user doesn't provide/override it by their own resolver.
        userDataContext.Serializer.RegisterConverter(RequestIdConverter.Instance);
        userDataContext.Serializer.RegisterConverter(RawMessagePackConverter.Instance);
        userDataContext.Serializer.RegisterConverter(EventArgsConverter.Instance);

        var resolvers = new IFormatterResolver[]
        {
            // Support for marshalled objects.
            // new RpcMarshalableResolver(this)

            // Stateful or per-connection resolvers.
            this.progressFormatterResolver,
            this.asyncEnumerableFormatterResolver,
            this.pipeFormatterResolver,
            this.exceptionResolver,
        };

        // Wrap the resolver in another class as a way to pass information to our custom formatters.
        IFormatterResolver userDataResolver = new ResolverWrapper(CompositeResolver.Create(resolvers), this);
    }

    private class ResolverWrapper : IFormatterResolver
    {
        private readonly IFormatterResolver inner;

        internal ResolverWrapper(IFormatterResolver inner, NerdbankMessagePackFormatter formatter)
        {
            this.inner = inner;
            this.Formatter = formatter;
        }

        internal NerdbankMessagePackFormatter Formatter { get; }

        public IMessagePackFormatter<T>? GetFormatter<T>() => this.inner.GetFormatter<T>();
    }

    private class MessagePackFormatterConverter : IFormatterConverter
    {
        private readonly FormatterContext options;

        internal MessagePackFormatterConverter(FormatterContext options)
        {
            this.options = options;
        }

#pragma warning disable CS8766 // This method may in fact return null, and no one cares.
        public object? Convert(object value, Type type)
#pragma warning restore CS8766
            => ((RawMessagePack)value).Deserialize(type, this.options);

        public object Convert(object value, TypeCode typeCode)
        {
            return typeCode switch
            {
                TypeCode.Object => ((RawMessagePack)value).Deserialize<object>(this.options),
                _ => ExceptionSerializationHelpers.Convert(this, value, typeCode),
            };
        }

        public bool ToBoolean(object value) => ((RawMessagePack)value).Deserialize<bool>(this.options);

        public byte ToByte(object value) => ((RawMessagePack)value).Deserialize<byte>(this.options);

        public char ToChar(object value) => ((RawMessagePack)value).Deserialize<char>(this.options);

        public DateTime ToDateTime(object value) => ((RawMessagePack)value).Deserialize<DateTime>(this.options);

        public decimal ToDecimal(object value) => ((RawMessagePack)value).Deserialize<decimal>(this.options);

        public double ToDouble(object value) => ((RawMessagePack)value).Deserialize<double>(this.options);

        public short ToInt16(object value) => ((RawMessagePack)value).Deserialize<short>(this.options);

        public int ToInt32(object value) => ((RawMessagePack)value).Deserialize<int>(this.options);

        public long ToInt64(object value) => ((RawMessagePack)value).Deserialize<long>(this.options);

        public sbyte ToSByte(object value) => ((RawMessagePack)value).Deserialize<sbyte>(this.options);

        public float ToSingle(object value) => ((RawMessagePack)value).Deserialize<float>(this.options);

        public string? ToString(object value) => value is null ? null : ((RawMessagePack)value).Deserialize<string?>(this.options);

        public ushort ToUInt16(object value) => ((RawMessagePack)value).Deserialize<ushort>(this.options);

        public uint ToUInt32(object value) => ((RawMessagePack)value).Deserialize<uint>(this.options);

        public ulong ToUInt64(object value) => ((RawMessagePack)value).Deserialize<ulong>(this.options);
    }

    /// <summary>
    /// A recyclable object that can serialize a message to JSON on demand.
    /// </summary>
    /// <remarks>
    /// In perf traces, creation of this object used to show up as one of the most allocated objects.
    /// It is used even when tracing isn't active. So we changed its design to be reused,
    /// since its lifetime is only required during a synchronous call to a trace API.
    /// </remarks>
    private class ToStringHelper
    {
        private ReadOnlySequence<byte>? encodedMessage;
        private string? jsonString;

        public override string ToString()
        {
            Verify.Operation(this.encodedMessage.HasValue, "This object has not been activated. It may have already been recycled.");

            return this.jsonString ??= NBMP::MessagePackSerializer.ConvertToJson(this.encodedMessage.Value);
        }

        /// <summary>
        /// Initializes this object to represent a message.
        /// </summary>
        internal void Activate(ReadOnlySequence<byte> encodedMessage)
        {
            this.encodedMessage = encodedMessage;
        }

        /// <summary>
        /// Cleans out this object to release memory and ensure <see cref="ToString"/> throws if someone uses it after deactivation.
        /// </summary>
        internal void Deactivate()
        {
            this.encodedMessage = null;
            this.jsonString = null;
        }
    }

    private class RequestIdConverter : NBMP::MessagePackConverter<RequestId>
    {
        internal static readonly RequestIdConverter Instance = new();

        public override RequestId Read(ref NBMP.MessagePackReader reader, SerializationContext context)
        {
            context.DepthStep();

            if (reader.NextMessagePackType == NBMP.MessagePackType.Integer)
            {
                return new RequestId(reader.ReadInt64());
            }
            else
            {
                // Do *not* read as an interned string here because this ID should be unique.
                return new RequestId(reader.ReadString());
            }
        }

        public override void Write(ref NBMP.MessagePackWriter writer, in RequestId value, SerializationContext context)
        {
            context.DepthStep();

            if (value.Number.HasValue)
            {
                writer.Write(value.Number.Value);
            }
            else
            {
                writer.Write(value.String);
            }
        }

        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape) => JsonNode.Parse("""
        {
            "type": ["string", { "type": "integer", "format": "int64" }]
        }
        """)?.AsObject();
    }

    private class RawMessagePackConverter : MessagePackConverter<RawMessagePack>
    {
        internal static readonly RawMessagePackConverter Instance = new();

        private RawMessagePackConverter()
        {
        }

        [SuppressMessage("Usage", "NBMsgPack031:Converters should read or write exactly one msgpack structure", Justification = "<Pending>")]
        public override RawMessagePack Read(ref NBMP.MessagePackReader reader, SerializationContext context)
        {
            return RawMessagePack.ReadRaw(ref reader, copy: false, context);
        }

        [SuppressMessage("Usage", "NBMsgPack031:Converters should read or write exactly one msgpack structure", Justification = "<Pending>")]
        public override void Write(ref NBMP.MessagePackWriter writer, in RawMessagePack value, SerializationContext context)
        {
            value.WriteRaw(ref writer);
        }

        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
        {
            return CreateUndocumentedSchema(typeof(RawMessagePackConverter));
        }
    }

    private class ProgressFormatterResolver : IFormatterResolver
    {
        private readonly MessagePackFormatter mainFormatter;

        private readonly Dictionary<Type, IMessagePackFormatter?> progressFormatters = [];

        internal ProgressFormatterResolver(MessagePackFormatter formatter)
        {
            this.mainFormatter = formatter;
        }

        public IMessagePackFormatter<T>? GetFormatter<T>()
        {
            lock (this.progressFormatters)
            {
                if (!this.progressFormatters.TryGetValue(typeof(T), out IMessagePackFormatter? formatter))
                {
                    if (MessageFormatterProgressTracker.CanDeserialize(typeof(T)))
                    {
                        formatter = new PreciseTypeFormatter<T>(this.mainFormatter);
                    }
                    else if (MessageFormatterProgressTracker.CanSerialize(typeof(T)))
                    {
                        formatter = new ProgressClientFormatter<T>(this.mainFormatter);
                    }

                    this.progressFormatters.Add(typeof(T), formatter);
                }

                return (IMessagePackFormatter<T>?)formatter;
            }
        }

        /// <summary>
        /// Converts an instance of <see cref="IProgress{T}"/> to a progress token.
        /// </summary>
        private class ProgressClientConverter<TClass> : MessagePackConverter<TClass>
        {
            private readonly NerdbankMessagePackFormatter formatter;

            internal ProgressClientConverter(NerdbankMessagePackFormatter formatter)
            {
                this.formatter = formatter;
            }

            public override TClass Read(ref NBMP.MessagePackReader reader, SerializationContext context)
            {
                throw new NotSupportedException("This formatter only serializes IProgress<T> instances.");
            }

            public override void Write(ref NBMP.MessagePackWriter writer, in TClass? value, SerializationContext context)
            {
                if (value is null)
                {
                    writer.WriteNil();
                }
                else
                {
                    long progressId = this.formatter.FormatterProgressTracker.GetTokenForProgress(value);
                    writer.Write(progressId);
                }
            }

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
            {
                return CreateUndocumentedSchema(typeof(ProgressClientConverter<TClass>));
            }
        }

        /// <summary>
        /// Converts a progress token to an <see cref="IProgress{T}"/> or an <see cref="IProgress{T}"/> into a token.
        /// </summary>
        private class PreciseTypeConverter<TClass> : MessagePackConverter<TClass>
        {
            private readonly NerdbankMessagePackFormatter formatter;

            internal PreciseTypeConverter(NerdbankMessagePackFormatter formatter)
            {
                this.formatter = formatter;
            }

            [return: MaybeNull]
            [SuppressMessage("Usage", "NBMsgPack031:Converters should read or write exactly one msgpack structure", Justification = "<Pending>")]
            public override TClass? Read(ref NBMP.MessagePackReader reader, SerializationContext context)
            {
                if (reader.TryReadNil())
                {
                    return default!;
                }

                Assumes.NotNull(this.formatter.JsonRpc);
                RawMessagePack token = RawMessagePack.ReadRaw(ref reader, copy: true, context);
                bool clientRequiresNamedArgs = this.formatter.ApplicableMethodAttributeOnDeserializingMethod?.ClientRequiresNamedArguments is true;
                return (TClass)this.formatter.FormatterProgressTracker.CreateProgress(this.formatter.JsonRpc, token, typeof(TClass), clientRequiresNamedArgs);
            }

            public override void Write(ref NBMP.MessagePackWriter writer, in TClass? value, SerializationContext context)
            {
                if (value is null)
                {
                    writer.WriteNil();
                }
                else
                {
                    long progressId = this.formatter.FormatterProgressTracker.GetTokenForProgress(value);
                    writer.Write(progressId);
                }
            }

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
            {
                return CreateUndocumentedSchema(typeof(PreciseTypeConverter<TClass>));
            }
        }
    }

    private class AsyncEnumerableFormatterResolver : IFormatterResolver
    {
        private readonly MessagePackFormatter mainFormatter;

        private readonly Dictionary<Type, IMessagePackFormatter?> enumerableFormatters = new Dictionary<Type, IMessagePackFormatter?>();

        internal AsyncEnumerableFormatterResolver(MessagePackFormatter formatter)
        {
            this.mainFormatter = formatter;
        }

        public IMessagePackFormatter<T>? GetFormatter<T>()
        {
            lock (this.enumerableFormatters)
            {
                if (!this.enumerableFormatters.TryGetValue(typeof(T), out IMessagePackFormatter? formatter))
                {
                    if (TrackerHelpers<IAsyncEnumerable<int>>.IsActualInterfaceMatch(typeof(T)))
                    {
                        formatter = (IMessagePackFormatter<T>?)Activator.CreateInstance(typeof(PreciseTypeConverter<>).MakeGenericType(typeof(T).GenericTypeArguments[0]), new object[] { this.mainFormatter });
                    }
                    else if (TrackerHelpers<IAsyncEnumerable<int>>.FindInterfaceImplementedBy(typeof(T)) is { } iface)
                    {
                        formatter = (IMessagePackFormatter<T>?)Activator.CreateInstance(typeof(GeneratorConverter<,>).MakeGenericType(typeof(T), iface.GenericTypeArguments[0]), new object[] { this.mainFormatter });
                    }

                    this.enumerableFormatters.Add(typeof(T), formatter);
                }

                return (IMessagePackFormatter<T>?)formatter;
            }
        }

        /// <summary>
        /// Converts an enumeration token to an <see cref="IAsyncEnumerable{T}"/>
        /// or an <see cref="IAsyncEnumerable{T}"/> into an enumeration token.
        /// </summary>
#pragma warning disable CA1812
        private partial class PreciseTypeConverter<T>(NerdbankMessagePackFormatter mainFormatter) : MessagePackConverter<IAsyncEnumerable<T>>
#pragma warning restore CA1812
        {
            /// <summary>
            /// The constant "token", in its various forms.
            /// </summary>
            private static readonly CommonString TokenPropertyName = new(MessageFormatterEnumerableTracker.TokenPropertyName);

            /// <summary>
            /// The constant "values", in its various forms.
            /// </summary>
            private static readonly CommonString ValuesPropertyName = new(MessageFormatterEnumerableTracker.ValuesPropertyName);

            public override IAsyncEnumerable<T>? Read(ref NBMP.MessagePackReader reader, SerializationContext context)
            {
                if (reader.TryReadNil())
                {
                    return default;
                }

                context.DepthStep();
                RawMessagePack token = default;
                IReadOnlyList<T>? initialElements = null;
                int propertyCount = reader.ReadMapHeader();
                for (int i = 0; i < propertyCount; i++)
                {
                    if (!reader.TryReadStringSpan(out ReadOnlySpan<byte> stringKey))
                    {
                        throw new NBMP.MessagePackSerializationException(Resources.UnexpectedErrorProcessingJsonRpc);
                    }

                    if (TokenPropertyName.TryRead(stringKey))
                    {
                        token = RawMessagePack.ReadRaw(ref reader, copy: true, context);
                    }
                    else if (ValuesPropertyName.TryRead(stringKey))
                    {
                        initialElements = context.GetConverter<IReadOnlyList<T>>(context.TypeShapeProvider).Read(ref reader, context);
                    }
                    else
                    {
                        reader.Skip(context);
                    }
                }

                return mainFormatter.EnumerableTracker.CreateEnumerableProxy(token.IsDefault ? null : token, initialElements);
            }

            [SuppressMessage("Usage", "NBMsgPack031:Converters should read or write exactly one msgpack structure", Justification = "<Pending>")]
            public override void Write(ref NBMP.MessagePackWriter writer, in IAsyncEnumerable<T>? value, SerializationContext context)
            {
                Serialize_Shared(mainFormatter, ref writer, value, context);
            }

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
            {
                return CreateUndocumentedSchema(typeof(PreciseTypeConverter<T>));
            }

            internal static void Serialize_Shared(NerdbankMessagePackFormatter mainFormatter, ref NBMP::MessagePackWriter writer, IAsyncEnumerable<T>? value, NBMP::SerializationContext context)
            {
                if (value is null)
                {
                    writer.WriteNil();
                }
                else
                {
                    (IReadOnlyList<T> Elements, bool Finished) prefetched = value.TearOffPrefetchedElements();
                    long token = mainFormatter.EnumerableTracker.GetToken(value);

                    int propertyCount = 0;
                    if (prefetched.Elements.Count > 0)
                    {
                        propertyCount++;
                    }

                    if (!prefetched.Finished)
                    {
                        propertyCount++;
                    }

                    writer.WriteMapHeader(propertyCount);

                    if (!prefetched.Finished)
                    {
                        writer.Write(MessageFormatterEnumerableTracker.TokenPropertyName);
                        writer.Write(token);
                    }

                    if (prefetched.Elements.Count > 0)
                    {
                        writer.Write(MessageFormatterEnumerableTracker.ValuesPropertyName);
                        context.GetConverter<IReadOnlyList<T>>(context.TypeShapeProvider).Write(ref writer, prefetched.Elements, context);
                    }
                }
            }
        }

        /// <summary>
        /// Converts an instance of <see cref="IAsyncEnumerable{T}"/> to an enumeration token.
        /// </summary>
#pragma warning disable CA1812
        private class GeneratorConverter<TClass, TElement>(NerdbankMessagePackFormatter mainFormatter) : MessagePackConverter<TClass> where TClass : IAsyncEnumerable<TElement>
#pragma warning restore CA1812
        {
            public override TClass Read(ref NBMP.MessagePackReader reader, SerializationContext context)
            {
                throw new NotSupportedException();
            }

            [SuppressMessage("Usage", "NBMsgPack031:Converters should read or write exactly one msgpack structure", Justification = "<Pending>")]
            public override void Write(ref NBMP.MessagePackWriter writer, in TClass? value, SerializationContext context)
            {
                PreciseTypeConverter<TElement>.Serialize_Shared(mainFormatter, ref writer, value, context);
            }

            public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
            {
                return CreateUndocumentedSchema(typeof(GeneratorConverter<TClass, TElement>));
            }
        }
    }

    private class PipeFormatterResolver : IFormatterResolver
    {
        private readonly NerdbankMessagePackFormatter mainFormatter;

        private readonly Dictionary<Type, IMessagePackFormatter?> pipeFormatters = [];

        internal PipeFormatterResolver(NerdbankMessagePackFormatter formatter)
        {
            this.mainFormatter = formatter;
        }

        public IMessagePackFormatter<T>? GetFormatter<T>()
        {
            lock (this.pipeFormatters)
            {
                if (!this.pipeFormatters.TryGetValue(typeof(T), out IMessagePackFormatter? formatter))
                {
                    if (typeof(IDuplexPipe).IsAssignableFrom(typeof(T)))
                    {
                        formatter = (IMessagePackFormatter)Activator.CreateInstance(typeof(DuplexPipeConverter<>).MakeGenericType(typeof(T)), this.mainFormatter)!;
                    }
                    else if (typeof(PipeReader).IsAssignableFrom(typeof(T)))
                    {
                        formatter = (IMessagePackFormatter)Activator.CreateInstance(typeof(PipeReaderConverter<>).MakeGenericType(typeof(T)), this.mainFormatter)!;
                    }
                    else if (typeof(PipeWriter).IsAssignableFrom(typeof(T)))
                    {
                        formatter = (IMessagePackFormatter)Activator.CreateInstance(typeof(PipeWriterConverter<>).MakeGenericType(typeof(T)), this.mainFormatter)!;
                    }
                    else if (typeof(Stream).IsAssignableFrom(typeof(T)))
                    {
                        formatter = (IMessagePackFormatter)Activator.CreateInstance(typeof(StreamConverter<>).MakeGenericType(typeof(T)), this.mainFormatter)!;
                    }

                    this.pipeFormatters.Add(typeof(T), formatter);
                }

                return (IMessagePackFormatter<T>?)formatter;
            }
        }

#pragma warning disable CA1812
#pragma warning disable NBMsgPack032 // Converters should override GetJsonSchema
        private class DuplexPipeConverter<T>(NerdbankMessagePackFormatter formatter) : MessagePackConverter<T>
            where T : class, IDuplexPipe
#pragma warning restore NBMsgPack032 // Converters should override GetJsonSchema
#pragma warning restore CA1812
        {
            public override T? Read(ref NBMP.MessagePackReader reader, SerializationContext context)
            {
                if (reader.TryReadNil())
                {
                    return null;
                }

                return (T)formatter.DuplexPipeTracker.GetPipe(reader.ReadUInt64());
            }

            public override void Write(ref NBMP.MessagePackWriter writer, in T? value, SerializationContext context)
            {
                if (formatter.DuplexPipeTracker.GetULongToken(value) is { } token)
                {
                    writer.Write(token);
                }
                else
                {
                    writer.WriteNil();
                }
            }
        }

#pragma warning disable CA1812
#pragma warning disable NBMsgPack032 // Converters should override GetJsonSchema
        private class PipeReaderConverter<T>(NerdbankMessagePackFormatter formatter) : MessagePackConverter<T>
            where T : PipeReader
#pragma warning restore NBMsgPack032 // Converters should override GetJsonSchema
#pragma warning restore CA1812
        {
            public override T? Read(ref NBMP.MessagePackReader reader, SerializationContext context)
            {
                if (reader.TryReadNil())
                {
                    return null;
                }

                return (T)formatter.DuplexPipeTracker.GetPipeReader(reader.ReadUInt64());
            }

            public override void Write(ref NBMP.MessagePackWriter writer, in T? value, SerializationContext context)
            {
                if (formatter.DuplexPipeTracker.GetULongToken(value) is { } token)
                {
                    writer.Write(token);
                }
                else
                {
                    writer.WriteNil();
                }
            }
        }

#pragma warning disable CA1812
#pragma warning disable NBMsgPack032 // Converters should override GetJsonSchema
        private class PipeWriterConverter<T>(NerdbankMessagePackFormatter formatter) : MessagePackConverter<T>
            where T : PipeWriter
#pragma warning restore NBMsgPack032 // Converters should override GetJsonSchema
#pragma warning restore CA1812
        {
            public override T? Read(ref NBMP.MessagePackReader reader, SerializationContext context)
            {
                if (reader.TryReadNil())
                {
                    return null;
                }

                return (T)formatter.DuplexPipeTracker.GetPipeWriter(reader.ReadUInt64());
            }

            public override void Write(ref NBMP.MessagePackWriter writer, in T? value, SerializationContext context)
            {
                if (formatter.DuplexPipeTracker.GetULongToken(value) is { } token)
                {
                    writer.Write(token);
                }
                else
                {
                    writer.WriteNil();
                }
            }
        }

#pragma warning disable CA1812
#pragma warning disable NBMsgPack032 // Converters should override GetJsonSchema
        private class StreamConverter<T> : MessagePackConverter<T>
            where T : Stream
#pragma warning restore NBMsgPack032 // Converters should override GetJsonSchema
#pragma warning restore CA1812
        {
            private readonly NerdbankMessagePackFormatter formatter;

            public StreamConverter(NerdbankMessagePackFormatter formatter)
            {
                this.formatter = formatter;
            }

            public override T? Read(ref NBMP.MessagePackReader reader, SerializationContext context)
            {
                if (reader.TryReadNil())
                {
                    return null;
                }

                return (T)this.formatter.DuplexPipeTracker.GetPipe(reader.ReadUInt64()).AsStream();
            }

            public override void Write(ref NBMP.MessagePackWriter writer, in T? value, SerializationContext context)
            {
                if (this.formatter.DuplexPipeTracker.GetULongToken(value?.UsePipe()) is { } token)
                {
                    writer.Write(token);
                }
                else
                {
                    writer.WriteNil();
                }
            }
        }
    }

    private class RpcMarshalableResolver : IFormatterResolver
    {
        private readonly NerdbankMessagePackFormatter formatter;
        private readonly Dictionary<Type, object> formatters = new Dictionary<Type, object>();

        internal RpcMarshalableResolver(NerdbankMessagePackFormatter formatter)
        {
            this.formatter = formatter;
        }

        public IMessagePackFormatter<T>? GetFormatter<T>()
        {
            if (typeof(T).IsValueType)
            {
                return null;
            }

            lock (this.formatters)
            {
                if (this.formatters.TryGetValue(typeof(T), out object? cachedFormatter))
                {
                    return (IMessagePackFormatter<T>)cachedFormatter;
                }
            }

            if (MessageFormatterRpcMarshaledContextTracker.TryGetMarshalOptionsForType(
                typeof(T),
                out JsonRpcProxyOptions? proxyOptions,
                out JsonRpcTargetOptions? targetOptions,
                out RpcMarshalableAttribute? attribute))
            {
                object formatter = Activator.CreateInstance(
                    typeof(RpcMarshalableFormatter<>).MakeGenericType(typeof(T)),
                    this.formatter,
                    proxyOptions,
                    targetOptions,
                    attribute)!;

                lock (this.formatters)
                {
                    if (!this.formatters.TryGetValue(typeof(T), out object? cachedFormatter))
                    {
                        this.formatters.Add(typeof(T), cachedFormatter = formatter);
                    }

                    return (IMessagePackFormatter<T>)cachedFormatter;
                }
            }

            return null;
        }
    }

#pragma warning disable CA1812
    private class RpcMarshalableFormatter<T>(NerdbankMessagePackFormatter messagePackFormatter, JsonRpcProxyOptions proxyOptions, JsonRpcTargetOptions targetOptions, RpcMarshalableAttribute rpcMarshalableAttribute) : IMessagePackFormatter<T?>
        where T : class
#pragma warning restore CA1812
    {
        public T? Deserialize(ref MessagePack.MessagePackReader reader, MessagePackSerializerOptions options)
        {
            MessageFormatterRpcMarshaledContextTracker.MarshalToken? token = MessagePack.MessagePackSerializer.Deserialize<MessageFormatterRpcMarshaledContextTracker.MarshalToken?>(ref reader, options);
            return token.HasValue ? (T?)messagePackFormatter.RpcMarshaledContextTracker.GetObject(typeof(T), token, proxyOptions) : null;
        }

        public void Serialize(ref MessagePack.MessagePackWriter writer, T? value, MessagePackSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNil();
            }
            else
            {
                MessageFormatterRpcMarshaledContextTracker.MarshalToken token = messagePackFormatter.RpcMarshaledContextTracker.GetToken(value, targetOptions, typeof(T), rpcMarshalableAttribute);
                MessagePack.MessagePackSerializer.Serialize(ref writer, token, options);
            }
        }
    }

    /// <summary>
    /// Manages serialization of any <see cref="Exception"/>-derived type that follows standard <see cref="SerializableAttribute"/> rules.
    /// </summary>
    /// <remarks>
    /// A serializable class will:
    /// 1. Derive from <see cref="Exception"/>
    /// 2. Be attributed with <see cref="SerializableAttribute"/>
    /// 3. Declare a constructor with a signature of (<see cref="SerializationInfo"/>, <see cref="StreamingContext"/>).
    /// </remarks>
    private class MessagePackExceptionResolver : IFormatterResolver
    {
        /// <summary>
        /// Tracks recursion count while serializing or deserializing an exception.
        /// </summary>
        /// <devremarks>
        /// This is placed here (<em>outside</em> the generic <see cref="ExceptionFormatter{T}"/> class)
        /// so that it's one counter shared across all exception types that may be serialized or deserialized.
        /// </devremarks>
        private static ThreadLocal<int> exceptionRecursionCounter = new();

        private readonly object[] formatterActivationArgs;

        private readonly Dictionary<Type, object?> formatterCache = new Dictionary<Type, object?>();

        internal MessagePackExceptionResolver(MessagePackFormatter formatter)
        {
            this.formatterActivationArgs = new object[] { formatter };
        }

        public IMessagePackFormatter<T>? GetFormatter<T>()
        {
            lock (this.formatterCache)
            {
                if (this.formatterCache.TryGetValue(typeof(T), out object? cachedFormatter))
                {
                    return (IMessagePackFormatter<T>?)cachedFormatter;
                }

                IMessagePackFormatter<T>? formatter = null;
                if (typeof(Exception).IsAssignableFrom(typeof(T)) && typeof(T).GetCustomAttribute<SerializableAttribute>() is object)
                {
                    formatter = (IMessagePackFormatter<T>)Activator.CreateInstance(typeof(ExceptionFormatter<>).MakeGenericType(typeof(T)), this.formatterActivationArgs)!;
                }

                this.formatterCache.Add(typeof(T), formatter);
                return formatter;
            }
        }

#pragma warning disable CA1812
        private partial class ExceptionFormatter<T>(NerdbankMessagePackFormatter formatter) : MessagePackConverter<T>
            where T : Exception
#pragma warning restore CA1812
        {
            public override T? Read(ref NBMP.MessagePackReader reader, SerializationContext context)
            {
                Assumes.NotNull(formatter.JsonRpc);
                if (reader.TryReadNil())
                {
                    return null;
                }

                // We have to guard our own recursion because the serializer has no visibility into inner exceptions.
                // Each exception in the russian doll is a new serialization job from its perspective.
                exceptionRecursionCounter.Value++;
                try
                {
                    if (exceptionRecursionCounter.Value > formatter.JsonRpc.ExceptionOptions.RecursionLimit)
                    {
                        // Exception recursion has gone too deep. Skip this value and return null as if there were no inner exception.
                        // Note that in skipping, the parser may use recursion internally and may still throw if its own limits are exceeded.
                        reader.Skip(context);
                        return null;
                    }

                    // TODO: Is this the right context?
                    var info = new SerializationInfo(typeof(T), new MessagePackFormatterConverter(formatter.rpcContext));
                    int memberCount = reader.ReadMapHeader();
                    for (int i = 0; i < memberCount; i++)
                    {
                        string? name = context.GetConverter<string>(context.TypeShapeProvider).Read(ref reader, context)
                            ?? throw new NBMP::MessagePackSerializationException(Resources.UnexpectedNullValueInMap);

                        // SerializationInfo.GetValue(string, typeof(object)) does not call our formatter,
                        // so the caller will get a boxed RawMessagePack struct in that case.
                        // Although we can't do much about *that* in general, we can at least ensure that null values
                        // are represented as null instead of this boxed struct.
                        var value = reader.TryReadNil() ? null : (object)RawMessagePack.ReadRaw(ref reader, false, context);

                        info.AddSafeValue(name, value);
                    }

                    return ExceptionSerializationHelpers.Deserialize<T>(formatter.JsonRpc, info, formatter.JsonRpc.TraceSource);
                }
                finally
                {
                    exceptionRecursionCounter.Value--;
                }
            }

            public override void Write(ref NBMP.MessagePackWriter writer, in T? value, SerializationContext context)
            {
                if (value is null)
                {
                    writer.WriteNil();
                    return;
                }

                exceptionRecursionCounter.Value++;
                try
                {
                    if (exceptionRecursionCounter.Value > formatter.JsonRpc?.ExceptionOptions.RecursionLimit)
                    {
                        // Exception recursion has gone too deep. Skip this value and write null as if there were no inner exception.
                        writer.WriteNil();
                        return;
                    }

                    // TODO: Is this the right context?
                    var info = new SerializationInfo(typeof(T), new MessagePackFormatterConverter(formatter.rpcContext));
                    ExceptionSerializationHelpers.Serialize(value, info);
                    writer.WriteMapHeader(info.GetSafeMemberCount());
                    foreach (SerializationEntry element in info.GetSafeMembers())
                    {
                        writer.Write(element.Name);
#pragma warning disable NBMsgPack030 // Converters should not call top-level `MessagePackSerializer` methods
                        formatter.rpcContext.Serializer.SerializeObject(
                            ref writer,
                            element.Value,
                            formatter.rpcContext.ShapeProvider.Resolve(element.ObjectType));
#pragma warning restore NBMsgPack030 // Converters should not call top-level `MessagePackSerializer` methods
                    }
                }
                finally
                {
                    exceptionRecursionCounter.Value--;
                }
            }
        }
    }

    private class JsonRpcMessageConverter : NBMP::MessagePackConverter<JsonRpcMessage>
    {
        private readonly NerdbankMessagePackFormatter formatter;

        internal JsonRpcMessageConverter(NerdbankMessagePackFormatter formatter)
        {
            this.formatter = formatter;
        }

        public override JsonRpcMessage? Read(ref NBMP.MessagePackReader reader, NBMP::SerializationContext context)
        {
            context.DepthStep();

            NBMP::MessagePackReader readAhead = reader.CreatePeekReader();
            int propertyCount = readAhead.ReadMapHeader();
            for (int i = 0; i < propertyCount; i++)
            {
                // We read the property name in this fancy way in order to avoid paying to decode and allocate a string when we already know what we're looking for.
                // MessagePackFormatter: ReadOnlySpan<byte> stringKey = MessagePack.Internal.CodeGenHelpers.ReadStringSpan(ref readAhead);
                if (!readAhead.TryReadStringSpan(out ReadOnlySpan<byte> stringKey))
                {
                    throw new UnrecognizedJsonRpcMessageException();
                }

                if (MethodPropertyName.TryRead(stringKey))
                {
                    return context.GetConverter<Protocol.JsonRpcRequest>(context.TypeShapeProvider).Read(ref reader, context);
                }
                else if (ResultPropertyName.TryRead(stringKey))
                {
                    return context.GetConverter<Protocol.JsonRpcResult>(context.TypeShapeProvider).Read(ref reader, context);
                }
                else if (ErrorPropertyName.TryRead(stringKey))
                {
                    return context.GetConverter<Protocol.JsonRpcError>(context.TypeShapeProvider).Read(ref reader, context);
                }
                else
                {
                    readAhead.Skip(context);
                }
            }

            throw new UnrecognizedJsonRpcMessageException();
        }

        public override void Write(ref NBMP.MessagePackWriter writer, in JsonRpcMessage? value, NBMP::SerializationContext context)
        {
            Requires.NotNull(value!, nameof(value));

            using (this.formatter.TrackSerialization(value))
            {
                context.DepthStep();

                switch (value)
                {
                    case Protocol.JsonRpcRequest request:
                        context.GetConverter<Protocol.JsonRpcRequest>(context.TypeShapeProvider).Write(ref writer, request, context);
                        break;
                    case Protocol.JsonRpcResult result:
                        context.GetConverter<Protocol.JsonRpcResult>(context.TypeShapeProvider).Write(ref writer, result, context);
                        break;
                    case Protocol.JsonRpcError error:
                        context.GetConverter<Protocol.JsonRpcError>(context.TypeShapeProvider).Write(ref writer, error, context);
                        break;
                    default:
                        throw new NotSupportedException("Unexpected JsonRpcMessage-derived type: " + value.GetType().Name);
                }
            }
        }

        public override JsonObject? GetJsonSchema(NBMP::JsonSchemaContext context, ITypeShape typeShape)
        {
            return base.GetJsonSchema(context, typeShape);
        }
    }

    private class JsonRpcRequestConverter : NBMP::MessagePackConverter<Protocol.JsonRpcRequest>
    {
        private readonly NerdbankMessagePackFormatter formatter;

        internal JsonRpcRequestConverter(NerdbankMessagePackFormatter formatter)
        {
            this.formatter = formatter;
        }

        public override Protocol.JsonRpcRequest? Read(ref NBMP::MessagePackReader reader, NBMP::SerializationContext context)
        {
            var result = new JsonRpcRequest(this.formatter)
            {
                OriginalMessagePack = reader.Sequence,
            };

            context.DepthStep();

            int propertyCount = reader.ReadMapHeader();
            Dictionary<string, ReadOnlySequence<byte>>? topLevelProperties = null;
            for (int propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++)
            {
                // We read the property name in this fancy way in order to avoid paying to decode and allocate a string when we already know what we're looking for.
                if (!reader.TryReadStringSpan(out ReadOnlySpan<byte> stringKey))
                {
                    throw new UnrecognizedJsonRpcMessageException();
                }

                if (VersionPropertyName.TryRead(stringKey))
                {
                    result.Version = ReadProtocolVersion(ref reader);
                }
                else if (IdPropertyName.TryRead(stringKey))
                {
                    result.RequestId = context.GetConverter<RequestId>(null).Read(ref reader, context);
                }
                else if (MethodPropertyName.TryRead(stringKey))
                {
                    result.Method = context.GetConverter<string>(null).Read(ref reader, context);
                }
                else if (ParamsPropertyName.TryRead(stringKey))
                {
                    SequencePosition paramsTokenStartPosition = reader.Position;

                    // Parse out the arguments into a dictionary or array, but don't deserialize them because we don't yet know what types to deserialize them to.
                    switch (reader.NextMessagePackType)
                    {
                        case NBMP::MessagePackType.Array:
                            var positionalArgs = new ReadOnlySequence<byte>[reader.ReadArrayHeader()];
                            for (int i = 0; i < positionalArgs.Length; i++)
                            {
                                positionalArgs[i] = GetSliceForNextToken(ref reader, context);
                            }

                            result.MsgPackPositionalArguments = positionalArgs;
                            break;
                        case NBMP::MessagePackType.Map:
                            int namedArgsCount = reader.ReadMapHeader();
                            var namedArgs = new Dictionary<string, ReadOnlySequence<byte>>(namedArgsCount);
                            for (int i = 0; i < namedArgsCount; i++)
                            {
                                string? propertyName = context.GetConverter<string>(null).Read(ref reader, context);
                                if (propertyName is null)
                                {
                                    throw new NBMP::MessagePackSerializationException(Resources.UnexpectedNullValueInMap);
                                }

                                namedArgs.Add(propertyName, GetSliceForNextToken(ref reader, context));
                            }

                            result.MsgPackNamedArguments = namedArgs;
                            break;
                        case NBMP::MessagePackType.Nil:
                            result.MsgPackPositionalArguments = Array.Empty<ReadOnlySequence<byte>>();
                            reader.ReadNil();
                            break;
                        case NBMP::MessagePackType type:
                            throw new NBMP::MessagePackSerializationException("Expected a map or array of arguments but got " + type);
                    }

                    result.MsgPackArguments = reader.Sequence.Slice(paramsTokenStartPosition, reader.Position);
                }
                else if (TraceParentPropertyName.TryRead(stringKey))
                {
                    TraceParent traceParent = context.GetConverter<TraceParent>(null).Read(ref reader, context);
                    result.TraceParent = traceParent.ToString();
                }
                else if (TraceStatePropertyName.TryRead(stringKey))
                {
                    result.TraceState = ReadTraceState(ref reader, context);
                }
                else
                {
                    ReadUnknownProperty(ref reader, context, ref topLevelProperties, stringKey);
                }
            }

            if (topLevelProperties is not null)
            {
                result.TopLevelPropertyBag = new TopLevelPropertyBag(this.formatter.userDataContext, topLevelProperties);
            }

            this.formatter.TryHandleSpecialIncomingMessage(result);

            return result;
        }

        public override void Write(ref NBMP.MessagePackWriter writer, in Protocol.JsonRpcRequest? value, NBMP::SerializationContext context)
        {
            Requires.NotNull(value!, nameof(value));

            context.DepthStep();

            var topLevelPropertyBag = (TopLevelPropertyBag?)(value as IMessageWithTopLevelPropertyBag)?.TopLevelPropertyBag;

            int mapElementCount = value.RequestId.IsEmpty ? 3 : 4;
            if (value.TraceParent?.Length > 0)
            {
                mapElementCount++;
                if (value.TraceState?.Length > 0)
                {
                    mapElementCount++;
                }
            }

            mapElementCount += topLevelPropertyBag?.PropertyCount ?? 0;
            writer.WriteMapHeader(mapElementCount);

            WriteProtocolVersionPropertyAndValue(ref writer, value.Version);

            if (!value.RequestId.IsEmpty)
            {
                IdPropertyName.Write(ref writer);
                context.GetConverter<RequestId>(context.TypeShapeProvider)
                    .Write(ref writer, value.RequestId, context);
            }

            MethodPropertyName.Write(ref writer);
            writer.Write(value.Method);

            ParamsPropertyName.Write(ref writer);

            // TODO: Get from SetOptions
            ITypeShapeProvider? userShapeProvider = context.TypeShapeProvider;

            if (value.ArgumentsList is not null)
            {
                writer.WriteArrayHeader(value.ArgumentsList.Count);


                for (int i = 0; i < value.ArgumentsList.Count; i++)
                {
                    object? arg = value.ArgumentsList[i];
                    ITypeShape? argShape = arg is null
                        ? null
                        : value.ArgumentListDeclaredTypes is not null
                            ? userShapeProvider?.GetShape(value.ArgumentListDeclaredTypes[i])
                            : ReflectionTypeShapeProvider.Default.Resolve(arg.GetType());

                    if (argShape is not null)
                    {
#pragma warning disable NBMsgPack030 // Converters should not call top-level `MessagePackSerializer` methods
                        this.formatter.userDataContext.Serializer.SerializeObject(ref writer, arg, argShape, context.CancellationToken);
#pragma warning restore NBMsgPack030 // Converters should not call top-level `MessagePackSerializer` methods
                    }
                    else
                    {
                        // TODO: NOT REALLY SURE ABOUT THIS YET
                        writer.WriteNil();
                    }
                }
            }
            else if (value.NamedArguments is not null)
            {
                writer.WriteMapHeader(value.NamedArguments.Count);
                foreach (KeyValuePair<string, object?> entry in value.NamedArguments)
                {
                    writer.Write(entry.Key);
                    ITypeShape? argShape = value.NamedArgumentDeclaredTypes?[entry.Key] is Type argType
                        ? userShapeProvider?.GetShape(argType)
                        : null;

                    if (argShape is not null)
                    {
#pragma warning disable NBMsgPack030 // Converters should not call top-level `MessagePackSerializer` methods
                        this.formatter.userDataContext.Serializer.SerializeObject(ref writer, entry.Value, argShape, context.CancellationToken);
#pragma warning restore NBMsgPack030 // Converters should not call top-level `MessagePackSerializer` methods
                    }
                    else
                    {
                        // TODO: NOT REALLY SURE ABOUT THIS YET
                        writer.WriteNil();
                    }
                }
            }
            else
            {
                writer.WriteNil();
            }

            if (value.TraceParent?.Length > 0)
            {
                TraceParentPropertyName.Write(ref writer);
                context.GetConverter<TraceParent>(context.TypeShapeProvider)
                    .Write(ref writer, new TraceParent(value.TraceParent), context);

                if (value.TraceState?.Length > 0)
                {
                    TraceStatePropertyName.Write(ref writer);
                    WriteTraceState(ref writer, value.TraceState);
                }
            }

            topLevelPropertyBag?.WriteProperties(ref writer);
        }

        public override JsonObject? GetJsonSchema(NBMP::JsonSchemaContext context, ITypeShape typeShape)
        {
            return CreateUndocumentedSchema(typeof(JsonRpcRequestConverter));
        }

        private static void WriteTraceState(ref NBMP::MessagePackWriter writer, string traceState)
        {
            ReadOnlySpan<char> traceStateChars = traceState.AsSpan();

            // Count elements first so we can write the header.
            int elementCount = 1;
            int commaIndex;
            while ((commaIndex = traceStateChars.IndexOf(',')) >= 0)
            {
                elementCount++;
                traceStateChars = traceStateChars.Slice(commaIndex + 1);
            }

            // For every element, we have a key and value to record.
            writer.WriteArrayHeader(elementCount * 2);

            traceStateChars = traceState.AsSpan();
            while ((commaIndex = traceStateChars.IndexOf(',')) >= 0)
            {
                ReadOnlySpan<char> element = traceStateChars.Slice(0, commaIndex);
                WritePair(ref writer, element);
                traceStateChars = traceStateChars.Slice(commaIndex + 1);
            }

            // Write out the last one.
            WritePair(ref writer, traceStateChars);

            static void WritePair(ref NBMP::MessagePackWriter writer, ReadOnlySpan<char> pair)
            {
                int equalsIndex = pair.IndexOf('=');
                ReadOnlySpan<char> key = pair.Slice(0, equalsIndex);
                ReadOnlySpan<char> value = pair.Slice(equalsIndex + 1);
                writer.Write(key);
                writer.Write(value);
            }
        }

        private static unsafe string ReadTraceState(ref NBMP::MessagePackReader reader, NBMP::SerializationContext context)
        {
            int elements = reader.ReadArrayHeader();
            if (elements % 2 != 0)
            {
                throw new NotSupportedException("Odd number of elements not expected.");
            }

            // With care, we could probably assemble this string with just two allocations (the string + a char[]).
            var resultBuilder = new StringBuilder();
            for (int i = 0; i < elements; i += 2)
            {
                if (resultBuilder.Length > 0)
                {
                    resultBuilder.Append(',');
                }

                // We assume the key is a frequent string, and the value is unique,
                // so we optimize whether to use string interning or not on that basis.
                resultBuilder.Append(context.GetConverter<string>(null).Read(ref reader, context));
                resultBuilder.Append('=');
                resultBuilder.Append(reader.ReadString());
            }

            return resultBuilder.ToString();
        }
    }

    private partial class JsonRpcResultConverter : NBMP::MessagePackConverter<Protocol.JsonRpcResult>
    {
        private readonly NerdbankMessagePackFormatter formatter;

        internal JsonRpcResultConverter(NerdbankMessagePackFormatter formatter)
        {
            this.formatter = formatter;
        }

        public override Protocol.JsonRpcResult Read(ref NBMP.MessagePackReader reader, NBMP::SerializationContext context)
        {
            var result = new JsonRpcResult(this.formatter, this.formatter.userDataContext)
            {
                OriginalMessagePack = reader.Sequence,
            };

            Dictionary<string, ReadOnlySequence<byte>>? topLevelProperties = null;
            context.DepthStep();

            int propertyCount = reader.ReadMapHeader();
            for (int propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++)
            {
                // We read the property name in this fancy way in order to avoid paying to decode and allocate a string when we already know what we're looking for.
                if (!reader.TryReadStringSpan(out ReadOnlySpan<byte> stringKey))
                {
                    throw new UnrecognizedJsonRpcMessageException();
                }

                if (VersionPropertyName.TryRead(stringKey))
                {
                    result.Version = ReadProtocolVersion(ref reader);
                }
                else if (IdPropertyName.TryRead(stringKey))
                {
                    result.RequestId = context.GetConverter<RequestId>(context.TypeShapeProvider).Read(ref reader, context);
                }
                else if (ResultPropertyName.TryRead(stringKey))
                {
                    result.MsgPackResult = GetSliceForNextToken(ref reader, context);
                }
                else
                {
                    ReadUnknownProperty(ref reader, context, ref topLevelProperties, stringKey);
                }
            }

            if (topLevelProperties is not null)
            {
                result.TopLevelPropertyBag = new TopLevelPropertyBag(this.formatter.userDataContext, topLevelProperties);
            }

            return result;
        }

        public override void Write(ref NBMP.MessagePackWriter writer, in Protocol.JsonRpcResult? value, NBMP::SerializationContext context)
        {
            Requires.NotNull(value!, nameof(value));

            var topLevelPropertyBagMessage = value as IMessageWithTopLevelPropertyBag;

            int mapElementCount = 3;
            mapElementCount += (topLevelPropertyBagMessage?.TopLevelPropertyBag as TopLevelPropertyBag)?.PropertyCount ?? 0;
            writer.WriteMapHeader(mapElementCount);

            WriteProtocolVersionPropertyAndValue(ref writer, value.Version);

            IdPropertyName.Write(ref writer);
            context.GetConverter<RequestId>(context.TypeShapeProvider).Write(ref writer, value.RequestId, context);

            ResultPropertyName.Write(ref writer);

            ITypeShape? typeShape = value.ResultDeclaredType is not null && value.ResultDeclaredType != typeof(void)
                ? this.formatter.userDataContext.ShapeProvider.Resolve(value.ResultDeclaredType)
                : value.Result is null
                    ? null
                    : this.formatter.userDataContext.ShapeProvider.Resolve(value.Result.GetType());

            if (typeShape is not null)
            {
#pragma warning disable NBMsgPack030 // Converters should not call top-level `MessagePackSerializer` methods
                this.formatter.userDataContext.Serializer.SerializeObject(ref writer, value.Result, typeShape, context.CancellationToken);
#pragma warning restore NBMsgPack030 // Converters should not call top-level `MessagePackSerializer` methods
            }
            else
            {
                // TODO: NOT REALLY SURE ABOUT THIS YET
                writer.WriteNil();
            }

            (topLevelPropertyBagMessage?.TopLevelPropertyBag as TopLevelPropertyBag)?.WriteProperties(ref writer);
        }

        public override JsonObject? GetJsonSchema(NBMP::JsonSchemaContext context, ITypeShape typeShape)
        {
            return CreateUndocumentedSchema(typeof(JsonRpcResultConverter));
        }
    }

    private partial class JsonRpcErrorConverter : MessagePackConverter<Protocol.JsonRpcError>
    {
        private readonly NerdbankMessagePackFormatter formatter;

        internal JsonRpcErrorConverter(NerdbankMessagePackFormatter formatter)
        {
            this.formatter = formatter;
        }

        public override Protocol.JsonRpcError Read(ref NBMP::MessagePackReader reader, SerializationContext context)
        {
            var error = new JsonRpcError(this.formatter.rpcContext)
            {
                OriginalMessagePack = reader.Sequence,
            };

            Dictionary<string, ReadOnlySequence<byte>>? topLevelProperties = null;

            context.DepthStep();

            int propertyCount = reader.ReadMapHeader();
            for (int propertyIdx = 0; propertyIdx < propertyCount; propertyIdx++)
            {
                // We read the property name in this fancy way in order to avoid paying to decode and allocate a string when we already know what we're looking for.
                if (!reader.TryReadStringSpan(out ReadOnlySpan<byte> stringKey))
                {
                    throw new UnrecognizedJsonRpcMessageException();
                }

                if (VersionPropertyName.TryRead(stringKey))
                {
                    error.Version = ReadProtocolVersion(ref reader);
                }
                else if (IdPropertyName.TryRead(stringKey))
                {
                    error.RequestId = context.GetConverter<RequestId>(context.TypeShapeProvider).Read(ref reader, context);
                }
                else if (ErrorPropertyName.TryRead(stringKey))
                {
                    error.Error = context.GetConverter<Protocol.JsonRpcError.ErrorDetail>(context.TypeShapeProvider).Read(ref reader, context);
                }
                else
                {
                    ReadUnknownProperty(ref reader, context, ref topLevelProperties, stringKey);
                }
            }

            if (topLevelProperties is not null)
            {
                error.TopLevelPropertyBag = new TopLevelPropertyBag(this.formatter.userDataContext, topLevelProperties);
            }

            return error;
        }

        public override void Write(ref NBMP::MessagePackWriter writer, in Protocol.JsonRpcError? value, SerializationContext context)
        {
            Requires.NotNull(value!, nameof(value));

            var topLevelPropertyBag = (TopLevelPropertyBag?)(value as IMessageWithTopLevelPropertyBag)?.TopLevelPropertyBag;

            int mapElementCount = 3;
            mapElementCount += topLevelPropertyBag?.PropertyCount ?? 0;
            writer.WriteMapHeader(mapElementCount);

            WriteProtocolVersionPropertyAndValue(ref writer, value.Version);

            IdPropertyName.Write(ref writer);
            context.GetConverter<RequestId>(context.TypeShapeProvider).Write(ref writer, value.RequestId, context);

            ErrorPropertyName.Write(ref writer);
            context.GetConverter<Protocol.JsonRpcError.ErrorDetail>(context.TypeShapeProvider).Write(ref writer, value.Error, context);

            topLevelPropertyBag?.WriteProperties(ref writer);
        }

        public override JsonObject? GetJsonSchema(NBMP::JsonSchemaContext context, ITypeShape typeShape)
        {
            return CreateUndocumentedSchema(typeof(JsonRpcErrorConverter));
        }
    }

    private partial class JsonRpcErrorDetailConverter : MessagePackConverter<Protocol.JsonRpcError.ErrorDetail>
    {
        private static readonly CommonString CodePropertyName = new("code");
        private static readonly CommonString MessagePropertyName = new("message");
        private static readonly CommonString DataPropertyName = new("data");

        private readonly NerdbankMessagePackFormatter formatter;

        internal JsonRpcErrorDetailConverter(NerdbankMessagePackFormatter formatter)
        {
            this.formatter = formatter;
        }

        public override Protocol.JsonRpcError.ErrorDetail Read(ref NBMP.MessagePackReader reader, SerializationContext context)
        {
            var result = new JsonRpcError.ErrorDetail(this.formatter.userDataContext);
            context.DepthStep();

            int propertyCount = reader.ReadMapHeader();
            for (int propertyIdx = 0; propertyIdx < propertyCount; propertyIdx++)
            {
                if (!reader.TryReadStringSpan(out ReadOnlySpan<byte> stringKey))
                {
                    throw new UnrecognizedJsonRpcMessageException();
                }

                if (CodePropertyName.TryRead(stringKey))
                {
                    result.Code = context.GetConverter<JsonRpcErrorCode>(context.TypeShapeProvider).Read(ref reader, context);
                }
                else if (MessagePropertyName.TryRead(stringKey))
                {
                    result.Message = context.GetConverter<string>(context.TypeShapeProvider).Read(ref reader, context);
                }
                else if (DataPropertyName.TryRead(stringKey))
                {
                    result.MsgPackData = GetSliceForNextToken(ref reader, context);
                }
                else
                {
                    reader.Skip(context);
                }
            }

            return result;
        }

        [SuppressMessage("Usage", "NBMsgPack031:Converters should read or write exactly one msgpack structure", Justification = "<Pending>")]
        public override void Write(ref NBMP.MessagePackWriter writer, in Protocol.JsonRpcError.ErrorDetail? value, SerializationContext context)
        {
            Requires.NotNull(value!, nameof(value));

            writer.WriteMapHeader(3);

            CodePropertyName.Write(ref writer);
            context.GetConverter<JsonRpcErrorCode>(context.TypeShapeProvider).Write(ref writer, value.Code, context);

            MessagePropertyName.Write(ref writer);
            writer.Write(value.Message);

            DataPropertyName.Write(ref writer);
#pragma warning disable NBMsgPack030 // Converters should not call top-level `MessagePackSerializer` methods
            this.formatter.userDataContext.Serializer.SerializeObject(
                ref writer,
                value.Data,
                this.formatter.userDataContext.ShapeProvider.Resolve<Protocol.JsonRpcError.ErrorDetail?>());
#pragma warning restore NBMsgPack030 // Converters should not call top-level `MessagePackSerializer` methods
        }

        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
        {
            return CreateUndocumentedSchema(typeof(JsonRpcErrorDetailConverter));
        }
    }

    /// <summary>
    /// Enables formatting the default/empty <see cref="EventArgs"/> class.
    /// </summary>
    private class EventArgsConverter : MessagePackConverter<EventArgs>
    {
        internal static readonly EventArgsConverter Instance = new();

        private EventArgsConverter()
        {
        }

        /// <inheritdoc/>
        public override void Write(ref NBMP.MessagePackWriter writer, in EventArgs? value, SerializationContext context)
        {
            Requires.NotNull(value!, nameof(value));
            writer.WriteMapHeader(0);
        }

        /// <inheritdoc/>
        public override EventArgs Read(ref NBMP.MessagePackReader reader, SerializationContext context)
        {
            reader.Skip(context);
            return EventArgs.Empty;
        }

        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
        {
            return CreateUndocumentedSchema(typeof(EventArgsConverter));
        }
    }

    private class TraceParentConverter : MessagePackConverter<TraceParent>
    {
        public unsafe override TraceParent Read(ref NBMP.MessagePackReader reader, SerializationContext context)
        {
            if (reader.ReadArrayHeader() != 2)
            {
                throw new NotSupportedException("Unexpected array length.");
            }

            var result = default(TraceParent);
            result.Version = reader.ReadByte();
            if (result.Version != 0)
            {
                throw new NotSupportedException("traceparent version " + result.Version + " is not supported.");
            }

            if (reader.ReadArrayHeader() != 3)
            {
                throw new NotSupportedException("Unexpected array length in version-format.");
            }

            ReadOnlySequence<byte> bytes = reader.ReadBytes() ?? throw new NotSupportedException("Expected traceid not found.");
            bytes.CopyTo(new Span<byte>(result.TraceId, TraceParent.TraceIdByteCount));

            bytes = reader.ReadBytes() ?? throw new NotSupportedException("Expected parentid not found.");
            bytes.CopyTo(new Span<byte>(result.ParentId, TraceParent.ParentIdByteCount));

            result.Flags = (TraceParent.TraceFlags)reader.ReadByte();

            return result;
        }

        public unsafe override void Write(ref NBMP.MessagePackWriter writer, in TraceParent value, SerializationContext context)
        {
            if (value.Version != 0)
            {
                throw new NotSupportedException("traceparent version " + value.Version + " is not supported.");
            }

            writer.WriteArrayHeader(2);

            writer.Write(value.Version);

            writer.WriteArrayHeader(3);

            fixed (byte* traceId = value.TraceId)
            {
                writer.Write(new ReadOnlySpan<byte>(traceId, TraceParent.TraceIdByteCount));
            }

            fixed (byte* parentId = value.ParentId)
            {
                writer.Write(new ReadOnlySpan<byte>(parentId, TraceParent.ParentIdByteCount));
            }

            writer.Write((byte)value.Flags);
        }

        public override JsonObject? GetJsonSchema(JsonSchemaContext context, ITypeShape typeShape)
        {
            return CreateUndocumentedSchema(typeof(TraceParentConverter));
        }
    }

    private class TopLevelPropertyBag : TopLevelPropertyBagBase
    {
        private readonly FormatterContext formatterContext;
        private readonly IReadOnlyDictionary<string, ReadOnlySequence<byte>>? inboundUnknownProperties;

        /// <summary>
        /// Initializes a new instance of the <see cref="TopLevelPropertyBag"/> class
        /// for an incoming message.
        /// </summary>
        /// <param name="userDataContext">The serializer options to use for this data.</param>
        /// <param name="inboundUnknownProperties">The map of unrecognized inbound properties.</param>
        internal TopLevelPropertyBag(FormatterContext userDataContext, IReadOnlyDictionary<string, ReadOnlySequence<byte>> inboundUnknownProperties)
            : base(isOutbound: false)
        {
            this.formatterContext = userDataContext;
            this.inboundUnknownProperties = inboundUnknownProperties;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TopLevelPropertyBag"/> class
        /// for an outbound message.
        /// </summary>
        /// <param name="formatterContext">The serializer options to use for this data.</param>
        internal TopLevelPropertyBag(FormatterContext formatterContext)
            : base(isOutbound: true)
        {
            this.formatterContext = formatterContext;
        }

        internal int PropertyCount => this.inboundUnknownProperties?.Count ?? this.OutboundProperties?.Count ?? 0;

        /// <summary>
        /// Writes the properties tracked by this collection to a messagepack writer.
        /// </summary>
        /// <param name="writer">The writer to use.</param>
        internal void WriteProperties(ref NBMP::MessagePackWriter writer)
        {
            if (this.inboundUnknownProperties is not null)
            {
                // We're actually re-transmitting an incoming message (remote target feature).
                // We need to copy all the properties that were in the original message.
                // Don't implement this without enabling the tests for the scenario found in JsonRpcRemoteTargetMessagePackFormatterTests.cs.
                // The tests fail for reasons even without this support, so there's work to do beyond just implementing this.
                throw new NotImplementedException();

                ////foreach (KeyValuePair<string, ReadOnlySequence<byte>> entry in this.inboundUnknownProperties)
                ////{
                ////    writer.Write(entry.Key);
                ////    writer.Write(entry.Value);
                ////}
            }
            else
            {
                foreach (KeyValuePair<string, (Type DeclaredType, object? Value)> entry in this.OutboundProperties)
                {
                    ITypeShape shape = this.formatterContext.ShapeProvider.Resolve(entry.Value.DeclaredType);

                    writer.Write(entry.Key);
                    this.formatterContext.Serializer.SerializeObject(ref writer, entry.Value.Value, shape);
                }
            }
        }

        protected internal override bool TryGetTopLevelProperty<T>(string name, [MaybeNull] out T value)
        {
            if (this.inboundUnknownProperties is null)
            {
                throw new InvalidOperationException(Resources.InboundMessageOnly);
            }

            value = default;

            if (this.inboundUnknownProperties.TryGetValue(name, out ReadOnlySequence<byte> serializedValue) is true)
            {
                var reader = new NBMP::MessagePackReader(serializedValue);
                value = this.formatterContext.Serializer.Deserialize<T>(ref reader, this.formatterContext.ShapeProvider);
                return true;
            }

            return false;
        }
    }

    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
    [DataContract]
    private class OutboundJsonRpcRequest : JsonRpcRequestBase
    {
        private readonly NerdbankMessagePackFormatter formatter;

        internal OutboundJsonRpcRequest(NerdbankMessagePackFormatter formatter)
        {
            this.formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        }

        protected override TopLevelPropertyBagBase? CreateTopLevelPropertyBag() => new TopLevelPropertyBag(this.formatter.userDataContext);
    }

    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
    [DataContract]
    private class JsonRpcRequest : JsonRpcRequestBase, IJsonRpcMessagePackRetention
    {
        private readonly NerdbankMessagePackFormatter formatter;

        internal JsonRpcRequest(NerdbankMessagePackFormatter formatter)
        {
            this.formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        }

        public override int ArgumentCount => this.MsgPackNamedArguments?.Count ?? this.MsgPackPositionalArguments?.Count ?? base.ArgumentCount;

        public override IEnumerable<string>? ArgumentNames => this.MsgPackNamedArguments?.Keys;

        public ReadOnlySequence<byte> OriginalMessagePack { get; internal set; }

        internal ReadOnlySequence<byte> MsgPackArguments { get; set; }

        internal IReadOnlyDictionary<string, ReadOnlySequence<byte>>? MsgPackNamedArguments { get; set; }

        internal IReadOnlyList<ReadOnlySequence<byte>>? MsgPackPositionalArguments { get; set; }

        public override ArgumentMatchResult TryGetTypedArguments(ReadOnlySpan<ParameterInfo> parameters, Span<object?> typedArguments)
        {
            using (this.formatter.TrackDeserialization(this, parameters))
            {
                if (parameters.Length == 1 && this.MsgPackNamedArguments is not null)
                {
                    if (this.formatter.ApplicableMethodAttributeOnDeserializingMethod?.UseSingleObjectParameterDeserialization ?? false)
                    {
                        var reader = new NBMP::MessagePackReader(this.MsgPackArguments);
                        try
                        {
                            typedArguments[0] = this.formatter.userDataContext.Serializer.DeserializeObject(
                                ref reader,
                                this.formatter.userDataContext.ShapeProvider.Resolve(parameters[0].ParameterType));

                            return ArgumentMatchResult.Success;
                        }
                        catch (NBMP::MessagePackSerializationException)
                        {
                            return ArgumentMatchResult.ParameterArgumentTypeMismatch;
                        }
                    }
                }

                return base.TryGetTypedArguments(parameters, typedArguments);
            }
        }

        public override bool TryGetArgumentByNameOrIndex(string? name, int position, Type? typeHint, out object? value)
        {
            // If anyone asks us for an argument *after* we've been told deserialization is done, there's something very wrong.
            Assumes.True(this.MsgPackNamedArguments is not null || this.MsgPackPositionalArguments is not null);

            ReadOnlySequence<byte> msgpackArgument = default;
            if (position >= 0 && this.MsgPackPositionalArguments?.Count > position)
            {
                msgpackArgument = this.MsgPackPositionalArguments[position];
            }
            else if (name is not null && this.MsgPackNamedArguments is not null)
            {
                this.MsgPackNamedArguments.TryGetValue(name, out msgpackArgument);
            }

            if (msgpackArgument.IsEmpty)
            {
                value = null;
                return false;
            }

            var reader = new NBMP::MessagePackReader(msgpackArgument);
            using (this.formatter.TrackDeserialization(this))
            {
                try
                {
                    value = this.formatter.userDataContext.Serializer.DeserializeObject(
                        ref reader,
                        this.formatter.userDataContext.ShapeProvider.Resolve(typeHint ?? typeof(object)));

                    return true;
                }
                catch (NBMP::MessagePackSerializationException ex)
                {
                    if (this.formatter.JsonRpc?.TraceSource.Switch.ShouldTrace(TraceEventType.Warning) ?? false)
                    {
                        this.formatter.JsonRpc.TraceSource.TraceEvent(TraceEventType.Warning, (int)JsonRpc.TraceEvents.MethodArgumentDeserializationFailure, Resources.FailureDeserializingRpcArgument, name, position, typeHint, ex);
                    }

                    throw new RpcArgumentDeserializationException(name, position, typeHint, ex);
                }
            }
        }

        protected override void ReleaseBuffers()
        {
            base.ReleaseBuffers();
            this.MsgPackNamedArguments = null;
            this.MsgPackPositionalArguments = null;
            this.TopLevelPropertyBag = null;
            this.MsgPackArguments = default;
            this.OriginalMessagePack = default;
        }

        protected override TopLevelPropertyBagBase? CreateTopLevelPropertyBag() => new TopLevelPropertyBag(this.formatter.userDataContext);
    }

    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
    [DataContract]
    private class JsonRpcResult : JsonRpcResultBase, IJsonRpcMessagePackRetention
    {
        private readonly NerdbankMessagePackFormatter formatter;
        private readonly FormatterContext serializerOptions;

        private Exception? resultDeserializationException;

        internal JsonRpcResult(NerdbankMessagePackFormatter formatter, FormatterContext serializationOptions)
        {
            this.formatter = formatter;
            this.serializerOptions = serializationOptions;
        }

        public ReadOnlySequence<byte> OriginalMessagePack { get; internal set; }

        internal ReadOnlySequence<byte> MsgPackResult { get; set; }

        public override T GetResult<T>()
        {
            if (this.resultDeserializationException is not null)
            {
                ExceptionDispatchInfo.Capture(this.resultDeserializationException).Throw();
            }

            return this.MsgPackResult.IsEmpty
                ? (T)this.Result!
                : this.serializerOptions.Serializer.Deserialize<T>(this.MsgPackResult, this.serializerOptions.ShapeProvider)
                ?? throw new NBMP::MessagePackSerializationException(Resources.FailureDeserializingJsonRpc);
        }

        protected internal override void SetExpectedResultType(Type resultType)
        {
            Verify.Operation(!this.MsgPackResult.IsEmpty, "Result is no longer available or has already been deserialized.");

            var reader = new NBMP::MessagePackReader(this.MsgPackResult);
            try
            {
                using (this.formatter.TrackDeserialization(this))
                {
                    this.Result = this.serializerOptions.Serializer.DeserializeObject(
                        ref reader,
                        this.serializerOptions.ShapeProvider.Resolve(resultType));
                }

                this.MsgPackResult = default;
            }
            catch (NBMP::MessagePackSerializationException ex)
            {
                // This was a best effort anyway. We'll throw again later at a more convenient time for JsonRpc.
                this.resultDeserializationException = ex;
            }
        }

        protected override void ReleaseBuffers()
        {
            base.ReleaseBuffers();
            this.MsgPackResult = default;
            this.OriginalMessagePack = default;
        }

        protected override TopLevelPropertyBagBase? CreateTopLevelPropertyBag() => new TopLevelPropertyBag(this.serializerOptions);
    }

    [DebuggerDisplay("{" + nameof(DebuggerDisplay) + ",nq}")]
    [DataContract]
    private class JsonRpcError : JsonRpcErrorBase, IJsonRpcMessagePackRetention
    {
        private readonly FormatterContext serializerOptions;

        public JsonRpcError(FormatterContext serializerOptions)
        {
            this.serializerOptions = serializerOptions;
        }

        public ReadOnlySequence<byte> OriginalMessagePack { get; internal set; }

        protected override TopLevelPropertyBagBase? CreateTopLevelPropertyBag() => new TopLevelPropertyBag(this.serializerOptions);

        protected override void ReleaseBuffers()
        {
            base.ReleaseBuffers();
            if (this.Error is ErrorDetail privateDetail)
            {
                privateDetail.MsgPackData = default;
            }

            this.OriginalMessagePack = default;
        }

        [DataContract]
        internal new class ErrorDetail : Protocol.JsonRpcError.ErrorDetail
        {
            private readonly FormatterContext serializerOptions;

            internal ErrorDetail(FormatterContext serializerOptions)
            {
                this.serializerOptions = serializerOptions ?? throw new ArgumentNullException(nameof(serializerOptions));
            }

            internal ReadOnlySequence<byte> MsgPackData { get; set; }

            public override object? GetData(Type dataType)
            {
                Requires.NotNull(dataType, nameof(dataType));
                if (this.MsgPackData.IsEmpty)
                {
                    return this.Data;
                }

                var reader = new NBMP::MessagePackReader(this.MsgPackData);
                try
                {
                    return this.serializerOptions.Serializer.DeserializeObject(
                        ref reader,
                        this.serializerOptions.ShapeProvider.Resolve(dataType))
                        ?? throw new NBMP::MessagePackSerializationException(Resources.FailureDeserializingJsonRpc);
                }
                catch (NBMP::MessagePackSerializationException)
                {
                    // Deserialization failed. Try returning array/dictionary based primitive objects.
                    try
                    {
                        // return MessagePackSerializer.Deserialize<object>(this.MsgPackData, this.serializerOptions.WithResolver(PrimitiveObjectResolver.Instance));
                        // TODO: Which Shape Provider to use?
                        return this.serializerOptions.Serializer.Deserialize<object>(this.MsgPackData, this.serializerOptions.ShapeProvider);
                    }
                    catch (NBMP::MessagePackSerializationException)
                    {
                        return null;
                    }
                }
            }

            protected internal override void SetExpectedDataType(Type dataType)
            {
                Verify.Operation(!this.MsgPackData.IsEmpty, "Data is no longer available or has already been deserialized.");

                this.Data = this.GetData(dataType);

                // Clear the source now that we've deserialized to prevent GetData from attempting
                // deserialization later when the buffer may be recycled on another thread.
                this.MsgPackData = default;
            }
        }
    }

    private record FormatterContext(NBMP::MessagePackSerializer Serializer, ITypeShapeProvider ShapeProvider);

    private class FormatterContextBuilder(NBMP::MessagePackSerializer serializer) : IFormatterContextBuilder
    {
        private readonly CompositeTypeShapeProviderBuilder providerBuilder = new();

        public ICompositeTypeShapeProviderBuilder TypeShapeProviderBuilder => this.providerBuilder;

        public void RegisterConverter<T>(NBMP::MessagePackConverter<T> converter) => serializer.RegisterConverter(converter);

        public void RegisterKnownSubTypes<TBase>(NBMP::KnownSubTypeMapping<TBase> mapping)
        {
            Requires.NotNull(mapping, nameof(mapping));
            serializer.RegisterKnownSubTypes(mapping);
        }

        internal FormatterContext Build() => new(serializer, this.providerBuilder.Build());
    }

    private class CompositeTypeShapeProviderBuilder : ICompositeTypeShapeProviderBuilder
    {
        private readonly List<ITypeShapeProvider> providers = [];

        public ICompositeTypeShapeProviderBuilder Add(ITypeShapeProvider provider)
        {
            this.providers.Add(provider);
            return this;
        }

        public ICompositeTypeShapeProviderBuilder AddRange(IEnumerable<ITypeShapeProvider> providers)
        {
            this.providers.AddRange(providers);
            return this;
        }

        public ICompositeTypeShapeProviderBuilder AddReflectionTypeShapeProvider(bool useReflectionEmit)
        {
            ReflectionTypeShapeProviderOptions options = new()
            {
                UseReflectionEmit = useReflectionEmit,
            };

            this.providers.Add(ReflectionTypeShapeProvider.Create(options));
            return this;
        }

        public ITypeShapeProvider Build()
        {
            return this.providers.Count switch
            {
                0 => ReflectionTypeShapeProvider.Default,
                1 => this.providers[0],
                _ => new CompositeTypeShapeProvider(this.providers.AsReadOnly()),
            };
        }

        private class CompositeTypeShapeProvider : ITypeShapeProvider
        {
            private readonly ReadOnlyCollection<ITypeShapeProvider> providers;

            internal CompositeTypeShapeProvider(ReadOnlyCollection<ITypeShapeProvider> providers)
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
}
