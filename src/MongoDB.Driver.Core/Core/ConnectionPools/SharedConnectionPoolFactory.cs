﻿/* Copyright 2013-2014 MongoDB Inc.
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
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using MongoDB.Driver.Core.Configuration;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Misc;
using MongoDB.Driver.Core.Servers;

namespace MongoDB.Driver.Core.ConnectionPools
{
    /// <summary>
    /// Represents a connection pool factory.
    /// </summary>
    public class SharedConnectionPoolFactory : IConnectionPoolFactory
    {
        // fields
        private readonly IConnectionFactory _connectionFactory;
        private readonly ConnectionPoolSettings _connectionPoolSettings;

        // constructors
        public SharedConnectionPoolFactory()
        {
            _connectionFactory = new BinaryConnectionFactory();
            _connectionPoolSettings = new ConnectionPoolSettings();
        }

        public SharedConnectionPoolFactory(
            IConnectionFactory connectionFactory,
            ConnectionPoolSettings connectionPoolSettings)
        {
            _connectionFactory = Ensure.IsNotNull(connectionFactory, "connectionFactory");
            _connectionPoolSettings = Ensure.IsNotNull(connectionPoolSettings, "connectionPoolSettings");
        }

        // methods
        public IConnectionPool CreateConnectionPool(ServerId serverId, EndPoint endPoint)
        {
            return new SharedConnectionPool(serverId, endPoint, _connectionPoolSettings, _connectionFactory);
        }
    }
}
