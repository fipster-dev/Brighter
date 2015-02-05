﻿// ***********************************************************************
// Assembly         : paramore.brighter.commandprocessor.messaginggateway.rmq
// Author           : ian
// Created          : 07-01-2014
//
// Last Modified By : ian
// Last Modified On : 07-29-2014
// ***********************************************************************
// <copyright file="ServerRequestHandler.cs" company="">
//     Copyright (c) . All rights reserved.
// </copyright>
// <summary></summary>
// ***********************************************************************

#region Licence
/* The MIT License (MIT)
Copyright © 2014 Ian Cooper <ian_hammond_cooper@yahoo.co.uk>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the “Software”), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE. */
#endregion

using System;

using Common.Logging;

using Newtonsoft.Json;

using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace paramore.brighter.commandprocessor.messaginggateway.rmq
{
    /// <summary>
    /// Class ServerRequestHandler .
    /// The <see cref="RmqMessageConsumer"/> is used on the server to receive messages from the broker. It abstracts away the details of 
    /// inter-process communication tasks from the server. It handles connection establishment, request reception and dispatching, 
    /// result sending, and error handling.
    /// </summary>
    public class RmqMessageConsumer : MessageGateway, IAmAMessageConsumer 
    {
        const bool AUTO_ACK = false;
        /// <summary>
        /// The consumer
        /// </summary>
        QueueingBasicConsumer consumer;
        private readonly RmqMessageCreator messageCreator;

        /// <summary>
        /// Initializes a new instance of the <see cref="MessageGateway" /> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        public RmqMessageConsumer(ILog logger) : base(logger)
        {
            messageCreator = new RmqMessageCreator(logger);
        }

        /// <summary>
        /// Acknowledges the specified message.
        /// </summary>
        /// <param name="message">The message.</param>
        public void Acknowledge(Message message)
         {
            if (Channel != null)
            {
                var deliveryTag = message.GetDeliveryTag();
                Logger.Debug(m => m("RmqMessageConsumer: Acknowledging message {0} as completed with delivery tag {1}", message.Id, deliveryTag));
                Channel.BasicAck(deliveryTag, false);
            }
         }

        /// <summary>
        /// Purges the specified queue name.
        /// </summary>
        /// <param name="queueName">Name of the queue.</param>
        public void Purge(string queueName)
        {
            if (Channel != null)
            {
                Logger.Debug(m => m("RmqMessageConsumer: Purging channel"));
                Channel.QueuePurge(queueName);
            }
        }

        public void Requeue(Message message)
        {
            if (Channel != null)
            {
                var rmqMessagePublisher = new RmqMessagePublisher(Channel, Configuration.Exchange.Name);
                Logger.Debug(m => m("RmqMessageConsumer: Re-queueing message"));
                rmqMessagePublisher.PublishMessage(message);
                Reject(message, false);
            }
        }

        /// <summary>
        /// Rejects the specified message.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="requeue">if set to <c>true</c> [requeue].</param>
        public void Reject(Message message, bool requeue)
        {
            if (Channel != null)
            {
                Logger.Debug(m => m("RmqMessageConsumer: NoAck message {0}", message.Id));
                Channel.BasicNack(message.GetDeliveryTag(), false, requeue);
            }
        }

        /// <summary>
        /// Receives the specified queue name.
        /// </summary>
        /// <param name="queueName">Name of the queue.</param>
        /// <param name="timeoutInMilliseconds">The timeout in milliseconds.</param>
        /// <returns>Message.</returns>
        public Message Receive(string queueName, string routingKey, int timeoutInMilliseconds)
        {
            Logger.Debug(m => m("RmqMessageConsumer: Preparing to retrieve next message from queue {0} with routing key {1} via exchange {2} on connection {3}", queueName, routingKey, Configuration.Exchange.Name, Configuration.AMPQUri.Uri.ToString()));

            if (!Connect(queueName, routingKey, true))
            {
                Logger.Debug(m => m("RmqMessageConsumer: Unable to connect to the queue {0} with routing key {1} via exchange {2} on connection {3}", queueName, routingKey, Configuration.Exchange.Name, Configuration.AMPQUri.Uri.ToString()));
                throw ConnectionFailure;
            }

            var message = new Message();
            try
            {
                BasicDeliverEventArgs fromQueue;
                if (consumer.Queue.Dequeue(timeoutInMilliseconds, out fromQueue))
                {
                    message = messageCreator.CreateMessage(fromQueue);
                    Logger.Debug(m => m("RmqMessageConsumer: Received message from queue {0} with routing key {1} via exchange {2} on connection {3}, message: {5}{4}",
                        queueName, routingKey, Configuration.Exchange.Name, Configuration.AMPQUri.Uri.ToString(), JsonConvert.SerializeObject(message), Environment.NewLine));
                }
                else
                {
                    Logger.Debug(m => m("RmqMessageConsumer: Time out without recieving message from queue {0} with routing key {1} via exchange {2} on connection {3}", queueName, routingKey, Configuration.Exchange.Name, Configuration.AMPQUri.Uri.ToString()));
                }
            }
            catch (Exception exception)
            {
                Logger.Error(m => m("RmqMessageConsumer: There was an error listening to queue {0} via exchange {1} via exchange {2} on connection {3}", queueName, routingKey, Configuration.Exchange.Name, Configuration.AMPQUri.Uri.ToString()), exception);
                throw;
            }

            return message;

        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~RmqMessageConsumer()
        {
            Dispose(false);
        }

        void Dispose(bool disposing)
        {
            CloseConnection();
        }

        /// <summary>
        /// Connects the specified queue name.
        /// </summary>
        /// <param name="queueName">Name of the queue.</param>
        /// <param name="routingKey"></param>
        /// <returns><c>true</c> if XXXX, <c>false</c> otherwise.</returns>
        protected override bool Connect(string queueName = "", string routingKey = "", bool createQueues = false)
        {
            try
            {
                if (NotConnected())
                {
                    if (base.Connect(queueName, routingKey, createQueues))
                    {
                        consumer = new QueueingBasicConsumer(Channel);
                        Channel.BasicConsume(queueName, AUTO_ACK, consumer);

                        return true;
                    }

                    return false;
                }
            }
            catch (Exception exception)
            {
                Logger.Error(m => m("RmqMessageConsumer: There was an error connecting to the queue {0} with routing key {1} via exchange {2} on connection {3}", queueName, routingKey, Configuration.Exchange.Name, Configuration.AMPQUri.Uri.ToString()), exception);
                return false;
            }

            return true;
        }
    }
}
