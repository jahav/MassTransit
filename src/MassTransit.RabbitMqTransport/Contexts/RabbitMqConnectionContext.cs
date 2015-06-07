// Copyright 2007-2015 Chris Patterson, Dru Sellers, Travis Smith, et. al.
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.
namespace MassTransit.RabbitMqTransport.Contexts
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Context;
    using RabbitMQ.Client;
    using Util;


    public class RabbitMqConnectionContext :
        ConnectionContext,
        IDisposable
    {
        readonly RabbitMqHostSettings _hostSettings;
        readonly object _lock = new object();
        readonly PayloadCache _payloadCache;
        readonly CancellationTokenSource _tokenSource;
        readonly QueuedTaskScheduler _taskScheduler;
        IConnection _connection;
        CancellationTokenRegistration _registration;

        public RabbitMqConnectionContext(IConnection connection, RabbitMqHostSettings hostSettings, CancellationToken cancellationToken)
        {
            _connection = connection;
            _hostSettings = hostSettings;
            _payloadCache = new PayloadCache();

            _tokenSource = new CancellationTokenSource();
            _registration = cancellationToken.Register(OnCancellationRequested);
            _taskScheduler = new QueuedTaskScheduler(TaskScheduler.Default, 1);

            connection.ConnectionShutdown += OnConnectionShutdown;
        }

        public RabbitMqHostSettings HostSettings
        {
            get { return _hostSettings; }
        }

        public async Task<IModel> CreateModel()
        {
            return await Task.Factory.StartNew(() => _connection.CreateModel(),
                _tokenSource.Token, TaskCreationOptions.HideScheduler, _taskScheduler);
        }

        public bool HasPayloadType(Type contextType)
        {
            return _payloadCache.HasPayloadType(contextType);
        }

        public bool TryGetPayload<TPayload>(out TPayload context)
            where TPayload : class
        {
            return _payloadCache.TryGetPayload(out context);
        }

        public TPayload GetOrAddPayload<TPayload>(PayloadFactory<TPayload> payloadFactory)
            where TPayload : class
        {
            return _payloadCache.GetOrAddPayload(payloadFactory);
        }

        public IConnection Connection
        {
            get
            {
                lock (_lock)
                {
                    if (_connection == null)
                        throw new InvalidOperationException("The connection was closed");

                    return _connection;
                }
            }
        }

        public CancellationToken CancellationToken
        {
            get { return _tokenSource.Token; }
        }

        public void Dispose()
        {
            _connection.ConnectionShutdown -= OnConnectionShutdown;

            Close(200, "Connection disposed");
        }

        void OnConnectionShutdown(object connection, ShutdownEventArgs reason)
        {
            _tokenSource.Cancel();

            Close(reason.ReplyCode, reason.ReplyText);
        }

        void OnCancellationRequested()
        {
            _tokenSource.Cancel();
        }

        void Close(ushort replyCode, string message)
        {
            lock (_lock)
            {
                _registration.Dispose();

                _connection.Cleanup(replyCode, message);
                _connection = null;
            }
        }
    }
}