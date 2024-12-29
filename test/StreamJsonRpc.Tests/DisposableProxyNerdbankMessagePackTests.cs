﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Nerdbank.MessagePack;
using PolyType;

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
            b.AddTypeShapeProvider(DisposableProxyWitness.ShapeProvider);
            b.AddTypeShapeProvider(PolyType.ReflectionProvider.ReflectionTypeShapeProvider.Default);
        });

        return formatter;
    }
}

[GenerateShape<DisposableProxyTests.ProxyContainer>]
[GenerateShape<DisposableProxyTests.DataContainer>]
[GenerateShape<DisposableProxyTests.Data>]
#pragma warning disable SA1402 // File may only contain a single type
public partial class DisposableProxyWitness;
#pragma warning restore SA1402 // File may only contain a single type
