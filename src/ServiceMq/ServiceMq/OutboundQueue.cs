﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ServiceWire.NamedPipes;
using ServiceWire.TcpIp;

namespace ServiceMq
{
    public enum QueueState
    {
        Running,
        Cautioned,
        Failed
    }

    internal class OutboundQueue
    {
        private const string DtFormat = "yyyyMMddHHmmssfff";

        private readonly Queue<OutboundMessage> mq = new Queue<OutboundMessage>();
        private readonly Dictionary<string, Queue<OutboundMessage>> retryQueues = new Dictionary<string, Queue<OutboundMessage>>();
        private readonly string msgDir;
        private readonly string outDir;
        private readonly string sentDir;
        private readonly string failDir;
        private readonly string name;
        private readonly int connectTimeOutMs;

        private volatile bool continueProcessing = true;
        private ManualResetEvent outgoingMessageWaitHandle = new ManualResetEvent(false);
        private readonly Timer timer = null;
        private readonly double hoursReadSentLogsToLive;
        private ushort tcount = 0;
        private DateTime lastCleaned = DateTime.Now.AddDays(-10);
        private Exception stateException = null;
        private QueueState state = QueueState.Running;

        public OutboundQueue(string name, string msgDir, double hoursReadSentLogsToLive, int connectTimeOutMs)
        {
            this.name = name;
            this.msgDir = msgDir;
            this.hoursReadSentLogsToLive = hoursReadSentLogsToLive;
            this.connectTimeOutMs = connectTimeOutMs;
            this.outDir = Path.Combine(msgDir, "out");
            this.sentDir = Path.Combine(msgDir, "sent");
            this.failDir = Path.Combine(msgDir, "fail");
            Directory.CreateDirectory(this.outDir);
            Directory.CreateDirectory(this.sentDir);
            Directory.CreateDirectory(this.failDir);

            //kick off sending thread
            Task.Factory.StartNew(SendMessages, TaskCreationOptions.LongRunning);

            //read from out to hydrate queue
            try
            {
                var list = new List<string>(Directory.GetFiles(this.outDir, "*.omq"));
                if (list.Count > 0)
                {
                    list.Sort();
                    foreach (var msgFile in list)
                    {
                        var msg = OutboundMessage.ReadFromFile(msgFile);
                        if (null != msg) mq.Enqueue(msg);
                    }
                    outgoingMessageWaitHandle.Set();
                }
            }
            catch (Exception e)
            {
                this.stateException = e;
                this.state = QueueState.Failed;
                throw; //be sure to bubble this one up
            }

            //fire send loop every two seconds to prevent failing messages from hanging up
            this.timer = new Timer(SpinSending, null, 2000, 2000);
        }

        public long Count
        {
            get
            {
                long count = 0;
                lock (mq)
                {
                    count = mq.Count;
                }
                lock (retryQueues)
                {
                    foreach (var kvp in retryQueues) count += kvp.Value.Count;
                    return count;
                }
            }
        }

        public Exception StateException
        {
            get { return stateException; }
        }

        public QueueState State
        {
            get { return state; }
        }

        public void ClearState()
        {
            stateException = null;
            state = QueueState.Running;
        }

        private void SpinSending(object state)
        {
            try
            {
                outgoingMessageWaitHandle.Set();
            }
            catch(Exception e)
            {
                this.stateException = e;
                this.state = QueueState.Cautioned;
            }
        }

        public void Stop()
        {
            try
            {
                continueProcessing = false;
                outgoingMessageWaitHandle.Set();
                outgoingMessageWaitHandle.Dispose();
                timer.Dispose();
            }
            catch (Exception e)
            {
                this.stateException = e;
                this.state = QueueState.Failed;
                throw; //bubble it up
            }
        }

        public void Enqueue(OutboundMessage msg)
        {
            lock (mq)
            {
                //write to q file for that address
                //yyyyMMddHHmmssfff-16bit-from-to-ipport-or-pipename
                //20140324142165412-0415-010-042-024-155-08746-pipename.omq

                var fileName = string.Format("{0}-{1}-{2}.omq", 
                    msg.Sent.ToString(DtFormat), 
                    tcount.ToString("0000"),
                    msg.To.ToFileNameString());

                msg.Filename = Path.Combine(this.outDir, fileName);

                //idguid   address-from   address-to   senttimestamp   msgtypename   bin/str   message(base64forbin)
                var line = msg.ToLine();
                File.WriteAllText(msg.Filename, line);

                mq.Enqueue(msg);

                //increment and roll tcount - max 9999
                tcount = (tcount > 9999) ? (ushort)0 : (ushort)(tcount + 1);

                //signal item to send
                outgoingMessageWaitHandle.Set();
            }
        }

        private void SendMessages()
        {
            while (continueProcessing)
            {
                try
                {
                    outgoingMessageWaitHandle.WaitOne();
                    if (!continueProcessing) break;
                    OutboundMessage message = null;
                    lock (mq)
                    {
                        if (mq.Count > 0)
                        {
                            message = mq.Dequeue();
                            //attempt to prevent excessive memory footprint
                            if (mq.Count > 1000) mq.TrimExcess();
                        }
                    }
                    var attemptTime = DateTime.Now;
                    bool fromRegularQueue = true;
                    if (null == message)  //look for oldest in retry queues
                    {
                        var keyWithOldest = string.Empty;
                        var oldest = DateTime.Now;
                        lock (retryQueues)
                        {
                            foreach (var kvp in retryQueues)
                            {
                                if (kvp.Value.Count > 0)
                                {
                                    var peekMsg = kvp.Value.Peek();
                                    if (peekMsg.LastSendAttempt < oldest)
                                    {
                                        //only choosee if it is a viable retry candidate
                                        var secondsSinceLast = (attemptTime - peekMsg.LastSendAttempt).TotalSeconds;
                                        if (peekMsg.SendAttempts < secondsSinceLast)
                                        {
                                            oldest = peekMsg.LastSendAttempt;
                                            keyWithOldest = kvp.Key;
                                        }
                                    }
                                }
                            }
                            if (keyWithOldest != string.Empty)
                            {
                                message = retryQueues[keyWithOldest].Dequeue();
                                fromRegularQueue = false;
                            }
                        }
                    }

                    if (null == message)
                    {
                        //set to nonsignaled and block on WaitOne again
                        outgoingMessageWaitHandle.Reset();
                    }
                    else
                    {
                        //process the message
                        var dest = message.To.ToFileNameString();
                        lock (retryQueues)
                        {
                            //check to see if this regular queue message is being sent to an address that is failing
                            if (fromRegularQueue && retryQueues.ContainsKey(dest) && retryQueues[dest].Count > 0)
                            {
                                //put regular mq msg onto retry queue to preserve order to that address
                                retryQueues[dest].Enqueue(message);

                                //set message to null and get oldest if it is time to retry - 
                                message = null;
                                var peekOld = retryQueues[dest].Peek();
                                //should attempt if diff is more than send attempts in seconds
                                var peekSeconds = (attemptTime - peekOld.LastSendAttempt).TotalSeconds;
                                if (peekOld.SendAttempts < peekSeconds)
                                {
                                    message = peekOld;
                                    fromRegularQueue = false;
                                }
                            }

                            //skip if message went onto failure heap
                            if (null != message)
                            {
                                //if attempts exceed X or time since sent, add to fail
                                message.LastSendAttempt = attemptTime;
                                message.SendAttempts++;
                                try
                                {
                                    SendMessage(message);
                                    LogSent(message);
                                    if (!fromRegularQueue) retryQueues[dest].Dequeue(); //pulls peeked obj off as success
                                }
                                catch (Exception e)
                                {
                                    if ((message.LastSendAttempt - message.Sent).TotalHours > 24.0)
                                    {
                                        LogFailed(message);
                                        if (!fromRegularQueue) retryQueues[dest].Dequeue(); //pulls peeked obj off no more trying
                                    }
                                    else
                                    {
                                        if (fromRegularQueue)
                                        {
                                            if (!retryQueues.ContainsKey(dest)) retryQueues.Add(dest, new Queue<OutboundMessage>());
                                            retryQueues[dest].Enqueue(message);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    this.stateException = e;
                    this.state = QueueState.Cautioned;
                }
            }
        }

        private void SendMessage(OutboundMessage message)
        {
            NpClient<IMessageService> npClient = null;
            TcpClient<IMessageService> tcpClient = null;
            IMessageService proxy = null;
            try
            {
                var useNpClient = false;
                if (message.To.Transport == Transport.Both)
                {
                    if (message.To.ServerName == message.From.ServerName)
                    {
                        useNpClient = true;
                    }
                }
                else if (message.To.Transport == Transport.Np) useNpClient = true;

                if (useNpClient)
                {
                    npClient = new NpClient<IMessageService>(new NpEndPoint(message.To.PipeName, connectTimeOutMs));
                    proxy = npClient.Proxy;
                }
                else
                {
                    tcpClient = new TcpClient<IMessageService>(new TcpEndPoint(
                        new IPEndPoint(IPAddress.Parse(message.To.IpAddress), message.To.Port), 
                        connectTimeOutMs));
                    proxy = tcpClient.Proxy;
                }

                if (null == message.MessageBytes)
                {
                    proxy.EnqueueString(message.Id, message.From.ToString(), message.Sent, message.SendAttempts,
                         message.MessageTypeName, message.MessageString);
                }
                else
                {
                    proxy.EnqueueBytes(message.Id, message.From.ToString(), message.Sent, message.SendAttempts,
                        message.MessageTypeName, message.MessageBytes);
                }
            }
            finally
            {
                if (null != tcpClient) tcpClient.Dispose();
                if (null != npClient) npClient.Dispose();
            }
        }

        private const string DtLogFormat = "yyyyMMdd-HH-mm";

        private void LogFailed(OutboundMessage message)
        {
            try
            {
                var fileName = string.Format("fail-{0}.log", DateTime.Now.ToString(DtLogFormat));
                var logFile = Path.Combine(this.failDir, fileName);
                var line = message.ToLine().ToFlatLine();
                File.AppendAllLines(logFile, new string[] { line });
                File.Delete(message.Filename);
            }
            catch (Exception e)
            {
                this.stateException = e;
                this.state = QueueState.Cautioned;
            }
        }

        private void LogSent(OutboundMessage message)
        {
            try
            {
                var fileName = string.Format("sent-{0}.log", DateTime.Now.ToString(DtLogFormat));
                var logFile = Path.Combine(this.sentDir, fileName);
                var line = message.ToLine().ToFlatLine();
                File.AppendAllLines(logFile, new string[] { line });
                File.Delete(message.Filename);
            }
            catch (Exception e)
            {
                this.stateException = e;
                this.state = QueueState.Cautioned;
            }

            //cleanup every two hours
            if ((DateTime.Now - lastCleaned).TotalHours > 2.0)
            {
                lastCleaned = DateTime.Now;
                Task.Factory.StartNew(() =>
                    {
                        try
                        {
                            var files = Directory.GetFiles(this.sentDir);
                            foreach (var file in files)
                            {
                                var info = new FileInfo(file);
                                if ((DateTime.Now - info.LastWriteTime).TotalHours > this.hoursReadSentLogsToLive)
                                {
                                    File.Delete(file);
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            this.stateException = e;
                            this.state = QueueState.Cautioned;
                        }
                    }, TaskCreationOptions.LongRunning);
            }
        }
    }
}
