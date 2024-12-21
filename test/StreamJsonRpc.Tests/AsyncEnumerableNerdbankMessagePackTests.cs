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
        this.serverMessageFormatter = new NerdbankMessagePackFormatter();
        this.clientMessageFormatter = new NerdbankMessagePackFormatter();
    }
}
