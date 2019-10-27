// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace StreamJsonRpc
{
    using System.Reflection;
    using System.Threading;
    using StreamJsonRpc.Protocol;

    public interface IJsonRpcMethodDispatchEvents
    {
        /// <summary>
        /// Called before the <paramref name="request"/> is dispatched to the <paramref name="methodInfo"/> on the <paramref name="target"/> object.
        /// </summary>
        /// <param name="request">The incoming request.</param>
        /// <param name="methodInfo">The method that will be invoked.</param>
        /// <param name="target">The target object the method will be invoked on.</param>
        /// <param name="cancellationToken">A cancellation token.</param>
        /// <remarks>Throw an exception to prevent the method dispatch.</remarks>
        void OnMethodInvoking(JsonRpcRequest request, MethodInfo methodInfo, object target, CancellationToken cancellationToken);
    }
}
