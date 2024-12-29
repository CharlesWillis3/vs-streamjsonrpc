// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Nerdbank.Streams;
using PolyType;

public class DuplexPipeMarshalingNerdbankMessagePackTests : DuplexPipeMarshalingTests
{
    public DuplexPipeMarshalingNerdbankMessagePackTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override void InitializeFormattersAndHandlers()
    {
        NerdbankMessagePackFormatter serverFormatter = new()
        {
            MultiplexingStream = this.serverMx,
        };

        NerdbankMessagePackFormatter clientFormatter = new()
        {
            MultiplexingStream = this.clientMx,
        };

        serverFormatter.SetFormatterProfile(Configure);
        clientFormatter.SetFormatterProfile(Configure);

        this.serverMessageFormatter = serverFormatter;
        this.clientMessageFormatter = clientFormatter;

        static void Configure(NerdbankMessagePackFormatter.Profile.Builder b)
        {
            b.RegisterDuplexPipeType<MultiplexingStream.Channel>();
            b.RegisterStreamType<OneWayWrapperStream>();
            b.RegisterStreamType<MonitoringStream>();
            b.RegisterStreamType<MemoryStream>();
            b.AddTypeShapeProvider(DuplexPipeWitness.ShapeProvider);
            b.AddTypeShapeProvider(PolyType.ReflectionProvider.ReflectionTypeShapeProvider.Default);
        }
    }
}

[GenerateShape<DuplexPipeMarshalingTests.StreamContainingClass>]
[GenerateShape<DuplexPipeMarshalingTests.OneWayWrapperStream>]
#pragma warning disable SA1402 // File may only contain a single type
public partial class DuplexPipeWitness;
#pragma warning restore SA1402 // File may only contain a single type
