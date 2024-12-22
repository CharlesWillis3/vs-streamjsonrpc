// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

public class AsyncEnumerableNerdbankMessagePackTests : AsyncEnumerableTests
{
    public AsyncEnumerableNerdbankMessagePackTests(ITestOutputHelper logger)
        : base(logger)
    {
    }

    protected override void InitializeFormattersAndHandlers()
    {
        NerdbankMessagePackFormatter serverFormatter = new();
        serverFormatter.SetFormatterContext(ConfigureContext);

        NerdbankMessagePackFormatter clientFormatter = new();
        clientFormatter.SetFormatterContext(ConfigureContext);

        this.serverMessageFormatter = serverFormatter;
        this.clientMessageFormatter = clientFormatter;

        static void ConfigureContext(NerdbankMessagePackFormatter.FormatterContextBuilder contextBuilder)
        {
            contextBuilder.RegisterAsyncEnumerableType<IAsyncEnumerable<int>, int>();
        }
    }
}
