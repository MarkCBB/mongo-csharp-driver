﻿/* Copyright 2010-2014 MongoDB Inc.
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
using System.Security;
using System.Text;
using MongoDB.Driver.Core.Authentication.Sspi;
using MongoDB.Driver.Core.Connections;
using MongoDB.Driver.Core.Misc;

namespace MongoDB.Driver.Core.Authentication
{
    public sealed class GssapiAuthenticator : SaslAuthenticator
    {
        // constants
        private const string __canonicalizeHostNamePropertyName = "CANONICALIZE_HOST_NAME";
        private const string __serviceNamePropertyName = "SERVICE_NAME";
        private const string __serviceRealmPropertyName = "REALM";

        // static properties
        public static string CanonicalizeHostNamePropertyName
        {
            get { return __canonicalizeHostNamePropertyName; }
        }

        public static string DefaultServiceName
        {
            get { return "mongodb"; }
        }

        public static string MechanismName
        {
            get { return "GSSAPI"; }
        }

        public static string ServiceNamePropertyName
        {
            get { return __serviceNamePropertyName; }
        }
        
        public static string ServiceRealmPropertyName
        {
            get { return __serviceRealmPropertyName; }
        }

        // constructors
        public GssapiAuthenticator(UsernamePasswordCredential credential, IEnumerable<KeyValuePair<string, string>> properties)
            : base(CreateMechanism(credential, properties))
        {
        }

        public GssapiAuthenticator(string username, IEnumerable<KeyValuePair<string, string>> properties)
            : base(CreateMechanism(username, null, properties))
        {
        }

        public override string DatabaseName
        {
            get { return "$external"; }
        }

        private static GssapiMechanism CreateMechanism(UsernamePasswordCredential credential, IEnumerable<KeyValuePair<string, string>> properties)
        {
            if (credential.Source != "$external")
            {
                throw new MongoAuthenticationException("GSSAPI authentication may only use the $external source.");
            }

            return CreateMechanism(credential.Username, credential.Password, properties);
        }

        private static GssapiMechanism CreateMechanism(string username, SecureString password, IEnumerable<KeyValuePair<string, string>> properties)
        {
            var serviceName = DefaultServiceName;
            var canonicalizeHostName = false;
            string realm = null;
            if (properties != null)
            {
                foreach (var pair in properties)
                {
                    switch (pair.Key.ToUpperInvariant())
                    {
                        case __serviceNamePropertyName:
                            serviceName = (string)pair.Value;
                            break;
                        case __serviceRealmPropertyName:
                            realm = (string)pair.Value;
                            break;
                        case __canonicalizeHostNamePropertyName:
                            canonicalizeHostName = bool.Parse(pair.Value);
                            break;
                        default:
                            var message = string.Format("Unknown GSSAPI property '{0}'.", pair.Key);
                            throw new MongoAuthenticationException(message);
                    }
                }
            }

            return new GssapiMechanism(serviceName, canonicalizeHostName, realm, username, password);
        }

        // nested classes
        private class GssapiMechanism : ISaslMechanism
        {
            // fields
            private readonly bool _canonicalizeHostName;
            private readonly SecureString _password;
            private readonly string _realm;
            private readonly string _serviceName;
            private readonly string _username;

            public GssapiMechanism(string serviceName, bool canonicalizeHostName, string realm, string username, SecureString password)
            {
                _serviceName = serviceName;
                _canonicalizeHostName = canonicalizeHostName;
                _realm = realm;
                _username = Ensure.IsNotNullOrEmpty(username, "username");
                _password = password;
            }

            public string Name
            {
                get { return MechanismName; }
            }

            public ISaslStep Initialize(IConnection connection, ConnectionDescription description)
            {
                Ensure.IsNotNull(connection, "connection");
                Ensure.IsNotNull(description, "description");

                string hostName;
                var dnsEndPoint = connection.EndPoint as DnsEndPoint;
                if (dnsEndPoint != null)
                {
                    hostName = dnsEndPoint.Host;
                }
                else if (connection.EndPoint is IPEndPoint)
                {
                    hostName = ((IPEndPoint)connection.EndPoint).Address.ToString();
                }
                else
                {
                    throw new MongoAuthenticationException("Only DnsEndPoint and IPEndPoint are supported for GSSAPI authentication.");
                }

                if (_canonicalizeHostName)
                {
                    var entry = Dns.GetHostEntry(hostName);
                    if (entry != null)
                    {
                        hostName = entry.HostName;
                    }
                }

                return new FirstStep(_serviceName, hostName, _realm, _username, _password);
            }
        }

        private class FirstStep : ISaslStep
        {
            private readonly string _authorizationId;
            private readonly SecureString _password;
            private readonly string _servicePrincipalName;

            public FirstStep(string serviceName, string hostName, string realm, string username, SecureString password)
            {
                _authorizationId = username;
                _password = password;
                _servicePrincipalName = string.Format("{0}/{1}", serviceName, hostName);
                if (!string.IsNullOrEmpty(realm))
                {
                    _servicePrincipalName += "@" + realm;
                }
            }

            public byte[] BytesToSendToServer
            {
                get { return new byte[0]; }
            }

            public bool IsComplete
            {
                get { return false; }
            }

            public ISaslStep Transition(SaslConversation conversation, byte[] bytesReceivedFromServer)
            {
                SecurityCredential securityCredential;
                try
                {
                    securityCredential = SecurityCredential.Acquire(SspiPackage.Kerberos, _authorizationId, _password);
                    conversation.RegisterItemForDisposal(securityCredential);
                }
                catch (Win32Exception ex)
                {
                    throw new MongoAuthenticationException("Unable to acquire security credential.", ex);
                }

                byte[] bytesToSendToServer;
                Sspi.SecurityContext context;
                try
                {
                    context = Sspi.SecurityContext.Initialize(securityCredential, _servicePrincipalName, bytesReceivedFromServer, out bytesToSendToServer);
                }
                catch (Win32Exception ex)
                {
                    if (_password != null)
                    {
                        throw new MongoAuthenticationException("Unable to initialize security context. Ensure the username and password are correct.", ex);
                    }
                    else
                    {
                        throw new MongoAuthenticationException("Unable to initialize security context.", ex);
                    }
                }

                if (!context.IsInitialized)
                {
                    return new InitializeStep(_servicePrincipalName, _authorizationId, context, bytesToSendToServer);
                }

                return new NegotiateStep(_authorizationId, context, bytesToSendToServer);
            }
        }

        private class InitializeStep : ISaslStep
        {
            private readonly string _authorizationId;
            private readonly Sspi.SecurityContext _context;
            private readonly byte[] _bytesToSendToServer;
            private readonly string _servicePrincipalName;

            public InitializeStep(string servicePrincipalName, string authorizationId, Sspi.SecurityContext context, byte[] bytesToSendToServer)
            {
                _servicePrincipalName = servicePrincipalName;
                _authorizationId = authorizationId;
                _context = context;
                _bytesToSendToServer = bytesToSendToServer ?? new byte[0];
            }

            public byte[] BytesToSendToServer
            {
                get { return _bytesToSendToServer; }
            }

            public bool IsComplete
            {
                get { return false; }
            }

            public ISaslStep Transition(SaslConversation conversation, byte[] bytesReceivedFromServer)
            {
                byte[] bytesToSendToServer;
                try
                {
                    _context.Initialize(_servicePrincipalName, bytesReceivedFromServer, out bytesToSendToServer);
                }
                catch (Win32Exception ex)
                {
                    throw new MongoAuthenticationException("Unable to initialize security context", ex);
                }

                if (!_context.IsInitialized)
                {
                    return new InitializeStep(_servicePrincipalName, _authorizationId, _context, bytesToSendToServer);
                }

                return new NegotiateStep(_authorizationId, _context, bytesToSendToServer);
            }
        }

        private class NegotiateStep : ISaslStep
        {
            private readonly string _authorizationId;
            private readonly Sspi.SecurityContext _context;
            private readonly byte[] _bytesToSendToServer;

            public NegotiateStep(string authorizationId, Sspi.SecurityContext context, byte[] bytesToSendToServer)
            {
                _authorizationId = authorizationId;
                _context = context;
                _bytesToSendToServer = bytesToSendToServer ?? new byte[0];
            }

            public byte[] BytesToSendToServer
            {
                get { return _bytesToSendToServer; }
            }

            public bool IsComplete
            {
                get { return false; }
            }

            public ISaslStep Transition(SaslConversation conversation, byte[] bytesReceivedFromServer)
            {
                // Even though RFC says that clients should specifically check this and raise an error
                // if it isn't true, this breaks on Windows XP, so we are skipping the check for windows
                // XP, identified as Win32NT 5.1: http://msdn.microsoft.com/en-us/library/windows/desktop/ms724832(v=vs.85).aspx
                if (Environment.OSVersion.Platform != PlatformID.Win32NT ||
                    Environment.OSVersion.Version.Major != 5)
                {
                    if (bytesReceivedFromServer == null || bytesReceivedFromServer.Length != 32) //RFC specifies this must be 4 octets
                    {
                        throw new MongoAuthenticationException("Invalid server response.");
                    }
                }

                byte[] decryptedBytes;
                try
                {
                    _context.DecryptMessage(0, bytesReceivedFromServer, out decryptedBytes);
                }
                catch (Win32Exception ex)
                {
                    throw new MongoAuthenticationException("Unabled to decrypt message.", ex);
                }

                int length = 4;
                if (_authorizationId != null)
                {
                    length += _authorizationId.Length;
                }

                bytesReceivedFromServer = new byte[length];
                bytesReceivedFromServer[0] = 0x1; // NO_PROTECTION
                bytesReceivedFromServer[1] = 0x0; // NO_PROTECTION
                bytesReceivedFromServer[2] = 0x0; // NO_PROTECTION
                bytesReceivedFromServer[3] = 0x0; // NO_PROTECTION

                if (_authorizationId != null)
                {
                    var authorizationIdBytes = Encoding.UTF8.GetBytes(_authorizationId);
                    authorizationIdBytes.CopyTo(bytesReceivedFromServer, 4);
                }

                byte[] bytesToSendToServer;
                try
                {
                    _context.EncryptMessage(bytesReceivedFromServer, out bytesToSendToServer);
                }
                catch (Win32Exception ex)
                {
                    throw new MongoAuthenticationException("Unabled to encrypt message.", ex);
                }

                return new CompletedStep(bytesToSendToServer);
            }
        }
    }
}