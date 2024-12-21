// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

public class DuplexPipeMarshalingNerdbankMessagePackTests : DuplexPipeMarshalingTests
{
    public DuplexPipeMarshalingNerdbankMessagePackTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override void InitializeFormattersAndHandlers()
    {
        this.serverMessageFormatter = new NerdbankMessagePackFormatter { MultiplexingStream = this.serverMx };
        this.clientMessageFormatter = new NerdbankMessagePackFormatter { MultiplexingStream = this.clientMx };
    }
}
