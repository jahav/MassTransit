namespace MassTransit.Transports.InMemory
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Context;
    using Contexts;
    using Events;
    using Fabric;
    using GreenPipes;
    using GreenPipes.Agents;


    /// <summary>
    /// Support in-memory message queue that is not durable, but supports parallel delivery of messages
    /// based on TPL usage.
    /// </summary>
    public class InMemoryReceiveTransport :
        Agent,
        IReceiveTransport,
        IInMemoryQueueConsumer
    {
        readonly Uri _inputAddress;
        readonly IInMemoryQueue _queue;
        readonly ReceiveEndpointContext _context;
        readonly IReceivePipeDispatcher _dispatcher;

        public InMemoryReceiveTransport(Uri inputAddress, IInMemoryQueue queue, ReceiveEndpointContext context)
        {
            _inputAddress = inputAddress;
            _queue = queue;
            _context = context;

            _dispatcher = context.CreateReceivePipeDispatcher();
        }

        public async Task Consume(InMemoryTransportMessage message, CancellationToken cancellationToken)
        {
            await Ready.ConfigureAwait(false);
            if (IsStopped)
                return;

            LogContext.Current = _context.LogContext;

            var context = new InMemoryReceiveContext(message, _context);
            try
            {
                await _dispatcher.Dispatch(context).ConfigureAwait(false);
            }
            catch (Exception)
            {
                message.DeliveryCount++;
            }
            finally
            {
                context.Dispose();
            }
        }

        public void Probe(ProbeContext context)
        {
            var scope = context.CreateScope("inMemoryReceiveTransport");
            scope.Set(new {Address = _inputAddress});
        }

        ReceiveTransportHandle IReceiveTransport.Start()
        {
            try
            {
                var consumerHandle = _queue.ConnectConsumer(this);

                void NotifyReady()
                {
                    _context.TransportObservers.Ready(new ReceiveTransportReadyEvent(_inputAddress));

                    SetReady();
                }

                Task.Factory.StartNew(NotifyReady, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);

                return new Handle(this, consumerHandle);
            }
            catch (Exception exception)
            {
                SetNotReady(exception);
                throw;
            }
        }

        ConnectHandle IReceiveObserverConnector.ConnectReceiveObserver(IReceiveObserver observer)
        {
            return _context.ConnectReceiveObserver(observer);
        }

        ConnectHandle IReceiveTransportObserverConnector.ConnectReceiveTransportObserver(IReceiveTransportObserver observer)
        {
            return _context.ConnectReceiveTransportObserver(observer);
        }

        ConnectHandle IPublishObserverConnector.ConnectPublishObserver(IPublishObserver observer)
        {
            return _context.ConnectPublishObserver(observer);
        }

        ConnectHandle ISendObserverConnector.ConnectSendObserver(ISendObserver observer)
        {
            return _context.ConnectSendObserver(observer);
        }


        class Handle :
            ReceiveTransportHandle
        {
            readonly InMemoryReceiveTransport _transport;
            readonly ConnectHandle _consumerHandle;

            public Handle(InMemoryReceiveTransport transport, ConnectHandle consumerHandle)
            {
                _transport = transport;
                _consumerHandle = consumerHandle;
            }

            async Task ReceiveTransportHandle.Stop(CancellationToken cancellationToken)
            {
                LogContext.SetCurrentIfNull(_transport._context.LogContext);

                await _transport.Stop("Stop", cancellationToken).ConfigureAwait(false);

                _consumerHandle.Disconnect();

                var completed = new ReceiveTransportCompletedEvent(_transport._inputAddress, _transport._dispatcher.GetDeliveryMetrics());

                await _transport._context.TransportObservers.Completed(completed).ConfigureAwait(false);
            }
        }
    }
}
