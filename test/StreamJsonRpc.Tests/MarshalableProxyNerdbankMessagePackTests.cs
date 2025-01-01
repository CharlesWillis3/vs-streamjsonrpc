// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack;
using PolyType;

public partial class MarshalableProxyNerdbankMessagePackTests : MarshalableProxyTests
{
    public MarshalableProxyNerdbankMessagePackTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override Type FormatterExceptionType => typeof(MessagePackSerializationException);

    protected override IJsonRpcMessageFormatter CreateFormatter()
    {
        NerdbankMessagePackFormatter formatter = new();
        formatter.SetFormatterProfile(b =>
        {
            b.RegisterRpcMarshalableType<IMarshalableAndSerializable>();
            b.RegisterRpcMarshalableType<IMarshalable>();
            b.RegisterRpcMarshalableType<IMarshalableWithCallScopedLifetime>();
            b.RegisterRpcMarshalableType<INonDisposableMarshalable>();
            b.RegisterRpcMarshalableType<IMarshalableSubType1>();
            b.RegisterRpcMarshalableType<IMarshalableSubType2>();
            b.RegisterRpcMarshalableType<IMarshalableSubType1Extended>();
            b.RegisterRpcMarshalableType<IMarshalableNonExtendingBase>();
            b.RegisterRpcMarshalableType<IMarshalableSubTypesCombined>();
            b.RegisterRpcMarshalableType<IMarshalableSubTypeWithIntermediateInterface>();
            b.RegisterRpcMarshalableType<IMarshalableSubTypeWithIntermediateInterface2>();
            b.RegisterRpcMarshalableType<IMarshalableWithOptionalInterfaces2>();
            b.RegisterRpcMarshalableType<IMarshalableSubType2Extended>();
            b.RegisterRpcMarshalableType<IGenericMarshalable<int>>();
            b.AddTypeShapeProvider(MarshalableProxyWitness.ShapeProvider);
            b.AddTypeShapeProvider(PolyType.ReflectionProvider.ReflectionTypeShapeProvider.Default);
        });

        return formatter;
    }

    [GenerateShape<Data>]
    public partial class MarshalableProxyWitness;
}
