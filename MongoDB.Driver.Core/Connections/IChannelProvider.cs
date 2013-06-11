﻿/* Copyright 2010-2013 10gen Inc.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Net;
using System.Threading;

namespace MongoDB.Driver.Core.Connections
{
    /// <summary>
    /// Provides connections.
    /// </summary>
    public interface IChannelProvider : IDisposable
    {
        /// <summary>
        /// Gets the DNS end point.
        /// </summary>
        DnsEndPoint DnsEndPoint { get; }

        /// <summary>
        /// Initializes the channel provider.
        /// </summary>
        void Initialize();

        /// <summary>
        /// Gets a channel.
        /// </summary>
        /// <param name="timeout">The timeout.</param>
        /// <param name="cancellationToken">The <see cref="T:System.Threading.CancellationToken" /> to observe.</param>
        /// <returns>A connection.</returns>
        IChannel GetChannel(TimeSpan timeout, CancellationToken cancellationToken);
    }

    public static class IConnectionProviderExtensions
    {
        /// <summary>
        /// Gets a channel.
        /// </summary>
        /// <returns>A channel.</returns>
        public static IChannel GetChannel(this IChannelProvider @this)
        {
            return @this.GetChannel(TimeSpan.FromMilliseconds(Timeout.Infinite), CancellationToken.None);
        }

        /// <summary>
        /// Gets a channel.
        /// </summary>
        /// <param name="cancellationToken">The <see cref="T:System.Threading.CancellationToken" /> to observe.</param>
        /// <returns>A channel.</returns>
        public static IChannel GetChannel(this IChannelProvider @this, CancellationToken cancellationToken)
        {
            return @this.GetChannel(TimeSpan.FromMilliseconds(Timeout.Infinite), cancellationToken);
        }

        /// <summary>
        /// Gets a channel.
        /// </summary>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or <see cref="F:System.Threading.Timeout.Infinite" />(-1) to wait indefinitely.</param>
        /// <returns>A channel.</returns>
        public static IChannel GetChannel(this IChannelProvider @this, TimeSpan timeout)
        {
            return @this.GetChannel(timeout, CancellationToken.None);
        }
    }
}