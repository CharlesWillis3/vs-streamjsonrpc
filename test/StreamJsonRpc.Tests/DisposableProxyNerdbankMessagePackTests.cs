// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Nerdbank.MessagePack;

public class DisposableProxyNerdbankMessagePackTests : DisposableProxyTests
{
    public DisposableProxyNerdbankMessagePackTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override Type FormatterExceptionType => typeof(MessagePackSerializationException);

    protected override IJsonRpcMessageFormatter CreateFormatter()
    {
        NerdbankMessagePackFormatter formatter = new();
        formatter.SetFormatterProfile(b =>
        {
            b.RegisterStreamType<Nerdbank.FullDuplexStream>();
            b.RegisterDuplexPipeType<IDuplexPipe>();
            b.RegisterRpcMarshalableType<IDisposable>();
            b.AddTypeShapeProvider(PolyType.SourceGenerator.ShapeProvider_StreamJsonRpc_Tests.Default);
            b.AddTypeShapeProvider(PolyType.ReflectionProvider.ReflectionTypeShapeProvider.Default);
        });

        return formatter;
    }
}
