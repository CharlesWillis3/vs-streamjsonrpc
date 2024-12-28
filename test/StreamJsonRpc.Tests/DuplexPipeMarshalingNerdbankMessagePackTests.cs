// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Nerdbank.Streams;

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

        serverFormatter.SetFormatterProfile(b =>
        {
            b.RegisterPipeReaderType<PipeReader>();
            b.RegisterPipeWriterType<PipeWriter>();
            b.RegisterDuplexPipeType<MultiplexingStream.Channel>();
            b.RegisterDuplexPipeType<IDuplexPipe>();
            b.AddTypeShapeProvider(PolyType.ReflectionProvider.ReflectionTypeShapeProvider.Default);
        });

        NerdbankMessagePackFormatter clientFormatter = new()
        {
            MultiplexingStream = this.clientMx,
        };

        clientFormatter.SetFormatterProfile(b =>
        {
            b.RegisterPipeReaderType<PipeReader>();
            b.RegisterPipeWriterType<PipeWriter>();
            b.RegisterDuplexPipeType<MultiplexingStream.Channel>();
            b.RegisterDuplexPipeType<IDuplexPipe>();
            b.AddTypeShapeProvider(PolyType.ReflectionProvider.ReflectionTypeShapeProvider.Default);
        });

        this.serverMessageFormatter = serverFormatter;
        this.clientMessageFormatter = clientFormatter;
    }
}
